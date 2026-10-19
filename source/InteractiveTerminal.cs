using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// Windows ConPTY owns only the local SSH client. Closing it never terminates the remote tmux window.
internal sealed class PtyConnection : IDisposable {
    [StructLayout(LayoutKind.Sequential)] struct Coord {public short X,Y;public Coord(int x,int y){X=(short)x;Y=(short)y;}}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Startup {
        public int cb;public string reserved,desktop,title;public int x,y,xSize,ySize,xCount,yCount,fill,flags;public short show,reserved2;public IntPtr reservedPtr,stdin,stdout,stderr;
    }
    [StructLayout(LayoutKind.Sequential)] struct StartupEx {public Startup Startup;public IntPtr Attributes;}
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo {public IntPtr Process,Thread;public int Pid,Tid;}
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool CreatePipe(out IntPtr read,out IntPtr write,IntPtr attributes,int size);
    [DllImport("kernel32.dll")] static extern int CreatePseudoConsole(Coord size,IntPtr input,IntPtr output,uint flags,out IntPtr console);
    [DllImport("kernel32.dll")] static extern int ResizePseudoConsole(IntPtr console,Coord size);
    [DllImport("kernel32.dll")] static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref IntPtr size);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr attribute,IntPtr value,IntPtr size,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttrs,IntPtr threadAttrs,bool inherit,uint flags,IntPtr environment,string directory,ref StartupEx startup,out ProcessInfo info);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr handle,uint code);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint timeout);
    IntPtr console,process;
    FileStream input,output;
    readonly BlockingCollection<string> queue=new BlockingCollection<string>();
    volatile bool disposed;
    public event Action<string> Data;
    public event Action Closed;
    public void Start(string command,int cols,int rows) {
        IntPtr inputRead=IntPtr.Zero,inputWrite=IntPtr.Zero,outputRead=IntPtr.Zero,outputWrite=IntPtr.Zero,attributes=IntPtr.Zero;
        try {
            if(!CreatePipe(out inputRead,out inputWrite,IntPtr.Zero,0)||!CreatePipe(out outputRead,out outputWrite,IntPtr.Zero,0))throw new System.ComponentModel.Win32Exception();
            int result=CreatePseudoConsole(new Coord(cols,rows),inputRead,outputWrite,0,out console);if(result!=0)Marshal.ThrowExceptionForHR(result);
            IntPtr size=IntPtr.Zero;InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref size);attributes=Marshal.AllocHGlobal(size);
            if(!InitializeProcThreadAttributeList(attributes,1,0,ref size)||!UpdateProcThreadAttribute(attributes,0,new IntPtr(0x00020016),console,new IntPtr(IntPtr.Size),IntPtr.Zero,IntPtr.Zero))throw new System.ComponentModel.Win32Exception();
            var startup=new StartupEx();startup.Startup.cb=Marshal.SizeOf(typeof(StartupEx));startup.Attributes=attributes;ProcessInfo info;
            if(!CreateProcess(null,new StringBuilder(command),IntPtr.Zero,IntPtr.Zero,false,0x00080000,IntPtr.Zero,null,ref startup,out info))throw new System.ComponentModel.Win32Exception();
            process=info.Process;CloseHandle(info.Thread);
            input=new FileStream(new SafeFileHandle(inputWrite,true),FileAccess.Write,4096,false);inputWrite=IntPtr.Zero;
            output=new FileStream(new SafeFileHandle(outputRead,true),FileAccess.Read,4096,false);outputRead=IntPtr.Zero;
            Task.Run(()=>ReadLoop());Task.Run(()=>WriteLoop());
        }catch {Dispose();throw;}
        finally {
            foreach(IntPtr handle in new[]{inputRead,inputWrite,outputRead,outputWrite})if(handle!=IntPtr.Zero)CloseHandle(handle);
            if(attributes!=IntPtr.Zero){DeleteProcThreadAttributeList(attributes);Marshal.FreeHGlobal(attributes);}
        }
    }
    void ReadLoop() {
        try {var decoder=Encoding.UTF8.GetDecoder();var bytes=new byte[16384];var chars=new char[16384];int count;
            while((count=output.Read(bytes,0,bytes.Length))>0){int length=decoder.GetChars(bytes,0,count,chars,0);if(Data!=null)Data(new string(chars,0,length));}
        }catch {}finally {if(!disposed && Closed!=null)Closed();}
    }
    void WriteLoop() {try {foreach(string text in queue.GetConsumingEnumerable()){byte[] bytes=Encoding.UTF8.GetBytes(text);input.Write(bytes,0,bytes.Length);input.Flush();}}catch {if(!disposed && Closed!=null)Closed();}}
    public void Write(string data) {if(!disposed && !queue.IsAddingCompleted)try {queue.Add(data);}catch(InvalidOperationException){}}
    public void Resize(int cols,int rows) {if(!disposed && console!=IntPtr.Zero)ResizePseudoConsole(console,new Coord(Math.Max(20,cols),Math.Max(5,rows)));}
    public void Dispose() {
        if(disposed)return;disposed=true;queue.CompleteAdding();
        var savedConsole=console;console=IntPtr.Zero;var savedProcess=process;process=IntPtr.Zero;
        if(savedProcess!=IntPtr.Zero)TerminateProcess(savedProcess,0);
        // Do not block the UI while ConPTY flushes its output pipe.
        Task.Run(()=>{if(savedProcess!=IntPtr.Zero){WaitForSingleObject(savedProcess,2000);CloseHandle(savedProcess);}if(savedConsole!=IntPtr.Zero)ClosePseudoConsole(savedConsole);if(input!=null)input.Dispose();if(output!=null)output.Dispose();});
    }
}

