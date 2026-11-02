using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Qingye.UI;
namespace Qingye.Tests
{
    public static class DesktopExperience
    {
        static string root=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"../.."));
        static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
        static void Until(Func<bool> condition){var deadline=DateTime.UtcNow.AddSeconds(15);while(!condition()){if(DateTime.UtcNow>deadline)throw new Exception("Desktop operation timed out");Pump();Thread.Sleep(10);}}
        static void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);}
        static void Await(Task task){Until(()=>task.IsCompleted);task.GetAwaiter().GetResult();Pump();}
        static void Capture(Window window,string name){window.UpdateLayout();Pump();var full=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);full.Render(window);var visual=new DrawingVisual();using(var drawing=visual.RenderOpen()){drawing.DrawImage(full,new Rect(0,0,window.ActualWidth,window.ActualHeight));var terminal=((MainWindow)window).TestHooks.Terminal;if(terminal.Browser!=null&&terminal.Browser.CoreWebView2!=null){using(var bytes=new MemoryStream()){Await(terminal.Browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,bytes));bytes.Position=0;var picture=new BitmapImage();picture.BeginInit();picture.CacheOption=BitmapCacheOption.OnLoad;picture.StreamSource=bytes;picture.EndInit();var origin=terminal.Browser.TranslatePoint(new Point(0,0),window);drawing.DrawImage(picture,new Rect(origin.X,origin.Y,terminal.Browser.ActualWidth,terminal.Browser.ActualHeight));}}}var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(visual);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(Path.Combine(root,"artifacts/ui",name)))png.Save(file);}
        static void WaitText(TerminalPanel terminal,string text){var deadline=DateTime.UtcNow.AddSeconds(20);while(DateTime.UtcNow<deadline){var task=terminal.SnapshotAsync();Await(task);if(task.Result.Contains(text))return;Pump();Thread.Sleep(30);}throw new Exception("Terminal text was not rendered: "+text+" / "+terminal.RendererError);}
        static void MultiWindow()
        {
            string exe=Path.Combine(root,"build/bin/轻页.exe");string a=Path.Combine(root,"examples/server.py"),b=Path.Combine(root,"examples/阅读示例.md");
            using(var first=Process.Start(new ProcessStartInfo(exe,"\""+a+"\""){UseShellExecute=false}))
            using(var second=Process.Start(new ProcessStartInfo(exe,"\""+b+"\""){UseShellExecute=false}))
            {
                try{Until(()=>{first.Refresh();second.Refresh();return first.MainWindowTitle.Contains("server.py")&&second.MainWindowTitle.Contains("阅读示例");});Check(first.Id!=second.Id&&!first.HasExited&&!second.HasExited,"Independent windows keep their own files");second.CloseMainWindow();Check(second.WaitForExit(5000)&&!first.HasExited,"Closing the new window keeps the old window alive");}
                finally{if(!second.HasExited)second.Kill();if(!first.HasExited){first.CloseMainWindow();if(!first.WaitForExit(5000))first.Kill();}}
            }
        }
        static void Measure()
        {
            string exe=Path.Combine(root,"build/bin/轻页.exe");var starts=new System.Collections.Generic.List<double>();var closes=new System.Collections.Generic.List<double>();long working=0;double idle=0;
            for(int i=0;i<6;i++)using(var p=Process.Start(new ProcessStartInfo(exe){UseShellExecute=false}))
            {var watch=Stopwatch.StartNew();Until(()=>{p.Refresh();return p.MainWindowHandle!=IntPtr.Zero;});starts.Add(watch.Elapsed.TotalMilliseconds);Thread.Sleep(300);p.Refresh();working=p.WorkingSet64;if(i==5){Thread.Sleep(2000);var samples=new System.Collections.Generic.List<double>();for(int sample=0;sample<3;sample++){var cpu=p.TotalProcessorTime;Thread.Sleep(1000);p.Refresh();samples.Add((p.TotalProcessorTime-cpu).TotalMilliseconds/10);}idle=samples.OrderBy(v=>v).ElementAt(1);}watch.Restart();p.CloseMainWindow();if(!p.WaitForExit(5000))throw new Exception("Close timed out");closes.Add(watch.Elapsed.TotalMilliseconds);}
            var lines=new[]{"First observed startup (OS cache not flushed): "+starts[0].ToString("F1")+" ms","Warm startup median (5): "+starts.Skip(1).OrderBy(x=>x).ElementAt(2).ToString("F1")+" ms","Close median: "+closes.OrderBy(x=>x).ElementAt(3).ToString("F1")+" ms","Blank working set: "+(working/1048576.0).ToString("F1")+" MiB","Idle CPU sample: "+idle.ToString("F2")+"% of one logical core"};File.WriteAllLines(Path.Combine(root,"artifacts/metrics/terminal-0.13-performance.txt"),lines);foreach(var line in lines)Console.WriteLine(line);
        }
        [STAThread]public static int Main(string[] args)
        {
            Directory.CreateDirectory(Path.Combine(root,"artifacts/ui"));Directory.CreateDirectory(Path.Combine(root,"artifacts/metrics"));
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                if(args.Contains("terminal")){TerminalAcceptance.Run();return 0;}
                if(args.Contains("measure")){Measure();return 0;}
                MultiWindow();
                var window=new MainWindow{ShowActivated=false,Left=-16000,Top=0};window.Show();Pump();
                try
                {
                    var terminal=window.TestHooks.Terminal;Check(terminal.ProcessId==0,"No shell process before terminal reveal");
                    Await(window.OpenPathsAsync(new[]{Path.Combine(root,"examples/server.py")},false));
                    window.ToggleTerminal();try{Until(()=>terminal.IsRunning||terminal.RendererError!=null);}catch{Console.WriteLine("STARTUP "+terminal.StartupTrace+" ERROR "+terminal.RendererError);if(terminal.Browser!=null&&terminal.Browser.CoreWebView2!=null){var diagnose=terminal.Browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify({ready:document.readyState,scripts:document.scripts.length,term:typeof qingyeTerminal,body:document.body.innerText.slice(0,150),uri:location.href})");Await(diagnose);Console.WriteLine(diagnose.Result);}throw;}Check(terminal.RendererError==null,"Renderer initialization: "+terminal.RendererError);terminal.Send("echo QINGYE_CMD_READY\r");WaitText(terminal,"\nQINGYE_CMD_READY");Check(true,"Real CMD produces command output");
                    terminal.Send("echo 中文终端\r");WaitText(terminal,"\n中文终端");Check(true,"UTF-8 terminal output retains Chinese");
                    int pid=terminal.ProcessId;window.ToggleTerminal();window.ToggleTerminal();Check(terminal.ProcessId==pid,"Hide and reveal reuse one CMD process");
                    terminal.Send("set /p QINGYE_INPUT=Input: \r");Thread.Sleep(100);terminal.Send("hello-terminal\r");terminal.Send("echo VALUE-%QINGYE_INPUT%\r");WaitText(terminal,"\nVALUE-hello-terminal");Check(true,"Interactive stdin supports set /p");
                    terminal.Send("ping -t 127.0.0.1\r");Thread.Sleep(250);terminal.Send("\x03");Thread.Sleep(150);terminal.Send("echo AFTER_INTERRUPT\r");WaitText(terminal,"\nAFTER_INTERRUPT");Check(true,"Ctrl+C interrupts a foreground command");
                    window.Width=1240;window.Height=820;Capture(window,"native-0.12-bottom.png");
                    window.Appearance.TerminalDock="right";window.Appearance.TerminalSize=380;window.UpdateToolbar();Capture(window,"native-0.12-right.png");
                    window.SetTheme("dark");Capture(window,"native-0.12-dark.png");window.Width=390;window.Height=640;window.UpdateToolbar();Capture(window,"native-0.12-narrow.png");
                    Check(terminal.ActualWidth>200&&terminal.ActualHeight>100,"Narrow layout keeps terminal usable");
                    window.Close();Pump();Until(()=>{try{using(var p=Process.GetProcessById(pid))return p.HasExited;}catch(ArgumentException){return true;}});Check(true,"Closing a window releases its CMD process");
                }
                finally{if(window.IsVisible)window.Close();Pump();}
                Measure();return 0;
            }catch(Exception e){Console.WriteLine("FAIL "+e);return 1;}finally{app.Shutdown();}
        }
    }
}
