using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Same-user named pipe: no network listener and no arbitrary command execution.
internal static class BackendApi {
    internal static bool Flag(Dictionary<string,object> d,string key) {return Reader.Get(d,key).Equals("True",StringComparison.OrdinalIgnoreCase);}
    internal static async Task<Dictionary<string,object>> Execute(Dictionary<string,object> request,Reader reader) {
        string action=Reader.Get(request,"action");
        if(action=="quota")return await reader.AccountQuota();
        if(action!="list" && action!="sync")throw new ArgumentException("Supported actions: list, sync, quota");
        string root=AppDomain.CurrentDomain.BaseDirectory;
        string projectsPath=Path.Combine(root,"projects.json");
        var json=new JavaScriptSerializer();
        var projects=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(projectsPath,Encoding.UTF8));
        var roots=new List<string>();
        foreach(object row in (System.Collections.IEnumerable)projects["projects"])roots.Add(Reader.Get((Dictionary<string,object>)row,"Path"));
        reader.ProjectRoots=roots.ToArray();
        string id=Reader.Get(request,"id"),title=Reader.Get(request,"title");
        // Validate and select on the server before changing local metadata.
        var response=await reader.Conversations("list",null,null,null);
        if(!Reader.Get(response,"ok").Equals("True",StringComparison.OrdinalIgnoreCase))throw new Exception(Reader.Get(response,"message"));
        var store=new LocalConversationStore(Path.Combine(root,"conversation-settings.json"),reader.MetadataScope);
        store.Import((System.Collections.IEnumerable)response["conversations"]);
        string projectRoot="",directory="",target="";
        if(action=="sync") {
            Dictionary<string,object> found=null;
            foreach(object row in (System.Collections.IEnumerable)response["conversations"]){var item=(Dictionary<string,object>)row;if(Reader.Get(item,"id")==id){found=item;break;}}
            if(found==null)throw new ArgumentException("Unknown conversation ID");
            directory=Reader.Get(found,"dir");
            foreach(string candidate in roots)if((directory==candidate || directory.StartsWith(candidate+"/",StringComparison.Ordinal)) && candidate.Length>projectRoot.Length)projectRoot=candidate;
            if(projectRoot=="")throw new ArgumentException("Conversation is outside configured project roots");
            if(Flag(request,"select")) {
                response=await reader.Conversations("switch",id,null,null);
                if(!Reader.Get(response,"ok").Equals("True",StringComparison.OrdinalIgnoreCase))throw new Exception(Reader.Get(response,"message"));
                if(Reader.Get(response,"active")!=id)throw new Exception("Server did not select requested conversation");
            }
            bool archived=request.ContainsKey("archived")?Flag(request,"archived"):store.Archived(id);
            store.Set(id,title!=""?title:store.Title(id),archived,title!="");
            if(Flag(request,"select")){projects["selected"]=projectRoot;AtomicWrite(projectsPath,json.Serialize(projects));}
        }
        var rows=new List<object>();
        foreach(object row in (System.Collections.IEnumerable)response["conversations"]) {
            var original=(Dictionary<string,object>)row;var item=new Dictionary<string,object>(original);
            string sid=Reader.Get(item,"id");item["title"]=store.Title(sid);item["archived"]=store.Archived(sid);rows.Add(item);
            if(sid==id)target=Reader.Get(item,"target");
        }
        response["conversations"]=rows;
        return new Dictionary<string,object>{{"ok",true},{"id",id},{"target",target},{"directory",directory},{"projectRoot",projectRoot},{"response",response}};
    }
    static void AtomicWrite(string path,string value) {
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,value,Encoding.UTF8);if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}finally{if(File.Exists(temp))File.Delete(temp);}
    }
}

internal sealed class BackendPipe : IDisposable {
    static string Name {get{return "ClaudeMonitorPublicInteractiveApi-"+WindowsIdentity.GetCurrent().User.Value;}}
    readonly Control owner;
    readonly Func<Dictionary<string,object>,Task<Dictionary<string,object>>> handler;
    volatile bool stopped;
    NamedPipeServerStream current;
    public BackendPipe(Control owner,Func<Dictionary<string,object>,Task<Dictionary<string,object>>> handler) {this.owner=owner;this.handler=handler;Task.Run((Action)Serve);}
    void Serve() {
        while(!stopped) {
            try {
                var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
                security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
                using(var pipe=new NamedPipeServerStream(Name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.None,65536,65536,security)) {
                    current=pipe;pipe.WaitForConnection();
                    using(var input=new StreamReader(pipe,Encoding.UTF8,false,4096,true))using(var output=new StreamWriter(pipe,new UTF8Encoding(false),4096,true){AutoFlush=true}) {
                        var json=new JavaScriptSerializer();var request=json.Deserialize<Dictionary<string,object>>(input.ReadLine());
                        var done=new TaskCompletionSource<Dictionary<string,object>>();
                        owner.BeginInvoke(new Action(async()=>{try{done.SetResult(await handler(request));}catch(Exception error){done.SetResult(new Dictionary<string,object>{{"ok",false},{"error",error.Message}});}}));
                        output.WriteLine(json.Serialize(done.Task.GetAwaiter().GetResult()));
                    }
                }
            }catch(Exception){if(stopped)return;System.Threading.Thread.Sleep(250);}finally{current=null;}
        }
    }
    internal static async Task<Dictionary<string,object>> CallOrExecute(Dictionary<string,object> request) {
        using(var pipe=new NamedPipeClientStream(".",Name,PipeDirection.InOut)) {
            bool connected=true;try{pipe.Connect(500);}catch(TimeoutException){connected=false;}
            if(connected) {
                var json=new JavaScriptSerializer();
                using(var output=new StreamWriter(pipe,new UTF8Encoding(false),4096,true){AutoFlush=true})using(var input=new StreamReader(pipe,Encoding.UTF8,false,4096,true)) {
                    await output.WriteLineAsync(json.Serialize(request));
                    var read=input.ReadLineAsync();
                    if(await Task.WhenAny(read,Task.Delay(60000))!=read)throw new Exception("API result unknown; inspect state before retrying");
                    return json.Deserialize<Dictionary<string,object>>(await read);
                }
            }
        }
        var result=await BackendApi.Execute(request,new Reader());result["guiReloadRequired"]=true;return result;
    }
    public void Dispose(){stopped=true;try{if(current!=null)current.Dispose();}catch{}}
}
