using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class NotificationTurn {
    public bool Armed;
    public int FinishedSamples;
    public string EventId;
}
internal sealed class NotificationEvent {
    public string Id, Session, Title, Directory, CompletedAt, SendError;
    public int Attempts;
    public bool Sent, Failed;
    public DateTime NextAttempt;
}
internal sealed class NotificationState {
    public Dictionary<string,NotificationTurn> Turns=new Dictionary<string,NotificationTurn>();
    public List<NotificationEvent> Events=new List<NotificationEvent>();
    public string LastCheckedAt, LastError, LastSendError;
    public void Observe(string id,string status,string title,string directory) {
        NotificationTurn turn;
        if(!Turns.TryGetValue(id,out turn))Turns[id]=turn=new NotificationTurn();
        if(status=="working") {
            if(!turn.Armed){turn.Armed=true;turn.EventId=Guid.NewGuid().ToString();}
            turn.FinishedSamples=0;
        } else if(status=="finished" && turn.Armed) {
            if(++turn.FinishedSamples<2)return;
            Events.Add(new NotificationEvent {Id=turn.EventId,Session=id,Title=title,Directory=directory,CompletedAt=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),NextAttempt=DateTime.UtcNow});
            turn.Armed=false;turn.FinishedSamples=0;
        } else {
            turn.FinishedSamples=0;
            if(status=="stopped" || status=="history"){turn.Armed=false;turn.EventId=null;}
        }
    }
}

