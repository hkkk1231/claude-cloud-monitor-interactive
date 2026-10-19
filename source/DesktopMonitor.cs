using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Runtime.InteropServices;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal class Snapshot {
    public string State, Reason, Output, Project, CheckedAt;
}
internal class Conversation {
    public string Id,Title,Target,Directory,State;
    public double Changed;
    public bool Archived;
    public override string ToString() {return Title;}
}
internal class ProjectEntry {
    public string Name {get;set;}
    public string Path {get;set;}
    public override string ToString() {return Name+" · "+Path;}
}
internal class BufferedTableLayoutPanel : TableLayoutPanel {
    public BufferedTableLayoutPanel() {DoubleBuffered=true;ResizeRedraw=true;}
}
internal class LocalConversationStore {
    readonly string path,scope;
    Dictionary<string,Dictionary<string,object>> entries;
    public LocalConversationStore(string path,string scope) {
        this.path=path;this.scope=scope;
        entries=File.Exists(path)?new JavaScriptSerializer().Deserialize<Dictionary<string,Dictionary<string,object>>>(File.ReadAllText(path,Encoding.UTF8)):new Dictionary<string,Dictionary<string,object>>();
        if(entries==null)throw new Exception("本地对话设置文件无效："+path);
    }
    string Key(string id) {return scope+"/"+id;}
    public void Reload() {if(File.Exists(path))entries=new JavaScriptSerializer().Deserialize<Dictionary<string,Dictionary<string,object>>>(File.ReadAllText(path,Encoding.UTF8));}
    void Commit(Dictionary<string,Dictionary<string,object>> next) {
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            File.WriteAllText(temp,new JavaScriptSerializer().Serialize(next),Encoding.UTF8);
            if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
            entries=next;
        }finally {if(File.Exists(temp))File.Delete(temp);}
    }
    public void Import(System.Collections.IEnumerable rows) {
        var next=new Dictionary<string,Dictionary<string,object>>(entries);bool changed=false;
        foreach(object row in rows) {
            var data=(Dictionary<string,object>)row;string key=Key(Reader.Get(data,"id"));
            string remoteTitle=Reader.Get(data,"title");
            if(next.ContainsKey(key)) {
                var old=next[key];string localTitle=Reader.Get(old,"title");
                bool renamed=old.ContainsKey("renamed")?Reader.Get(old,"renamed").Equals("True",StringComparison.OrdinalIgnoreCase):localTitle!=remoteTitle && !localTitle.StartsWith("Hello, Claude!") && !localTitle.StartsWith("历史对话 ") && localTitle!="当前对话";
                if(!old.ContainsKey("renamed") || (!renamed && localTitle!=remoteTitle)) {next[key]=new Dictionary<string,object>{{"title",renamed?localTitle:remoteTitle},{"archived",Reader.Get(old,"archived").Equals("True",StringComparison.OrdinalIgnoreCase)},{"renamed",renamed}};changed=true;}
                continue;
            }
            next[key]=new Dictionary<string,object>{{"title",remoteTitle},{"archived",Reader.Get(data,"archived").Equals("True",StringComparison.OrdinalIgnoreCase)},{"renamed",false}};changed=true;
        }
        if(changed)Commit(next);
    }
    public string Title(string id) {return Reader.Get(entries[Key(id)],"title");}
    public bool Archived(string id) {return Reader.Get(entries[Key(id)],"archived").Equals("True",StringComparison.OrdinalIgnoreCase);}
    public void Set(string id,string title,bool archived,bool rename=false) {
        var next=new Dictionary<string,Dictionary<string,object>>(entries);
        Dictionary<string,object> old;entries.TryGetValue(Key(id),out old);
        next[Key(id)]=new Dictionary<string,object>{{"title",title},{"archived",archived},{"renamed",rename || (old!=null && Reader.Get(old,"renamed").Equals("True",StringComparison.OrdinalIgnoreCase))}};Commit(next);
    }
}

internal class Reader {
    readonly Dictionary<string, object> config;
    readonly string probe;
    readonly string inputScript;
    readonly string conversationsScript;
    readonly string baseSession;
    readonly List<Process> processes=new List<Process>();
    public int Interval;
    public string Session;
    public string[] ProjectRoots=new [] {"/home/remoteuser/projects"};
    public string MetadataScope {get {return Get(config,"user")+"@"+Get(config,"host")+"/"+baseSession;}}
    public Reader() {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        config = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(root, "config.json"), Encoding.UTF8));
        foreach (string key in new [] {"host", "user", "session"})
            if (!Regex.IsMatch(Get(config, key), @"^[\w.-]+$")) throw new Exception("连接配置无效：" + key);
        Session = Get(config, "session");
        baseSession=Session;
        Interval = Math.Max(3, Convert.ToInt32(config["refreshSeconds"])) * 1000;
        probe = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(root, "probe.py")));
        inputScript = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(root, "input.py")));
        conversationsScript=Convert.ToBase64String(File.ReadAllBytes(Path.Combine(root,"conversations.py")));
    }
    public static string Get(Dictionary<string, object> data, string key) {
        object value; return data.TryGetValue(key, out value) && value != null ? value.ToString() : "";
    }
    public static Snapshot Classify(Dictionary<string, object> data) {
        var s = new Snapshot {State = Get(data, "state"), Reason = Get(data, "reason"), Output = Get(data, "output"), Project = Get(data, "project"), CheckedAt = Get(data, "checkedAt")};
        if (s.State != "") return s;
        string[] lines = s.Output.Split('\n');
        int prompt = -1;
        for (int i = 0; i < lines.Length; i++) if (Regex.IsMatch(lines[i], @"^\s*❯")) prompt = i;
        string tail = String.Join("\n", lines, Math.Max(0, lines.Length - 22), Math.Min(22, lines.Length));
        string after = prompt >= 0 ? String.Join("\n", lines, prompt, lines.Length - prompt) : tail;
        string end = String.Join("\n", lines, Math.Max(0, prompt - 12), lines.Length - Math.Max(0, prompt - 12));
        if (Get(data, "dead").Equals("True", StringComparison.OrdinalIgnoreCase) || Get(data, "command") != "claude") {s.State = "stopped"; s.Reason = "共享终端当前没有运行 Claude Code";}
        else if (Regex.IsMatch(after, @"esc to interrupt|ctrl.c to interrupt|interrupt to stop", RegexOptions.IgnoreCase)) {s.State = "working"; s.Reason = "终端显示任务正在执行";}
        else if (Regex.IsMatch(after, @"Do you want to proceed|Allow .*\?|requires your approval|Yes,? allow|Enter to confirm|Esc to cancel", RegexOptions.IgnoreCase)) {s.State = "waiting"; s.Reason = "终端显示需要确认或选择";}
        else if (prompt >= 0 && Regex.IsMatch(end, @"(?:✻|✽|✶|✢|·).*\b(?:done|Brewed|Worked|Cooked|Sautéed|Baked|Cogitated)\b|\bdone \d{1,2}:\d{2}", RegexOptions.IgnoreCase)) {s.State = "finished"; s.Reason = "已出现本轮结束标记，终端等待下一条输入";}
        else if (prompt >= 0) {s.State = "idle"; s.Reason = "终端停在输入提示符";}
        else {s.State = "unknown"; s.Reason = "会话在线，暂时无法可靠判断；请查看最近输出";}
        return s;
    }
    public async Task<Snapshot> Read() {
        return Classify(await Execute(probe, null));
    }
    public Task<Dictionary<string,object>> AccountQuota(CancellationToken cancellation=default(CancellationToken)) {
        return Execute(Convert.ToBase64String(File.ReadAllBytes(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"quota_probe.py"))),null,baseSession,cancellation);
    }
    public Task<Dictionary<string,object>> NotificationMetadata() {
        return Execute(conversationsScript,new JavaScriptSerializer().Serialize(new {action="list",metadataOnly=true,roots=ProjectRoots}),baseSession);
    }
    public Task<Dictionary<string, object>> Input(string action, string text, string key) {
        return Execute(inputScript, new JavaScriptSerializer().Serialize(new {action=action, text=text, key=key}));
    }
    public Func<string,string,string,string,CancellationToken,Task<Dictionary<string,object>>> ConversationProvider;
    public Task<Dictionary<string,object>> Conversations(string action,string id,string title,string directory,CancellationToken cancellation=default(CancellationToken)) {
        if(ConversationProvider!=null)return ConversationProvider(action,id,title,directory,cancellation);
        return Execute(conversationsScript,new JavaScriptSerializer().Serialize(new {action=action,id=id,title=title,dir=directory,roots=ProjectRoots}),baseSession,cancellation);
    }
    async Task<Dictionary<string, object>> Execute(string script, string input, string target=null,CancellationToken cancellation=default(CancellationToken)) {
        cancellation.ThrowIfCancellationRequested();
        string identity = Get(config, "identityFile");
        if (!File.Exists(identity)) throw new Exception("找不到配置中的 SSH 私钥文件");
        if (identity.Contains("\"")) throw new Exception("SSH 密钥路径包含无效字符");
        string destination=target ?? Session;
        if(!Regex.IsMatch(destination,@"^[\w-]+(?::\d+)?$")) throw new Exception("非法终端目标");
        string remote = "python3 -c \"import base64; exec(base64.b64decode('" + script + "'))\" " + destination;
        // Escape nested quotation marks for Windows' native argument parser.
        string args = "-i \"" + identity + "\" -o BatchMode=yes -o ConnectTimeout=7 -o ConnectionAttempts=1 -o ServerAliveInterval=5 -o ServerAliveCountMax=1 " + Get(config, "user") + "@" + Get(config, "host") + " \"" + remote.Replace("\"", "\\\"") + "\"";
        var start = new ProcessStartInfo("ssh.exe", args) {UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8};
        using (var process = new Process {StartInfo = start}) {
            lock(processes)processes.Add(process);
            CancellationTokenRegistration registration=default(CancellationTokenRegistration);
            try {
                process.Start();
                registration=cancellation.Register(()=>{try {if(!process.HasExited)process.Kill();}catch {}});
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (input != null) {byte[] payload=Encoding.UTF8.GetBytes(input); await process.StandardInput.BaseStream.WriteAsync(payload,0,payload.Length); await process.StandardInput.BaseStream.FlushAsync();}
                process.StandardInput.Close();
                bool exited = await Task.Run(() => process.WaitForExit(15000));
                cancellation.ThrowIfCancellationRequested();
                if (!exited) {process.Kill(); throw new Exception("SSH 读取超时，请检查网络");}
                string output = await stdout;
                string error = await stderr;
                if (process.ExitCode != 0) throw new Exception(error.Contains("Permission denied") ? "SSH 密钥验证失败" : error.Contains("Host key verification failed") ? "SSH 服务器身份验证失败" : "无法连接服务器，请检查网络或服务器状态");
                return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(output);
            } finally {registration.Dispose();lock(processes)processes.Remove(process);}
        }
    }
    public void Stop() {lock(processes)foreach(var process in processes)try {if(!process.HasExited)process.Kill();}catch {}}
}

