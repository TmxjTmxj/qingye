using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Qingye.Core;

namespace Qingye.UI
{
    internal sealed class TerminalPanel : UserControl,IDisposable
    {
        const string PageUri="https://qingye-terminal.invalid/index.html";
        readonly MainWindow owner;readonly TextBlock status;readonly Grid surface=new Grid();readonly JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=2*1024*1024};
        readonly object outputGate=new object();readonly Queue<string> output=new Queue<string>();
        TerminalSession session;Task startTask=Task.FromResult(0),initialization=Task.FromResult(0);TaskCompletionSource<bool> ready,reset;
        CoreWebView2Environment environment;string profile;int queued,sequence,waiting,generation;bool disposed,drainScheduled,visible;
        int columns=80,rows=24,fontSize=14;string selection="";
        internal WebView2 Browser{get;private set;}
        internal int ProcessId{get{return session==null?0:session.ProcessId;}}
        internal Task StartupTask{get{return startTask;}}
        internal bool IsRunning{get{return session!=null&&session.IsRunning;}}
        internal string RendererError{get;private set;}
        internal string StartupTrace{get;private set;}
        internal TerminalPanel(MainWindow owner)
        {
            this.owner=owner;HorizontalContentAlignment=HorizontalAlignment.Stretch;VerticalContentAlignment=VerticalAlignment.Stretch;
            var root=new DockPanel();root.SetResourceReference(Panel.BackgroundProperty,"SurfaceBrush");
            var header=new DockPanel{Margin=new Thickness(12,4,8,4)};DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
            var title=new TextBlock{Text="终端",FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,14,0)};title.SetResourceReference(TextBlock.ForegroundProperty,"AccentBrush");header.Children.Add(title);
            var tools=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(tools,Dock.Right);header.Children.Add(tools);
            var actions=Theme.Button("ellipsis","",delegate{});actions.ToolTip="终端操作与位置";actions.Click+=delegate{ShowMenu(actions);};tools.Children.Add(actions);
            var restart=Theme.Button("plus","",Start);restart.ToolTip="启动 CMD（复用当前会话）";tools.Children.Add(restart);
            var hide=Theme.Button("x","",owner.ToggleTerminal);hide.ToolTip="收起终端  Ctrl+`（会话继续）";tools.Children.Add(hide);
            status=new TextBlock{Text="CMD · 按需启动",FontSize=11,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};status.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");header.Children.Add(status);
            root.Children.Add(surface);Content=root;
            IsVisibleChanged+=delegate{visible=IsVisible;if(visible)Dispatcher.BeginInvoke(new Action(FocusInput));else Post(new{type="hidden"});};
        }
        void ShowMenu(FrameworkElement anchor)
        {
            var menu=new ContextMenu();menu.Items.Add(Theme.Menu("停靠底部",()=>SetDock("bottom")));menu.Items.Add(Theme.Menu("停靠右侧",()=>SetDock("right")));menu.Items.Add(new Separator());
            menu.Items.Add(Theme.Menu("复制所选内容  Ctrl+Shift+C",()=>Post(new{type="selection"})));menu.Items.Add(Theme.Menu("粘贴  Ctrl+V",Paste));
            menu.Items.Add(Theme.Menu("清除滚动历史（保留当前画面）",()=>Post(new{type="clear"})));
            menu.Items.Add(Theme.Menu("字号放大  Ctrl++",()=>Zoom(1)));menu.Items.Add(Theme.Menu("字号缩小  Ctrl+-",()=>Zoom(-1)));menu.Items.Add(Theme.Menu("重置字号  Ctrl+0",()=>Zoom(0)));
            menu.Items.Add(new Separator());menu.Items.Add(Theme.Menu("中断当前命令  Ctrl+C",()=>Send("\x03")));menu.Items.Add(Theme.Menu("结束会话",Stop));menu.Items.Add(Theme.Menu("在系统 CMD 中新开会话",OpenSystemCmd));
            menu.PlacementTarget=anchor;menu.Closed+=delegate{FocusInput();};menu.IsOpen=true;
        }
        string DirectoryPath(){string directory=owner.WorkspaceRoot??(owner.Current!=null&&owner.Current.Path!=null?Path.GetDirectoryName(owner.Current.Path):Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));return Directory.Exists(directory)?directory:Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);}
        void OpenSystemCmd(){Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe")){WorkingDirectory=DirectoryPath(),UseShellExecute=true});}
        void SetDock(string dock){owner.Appearance.TerminalDock=dock;owner.Appearance.TerminalSize=dock=="right"?420:300;owner.UpdateToolbar();FocusInput();}
        void FocusInput(){if(disposed||!visible||Browser==null||!Browser.IsVisible)return;Browser.Focus();Post(new{type="focus"});}
        internal void Reveal(){visible=true;Start();FocusInput();}
        internal void Start(){if(disposed)return;if(!startTask.IsCompleted)return;startTask=StartAsync();owner.Observe(startTask);}
        async Task StartAsync()
        {
            bool recover=false;
            try
            {
                RendererError=null;int request=generation;
                if(Browser==null){initialization=InitializeAsync();await initialization;}else await initialization;
                if(disposed||request!=generation)return;if(IsRunning){FocusInput();return;}
                StopSession();int resetGeneration=generation;reset=new TaskCompletionSource<bool>();Post(new{type="reset",id=resetGeneration});
                if(await Task.WhenAny(reset.Task,Task.Delay(10000))!=reset.Task)throw new TimeoutException("终端重置未完成。");
                if(disposed||resetGeneration!=generation)return;
                var current=new TerminalSession();session=current;int mine=generation;
                current.Output+=text=>Enqueue(text,mine);
                current.Exited+=()=>Dispatcher.BeginInvoke(new Action(delegate{if(!disposed&&session==current)status.Text="CMD 已结束 · 点击 + 重开";}));
                current.Start(DirectoryPath(),columns,rows);
                status.Text="CMD · "+Path.GetFileName(DirectoryPath());status.ToolTip=DirectoryPath()+"\n真实命令行权限；收起保留会话，关闭窗口结束子进程。";
                FocusInput();Drain();
            }
            catch(Exception error){if(!disposed){RendererError=error.Message;status.Text="终端启动失败："+error.Message;recover=initialization.IsFaulted||initialization.IsCanceled;}}
            if(recover){await ReleaseRendererAsync();initialization=Task.FromResult(0);}
        }
        async Task InitializeAsync()
        {
            StartupTrace="environment";ready=new TaskCompletionSource<bool>();profile=Path.Combine(Path.GetTempPath(),"Qingye-Terminal-"+Guid.NewGuid().ToString("N"));
            var view=new WebView2{DefaultBackgroundColor=System.Drawing.Color.FromArgb(255,23,34,41)};Browser=view;surface.Children.Add(view);
            var options=new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-sync --disk-cache-size=1048576 --media-cache-size=1048576");
            environment=await CoreWebView2Environment.CreateAsync(null,profile,options);StartupTrace="controller";await view.EnsureCoreWebView2Async(environment);StartupTrace="core ready";if(disposed)return;
            var core=view.CoreWebView2;var settings=core.Settings;settings.AreDefaultContextMenusEnabled=false;settings.AreDevToolsEnabled=false;settings.IsStatusBarEnabled=false;settings.AreBrowserAcceleratorKeysEnabled=false;settings.IsZoomControlEnabled=false;settings.IsGeneralAutofillEnabled=false;settings.IsPasswordAutosaveEnabled=false;
            core.NavigationStarting+=delegate(object sender,CoreWebView2NavigationStartingEventArgs e){StartupTrace+=" | navigation "+e.Uri.Substring(0,Math.Min(80,e.Uri.Length));if(e.Uri!=PageUri&&e.Uri!="about:blank")e.Cancel=true;};
            core.NavigationCompleted+=delegate(object sender,CoreWebView2NavigationCompletedEventArgs e){StartupTrace+=" | completed "+e.IsSuccess+" "+e.WebErrorStatus;};
            core.NewWindowRequested+=delegate(object sender,CoreWebView2NewWindowRequestedEventArgs e){e.Handled=true;};
            core.PermissionRequested+=delegate(object sender,CoreWebView2PermissionRequestedEventArgs e){e.State=CoreWebView2PermissionState.Deny;};
            core.DownloadStarting+=delegate(object sender,CoreWebView2DownloadStartingEventArgs e){e.Cancel=true;};
            core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
            byte[] document=Encoding.UTF8.GetBytes(TerminalPage.Build());
            core.WebResourceRequested+=delegate(object sender,CoreWebView2WebResourceRequestedEventArgs e){
                e.Response=e.Request.Uri==PageUri&&e.Request.Method=="GET"
                    ? environment.CreateWebResourceResponse(new MemoryStream(document,false),200,"OK","Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store")
                    : environment.CreateWebResourceResponse(new MemoryStream(new byte[0]),403,"Blocked","Cache-Control: no-store");
            };
            core.WebMessageReceived+=OnMessage;StartupTrace="private page navigation";core.Navigate(PageUri);
            if(await Task.WhenAny(ready.Task,Task.Delay(15000))!=ready.Task)throw new TimeoutException("终端渲染器未就绪。请检查系统 WebView2 Runtime。");
            await ready.Task;if(!disposed)RefreshAppearance();
        }
        void OnMessage(object sender,CoreWebView2WebMessageReceivedEventArgs e)
        {
            StartupTrace="message source="+e.Source; if(disposed||Browser==null||!Object.ReferenceEquals(sender,Browser.CoreWebView2)||e.Source!=PageUri)return;
            try
            {
                var message=json.Deserialize<Dictionary<string,object>>(e.WebMessageAsJson);string type=Convert.ToString(message["type"]);
                string text=message.ContainsKey("text")?Convert.ToString(message["text"]):"";
                if(text.Length>1048576){status.Text="一次输入过长，请分段粘贴。";return;}
                switch(type)
                {
                    case "ready":case "resize":columns=Math.Max(2,Math.Min(1000,Convert.ToInt32(message["cols"])));rows=Math.Max(1,Math.Min(500,Convert.ToInt32(message["rows"])));if(IsRunning)session.Resize(columns,rows);if(type=="ready")ready.TrySetResult(true);break;
                    case "resetReady":if(reset!=null&&Convert.ToInt32(message["id"])==generation)reset.TrySetResult(true);break;
                    case "input":Send(text);break;
                    case "binary":if(IsRunning)session.SendBytes(Convert.FromBase64String(text));break;
                    case "ack":if(Convert.ToInt32(message["id"])==waiting){waiting=0;Drain();}break;
                    case "copy":if(text.Length>0)Clipboard.SetText(text);break;
                    case "paste":Paste();break;
                    case "context":selection=text;var menu=new ContextMenu();menu.Items.Add(Theme.Menu("复制",()=>{if(selection.Length>0)Clipboard.SetText(selection);}));menu.Items.Add(Theme.Menu("粘贴",Paste));menu.PlacementTarget=Browser;menu.IsOpen=true;break;
                    case "toggle":owner.ToggleTerminal();break;
                    case "newWindow":owner.NewWindow();break;
                    case "closeWindow":owner.Close();break;
                    case "zoomIn":Zoom(1);break;case "zoomOut":Zoom(-1);break;case "zoomReset":Zoom(0);break;
                    case "error":RendererError=text;if(ready!=null)ready.TrySetException(new InvalidOperationException(text));status.Text="终端显示错误："+text;break;
                }
            }
            catch(Exception error){status.Text="终端操作未完成："+error.Message;}
        }
        void Enqueue(string text,int mine)
        {
            lock(outputGate)
            {
                // Bound queued output and apply backpressure to the PTY, never drop VT bytes.
                while(!disposed&&mine==generation&&queued>512*1024)Monitor.Wait(outputGate);
                if(disposed||mine!=generation)return;output.Enqueue(text);queued+=text.Length;
                if(drainScheduled)return;drainScheduled=true;
            }
            Dispatcher.BeginInvoke(new Action(Drain));
        }
        void Drain()
        {
            if(disposed||Browser==null||Browser.CoreWebView2==null)return;
            string text;lock(outputGate){drainScheduled=false;if(waiting!=0||output.Count==0)return;var next=new StringBuilder();while(output.Count>0&&next.Length<32768){string part=output.Dequeue();next.Append(part);queued-=part.Length;}text=next.ToString();Monitor.PulseAll(outputGate);}
            waiting=++sequence;Post(new{type="write",id=waiting,text=text});
        }
        void Post(object message){if(!disposed&&Browser!=null&&Browser.CoreWebView2!=null)Browser.CoreWebView2.PostWebMessageAsJson(json.Serialize(message));}
        internal void Send(string text){if(IsRunning)session.Send(text);}
        void Paste(){if(Clipboard.ContainsText())Post(new{type="paste",text=Clipboard.GetText()});}
        void Zoom(int change){fontSize=change==0?14:Math.Max(9,Math.Min(28,fontSize+change));Post(new{type="font",size=fontSize});}
        internal void Stop(){StopSession();status.Text="会话已结束 · 点击 + 重开";}
        void StopSession(){lock(outputGate){generation++;output.Clear();queued=0;waiting=0;drainScheduled=false;Monitor.PulseAll(outputGate);}if(session!=null){session.Dispose();session=null;}}
        internal void RefreshAppearance()
        {
            Func<string,string> color=key=>{var brush=owner.TryFindResource(key) as SolidColorBrush;return brush==null?"#172229":brush.Color.ToString().Replace("#FF","#");};
            Post(new{type="theme",theme=new{background=color("SurfaceBrush"),foreground=color("TextBrush"),cursor=color("AccentBrush"),selectionBackground=color("HoverBrush"),scrollbarSliderBackground="#71838e55",scrollbarSliderHoverBackground="#71838e88",scrollbarSliderActiveBackground="#71838ebb",black="#24323b",red="#d65f65",green="#42a879",yellow="#b49a49",blue="#669bd1",magenta="#aa7dce",cyan="#43a7ae",white="#dbe6ec",brightBlack="#71838e",brightRed="#ee858b",brightGreen="#74cfa2",brightYellow="#d6bb70",brightBlue="#94b9e3",brightMagenta="#c6a1e5",brightCyan="#7bc9cf",brightWhite="#f3f7fa"}});
            if(Browser!=null)Browser.DefaultBackgroundColor=System.Drawing.ColorTranslator.FromHtml(color("SurfaceBrush"));
        }
        internal async Task<Dictionary<string,object>> StateAsync(){if(Browser==null||Browser.CoreWebView2==null)return new Dictionary<string,object>();string value=await Browser.CoreWebView2.ExecuteScriptAsync("window.qingyeTerminal ? window.qingyeTerminal.state() : null");return json.Deserialize<Dictionary<string,object>>(value)??new Dictionary<string,object>();}
        internal async Task<string> SnapshotAsync(){var state=await StateAsync();return state.ContainsKey("text")?Convert.ToString(state["text"]):"";}
        internal async Task ShutdownAsync()
        {
            if(disposed)return;disposed=true;StopSession();if(ready!=null)ready.TrySetCanceled();if(reset!=null)reset.TrySetCanceled();try{await initialization;}catch{}
            await ReleaseRendererAsync();
        }
        async Task ReleaseRendererAsync()
        {
            var view=Browser;Browser=null;var processes=new Dictionary<int,DateTime>();
            try{if(environment!=null)foreach(var info in environment.GetProcessInfos())try{using(var p=Process.GetProcessById(info.ProcessId))if(p.ProcessName=="msedgewebview2")processes[p.Id]=p.StartTime.ToUniversalTime();}catch{}}catch{}
            if(view!=null){view.Dispose();surface.Children.Remove(view);}environment=null;
            string folder=profile;profile=null;if(view==null&&folder==null)return;
            await Task.Run(delegate{
                Thread.Sleep(200);foreach(var entry in processes)try{using(var p=Process.GetProcessById(entry.Key))if(!p.HasExited&&p.ProcessName=="msedgewebview2"&&p.StartTime.ToUniversalTime()==entry.Value){p.Kill();p.WaitForExit(1500);}}catch{}
                if(folder!=null&&folder.StartsWith(Path.Combine(Path.GetTempPath(),"Qingye-Terminal-"),StringComparison.OrdinalIgnoreCase))for(int attempt=0;attempt<8;attempt++){try{if(Directory.Exists(folder))Directory.Delete(folder,true);break;}catch{Thread.Sleep(100);}}
            });
        }
        public void Dispose(){owner.Observe(ShutdownAsync());}
    }
}