internal sealed class InteractiveTerminal : UserControl {
    readonly WebView2 view=new WebView2();
    readonly JavaScriptSerializer json=new JavaScriptSerializer();
    readonly Dictionary<string,object> config;
    PtyConnection pty;
    bool ready,enabled,starting;
    int epoch,cols=100,rows=30;
    string target="";
    public bool Connected {get {return enabled;}}
    public int Columns {get {return cols;}}
    public event Action<string> ConnectionChanged;
    public event Action RefreshRequested;
    public string Inspection;
    public InteractiveTerminal() {
        Dock=DockStyle.Fill;BackColor=Color.FromArgb(30,40,35);Margin=Padding.Empty;view.Dock=DockStyle.Fill;Controls.Add(view);
        config=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"config.json"),Encoding.UTF8));
        Load+=async(s,e)=>{try {
            var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ClaudeMonitorPublicInteractive","TerminalRuntime"));
            await view.EnsureCoreWebView2Async(environment);
            view.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;view.CoreWebView2.Settings.AreDevToolsEnabled=false;view.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled=false;view.CoreWebView2.Settings.IsStatusBarEnabled=false;
            view.CoreWebView2.SetVirtualHostNameToFolderMapping("terminal.claude-monitor.invalid",AppDomain.CurrentDomain.BaseDirectory,CoreWebView2HostResourceAccessKind.DenyCors);
            view.CoreWebView2.NavigationStarting+=(sender,args)=>{if(args.Uri!="https://terminal.claude-monitor.invalid/terminal.html")args.Cancel=true;};
            view.CoreWebView2.NewWindowRequested+=(sender,args)=>args.Handled=true;
            view.CoreWebView2.WebMessageReceived+=(sender,args)=>{try {Message(args.WebMessageAsJson);}catch(Exception error){Notify("终端操作失败："+error.Message);}};
            view.CoreWebView2.Navigate("https://terminal.claude-monitor.invalid/terminal.html");
        }catch(Exception error){Notify("终端初始化失败："+error.Message);}};
        Disposed+=(s,e)=>Stop();
    }
    void Notify(string text) {if(ConnectionChanged!=null)ConnectionChanged(text);}
    void Post(object message) {if(ready && !IsDisposed)view.CoreWebView2.PostWebMessageAsJson(json.Serialize(message));}
    void Ui(Action action) {if(IsDisposed || !IsHandleCreated)return;try {BeginInvoke(action);}catch(InvalidOperationException){}}
    public void Connect(string pane,bool force=false) {
        if(pane==target && (enabled||starting) && !force)return;
        Stop();target=pane;
        if(String.IsNullOrEmpty(pane)){Post(new {type="activate",epoch=epoch});Post(new {type="notice",data="请先选择或新建对话。",epoch=epoch});return;}
        if(!System.Text.RegularExpressions.Regex.IsMatch(pane,@"^[\w-]+:\d+$"))throw new Exception("非法终端目标");
        starting=true;if(ready)Post(new {type="activate",epoch=epoch});
    }
    void StartTransport() {
        if(!starting || target=="")return;
        int generation=epoch;string group="monitor-"+Guid.NewGuid().ToString("N");string session=target.Split(':')[0],index=target.Split(':')[1];
        string remote="trap 'tmux kill-session -t "+group+" 2>/dev/null' EXIT HUP INT TERM; export TERM=xterm-256color; tmux new-session -d -s "+group+" -t "+session+" && tmux set-option -t "+group+" prefix None && tmux set-option -t "+group+" prefix2 None && tmux set-option -t "+group+" status off && tmux set-hook -t "+group+" client-detached 'kill-session -t "+group+"' && tmux select-window -t "+group+":"+index+" && printf '\\033]0;CLAUDE_MONITOR_READY\\007' && tmux attach-session -t "+group+"; tmux kill-session -t "+group+" 2>/dev/null";
        string identity=Reader.Get(config,"identityFile");if(identity.Contains("\""))throw new Exception("非法 SSH 私钥路径");
        string command="ssh.exe -tt -i \""+identity+"\" -o BatchMode=yes -o ConnectTimeout=7 -o ServerAliveInterval=10 -o ServerAliveCountMax=2 "+Reader.Get(config,"user")+"@"+Reader.Get(config,"host")+" \""+remote+"\"";
        var transport=new PtyConnection();pty=transport;
        transport.Data+=data=>Ui(()=>{if(generation==epoch){Post(new {type="output",data=data,epoch=generation});}});
        transport.Closed+=()=>Ui(()=>{if(generation==epoch){enabled=false;starting=false;Post(new {type="notice",data="连接已断开。按 F5 重新连接；未发送的按键不会重放。",epoch=generation});Notify("交互终端已断开 · F5 重连");}});
        Post(new {type="clear",epoch=epoch});
        try {transport.Start(command,cols,rows);Notify("正在连接云端交互终端…");}
        catch(Exception error){enabled=false;starting=false;transport.Dispose();Post(new {type="notice",data="连接失败："+error.Message,epoch=epoch});Notify("连接失败："+error.Message);}
    }
    void Message(string raw) {
        var message=json.Deserialize<Dictionary<string,object>>(raw);string type=Reader.Get(message,"type");
        if(type=="ready"){ready=true;cols=Convert.ToInt32(message["cols"]);rows=Convert.ToInt32(message["rows"]);if(starting)Post(new {type="activate",epoch=epoch});return;}
        if(type=="resize"){cols=Convert.ToInt32(message["cols"]);rows=Convert.ToInt32(message["rows"]);if(pty!=null)pty.Resize(cols,rows);return;}
        if(type=="inspect"){Inspection=Reader.Get(message,"text");return;}
        if(type=="reconnect"){Connect(target,true);if(RefreshRequested!=null)RefreshRequested();return;}
        if(message.ContainsKey("epoch") && Convert.ToInt32(message["epoch"])!=epoch)return;
        if(type=="attached"){starting=false;enabled=true;Post(new {type="enabled",value=true,epoch=epoch});Notify("直接在终端输入 · Enter 提交 · Ctrl+V 粘贴 · F5 重连");return;}
        if(type=="size"){cols=Convert.ToInt32(message["cols"]);rows=Convert.ToInt32(message["rows"]);if(starting && pty==null)StartTransport();return;}
        if(type=="input"){if(enabled && pty!=null)pty.Write(Reader.Get(message,"data"));return;}
        if(type=="copy"){CopyClipboard(Reader.Get(message,"data"));return;}
        if(type=="paste"){PasteClipboard(epoch);return;}
        if(type=="context"){
            var menu=new ContextMenuStrip();string selection=Reader.Get(message,"selection");int generation=epoch;
            var copy=menu.Items.Add("复制");copy.Enabled=selection!="";copy.Click+=(s,e)=>CopyClipboard(selection);
            var paste=menu.Items.Add("粘贴");paste.Enabled=enabled;paste.Click+=(s,e)=>PasteClipboard(generation);menu.Closed+=(s,e)=>menu.Dispose();menu.Show(Cursor.Position);
        }
    }
    async void CopyClipboard(string data) {
        if(data=="")return;
        for(int i=0;i<5;i++){try {Clipboard.SetText(data,TextDataFormat.UnicodeText);Notify("已复制终端选中文本。");return;}catch(ExternalException){}await Task.Delay(60);}
        Notify("剪贴板暂时被占用，请重试复制。");
    }
    async void PasteClipboard(int generation) {
        for(int i=0;i<5;i++) {
            if(generation!=epoch || !enabled || IsDisposed)return;
            try {if(Clipboard.ContainsText(TextDataFormat.UnicodeText)){string data=Clipboard.GetText(TextDataFormat.UnicodeText);if(generation==epoch && enabled)Paste(data);}return;}
            catch(ExternalException){}await Task.Delay(60);
        }
        Notify("剪贴板暂时被占用，请重试粘贴。");
    }
    public void Paste(string text) {if(enabled)Post(new {type="paste",data=text,epoch=epoch});}
    public void Key(string key) {
        var keys=new Dictionary<string,string>{{"Enter","\r"},{"Escape","\x1b"},{"Up","\x1b[A"},{"Down","\x1b[B"},{"Left","\x1b[D"},{"Right","\x1b[C"},{"Tab","\t"},{"BTab","\x1b[Z"},{"Rewind","\x1b\x1b"},{"QueueNow","\x18\x13"}};
        string data;if(!keys.TryGetValue(key,out data)){if(key.StartsWith("C-"))data=((char)(Char.ToUpperInvariant(key[2])-64)).ToString();else if(key.StartsWith("M-"))data="\x1b"+key.Substring(2);else return;}
        if(enabled)Post(new {type="key",data=data,epoch=epoch});
    }
    public void Latest() {Post(new {type="latest"});}
    public void Inspect() {Post(new {type="inspect"});}
    public async Task<bool> ClipboardEventCheck(string command) {
        string result=await view.CoreWebView2.ExecuteScriptAsync("(()=>{terminal.select(0,0,5);const expected=terminal.getSelection();const copyData=new DataTransfer();document.dispatchEvent(new ClipboardEvent('copy',{clipboardData:copyData,bubbles:true,cancelable:true}));const copied=expected.length>0&&copyData.getData('text/plain')===expected;terminal.clearSelection();const pasteData=new DataTransfer();pasteData.setData('text/plain',"+json.Serialize(command)+");document.dispatchEvent(new ClipboardEvent('paste',{clipboardData:pasteData,bubbles:true,cancelable:true}));return copied;})()");
        return result=="true";
    }
    public async Task CapturePreview(string path) {if(ready)using(var stream=File.Create(path))await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,stream);}
    public void Stop() {Post(new {type="enabled",value=false,epoch=epoch});epoch++;enabled=false;starting=false;if(pty!=null){pty.Dispose();pty=null;}}
}