internal class MonitorForm : Form {
    readonly Reader reader;
    BackendPipe backendPipe;
    InteractiveTerminal terminal;
    readonly LocalConversationStore conversationSettings;
    readonly string metadataPath;
    readonly ComboBox projectSelector=new ComboBox();
    readonly Button addProject=new Button();
    readonly List<ProjectEntry> projects=new List<ProjectEntry>();
    readonly Dictionary<string,string> projectSelections=new Dictionary<string,string>();
    string selectedProject="/home/remoteuser/projects";
    bool updatingProjects;
    readonly bool testing;
    readonly Label status = new Label(), reason = new Label(), project = new Label(), updated = new Label(), connection = new Label();
    readonly RichTextBox output = new RichTextBox();
    readonly Button refresh = new Button(), latest = new Button();
    readonly Button locate=new Button();
    readonly Button toggleSidebar=new Button();
    QuotaDashboard quotaDashboard;
    readonly TableLayoutPanel shell=new BufferedTableLayoutPanel(),sidebar=new BufferedTableLayoutPanel();
    bool sidebarHidden;
    readonly CheckBox auto = new CheckBox();
    readonly TextBox input = new TextBox();
    readonly Button send = new Button();
    readonly TableLayoutPanel keys = new TableLayoutPanel();
    readonly Label inputStatus = new Label();
    readonly ToolTip tips = new ToolTip();
    readonly ListBox chats=new ListBox();
    readonly Button newChat=new Button(),renameChat=new Button(),archiveChat=new Button();
    readonly CheckBox archivedView=new CheckBox();
    Dictionary<string,object> lastChatResponse;
    readonly Dictionary<string,Snapshot> snapshotCache=new Dictionary<string,Snapshot>();
    bool refreshing,switching;
    int viewVersion;
    CancellationTokenSource refreshCancellation;
    Conversation pendingSelection;
    readonly Dictionary<string,string> drafts=new Dictionary<string,string>();
    string selectedId;
    bool updatingChats;
    string[] directories=new string[0];
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    bool busy;
    string previous = null;
    readonly Color ink = Color.FromArgb(37, 54, 47), muted = Color.FromArgb(119, 128, 120);
    public MonitorForm(bool layoutTest=false) {
        SuspendLayout();
        DoubleBuffered=true;ResizeRedraw=true;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        reader = new Reader();
        testing=layoutTest;
        projects.Add(new ProjectEntry {Name="我的项目",Path=selectedProject});
        if(!testing) {
            string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"projects.json");
            if(File.Exists(path)) {
                var saved=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Encoding.UTF8));
                projects.Clear();
                foreach(object savedRow in (System.Collections.IEnumerable)saved["projects"]) {var item=(Dictionary<string,object>)savedRow;projects.Add(new ProjectEntry {Name=Reader.Get(item,"Name"),Path=Reader.Get(item,"Path").TrimEnd('/')});}
                if(projects.Count==0)projects.Add(new ProjectEntry {Name="我的项目",Path=selectedProject});
                string chosen=Reader.Get(saved,"selected");if(projects.Exists(p=>p.Path==chosen))selectedProject=chosen;
            }
        }
        reader.ProjectRoots=projects.ConvertAll(p=>p.Path).ToArray();
        metadataPath=layoutTest?Path.Combine(Path.GetTempPath(),"claude-monitor-test-"+Guid.NewGuid().ToString("N")+".json"):Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"conversation-settings.json");
        conversationSettings=new LocalConversationStore(metadataPath,reader.MetadataScope);
        Text = "Claude 云端监控"; Size = new Size(1260, 760); MinimumSize = new Size(1020, 540);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(245, 244, 237);
        Font = new Font("Microsoft YaHei UI", 9); Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        var layout = new BufferedTableLayoutPanel {Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 12), ColumnCount = 1, RowCount = 6};
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        shell.Dock=DockStyle.Fill;shell.ColumnCount=2;shell.RowCount=1;
        shell.GrowStyle=TableLayoutPanelGrowStyle.FixedSize;shell.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,230)); shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        sidebar.Dock=DockStyle.Fill;sidebar.RowCount=4;sidebar.ColumnCount=1;sidebar.Padding=new Padding(12,12,0,12);
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute,34)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute,36)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute,28)); sidebar.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        sidebar.Controls.Add(new Label {Text="对话",Dock=DockStyle.Fill,Font=new Font("Microsoft YaHei UI",14),ForeColor=ink});
        var chatActions=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3}; for(int i=0;i<3;i++)chatActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.333F));
        newChat.Text="新建对话";newChat.Dock=DockStyle.Fill;newChat.FlatStyle=FlatStyle.Flat;newChat.Click+=async(s,e)=>await EditConversation(false);
        renameChat.Text="重命名";renameChat.Dock=DockStyle.Fill;renameChat.FlatStyle=FlatStyle.Flat;renameChat.Click+=async(s,e)=>await EditConversation(true);
        archiveChat.Text="归档";archiveChat.Dock=DockStyle.Fill;archiveChat.FlatStyle=FlatStyle.Flat;archiveChat.Click+=(s,e)=>ArchiveConversation();
        chatActions.Controls.Add(newChat);chatActions.Controls.Add(renameChat);chatActions.Controls.Add(archiveChat);sidebar.Controls.Add(chatActions);
        archivedView.Text="已归档";archivedView.Dock=DockStyle.Fill;archivedView.ForeColor=muted;
        archivedView.CheckedChanged+=(s,e)=>{if(!busy && !updatingChats && lastChatResponse!=null)LoadChats(lastChatResponse);};sidebar.Controls.Add(archivedView);
        chats.Dock=DockStyle.Fill;chats.BorderStyle=BorderStyle.None;chats.BackColor=BackColor;chats.DrawMode=DrawMode.OwnerDrawFixed;chats.ItemHeight=60;chats.IntegralHeight=false;
        chats.DrawItem+=(s,e)=>{
            if(e.Index<0)return;var chat=(Conversation)chats.Items[e.Index];
            using(var brush=new SolidBrush((e.State&DrawItemState.Selected)!=0 ? Color.FromArgb(222,231,220):BackColor))e.Graphics.FillRectangle(brush,e.Bounds);
            var titleRect=new Rectangle(e.Bounds.X+8,e.Bounds.Y+6,e.Bounds.Width-16,Font.Height+6);
            TextRenderer.DrawText(e.Graphics,chat.Title,Font,titleRect,ink,TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine);
            string info=ChatState(chat.State)+(chat.Changed>0 ? " · "+new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(chat.Changed).ToLocalTime().ToString("MM-dd HH:mm") : "");
            TextRenderer.DrawText(e.Graphics,info,Font,new Rectangle(e.Bounds.X+8,e.Bounds.Y+Font.Height+14,e.Bounds.Width-16,Font.Height+4),muted,TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine);
        };
        chats.SelectedIndexChanged+=async(s,e)=>{if(chats.SelectedItem!=null)archiveChat.Text=((Conversation)chats.SelectedItem).Archived?"恢复":"归档";if(!updatingChats && chats.SelectedItem!=null)await SwitchConversation((Conversation)chats.SelectedItem);};
        sidebar.Controls.Add(chats);shell.Controls.Add(sidebar,0,0);shell.Controls.Add(layout,1,0);Controls.Add(shell);
        var row = new TableLayoutPanel {Dock = DockStyle.Fill, ColumnCount = 7, Margin=Padding.Empty};
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,88));row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,112));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,88));
        toggleSidebar.Text="隐藏侧栏";toggleSidebar.Dock=DockStyle.Fill;toggleSidebar.Margin=new Padding(0,5,8,5);toggleSidebar.FlatStyle=FlatStyle.Flat;toggleSidebar.FlatAppearance.BorderColor=Color.FromArgb(210,214,204);
        toggleSidebar.Click+=(s,e)=>SetSidebarHidden(!sidebarHidden,true);
        status.Text = "● 正在连接"; status.Font = new Font("Microsoft YaHei UI", 14); status.ForeColor = ink; status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleLeft; status.Margin=Padding.Empty;
        tips.SetToolTip(status,"读取服务器上的共享会话…");
        auto.Text = "自动刷新"; auto.Checked = true; auto.Dock = DockStyle.Fill;
        refresh.Text = "刷新"; refresh.Dock = DockStyle.Fill; refresh.Margin=new Padding(4,5,4,5); refresh.FlatStyle = FlatStyle.Flat; refresh.FlatAppearance.BorderColor = Color.FromArgb(210, 214, 204); refresh.Click += async (s, e) => {if(terminal!=null && !terminal.Connected && selectedId!=null)terminal.Connect(reader.Session,true);await UpdateStatus();};
        latest.Text = "跳到最新"; latest.Dock = DockStyle.Fill; latest.Margin=new Padding(4,5,0,5); latest.FlatStyle = FlatStyle.Flat; latest.FlatAppearance.BorderColor = Color.FromArgb(210, 214, 204); latest.Click += (s, e) => {if(terminal!=null)terminal.Latest();else ScrollLatest();};
        locate.Text="定位进行中";locate.Dock=DockStyle.Fill;locate.Margin=new Padding(6,5,0,5);locate.FlatStyle=FlatStyle.Flat;locate.Click+=async(s,e)=>await LocateConversation();tips.SetToolTip(locate,"定位正在执行的对话；没有执行中的任务时定位云端当前窗口，包括已归档对话。");
        var quotaButton=new Button {Text="账号额度",Dock=DockStyle.Fill,Margin=new Padding(6,5,0,5),FlatStyle=FlatStyle.Flat};
        quotaButton.Click+=(s,e)=>{if(quotaDashboard==null || quotaDashboard.IsDisposed){quotaDashboard=new QuotaDashboard(reader);quotaDashboard.Show(this);}else quotaDashboard.Activate();};
        row.Controls.Add(toggleSidebar);row.Controls.Add(status); row.Controls.Add(auto); row.Controls.Add(refresh); row.Controls.Add(latest);row.Controls.Add(locate);row.Controls.Add(quotaButton); layout.Controls.Add(row);
        var meta = new TableLayoutPanel {Dock = DockStyle.Fill, ColumnCount = 4, Margin=Padding.Empty};
        meta.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));meta.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,32)); meta.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,150)); meta.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,130));
        project.Dock = DockStyle.Fill; project.ForeColor = muted; project.Text = "项目：—"; project.AutoEllipsis=true; project.Margin=Padding.Empty; project.TextAlign=ContentAlignment.MiddleLeft;
        connection.Text = "连接中"; connection.Dock=DockStyle.Fill; connection.TextAlign=ContentAlignment.MiddleRight; connection.ForeColor=muted;
        updated.Dock = DockStyle.Fill; updated.ForeColor = muted; updated.TextAlign = ContentAlignment.MiddleRight; updated.Text = "更新：—";
        projectSelector.Dock=DockStyle.Fill;projectSelector.DropDownStyle=ComboBoxStyle.DropDownList;projectSelector.Margin=Padding.Empty;
        updatingProjects=true;foreach(var entry in projects)projectSelector.Items.Add(entry);projectSelector.SelectedItem=projects.Find(p=>p.Path==selectedProject);updatingProjects=false;
        projectSelector.SelectedIndexChanged+=async(s,e)=>{if(!updatingProjects && projectSelector.SelectedItem!=null)await SelectProject((ProjectEntry)projectSelector.SelectedItem);};
        addProject.Text="+";addProject.Dock=DockStyle.Fill;addProject.Margin=Padding.Empty;addProject.FlatStyle=FlatStyle.Flat;addProject.Click+=async(s,e)=>await AddProject();tips.SetToolTip(addProject,"添加服务器上的现有项目目录");
        meta.Controls.Add(projectSelector);meta.Controls.Add(addProject);meta.Controls.Add(connection); meta.Controls.Add(updated); layout.Controls.Add(meta);
        output.Dock = DockStyle.Fill; output.ReadOnly = true; output.BorderStyle = BorderStyle.None; output.BackColor = Color.FromArgb(30, 40, 35); output.ForeColor = Color.FromArgb(211, 223, 212); output.Font = new Font("Consolas", 10); output.WordWrap = true; output.Text = "正在读取…";
        if(testing)layout.Controls.Add(output);else {
            terminal=new InteractiveTerminal();terminal.ConnectionChanged+=text=>inputStatus.Text=text;terminal.RefreshRequested+=async()=>await UpdateStatus();layout.Controls.Add(terminal);
            layout.RowStyles[4].Height=0;
        }
        keys.Dock=DockStyle.Fill; keys.ColumnCount=4; keys.RowCount=1; keys.Margin=Padding.Empty;
        keys.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));
        keys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        keys.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,112));
        keys.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,120));
        var editorToggle=new Button {Text=testing?"Claude 快捷键":"长文本编辑",Dock=DockStyle.Top,Height=27,Margin=new Padding(0,6,6,6),FlatStyle=FlatStyle.Flat,ForeColor=muted};
        editorToggle.Click+=(s,e)=>{layout.RowStyles[4].Height=layout.RowStyles[4].Height==0?80:0;editorToggle.Text=layout.RowStyles[4].Height==0?"长文本编辑":"收起编辑器";};keys.Controls.Add(editorToggle,0,0);
        var commands = new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Top,FlatStyle=FlatStyle.Standard,Margin=new Padding(0,6,0,6)};
        commands.Items.AddRange(new object[] {"常用命令", "/model", "/effort", "/status", "/usage", "/help", "/config", "/tasks", "/clear", "/compact"}); commands.SelectedIndex=0;
        commands.SelectedIndexChanged += (s,e) => {if(commands.SelectedIndex>0) {if(terminal!=null && terminal.Connected)terminal.Paste(commands.SelectedItem.ToString());else {input.Text=commands.SelectedItem.ToString(); input.Focus(); input.SelectionStart=input.TextLength;}commands.SelectedIndex=0;}};
        var shortcut = new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Top,FlatStyle=FlatStyle.Standard,Margin=new Padding(0,6,10,6)};
        string[] shortcutNames={"切换权限 / 计划模式 · Shift+Tab","选择模型 · Alt+P","开关扩展思考 · Alt+T（Opus 5.5 无效）","切换快速模式 · Alt+O","展开 / 收起详细输出 · Ctrl+O","任务清单 · Ctrl+T","查找输入历史 · Ctrl+R","重绘终端 · Ctrl+L","后台运行当前任务 · Ctrl+B","暂存 / 恢复终端草稿 · Ctrl+S","回退菜单 / 清空终端草稿 · Esc × 2","立即提交排队消息 · Ctrl+X Ctrl+S","确认 · Enter","中断 / 关闭菜单 · Esc","中断 / 清空输入 · Ctrl+C","向上选择 · ↑","向下选择 · ↓","向左选择 · ←","向右选择 · →","补全 / 切换焦点 · Tab"};
        string[] shortcutCodes={"BTab","M-p","M-t","M-o","C-o","C-t","C-r","C-l","C-b","C-s","Rewind","QueueNow","Enter","Escape","C-c","Up","Down","Left","Right","Tab"};
        shortcut.Items.AddRange(shortcutNames); shortcut.SelectedIndex=0; keys.Controls.Add(shortcut,1,0);
        var applyKey=new Button {Text="执行快捷键",AutoSize=false,Height=shortcut.PreferredHeight,Dock=DockStyle.Top,Margin=new Padding(0,6,10,6),FlatStyle=FlatStyle.Flat,UseVisualStyleBackColor=false,BackColor=BackColor};
        applyKey.FlatAppearance.BorderSize=1; applyKey.FlatAppearance.BorderColor=Color.FromArgb(210,214,204);
        applyKey.Click+=async(s,e)=>await SendInput("key",shortcutCodes[shortcut.SelectedIndex]); keys.Controls.Add(applyKey,2,0);
        keys.Controls.Add(commands,3,0); layout.Controls.Add(keys);
        tips.SetToolTip(shortcut,"快捷键作用于远程终端。Ctrl+C 可中断任务或清空远程输入，再次按可能退出会话。");
        tips.SetToolTip(applyKey,"在下拉框选择后点击执行。Alt+T 无法调整 Opus 5.5 的思考档位；请使用模型菜单或 /effort。");
        var composer = new TableLayoutPanel {Dock=DockStyle.Fill, ColumnCount=2};
        composer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); composer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,90));
        input.Multiline=true; input.AcceptsReturn=true; input.ScrollBars=ScrollBars.Vertical; input.Dock=DockStyle.Fill; input.MaxLength=20000; input.Font=new Font("Microsoft YaHei UI",10);
        send.Text=testing?"发送\nCtrl+Enter":"粘贴到终端"; send.Dock=DockStyle.Fill; send.FlatStyle=FlatStyle.Flat; send.Click += async(s,e) => await SendInput("send",null);
        composer.Controls.Add(input); composer.Controls.Add(send); layout.Controls.Add(composer);
        inputStatus.Text=testing?"Enter 换行 · Ctrl+Enter 发送 · F5 刷新":"点击终端直接输入 · 方向键直接移动 · Ctrl+V 粘贴 · Enter 提交"; inputStatus.ForeColor=muted; inputStatus.Dock=DockStyle.Fill; inputStatus.AutoEllipsis=true; layout.Controls.Add(inputStatus);
        tips.SetToolTip(input,"输入按原文发送。Ctrl+C/V/A/Z 保留本地编辑；Claude Ctrl+O/T/R、Alt+P/T/O、Shift+Tab 可直接使用。");
        KeyPreview=true;
        KeyDown += async(s,e) => {
            if(terminal!=null && terminal.ContainsFocus)return;
            if(e.Control && e.KeyCode==Keys.Enter) {e.SuppressKeyPress=true; await SendInput("send",null);}
            else if(e.KeyCode==Keys.F5) {e.SuppressKeyPress=true; await UpdateStatus();}
            else if(e.Alt && (e.KeyCode==Keys.Escape || e.KeyCode==Keys.Up || e.KeyCode==Keys.Down)) {e.SuppressKeyPress=true; await SendInput("key",e.KeyCode==Keys.Escape ? "Escape" : e.KeyCode==Keys.Up ? "Up" : "Down");}
            else if(e.Shift && e.KeyCode==Keys.Tab) {e.SuppressKeyPress=true; await SendInput("key","BTab");}
            else if(e.Alt && (e.KeyCode==Keys.P || e.KeyCode==Keys.T || e.KeyCode==Keys.O)) {e.SuppressKeyPress=true; await SendInput("key","M-"+e.KeyCode.ToString().ToLowerInvariant());}
            else if(e.Control && (e.KeyCode==Keys.O || e.KeyCode==Keys.T || e.KeyCode==Keys.R || e.KeyCode==Keys.L || e.KeyCode==Keys.B || e.KeyCode==Keys.S)) {e.SuppressKeyPress=true; await SendInput("key","C-"+e.KeyCode.ToString().ToLowerInvariant());}
        };
        timer.Interval = reader.Interval; timer.Tick += async (s, e) => {if (auto.Checked) await UpdateStatus();};
        Shown += async (s, e) => {
            if(!testing)backendPipe=new BackendPipe(this,ApplyBackendRequest);
            try {var settingsPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-settings.json");if(File.Exists(settingsPath)){var settings=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(settingsPath,Encoding.UTF8));SetSidebarHidden(Reader.Get(settings,"sidebarHidden").Equals("True",StringComparison.OrdinalIgnoreCase),false);}}catch {}
            if(!layoutTest){timer.Start(); await UpdateStatus();}
        };
        FormClosing += (s, e) => {if(quotaDashboard!=null && !quotaDashboard.IsDisposed)quotaDashboard.Close();if(backendPipe!=null)backendPipe.Dispose();timer.Stop();CancelRefresh();reader.Stop();if(terminal!=null)terminal.Stop();};
        if(layoutTest)FormClosed+=(s,e)=>{if(File.Exists(metadataPath))File.Delete(metadataPath);};
        ResumeLayout(true);
    }
    void ScrollLatest() {output.SelectionStart = output.TextLength; output.SelectionLength = 0; output.ScrollToCaret();}
    void SetSidebarHidden(bool hidden,bool persist) {
        shell.SuspendLayout();
        try {
            sidebarHidden=hidden;
            using(var graphics=CreateGraphics())shell.ColumnStyles[0].Width=hidden?0:230F*graphics.DpiX/96F;
            sidebar.Visible=!hidden;
            toggleSidebar.Text=hidden?"显示侧栏":"隐藏侧栏";
        }finally {shell.ResumeLayout(true);}
        if(persist)try {File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-settings.json"),new JavaScriptSerializer().Serialize(new {sidebarHidden=hidden}),Encoding.UTF8);}catch {inputStatus.Text="布局已切换，但无法保存偏好。";}
    }
    public string UiCheck() {return new JavaScriptSerializer().Serialize(new {project=selectedProject,conversations=chats.Items.Count,selected=selectedId,target=reader.Session,status=status.Text,terminalHeight=terminal!=null?terminal.Height:output.Height,interactive=terminal!=null && terminal.Connected,renderedCharacters=terminal!=null && terminal.Inspection!=null?terminal.Inspection.Length:0,clientHeight=ClientSize.Height,notificationsRunning=FeishuNotifications.Running});}
    async Task<Dictionary<string,object>> ApplyBackendRequest(Dictionary<string,object> request) {
        if(Reader.Get(request,"action")=="quota")return await reader.AccountQuota();
        if(busy || switching)return new Dictionary<string,object>{{"ok",false},{"error","Monitor is busy; retry when the current operation finishes."}};
        CancelRefresh();++viewVersion;
        Dictionary<string,object> result;
        busy=true;try{result=await BackendApi.Execute(request,reader);}finally{busy=false;}
        if(!Reader.Get(result,"ok").Equals("True",StringComparison.OrdinalIgnoreCase))return result;
        if(Reader.Get(request,"action")=="sync") {
            conversationSettings.Reload();
            var response=(Dictionary<string,object>)result["response"];
            string wanted=Reader.Get(request,"id");
            if(BackendApi.Flag(request,"select")) {
                string root=Reader.Get(result,"projectRoot");
                if(!projects.Exists(p=>p.Path==root)){projects.Add(new ProjectEntry{Name=root,Path=root});projectSelector.Items.Add(projects[projects.Count-1]);reader.ProjectRoots=projects.ConvertAll(p=>p.Path).ToArray();}
                if(selectedId!=null)drafts[selectedId]=input.Text;
                selectedProject=root;updatingProjects=true;projectSelector.SelectedItem=projects.Find(p=>p.Path==root);updatingProjects=false;
                updatingChats=true;archivedView.Checked=conversationSettings.Archived(wanted);updatingChats=false;
                LoadChats(response,wanted,false);
                var chosen=chats.SelectedItem as Conversation;
                if(chosen==null)throw new Exception("Synced conversation is not visible in the monitor.");
                if(selectedId==wanted){reader.Session=chosen.Target;if(terminal!=null)terminal.Connect(reader.Session);}
                else await SwitchConversation(chosen);
                if(selectedId!=wanted || reader.Session!=chosen.Target)throw new Exception("Monitor selection did not complete.");
                SaveProjects();
            } else LoadChats(response);
        }
        result["ui"]=new JavaScriptSerializer().DeserializeObject(UiCheck());result["guiReloadRequired"]=false;return result;
    }
    public async Task CheckTerminalView() {if(terminal!=null){terminal.Inspect();await Task.Delay(300);await terminal.CapturePreview(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"interactive-terminal-preview.png"));}}
    void SaveProjects() {
        if(testing)return;
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"projects.json"),new JavaScriptSerializer().Serialize(new {projects=projects,selected=selectedProject}),Encoding.UTF8);
    }
    string ProjectFor(string directory) {
        string match=null;
        foreach(var entry in projects)if((directory==entry.Path || directory.StartsWith(entry.Path+"/",StringComparison.Ordinal)) && (match==null || entry.Path.Length>match.Length))match=entry.Path;
        return match;
    }
    async Task SelectProject(ProjectEntry entry) {
        if(entry.Path==selectedProject)return;
        if(busy) {updatingProjects=true;projectSelector.SelectedItem=projects.Find(p=>p.Path==selectedProject);updatingProjects=false;inputStatus.Text="请等待当前操作完成后切换项目。";return;}
        CancelRefresh();viewVersion++;
        if(terminal!=null)terminal.Connect("");
        if(selectedId!=null){drafts[selectedId]=input.Text;projectSelections[selectedProject]=selectedId;}
        selectedProject=entry.Path;selectedId=null;reader.Session="";input.Clear();previous=null;
        updatingChats=true;archivedView.Checked=false;updatingChats=false;
        send.Enabled=false;keys.Enabled=false;output.Text="该项目暂无对话，可点击「新建对话」。";status.Text="● 暂未选择对话";
        connection.Text="未选择终端";updated.Text="更新：—";
        string chosen;projectSelections.TryGetValue(selectedProject,out chosen);
        if(lastChatResponse!=null) {
            LoadChats(lastChatResponse,chosen,false);
            if(chats.SelectedItem==null && chats.Items.Count>0){updatingChats=true;chats.SelectedIndex=0;updatingChats=false;}
            if(chats.SelectedItem!=null)await SwitchConversation((Conversation)chats.SelectedItem);
        }
        try {SaveProjects();}catch(Exception error){inputStatus.Text="项目已切换，但无法保存选择："+error.Message;}
    }
    async Task AddProject() {
        if(busy)return;
        string name,path;
        using(var dialog=new Form {Text="添加云端项目",ClientSize=new Size(480,150),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,Font=Font}) {
            var title=new TextBox {Left=16,Top=16,Width=448,Text="新项目"};var directory=new TextBox {Left=16,Top=54,Width=448,Text="/home/remoteuser/"};
            var ok=new Button {Left=364,Top=100,Width=100,Text="添加",DialogResult=DialogResult.OK};
            dialog.Controls.AddRange(new Control[]{title,directory,ok});dialog.AcceptButton=ok;
            if(dialog.ShowDialog(this)!=DialogResult.OK)return;name=title.Text.Trim();path=directory.Text.Trim().TrimEnd('/');
        }
        if(name=="" || path=="")return;
        busy=true;CancelRefresh();addProject.Enabled=false;
        ProjectEntry entry=null;
        try {
            var response=await reader.Conversations("validate-project",null,null,path);
            if(!Reader.Get(response,"ok").Equals("True",StringComparison.OrdinalIgnoreCase))throw new Exception(Reader.Get(response,"message"));
            path=Reader.Get(response,"path");entry=projects.Find(p=>p.Path==path);
            if(entry==null){entry=new ProjectEntry {Name=name,Path=path};projects.Add(entry);reader.ProjectRoots=projects.ConvertAll(p=>p.Path).ToArray();projectSelector.Items.Add(entry);SaveProjects();}
        }catch(Exception error){inputStatus.Text="添加失败："+error.Message;}
        finally {busy=false;addProject.Enabled=true;}
        if(entry!=null){updatingProjects=true;projectSelector.SelectedItem=entry;updatingProjects=false;await SelectProject(entry);}
    }
    public string SidebarRegression() {
        var samples=new List<object>();bool passed=true;
        for(int i=0;i<24;i++) {
            SetSidebarHidden(i%2==1,false);PerformLayout();
            var main=shell.Controls[1];var cell=shell.GetCellPosition(main);
            bool valid=main.Visible && output.Visible && main.Width>500 && output.Width>400 && cell.Column==1;
            passed &= valid;samples.Add(new {hidden=sidebarHidden,column=cell.Column,mainWidth=main.Width,outputWidth=output.Width,visible=output.Visible,valid=valid});
        }
        using(var bitmap=new Bitmap(Width,Height)){DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height));bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"sidebar-preview.png"));}
        return new JavaScriptSerializer().Serialize(new {passed=passed,samples=samples});
    }
    public async Task<string> InteractionRegression() {
        const string a="test-a",b="test-b",c="test-c";
        var requested=new List<string>();bool fail=false;
        Func<string,Dictionary<string,object>> response=id=>new Dictionary<string,object> {
            {"ok",true},{"selected",id},{"dirs",new object[]{"/home/remoteuser/projects/eclipse"}},
            {"conversations",new object[]{
                new Dictionary<string,object>{{"id",a},{"title","对话 A"},{"dir","/home/remoteuser/projects/eclipse"},{"target","claude-shared:0"},{"state","idle"},{"changed",1},{"archived",false}},
                new Dictionary<string,object>{{"id",b},{"title","对话 B"},{"dir","/home/remoteuser/projects/eclipse"},{"target","claude-shared:1"},{"state","idle"},{"changed",1},{"archived",false}},
                new Dictionary<string,object>{{"id",c},{"title","其他项目对话"},{"dir","/home/remoteuser/other-project/src"},{"target","claude-shared:2"},{"state","idle"},{"changed",1},{"archived",false}}}},
            {"snapshots",new Dictionary<string,object>{
                {a,new Dictionary<string,object>{{"output","output A"},{"command","claude"},{"project","/home/remoteuser/projects/eclipse"}}},
                {b,new Dictionary<string,object>{{"output","output B"},{"command","claude"},{"project","/home/remoteuser/projects/eclipse"}}},
                {c,new Dictionary<string,object>{{"output","output C"},{"command","claude"},{"project","/home/remoteuser/other-project/src"}}}}}
        };
        reader.ConversationProvider=async(action,id,title,dir,token)=>{
            if(action=="list"){await Task.Delay(400);return response(a);}
            requested.Add(id);await Task.Delay(80);if(fail)throw new Exception("模拟网络故障");return response(id);
        };
        archivedView.Checked=false;LoadChats(response(a));input.Text="草稿 A";drafts[b]="草稿 B";
        var original=chats.Items[0];LoadChats(response(a));bool stableList=Object.ReferenceEquals(original,chats.Items[0]);
        var oldRefresh=UpdateStatus();
        var chatA=(Conversation)chats.Items[0];var chatB=(Conversation)chats.Items[1];
        var clock=Stopwatch.StartNew();var switchingTask=SwitchConversation(chatB);bool immediate=selectedId==b && output.Text=="output B";long previewMs=clock.ElapsedMilliseconds;
        await SwitchConversation(chatA);await SwitchConversation(chatB);
        await switchingTask;await oldRefresh;
        bool latest=selectedId==b && reader.Session=="claude-shared:1" && output.Text=="output B";
        bool savedDrafts=drafts[a]=="草稿 A" && input.Text=="草稿 B";
        fail=true;await SwitchConversation(chatA);
        bool rollback=selectedId==b && reader.Session=="claude-shared:1" && output.Text=="output B" && !busy && send.Enabled;
        int calls=requested.Count;int allCalls=0;
        reader.ConversationProvider=(action,id,title,dir,token)=>{allCalls++;throw new Exception("本地管理不应发出服务器请求");};
        RenameConversation((Conversation)chats.SelectedItem,"本地标题");ArchiveConversation();
        bool localArchive=archivedView.Checked && chats.SelectedItem!=null && ((Conversation)chats.SelectedItem).Archived && reader.Session=="claude-shared:1";
        LoadChats(response(a),b);
        bool retained=((Conversation)chats.SelectedItem).Title=="本地标题" && ((Conversation)chats.SelectedItem).Archived;
        var reloaded=new LocalConversationStore(metadataPath,reader.MetadataScope);
        bool persisted=reloaded.Title(b)=="本地标题" && reloaded.Archived(b);
        var otherServer=new LocalConversationStore(metadataPath,"other-server");otherServer.Import((System.Collections.IEnumerable)response(a)["conversations"]);
        bool isolated=otherServer.Title(b)=="对话 B" && !otherServer.Archived(b);
        ArchiveConversation();bool restored=!archivedView.Checked && chats.SelectedItem!=null && !((Conversation)chats.SelectedItem).Archived;
        bool localOnly=allCalls==0 && requested.Count==calls;
        reader.ConversationProvider=(action,id,title,dir,token)=>Task.FromResult(response(id));
        var locationResponse=response(a);locationResponse["running"]=b;locationResponse["active"]=a;
        ArchiveConversation();lastChatResponse=locationResponse;await LocateConversation(false);
        bool locatedArchived=selectedId==b && archivedView.Checked && chats.SelectedItem!=null && ((Conversation)chats.SelectedItem).Id==b && conversationSettings.Archived(b) && !sidebarHidden;
        ArchiveConversation();
        var otherProject=new ProjectEntry {Name="其他项目",Path="/home/remoteuser/other-project"};var emptyProject=new ProjectEntry {Name="空项目",Path="/home/remoteuser/empty"};
        projects.Add(otherProject);projects.Add(emptyProject);reader.ProjectRoots=projects.ConvertAll(p=>p.Path).ToArray();
        LoadChats(response(b),b);bool animationGroup=chats.Items.Count==2 && ProjectFor("/home/remoteuser/projects2/test")==null;
        await SelectProject(otherProject);bool projectSwitch=chats.Items.Count==1 && selectedId==c && output.Text=="output C" && reader.Session=="claude-shared:2";
        await SelectProject(emptyProject);bool emptySafe=chats.Items.Count==0 && selectedId==null && reader.Session=="" && !send.Enabled && !keys.Enabled;
        await SelectProject(projects[0]);bool projectReturn=selectedId==b && chats.Items.Count==2 && input.Text=="草稿 B" && reader.Session=="claude-shared:1";
        reader.ConversationProvider=null;
        bool metadata=localArchive && retained && persisted && isolated && restored && localOnly;
        return new JavaScriptSerializer().Serialize(new {passed=stableList && immediate && latest && savedDrafts && rollback && metadata && animationGroup && projectSwitch && emptySafe && projectReturn && locatedArchived,stableList=stableList,immediatePreview=immediate,previewMs=previewMs,latestSelection=latest,draftsPreserved=savedDrafts,failureRollback=rollback,localMetadata=metadata,noServerRequests=localOnly,metadataPersists=persisted,refreshPreservesMetadata=retained,serverScopesIsolated=isolated,projectGrouping=animationGroup,projectSwitch=projectSwitch,emptyProjectSafe=emptySafe,projectReturn=projectReturn,locateArchivedRunning=locatedArchived,requests=requested});
    }
    static string ChatState(string state) {
        var names=new Dictionary<string,string>{{"working","正在工作"},{"waiting","等待确认"},{"finished","本轮结束"},{"idle","等待输入"},{"history","历史对话"},{"starting","正在启动"},{"stopped","已停止"}};
        return names.ContainsKey(state)?names[state]:"会话在线";
    }
    void CancelRefresh() {if(refreshCancellation!=null)refreshCancellation.Cancel();}
    async Task LocateConversation(bool fetch=true) {
        if(busy){inputStatus.Text="请等待当前操作完成后定位。";return;}
        locate.Enabled=false;CancelRefresh();
        int generation=++viewVersion;
        try {
            var response=fetch?await reader.Conversations("list",null,null,null):lastChatResponse;
            if(IsDisposed || generation!=viewVersion || busy)return;
            if(response==null)return;
            if(!Reader.Get(response,"ok").Equals("True",StringComparison.OrdinalIgnoreCase))throw new Exception(Reader.Get(response,"message"));
            string wanted=Reader.Get(response,"running");if(wanted=="")wanted=Reader.Get(response,"active");if(wanted=="")wanted=Reader.Get(response,"selected");
            Dictionary<string,object> found=null;
            foreach(object row in (System.Collections.IEnumerable)response["conversations"]){var data=(Dictionary<string,object>)row;if(Reader.Get(data,"id")==wanted && Reader.Get(data,"target")!=""){found=data;break;}}
            if(found==null){LoadChats(response);inputStatus.Text="目前没有可定位的云端活动对话。";return;}
            string root=ProjectFor(Reader.Get(found,"dir"));
            if(root==null){LoadChats(response);inputStatus.Text="活动对话位于尚未添加的项目，请先通过＋添加："+Reader.Get(found,"dir");return;}
            conversationSettings.Import((System.Collections.IEnumerable)response["conversations"]);
            if(root!=selectedProject){if(selectedId!=null){drafts[selectedId]=input.Text;projectSelections[selectedProject]=selectedId;}selectedId=null;selectedProject=root;reader.Session="";viewVersion++;if(terminal!=null)terminal.Stop();updatingProjects=true;projectSelector.SelectedItem=projects.Find(p=>p.Path==root);updatingProjects=false;}
            updatingChats=true;archivedView.Checked=conversationSettings.Archived(wanted);updatingChats=false;
            LoadChats(response,wanted,false);SetSidebarHidden(false,fetch);
            var chosen=chats.SelectedItem as Conversation;if(chosen==null)return;
            if(selectedId!=wanted)await SwitchConversation(chosen);
            else {reader.Session=chosen.Target;Snapshot snapshot;if(snapshotCache.TryGetValue(wanted,out snapshot))ApplySnapshot(snapshot);if(terminal!=null)terminal.Connect(reader.Session);}
            if(chats.SelectedIndex>=0)chats.TopIndex=chats.SelectedIndex;
            SaveProjects();inputStatus.Text="已定位："+chosen.Title+(chosen.Archived?"（已归档）":"");
        }catch(Exception error){inputStatus.Text="定位失败："+error.Message;}
        finally {if(!IsDisposed)locate.Enabled=true;}
    }
    void CacheSnapshots(Dictionary<string,object> response) {
        object raw;if(!response.TryGetValue("snapshots",out raw))return;
        foreach(var pair in (Dictionary<string,object>)raw)snapshotCache[pair.Key]=Reader.Classify((Dictionary<string,object>)pair.Value);
    }
    void LoadChats(Dictionary<string,object> response,string prefer=null,bool initialize=true) {
        if(!Reader.Get(response,"ok").Equals("True",StringComparison.OrdinalIgnoreCase))throw new Exception(Reader.Get(response,"message"));
        var dirs=new List<string>();foreach(object d in (System.Collections.IEnumerable)response["dirs"])dirs.Add(d.ToString());directories=dirs.ToArray();
        lastChatResponse=response;
        CacheSnapshots(response);
        string chosen=prefer ?? selectedId ?? Reader.Get(response,"selected");
        var desired=new List<Conversation>();
        conversationSettings.Import((System.Collections.IEnumerable)response["conversations"]);
        foreach(object row in (System.Collections.IEnumerable)response["conversations"]) {
            var data=(Dictionary<string,object>)row;double changed;Double.TryParse(Reader.Get(data,"changed"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out changed);
            if(ProjectFor(Reader.Get(data,"dir"))!=selectedProject)continue;
            string id=Reader.Get(data,"id");bool archived=conversationSettings.Archived(id);
            if(archived!=archivedView.Checked)continue;
            desired.Add(new Conversation {Id=id,Title=conversationSettings.Title(id),Target=Reader.Get(data,"target"),Directory=Reader.Get(data,"dir"),State=Reader.Get(data,"state"),Changed=changed,Archived=archived});
        }
        bool same=chats.Items.Count==desired.Count;
        if(same)for(int i=0;i<desired.Count;i++)if(((Conversation)chats.Items[i]).Id!=desired[i].Id){same=false;break;}
        int top=chats.TopIndex;
        updatingChats=true;if(!same)chats.BeginUpdate();
        try {
            if(!same)chats.Items.Clear();
            for(int i=0;i<desired.Count;i++) {
                var chat=desired[i];
                if(!same)chats.Items.Add(chat);
                else {
                    var old=(Conversation)chats.Items[i];
                    bool changed=old.Title!=chat.Title || old.State!=chat.State || old.Changed!=chat.Changed;
                    old.Title=chat.Title;old.Target=chat.Target;old.Directory=chat.Directory;old.State=chat.State;old.Changed=chat.Changed;old.Archived=chat.Archived;
                    if(changed)chats.Invalidate(chats.GetItemRectangle(i));
                }
                if(chat.Id==chosen && chats.SelectedIndex!=i)chats.SelectedIndex=i;
            }
            if(!same && chats.Items.Count>0)chats.TopIndex=Math.Min(top,chats.Items.Count-1);
            if(initialize && selectedId==null && chats.SelectedItem!=null) {var chat=(Conversation)chats.SelectedItem;if(chat.Target!=""){selectedId=chat.Id;reader.Session=chat.Target;}}
            archiveChat.Text=archivedView.Checked?"恢复":"归档";
        } finally {if(!same)chats.EndUpdate();updatingChats=false;}
    }
    void RenameConversation(Conversation chat,string title) {
        conversationSettings.Set(chat.Id,title,chat.Archived,true);
        LoadChats(lastChatResponse,chat.Id);inputStatus.Text="对话标题已保存在本机。";
    }
    void ArchiveConversation() {
        if(busy){inputStatus.Text="正在读取或发送，请稍后再试。";return;}
        var chat=chats.SelectedItem as Conversation;if(chat==null)return;
        try {
            bool archive=!chat.Archived;
            conversationSettings.Set(chat.Id,chat.Title,archive);
            updatingChats=true;archivedView.Checked=archive;LoadChats(lastChatResponse,chat.Id);
            inputStatus.Text=archive?"已在本机归档；云端任务继续运行。":"已在本机恢复到主列表。";
        }catch(Exception e){if(!IsDisposed)inputStatus.Text="操作失败："+e.Message;}
        finally {updatingChats=false;}
    }
    async Task SwitchConversation(Conversation chat) {
        if(chat.Id==selectedId)return;
        if(switching){pendingSelection=chat;PreviewConversation(chat);return;}
        if(busy) {updatingChats=true;for(int i=0;i<chats.Items.Count;i++)if(((Conversation)chats.Items[i]).Id==selectedId)chats.SelectedIndex=i;updatingChats=false;inputStatus.Text="正在读取或发送，请稍后切换。";return;}
        string originalId=selectedId,originalTarget=reader.Session;
        busy=true;switching=true;CancelRefresh();newChat.Enabled=false;renameChat.Enabled=false;send.Enabled=false;keys.Enabled=false;archiveChat.Enabled=false;input.ReadOnly=true;
        if(terminal!=null)terminal.Stop();
        pendingSelection=chat;PreviewConversation(chat);
        try {
            while(pendingSelection!=null) {
                var next=pendingSelection;pendingSelection=null;
                var response=await reader.Conversations("switch",next.Id,null,null);
                if(IsDisposed)return;
                if(!Reader.Get(response,"ok").Equals("True",StringComparison.OrdinalIgnoreCase))throw new Exception(Reader.Get(response,"message"));
                CacheSnapshots(response);
                if(pendingSelection!=null)continue;
                selectedId=next.Id;LoadChats(response,selectedId);reader.Session=((Conversation)chats.SelectedItem).Target;projectSelections[selectedProject]=selectedId;
                Snapshot cached;if(snapshotCache.TryGetValue(selectedId,out cached))ApplySnapshot(cached);
                else {status.Text="● 正在启动对话";inputStatus.Text="会话已恢复，等待终端就绪…";}
                inputStatus.Text="已切换："+next.Title;
            }
        }catch(Exception e){if(!IsDisposed){
            reader.Session=originalTarget;selectedId=originalId;viewVersion++;
            input.Text=originalId!=null && drafts.ContainsKey(originalId)?drafts[originalId]:"";
            Snapshot cached;if(originalId!=null && snapshotCache.TryGetValue(originalId,out cached))ApplySnapshot(cached);
            inputStatus.Text="切换失败："+e.Message;
            updatingChats=true;chats.SelectedIndex=-1;for(int i=0;i<chats.Items.Count;i++)if(((Conversation)chats.Items[i]).Id==selectedId)chats.SelectedIndex=i;updatingChats=false;
        }}
        finally {busy=false;switching=false;pendingSelection=null;if(!IsDisposed){newChat.Enabled=true;renameChat.Enabled=true;send.Enabled=selectedId!=null && reader.Session!="";keys.Enabled=send.Enabled;archiveChat.Enabled=true;input.ReadOnly=false;if(terminal!=null)terminal.Connect(reader.Session);}}
    }
    void PreviewConversation(Conversation chat) {
        if(selectedId!=null)drafts[selectedId]=input.Text;
        selectedId=chat.Id;viewVersion++;
        input.Text=drafts.ContainsKey(selectedId)?drafts[selectedId]:"";
        previous=null;
        Snapshot cached;if(snapshotCache.TryGetValue(chat.Id,out cached))ApplySnapshot(cached);
        else {output.Text="正在恢复历史对话…";project.Text="项目："+chat.Directory;}
        status.Text="● 正在切换 · "+chat.Title;status.AutoEllipsis=true;
        inputStatus.Text="正在连接所选对话，其他任务继续运行…";
    }
    async Task EditConversation(bool rename) {
        if(busy)return;
        var selected=chats.SelectedItem as Conversation;
        if(rename && selected==null)return;
        string title,directory;
        using(var dialog=new Form {Text=rename?"重命名对话":"新建对话",ClientSize=new Size(480,155),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,Font=Font,AutoScaleMode=AutoScaleMode.Dpi}) {
            var name=new TextBox {Text=rename?selected.Title:"新对话",Left=16,Top=15,Width=448,MaxLength=100};
            var dir=new ComboBox {Left=16,Top=53,Width=448,DropDownStyle=ComboBoxStyle.DropDownList};dir.Items.Add(selectedProject);
            foreach(string candidate in directories)if(candidate!=selectedProject && ProjectFor(candidate)==selectedProject)dir.Items.Add(candidate);
            if(dir.Items.Count>0)dir.SelectedIndex=0;if(rename && selected!=null && dir.Items.Contains(selected.Directory))dir.SelectedItem=selected.Directory;dir.Enabled=!rename;
            var ok=new Button {Text=rename?"保存":"创建",Left=364,Top=105,Width=100,DialogResult=DialogResult.OK};
            dialog.Controls.Add(name);dialog.Controls.Add(dir);dialog.Controls.Add(ok);dialog.AcceptButton=ok;
            if(dialog.ShowDialog(this)!=DialogResult.OK)return;title=name.Text.Trim();directory=dir.SelectedItem==null?"":dir.SelectedItem.ToString();
        }
        if(title=="")return;
        if(rename) {
            try {RenameConversation(selected,title);}catch(Exception e){inputStatus.Text="保存失败："+e.Message;}
            return;
        }
        busy=true;CancelRefresh();newChat.Enabled=false;renameChat.Enabled=false;send.Enabled=false;keys.Enabled=false;
        if(terminal!=null)terminal.Stop();
        try {
            var response=await reader.Conversations("new",null,title,directory);
            if(IsDisposed)return;
            if(!Reader.Get(response,"ok").Equals("True",StringComparison.OrdinalIgnoreCase))throw new Exception(Reader.Get(response,"message"));
            if(!rename) {if(selectedId!=null)drafts[selectedId]=input.Text;selectedId=Reader.Get(response,"selected");input.Clear();previous=null;archivedView.Checked=false;}
            LoadChats(response,selectedId);if(chats.SelectedItem!=null)reader.Session=((Conversation)chats.SelectedItem).Target;
            inputStatus.Text=rename?"对话标题已保存":"新对话已创建；请通过模型菜单选择模型后发送需求。";
        }catch(Exception e){if(!IsDisposed)inputStatus.Text="操作失败："+e.Message;}
        finally{busy=false;if(!IsDisposed){newChat.Enabled=true;renameChat.Enabled=true;send.Enabled=true;keys.Enabled=true;}}
        if(!IsDisposed)await UpdateStatus();
    }
    async Task SendInput(string action, string key) {
        if(selectedId==null || reader.Session==""){inputStatus.Text="请先选择或新建当前项目的对话。";return;}
        if(busy) {inputStatus.Text="正在读取或发送，请稍后再试。"; return;}
        if(terminal!=null) {
            if(!terminal.Connected){inputStatus.Text="交互终端尚未连接，请稍后或按刷新重连。";return;}
            if(action=="key")terminal.Key(key);
            else if(!String.IsNullOrEmpty(input.Text)){terminal.Paste(input.Text);input.Clear();inputStatus.Text="已粘贴到远端输入位置，确认后在终端按 Enter 提交。";}
            return;
        }
        string draft=input.Text;
        if(action=="send" && String.IsNullOrWhiteSpace(draft)) {input.Focus(); return;}
        busy=true;CancelRefresh(); send.Enabled=false; keys.Enabled=false; refresh.Enabled=false; input.ReadOnly=true;
        inputStatus.Text=action=="send" ? "正在检查共享终端并发送…" : "正在发送按键…";
        try {
            var result=await reader.Input(action,draft,key);
            if(IsDisposed) return;
            inputStatus.Text=Reader.Get(result,"message");
            if(Reader.Get(result,"ok").Equals("True",StringComparison.OrdinalIgnoreCase) && action=="send") input.Clear();
        } catch(Exception e) {if(!IsDisposed) inputStatus.Text="发送结果未确认："+e.Message+"；请查看输出后再重试。";}
        finally {busy=false; if(!IsDisposed) {send.Enabled=true; keys.Enabled=true; refresh.Enabled=true; input.ReadOnly=false;}}
        if(!IsDisposed) await UpdateStatus();
    }
    async Task UpdateStatus() {
        if (busy || refreshing || IsDisposed) return;
        refreshing=true;refresh.Enabled=false;
        int generation=viewVersion;
        var cancellation=new CancellationTokenSource();refreshCancellation=cancellation;
        try {
            var conversations=await reader.Conversations("list",null,null,null,cancellation.Token);
            if(IsDisposed || cancellation.IsCancellationRequested || busy || generation!=viewVersion)return;
            if(selectedId==null && !testing) {
                lastChatResponse=conversations;await LocateConversation(false);
                if(selectedId!=null)return;
            }
            LoadChats(conversations);
            if(selectedId==null) {
                if(chats.Items.Count>0){await SwitchConversation(chats.SelectedItem as Conversation ?? (Conversation)chats.Items[0]);return;}
                reader.Session="";send.Enabled=false;keys.Enabled=false;status.Text="● 该项目暂无对话";output.Text="可点击「新建对话」开始。";connection.Text="未选择终端";
            }
            Snapshot cached;if(selectedId!=null && snapshotCache.TryGetValue(selectedId,out cached))ApplySnapshot(cached);
            if(terminal!=null && selectedId!=null)terminal.Connect(reader.Session);
        } catch(OperationCanceledException) {
        } catch (Exception e) {
            if (!IsDisposed && !cancellation.IsCancellationRequested && generation==viewVersion && !busy) {status.Text = "● 连接失败 · 输出非实时"; status.ForeColor = Color.FromArgb(180, 86, 67); reason.Text = e.Message + "；保留上次内容，非实时状态。"; tips.SetToolTip(status,reason.Text); connection.Text = "SSH 离线";}
        } finally {refreshing=false;if(refreshCancellation==cancellation)refreshCancellation=null;cancellation.Dispose();if(!IsDisposed)refresh.Enabled=!busy;}
    }
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr handle,int message,IntPtr wParam,IntPtr lParam);
    void ApplySnapshot(Snapshot s) {
        var names = new Dictionary<string, string> {{"working", "正在工作"}, {"waiting", "等待你的确认"}, {"finished", "本轮已结束"}, {"idle", "等待新指令"}, {"stopped", "会话未运行"}, {"unknown", "状态待确认"},{"history","历史对话"}};
        status.Text = "● " + (names.ContainsKey(s.State) ? names[s.State] : "状态待确认");
        status.ForeColor = s.State == "working" || s.State == "waiting" ? Color.FromArgb(173, 121, 36) : s.State == "stopped" ? Color.FromArgb(180, 86, 67) : Color.FromArgb(49, 119, 84);
        reason.Text = s.Reason; tips.SetToolTip(status,s.Reason); connection.Text = "SSH · " + reader.Session;
        tips.SetToolTip(projectSelector,"项目："+selectedProject+"\n当前对话工作目录："+s.Project);
        DateTime date; updated.Text = "更新 " + (DateTime.TryParse(s.CheckedAt, out date) ? date.ToLocalTime().ToString("HH:mm:ss") : "—");
        tips.SetToolTip(updated,"最后成功更新："+(DateTime.TryParse(s.CheckedAt,out date) ? date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "—"));
        if(terminal!=null)return;
        if(previous==s.Output)return;
        bool follow=previous==null || output.GetCharIndexFromPosition(new Point(Math.Max(0,output.ClientSize.Width-4),Math.Max(0,output.ClientSize.Height-4)))>=output.TextLength-4;
        int selection=output.SelectionStart,length=output.SelectionLength,top=output.GetCharIndexFromPosition(new Point(1,1));
        SendMessage(output.Handle,0x000B,IntPtr.Zero,IntPtr.Zero);
        try {
            previous=s.Output;output.Text=String.IsNullOrEmpty(s.Output)?"暂无终端输出。":s.Output;
            if(follow)ScrollLatest();
            else {output.SelectionStart=Math.Min(top,output.TextLength);output.SelectionLength=0;output.ScrollToCaret();output.SelectionStart=Math.Min(selection,output.TextLength);output.SelectionLength=Math.Min(length,output.TextLength-output.SelectionStart);}
        }finally {SendMessage(output.Handle,0x000B,new IntPtr(1),IntPtr.Zero);output.Invalidate();}
    }
}

