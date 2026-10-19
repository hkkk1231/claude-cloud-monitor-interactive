using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class QuotaDashboard : Form {
    readonly Reader reader;
    readonly DataGridView grid=new DataGridView();
    readonly Label note=new Label();
    readonly Button refresh=new Button();
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer {Interval=60000};
    readonly CancellationTokenSource cancellation=new CancellationTokenSource();
    bool busy;
    static readonly TimeZoneInfo China=TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    internal QuotaDashboard(Reader reader,bool check=false) {
        this.reader=reader;
        Text="账号额度 · Claude 云端监控";Size=new Size(1200,500);MinimumSize=new Size(1000,400);
        StartPosition=FormStartPosition.CenterParent;Font=new Font("Microsoft YaHei UI",9);
        BackColor=Color.FromArgb(245,244,237);
        var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(16),ColumnCount=1,RowCount=3};
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,64));
        var header=new FlowLayoutPanel {Dock=DockStyle.Fill};
        header.Controls.Add(new Label {Text="账号剩余额度",AutoSize=true,Font=new Font(Font.FontFamily,15),Margin=new Padding(0,4,24,0)});
        refresh.Text="刷新额度";refresh.AutoSize=true;refresh.Click+=async(s,e)=>await Reload();header.Controls.Add(refresh);layout.Controls.Add(header);
        grid.Dock=DockStyle.Fill;grid.ReadOnly=true;grid.AllowUserToAddRows=false;grid.AllowUserToDeleteRows=false;grid.AllowUserToResizeRows=false;
        grid.RowHeadersVisible=false;grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill;grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect;
        grid.BackgroundColor=BackColor;grid.BorderStyle=BorderStyle.None;grid.RowTemplate.Height=38;grid.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells;
        grid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;
        foreach(string title in new[]{"账号 / 邮箱","套餐","5小时剩余","5小时重置（北京）","一周剩余","一周重置（北京）","状态 / 采样时间"})grid.Columns.Add(title,title);
        grid.Columns[0].FillWeight=160;grid.Columns[1].FillWeight=55;grid.Columns[2].FillWeight=65;grid.Columns[3].FillWeight=100;grid.Columns[4].FillWeight=65;grid.Columns[5].FillWeight=100;grid.Columns[6].FillWeight=140;
        grid.Columns[1].DefaultCellStyle.Font=new Font(Font.FontFamily,11,FontStyle.Bold);grid.Columns[1].DefaultCellStyle.Alignment=DataGridViewContentAlignment.MiddleCenter;
        foreach(int column in new[]{2,4}) {grid.Columns[column].DefaultCellStyle.Font=new Font(Font.FontFamily,12,FontStyle.Bold);grid.Columns[column].DefaultCellStyle.Alignment=DataGridViewContentAlignment.MiddleCenter;}
        grid.CellPainting+=(s,e)=>{
            if(e.RowIndex<0 || (e.ColumnIndex!=2 && e.ColumnIndex!=4))return;
            string value=Convert.ToString(e.FormattedValue);int split=value.IndexOf('%');double pct;
            if(split<0 || !Double.TryParse(value.Substring(0,split),NumberStyles.Float,CultureInfo.InvariantCulture,out pct))return;
            e.Paint(e.ClipBounds,DataGridViewPaintParts.All);
            var track=new Rectangle(e.CellBounds.X+5,e.CellBounds.Bottom-5,e.CellBounds.Width-10,3);
            using(var background=new SolidBrush(Color.FromArgb(220,225,220)))e.Graphics.FillRectangle(background,track);
            track.Width=(int)(track.Width*pct/100);
            using(var fill=new SolidBrush(pct<=5?Color.FromArgb(205,74,62):pct<=20?Color.FromArgb(205,148,48):Color.FromArgb(65,142,108)))e.Graphics.FillRectangle(fill,track);
            e.Handled=true;
        };
        layout.Controls.Add(grid);note.Dock=DockStyle.Fill;note.Text="正在读取服务器额度…";layout.Controls.Add(note);Controls.Add(layout);
        timer.Tick+=async(s,e)=>await Reload();if(!check)Shown+=async(s,e)=>{timer.Start();await Reload();};
        FormClosing+=(s,e)=>{timer.Stop();cancellation.Cancel();};FormClosed+=(s,e)=>{timer.Dispose();cancellation.Dispose();};
    }
    internal static string Beijing(string value) {
        DateTimeOffset instant;
        return DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.None,out instant)?TimeZoneInfo.ConvertTime(instant,China).ToString("MM-dd HH:mm:ss"):"未提供";
    }
    internal static string Percent(Dictionary<string,object> window) {
        double used;
        if(window==null || !Double.TryParse(Reader.Get(window,"pct"),NumberStyles.Float,CultureInfo.InvariantCulture,out used) || Double.IsNaN(used) || Double.IsInfinity(used) || used<0 || used>100)return "不可获得";
        return (100-used).ToString("0.#",CultureInfo.InvariantCulture)+"%";
    }
    static Dictionary<string,object> Object(Dictionary<string,object> source,string key) {object value;return source.TryGetValue(key,out value)?value as Dictionary<string,object>:null;}
    internal static List<string[]> Rows(Dictionary<string,object> response) {
        var rows=new List<string[]>();
        foreach(object raw in (IEnumerable)response["accounts"]) {
            var account=(Dictionary<string,object>)raw;
            string number=Reader.Get(account,"number"),letter=number=="1"?"A":number=="2"?"B":number=="3"?"C":number=="4"?"D":"#"+number;
            string name=letter+" · "+Reader.Get(account,"email");
            if(BackendApi.Flag(account,"active"))name+="\n服务器默认账号";
            string subscription=Reader.Get(account,"subscriptionType");
            if(subscription=="")subscription="不可获得";
            string state=Reader.Get(account,"usageStatus");
            var usage=Object(account,"usage");bool cached=state!="ok" || usage==null;
            if(cached)usage=Object(account,"lastGoodUsage") ?? usage;
            string label=cached?"缓存 · "+state:"已读取（可能使用缓存）";
            if(BackendApi.Flag(account,"disabled"))label+=" · 已禁用轮换";
            label+="\n采样："+Beijing(Reader.Get(account,cached?"lastGoodFetchedAt":"usageFetchedAt"));
            string retry=Reader.Get(account,"usageRetryAt");if(retry!="")label+="\n下次尝试："+Beijing(retry);
            var five=usage==null?null:Object(usage,"fiveHour");
            var week=usage==null?null:Object(usage,"sevenDay");
            rows.Add(new[]{name,subscription,Percent(five),five==null?"未提供":Beijing(Reader.Get(five,"resetsAt")),Percent(week),week==null?"未提供":Beijing(Reader.Get(week,"resetsAt")),label});
        }
        return rows;
    }
    async Task Reload() {
        if(busy || IsDisposed)return;busy=true;refresh.Enabled=false;
        try {
            var data=await reader.AccountQuota(cancellation.Token);
            if(IsDisposed || cancellation.IsCancellationRequested)return;
            if(!BackendApi.Flag(data,"ok"))throw new Exception("服务器额度查询未成功");
            Populate(data);
        }catch(OperationCanceledException) {}catch(Exception) {
            if(!IsDisposed && !cancellation.IsCancellationRequested)note.Text="查询失败，表中为上次结果，不能当作当前可用额度。请检查 SSH 或服务器账号状态后重试。";
        }finally {busy=false;if(!IsDisposed)refresh.Enabled=true;}
    }
    internal void Populate(Dictionary<string,object> data) {
        var rows=Rows(data);grid.Rows.Clear();foreach(var row in rows)grid.Rows.Add(row);
        int index=0;
        foreach(object raw in (IEnumerable)data["accounts"]) {
            var account=(Dictionary<string,object>)raw;var usage=Object(account,"usage") ?? Object(account,"lastGoodUsage");
            string details="";
            if(usage!=null && usage.ContainsKey("scoped") && usage["scoped"] is IEnumerable)
                foreach(object item in (IEnumerable)usage["scoped"]) {var window=item as Dictionary<string,object>;if(window!=null)details+=Reader.Get(window,"name")+" 专项剩余："+Percent(window)+"；重置（北京）："+Beijing(Reader.Get(window,"resetsAt"))+"\n";}
            foreach(DataGridViewCell cell in grid.Rows[index++].Cells)cell.ToolTipText=details;
        }
        grid.ClearSelection();
        note.Text="查询时间（北京）："+Beijing(Reader.Get(data,"checkedAt"))+" · 打开时每分钟刷新。模型专项额度可悬停账号行查看。\n额度为账号级剩余，包含该账号所有会话；采样时间可能来自缓存，重置以实际查询为准。";
    }
    internal static void SelfTest() {
        var json=new System.Web.Script.Serialization.JavaScriptSerializer();
        var data=json.Deserialize<Dictionary<string,object>>("{\"accounts\":[{\"number\":1,\"email\":\"test\",\"subscriptionType\":\"Pro\",\"usageStatus\":\"token_expired\",\"usage\":null,\"lastGoodFetchedAt\":\"2026-10-10T00:00:00Z\",\"lastGoodUsage\":{\"fiveHour\":{\"pct\":100},\"sevenDay\":null}}]}");
        var rows=Rows(data);
        if(rows.Count!=1 || rows[0][1]!="Pro" || rows[0][2]!="0%" || rows[0][4]!="不可获得" || !rows[0][6].Contains("缓存") || Beijing("2026-10-10T00:00:00Z")!="10-10 08:00:00" || Percent(new Dictionary<string,object>{{"pct",-1}})!="不可获得")throw new Exception("Quota display regression");
    }
}