// Independent of GUI selection, refresh toggle, drafts, and Claude quota control.
internal static class FeishuNotifications {
    internal static volatile bool Running;
    static readonly string Root=AppDomain.CurrentDomain.BaseDirectory;
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static string StatePath {get{return Path.Combine(Root,"feishu-notification-state.json");}}
    static string ConfigPath {get{return Path.Combine(Root,"feishu-notifications.json");}}
    static void Save(NotificationState state) {
        string temp=StatePath+".tmp";
        File.WriteAllText(temp,Json.Serialize(state),new UTF8Encoding(false));
        if(File.Exists(StatePath))File.Replace(temp,StatePath,null);else File.Move(temp,StatePath);
    }
    internal static string Quote(string value) {
        var output=new StringBuilder("\"");int slashes=0;
        foreach(char c in value) {
            if(c=='\\'){slashes++;continue;}
            if(c=='\"'){output.Append('\\',slashes*2+1);output.Append(c);slashes=0;continue;}
            output.Append('\\',slashes);slashes=0;output.Append(c);
        }
        output.Append('\\',slashes*2);return output.Append('"').ToString();
    }
    internal static string Message(NotificationEvent item) {
        string project=item.Directory.TrimEnd('/');int index=project.LastIndexOf('/');
        if(index>=0)project=project.Substring(index+1);
        return "Claude 对话本轮已完成\n项目："+project+"\n对话："+item.Title+"\n完成时间："+item.CompletedAt+"\n目录："+item.Directory+"\n会话："+item.Session;
    }
    internal static string SafeSendError(string output,int exitCode) {
        // Keep only structured diagnostic codes; never store message, hint, URL or tokens.
        try {
            var data=Json.Deserialize<Dictionary<string,object>>(output);
            object value;
            if(data.TryGetValue("error",out value) && value is Dictionary<string,object>) {
                var error=(Dictionary<string,object>)value;var fields=new List<string>();
                foreach(string key in new[]{"type","subtype","code"}) {
                    string field=Reader.Get(error,key);
                    if(field.Length>0 && field.Length<=80 && System.Text.RegularExpressions.Regex.IsMatch(field,"^[A-Za-z0-9_.-]+$"))fields.Add(key+"="+field);
                }
                if(fields.Count>0)return "CLI exit="+exitCode+"; "+String.Join("; ",fields);
            }
        }catch{}
        return "CLI exit="+exitCode+"; response_unconfirmed";
    }
    internal static void ApplyReceipts(NotificationState state,string receipts) {
        foreach(var row in Json.Deserialize<List<Dictionary<string,object>>>(receipts)) {
            string id=Reader.Get(row,"Id");
            if(Reader.Get(row,"MessageId")=="")continue;
            foreach(var item in state.Events)if(item.Id==id){item.Sent=true;item.Failed=false;item.SendError="";}
        }
    }
    static bool Send(Dictionary<string,object> config,NotificationEvent item,out string error,bool test=false) {
        error="";
        string cli=Reader.Get(config,"cliPath"),profile=Reader.Get(config,"profile"),user=Reader.Get(config,"userId");
        if(!File.Exists(cli) || profile=="" || !user.StartsWith("ou_")){error="invalid_notification_config";return false;}
        string message=test?"Claude 云端监控 · 通知测试\n完成通知已接通。后续任务本轮结束时，会发送项目名、对话标题和完成时间。\n这是一条测试消息，不代表任何任务完成。":Message(item);
        string args="--profile "+Quote(profile)+" im +messages-send --as bot --user-id "+Quote(user)+" --text "+Quote(message)+" --idempotency-key "+Quote(item.Id);
        var start=new ProcessStartInfo(cli,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=Root};
        using(var process=Process.Start(start)) {
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(30000)){process.Kill();error="cli_timeout_30s";return false;}
            // Never persist CLI responses, auth material, or conversation text.
            string output=stdout.GetAwaiter().GetResult(),errorOutput=stderr.GetAwaiter().GetResult();
            if(process.ExitCode!=0){error=SafeSendError(errorOutput,process.ExitCode);return false;}
            try {
                var data=Json.Deserialize<Dictionary<string,object>>(output);
                if(BackendApi.Flag(data,"ok"))return true;
                error=SafeSendError(output,process.ExitCode);return false;
            }catch{error="invalid_cli_json";return false;}
        }
    }
    internal static int TestSend() {
        var config=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(ConfigPath,Encoding.UTF8));
        string error;bool sent=Send(config,new NotificationEvent {Id=Guid.NewGuid().ToString()},out error,true);
        File.WriteAllText(Path.Combine(Root,"feishu-send-test.json"),Json.Serialize(new {sent=sent,test=true,error=error,checkedAt=DateTime.UtcNow.ToString("o")}),Encoding.UTF8);
        return sent?0:1;
    }
    internal static int Watch(CancellationToken cancellation=default(CancellationToken)) {
        if(cancellation.IsCancellationRequested)return 0;
        bool first;
        using(var mutex=new Mutex(true,"Local\\ClaudeFeishuNotificationsPublicInteractive",out first)) {
            if(!first)return 0;
            Running=true;
            try {
            var state=File.Exists(StatePath)?Json.Deserialize<NotificationState>(File.ReadAllText(StatePath,Encoding.UTF8)):new NotificationState();
            var reader=new Reader();
            using(cancellation.Register(reader.Stop))
            while(!cancellation.IsCancellationRequested) {
                try {
                    var config=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(ConfigPath,Encoding.UTF8));
                    string receiptsPath=Path.Combine(Root,"feishu-recovery-receipts.json");
                    if(File.Exists(receiptsPath))ApplyReceipts(state,File.ReadAllText(receiptsPath,Encoding.UTF8));
                    if(!BackendApi.Flag(config,"enabled")){if(cancellation.WaitHandle.WaitOne(8000))break;continue;}
                    var response=reader.NotificationMetadata().GetAwaiter().GetResult();
                    if(cancellation.IsCancellationRequested)break;
                    if(!BackendApi.Flag(response,"ok"))throw new Exception("metadata unavailable");
                    Dictionary<string,Dictionary<string,object>> local=null;
                    string metadata=Path.Combine(Root,"conversation-settings.json");
                    if(File.Exists(metadata))local=Json.Deserialize<Dictionary<string,Dictionary<string,object>>>(File.ReadAllText(metadata,Encoding.UTF8));
                    foreach(object row in (System.Collections.IEnumerable)response["conversations"]) {
                        var data=(Dictionary<string,object>)row;string id=Reader.Get(data,"id"),title=Reader.Get(data,"title");
                        Dictionary<string,object> settings;
                        if(local!=null && local.TryGetValue(reader.MetadataScope+"/"+id,out settings) && Reader.Get(settings,"title")!="")title=Reader.Get(settings,"title");
                        state.Observe(id,Reader.Get(data,"state"),title,Reader.Get(data,"dir"));
                    }
                    state.LastCheckedAt=DateTime.UtcNow.ToString("o");state.LastError="";Save(state);
                    foreach(var item in state.Events) {
                        if(cancellation.IsCancellationRequested)break;
                        if(item.Sent || item.Failed || item.NextAttempt>DateTime.UtcNow)continue;
                        if(item.Attempts>=3){item.Failed=true;Save(state);continue;}
                        item.Attempts++;item.NextAttempt=DateTime.UtcNow.AddMinutes(1);Save(state);
                        try {string error;item.Sent=Send(config,item,out error);item.SendError=error;}catch {item.Sent=false;item.SendError="cli_process_exception";}
                        item.Failed=!item.Sent && item.Attempts>=3;
                        if(!item.Sent)state.LastSendError=state.LastError="飞书通知发送失败："+item.Id+"；"+item.SendError+(item.Failed?"；已停止重试":"；稍后重试");
                        Save(state);
                    }
                    state.Events.RemoveAll(e=>(e.Sent || e.Failed) && DateTime.UtcNow-e.NextAttempt>TimeSpan.FromDays(30));
                    Save(state);
                }catch(Exception) {
                    if(cancellation.IsCancellationRequested)break;
                    state.LastError="通知服务无法读取配置或服务器状态；下次采样重试";
                    foreach(var turn in state.Turns.Values)turn.FinishedSamples=0;
                    try{Save(state);}catch{}
                }
                if(cancellation.WaitHandle.WaitOne(8000))break;
            }
            return 0;
            } finally {Running=false;mutex.ReleaseMutex();}
        }
    }
    internal static int SelfTest() {
        string safe=SafeSendError("{\"error\":{\"type\":\"authorization\",\"code\":99991679,\"message\":\"secret-token\",\"hint\":\"https://private\"}}",1);
        if(!safe.Contains("99991679") || safe.Contains("secret") || safe.Contains("https"))return 8;
        if(SafeSendError("private token",1).Contains("private"))return 9;
        var recovered=new NotificationState();recovered.Events.Add(new NotificationEvent {Id="recovered",Failed=true});
        ApplyReceipts(recovered,"[{\"Id\":\"recovered\",\"MessageId\":\"om_test\"}]");
        if(!recovered.Events[0].Sent || recovered.Events[0].Failed)return 10;
        using(var cancellation=new CancellationTokenSource()) {
            cancellation.Cancel();
            if(Watch(cancellation.Token)!=0 || Running || Watch(cancellation.Token)!=0 || Running)return 7;
        }
        var state=new NotificationState();
        state.Observe("a","finished","old","/projects/old");state.Observe("a","finished","old","/projects/old");
        if(state.Events.Count!=0)return 1;
        state.Observe("a","working","A","/projects/one");state.Observe("b","working","B","/projects/two");
        state.Observe("a","waiting","A","/projects/one");state.Observe("a","finished","A","/projects/one");
        state.Observe("a","unknown","A","/projects/one");state.Observe("a","finished","A","/projects/one");
        if(state.Events.Count!=0)return 2;
        state=Json.Deserialize<NotificationState>(Json.Serialize(state));
        state.Observe("a","finished","A 新标题","/projects/one");
        state.Observe("a","finished","A","/projects/one");
        state.Observe("b","finished","B","/projects/two");state.Observe("b","finished","B","/projects/two");
        if(state.Events.Count!=2 || state.Events[0].Title!="A 新标题" || state.Events[0].Id==state.Events[1].Id)return 3;
        state=Json.Deserialize<NotificationState>(Json.Serialize(state));state.Observe("a","finished","A","/projects/one");
        if(state.Events.Count!=2)return 4;
        state.Observe("a","working","A","/projects/one");state.Observe("a","finished","A","/projects/one");state.Observe("a","finished","A","/projects/one");
        if(state.Events.Count!=3 || state.Events[2].Id==state.Events[0].Id)return 5;
        state.Observe("c","working","C","/projects/three");state.Observe("c","stopped","C","/projects/three");state.Observe("c","finished","C","/projects/three");state.Observe("c","finished","C","/projects/three");
        if(state.Events.Count!=3 || !Message(state.Events[0]).Contains("项目：one") || !Message(state.Events[0]).Contains("A 新标题"))return 6;
        File.WriteAllText(Path.Combine(Root,"feishu-notification-test.json"),Json.Serialize(new {passed=true,checks=new[]{"startup baseline","two samples","parallel sessions","waiting and unknown","restart deduplication","new turn","stopped is not completed","project and local title"}}),Encoding.UTF8);
        return 0;
    }
}