internal static class Program {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
    [STAThread] static int Main(string[] args) {
        try {
            if(args.Length>0 && args[0]=="--quota-self-test") {QuotaDashboard.SelfTest();return 0;}
            if(args.Length>0 && args[0]=="--quota-ui-check") {
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                var quota=new Reader().AccountQuota().GetAwaiter().GetResult();
                if(!BackendApi.Flag(quota,"ok"))return 1;
                using(var form=new QuotaDashboard(new Reader(),true)) {
                    form.ShowInTaskbar=false;form.Opacity=0;form.Show();form.Populate(quota);Application.DoEvents();
                    using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"quota-ui-check.png"));}
                    form.Close();
                }
                return 0;
            }
            if(args.Length>0 && args[0]=="--quota-check") {
                var quota=new Reader().AccountQuota().GetAwaiter().GetResult();
                if(BackendApi.Flag(quota,"ok"))QuotaDashboard.Rows(quota);
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"quota-check.json"),new JavaScriptSerializer().Serialize(quota),Encoding.UTF8);
                return BackendApi.Flag(quota,"ok")?0:1;
            }
            if(args.Length>0 && args[0]=="--notify-watch")return FeishuNotifications.Watch();
            if(args.Length>0 && args[0]=="--notify-self-test")return FeishuNotifications.SelfTest();
            if(args.Length>0 && args[0]=="--notify-test")return FeishuNotifications.TestSend();
            if(args.Length>0 && args[0]=="--notify-check") {
                var metadata=new Reader().NotificationMetadata().GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"feishu-metadata-check.json"),new JavaScriptSerializer().Serialize(metadata),Encoding.UTF8);
                return BackendApi.Flag(metadata,"ok")?0:1;
            }
            if(args.Length==3 && args[0]=="--api-check") {
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                bool passed=false;
                using(var form=new MonitorForm()) {
                    form.ShowInTaskbar=false;form.Opacity=0;
                    form.Shown+=async(s,e)=>{try {
                        await Task.Delay(2500);
                        var request=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(args[1],Encoding.UTF8));
                        Dictionary<string,object> result=null;
                        for(int attempt=0;attempt<30;attempt++) {
                            result=await BackendPipe.CallOrExecute(request);
                            if(Reader.Get(result,"error")!="Monitor is busy; retry when the current operation finishes.")break;
                            await Task.Delay(1000);
                        }
                        var ui=result.ContainsKey("ui")?(Dictionary<string,object>)result["ui"]:new Dictionary<string,object>();
                        passed=Reader.Get(result,"ok").Equals("True",StringComparison.OrdinalIgnoreCase) && !BackendApi.Flag(result,"guiReloadRequired") && Reader.Get(ui,"selected")==Reader.Get(request,"id") && Reader.Get(ui,"target")==Reader.Get(result,"target");
                        result["passed"]=passed;File.WriteAllText(args[2],new JavaScriptSerializer().Serialize(result),Encoding.UTF8);
                    }catch(Exception error){File.WriteAllText(args[2],new JavaScriptSerializer().Serialize(new {passed=false,error=error.ToString()}),Encoding.UTF8);}finally{form.Close();}};
                    Application.Run(form);
                }
                return passed?0:1;
            }
            if(args.Length==3 && args[0]=="--api") {
                var request=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(args[1],Encoding.UTF8));
                var result=BackendPipe.CallOrExecute(request).GetAwaiter().GetResult();
                File.WriteAllText(args[2],new JavaScriptSerializer().Serialize(result),Encoding.UTF8);
                return Reader.Get(result,"ok").Equals("True",StringComparison.OrdinalIgnoreCase)?0:1;
            }
            if (args.Length > 0 && args[0] == "--check") {
                Snapshot s = new Reader().Read().GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "desktop-check.json"), new JavaScriptSerializer().Serialize(new {state=s.State, reason=s.Reason, project=s.Project, checkedAt=s.CheckedAt}), Encoding.UTF8); return 0;
            }
            if(args.Length>0 && args[0]=="--conversations-check") {
                var result=new Reader().Conversations("list",null,null,null).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"conversations-check.json"),new JavaScriptSerializer().Serialize(result),Encoding.UTF8);
                return Reader.Get(result,"ok").Equals("True",StringComparison.OrdinalIgnoreCase)?0:1;
            }
            if(args.Length>0 && args[0]=="--ui-check") {
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                using(var form=new MonitorForm())using(var checkTimer=new System.Windows.Forms.Timer {Interval=8000}) {
                    form.ShowInTaskbar=false;form.Opacity=0;
                    checkTimer.Tick+=async(s,e)=>{checkTimer.Stop();await form.CheckTerminalView();File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-check.json"),form.UiCheck(),Encoding.UTF8);form.Close();};
                    checkTimer.Start();Application.Run(form);
                }
                return 0;
            }
            if(args.Length>1 && args[0]=="--terminal-check") {
                foreach(string target in new[]{args[1],args.Length>2?args[2]:args[1]})if(!Regex.IsMatch(target,@"^monitor-io-test[\w-]*:\d+$"))throw new Exception("交互测试只能指向独立的 monitor-io-test 测试会话");
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);bool passed=false;
                using(var form=new Form {Width=1000,Height=700,ShowInTaskbar=false,Opacity=0})using(var test=new InteractiveTerminal()) {
                    form.Controls.Add(test);
                    form.Shown+=async(s,e)=>{try {
                        test.Connect(args[1]);for(int i=0;i<60 && !test.Connected;i++)await Task.Delay(200);
                        string firstScreen="",secondScreen="",returnScreen="";
                        for(int i=0;i<15;i++){test.Inspect();await Task.Delay(200);if(test.Inspection!=null && test.Inspection.Contains("echo stale"))break;}
                        firstScreen=test.Inspection;bool initial=firstScreen!=null && firstScreen.Contains("echo stale");
                        test.Key("C-u");test.Paste("echo ac");await Task.Delay(200);test.Key("Left");test.Key("Right");test.Key("C-b");test.Paste("b");test.Key("Enter");await Task.Delay(500);
                        test.Inspect();await Task.Delay(200);bool arrows=test.Inspection!=null && test.Inspection.Contains("\nabc\n");
                        test.Key("Up");test.Key("C-u");test.Paste("printf '中文-TERMINAL_OK\\n'");test.Key("Enter");await Task.Delay(500);
                        test.Inspect();await Task.Delay(200);bool unicode=test.Inspection!=null && test.Inspection.Contains("\n中文-TERMINAL_OK\n");
                        string marker="PASTE_"+Guid.NewGuid().ToString("N").Substring(0,8);
                        test.Paste("echo "+marker+"_ONE\necho "+marker+"_TWO");await Task.Delay(500);test.Inspect();await Task.Delay(200);
                        bool notSubmitted=test.Inspection!=null && !test.Inspection.Contains("\n"+marker+"_ONE\n");
                        test.Key("Enter");await Task.Delay(500);test.Inspect();await Task.Delay(200);bool multiline=test.Inspection!=null && test.Inspection.Contains("\n"+marker+"_ONE\n"+marker+"_TWO\n");
                        string clip="CLIP_"+Guid.NewGuid().ToString("N").Substring(0,8);bool copied=await test.ClipboardEventCheck("echo "+clip);await Task.Delay(400);test.Inspect();await Task.Delay(200);
                        bool clipDeferred=test.Inspection!=null && !test.Inspection.Contains("\n"+clip+"\n");
                        test.Key("Enter");await Task.Delay(400);test.Inspect();await Task.Delay(200);bool clipboard=copied && clipDeferred && test.Inspection!=null && test.Inspection.Contains("\n"+clip+"\n");
                        int width=test.Columns;form.Width+=160;await Task.Delay(500);bool resized=test.Columns>width;
                        bool switched=true;
                        if(args.Length>2){test.Connect(args[2]);for(int i=0;i<60 && !test.Connected;i++)await Task.Delay(200);
                            for(int i=0;i<15;i++){test.Inspect();await Task.Delay(200);if(test.Inspection!=null && test.Inspection.Contains("echo second"))break;}
                            secondScreen=test.Inspection;switched=secondScreen!=null && secondScreen.Contains("echo second");
                            test.Key("C-u");test.Paste("echo SECOND_ONLY");test.Key("Enter");await Task.Delay(400);
                            test.Connect(args[1]);for(int i=0;i<60 && !test.Connected;i++)await Task.Delay(200);
                            for(int i=0;i<15;i++){test.Inspect();await Task.Delay(200);if(test.Inspection!=null && test.Inspection.Contains(marker+"_TWO"))break;}
                            returnScreen=test.Inspection;switched=switched && returnScreen!=null && !returnScreen.Contains("SECOND_ONLY") && returnScreen.Contains(marker+"_TWO");}
                        passed=initial && arrows && unicode && notSubmitted && multiline && resized && switched && clipboard;
                        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"terminal-check.json"),new JavaScriptSerializer().Serialize(new {passed=passed,connected=test.Connected,existingDraftVisible=initial,arrowAndCtrlBEditing=arrows,unicodeInput=unicode,pasteRequiresEnter=notSubmitted,multilinePaste=multiline,resize=resized,paneSwitchIsolation=switched,clipboardCopyPasteEvents=clipboard}),Encoding.UTF8);
                        if(!passed)File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"terminal-test-screen.txt"),"FIRST\n"+firstScreen+"\nSECOND\n"+secondScreen+"\nRETURN\n"+returnScreen,Encoding.UTF8);
                    }catch(Exception error){File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"terminal-check.json"),new JavaScriptSerializer().Serialize(new {passed=false,error=error.ToString()}));}finally {test.Stop();form.Close();}};
                    Application.Run(form);
                }
                return passed?0:1;
            }
            if(args.Length>0 && args[0]=="--sidebar-check") {
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                using(var form=new MonitorForm(true)){form.ShowInTaskbar=false;form.Opacity=0;form.Show();File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"sidebar-check.json"),form.SidebarRegression(),Encoding.UTF8);form.Close();}
                return 0;
            }
            if(args.Length>0 && args[0]=="--interaction-check") {
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                string result="";
                using(var form=new MonitorForm(true)) {
                    form.ShowInTaskbar=false;form.Opacity=0;
                    form.Shown+=async(s,e)=>{try {result=await form.InteractionRegression();}catch(Exception error){result=new JavaScriptSerializer().Serialize(new {passed=false,error=error.ToString()});}finally{File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"interaction-check.json"),result,Encoding.UTF8);form.Close();}};
                    Application.Run(form);
                }
                return Reader.Get(new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(result),"passed").Equals("True",StringComparison.OrdinalIgnoreCase)?0:1;
            }
            if (args.Length > 0 && args[0] == "--input-check") {
                var result = new Reader().Input("check", "连接测试，仅检查不发送", null).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"input-check.json"),new JavaScriptSerializer().Serialize(result),Encoding.UTF8);
                return Reader.Get(result,"ok").Equals("True",StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            }
            if (args.Length > 0 && args[0] == "--self-test") {
                string[] inputs = {"✻ Brewed · done 3:50 AM\n❯ ", "❯ old\nWorking… (esc to interrupt)", "❯ old\nDo you want to proceed?", "❯ ", "Intermediate", "Working… (esc to interrupt)\n✻ Brewed · done 3:50 AM\n❯ "};
                string[] states = {"finished", "working", "waiting", "idle", "unknown", "finished"};
                for (int i = 0; i < inputs.Length; i++) if (Reader.Classify(new Dictionary<string, object> {{"command", "claude"}, {"output", inputs[i]}}).State != states[i]) return 1;
                return 0;
            }
            bool first;
            using (var mutex = new Mutex(true, "Local\\ClaudeRemoteDesktopMonitorPublicInteractive", out first)) {
                if (!first) {IntPtr existing = FindWindow(null, "Claude 云端监控"); if (existing != IntPtr.Zero) {ShowWindow(existing, 9); SetForegroundWindow(existing);} return 0;}
                using(var notificationCancellation=new CancellationTokenSource()) {
                    var notifications=Task.Run(()=>FeishuNotifications.Watch(notificationCancellation.Token));
                    try {Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new MonitorForm());}
                    finally {notificationCancellation.Cancel();try{notifications.Wait(5000);}catch{}}
                }
            }
            return 0;
        } catch (Exception e) {if (args.Length == 0) MessageBox.Show(e.Message, "Claude 监控启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error); else File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "desktop-error.txt"), e.ToString()); return 1;}
    }
}
