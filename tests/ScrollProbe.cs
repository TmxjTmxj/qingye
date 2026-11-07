using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using Qingye.Core;
using Qingye.UI;

namespace Qingye.Tests
{
    // Measures how far and how fast a real wheel event scrolls the two main
    // reading surfaces. Run offscreen; prints one line per case.
    public static class ScrollProbe
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
        const uint WM_MOUSEWHEEL=0x020A;
        const uint WM_MOUSEMOVE=0x0200;
        static double MeasureStep(string surface,TextEditor editor,int notches)
        {
            editor.ScrollToVerticalOffset(0);Pump();
            var watch=Stopwatch.StartNew();
            for(int i=1;i<=notches;i++){editor.ScrollToVerticalOffset(i*63.8);PumpRender();}
            watch.Stop();
            Console.WriteLine(string.Format("{0} step {1} notches: total={2:F2}ms per={3:F2}ms",surface,notches,watch.Elapsed.TotalMilliseconds,watch.Elapsed.TotalMilliseconds/notches));
            return watch.Elapsed.TotalMilliseconds/notches;
        }
        static double MeasureStepScroll(string surface,ScrollViewer scroll,int notches,double step)
        {
            scroll.ScrollToVerticalOffset(0);Pump();
            var watch=Stopwatch.StartNew();
            for(int i=1;i<=notches;i++){scroll.ScrollToVerticalOffset(i*step);PumpRender();}
            watch.Stop();
            Console.WriteLine(string.Format("{0} step {1} notches: total={2:F2}ms per={3:F2}ms",surface,notches,watch.Elapsed.TotalMilliseconds,watch.Elapsed.TotalMilliseconds/notches));
            return watch.Elapsed.TotalMilliseconds/notches;
        }
        static void NativeMove(IntPtr hwnd,Point client)
        {
            IntPtr lParam=(IntPtr)((((int)client.Y)<<16)|(((int)client.X)&0xFFFF));
            SendMessage(hwnd,WM_MOUSEMOVE,IntPtr.Zero,lParam);
        }
        static void NativeWheel(IntPtr hwnd,int delta,Point screen)
        {
            int x=(int)screen.X,y=(int)screen.Y;
            IntPtr lParam=(IntPtr)((y<<16)|(x&0xFFFF));
            IntPtr wParam=(IntPtr)((delta&0xFFFF)<<16);
            SendMessage(hwnd,WM_MOUSEWHEEL,wParam,lParam);
        }
        static void Pump()
        {
            var frame=new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate{frame.Continue=false;}));
            Dispatcher.PushFrame(frame);
        }
        static void PumpRender()
        {
            var frame=new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Render,new Action(delegate{frame.Continue=false;}));
            Dispatcher.PushFrame(frame);
        }
        static T Find<T>(DependencyObject root) where T:DependencyObject
        {
            if(root==null)return null;
            var own=root as T;
            if(own!=null)return own;
            int count=VisualTreeHelper.GetChildrenCount(root);
            for(int i=0;i<count;i++)
            {
                var found=Find<T>(VisualTreeHelper.GetChild(root,i));
                if(found!=null)return found;
            }
            return null;
        }
        static int Count(DependencyObject root)
        {
            int total=1;int count=VisualTreeHelper.GetChildrenCount(root);
            for(int i=0;i<count;i++)total+=Count(VisualTreeHelper.GetChild(root,i));
            return total;
        }
        static void Await(System.Threading.Tasks.Task task)
        {
            var deadline=DateTime.UtcNow.AddSeconds(40);
            while(!task.IsCompleted)
            {
                if(DateTime.UtcNow>deadline)throw new TimeoutException("Probe operation timed out");
                Pump();
                System.Threading.Thread.Sleep(5);
            }
            task.GetAwaiter().GetResult();
            Pump();
        }
        static void Wheel(UIElement target,int delta)
        {
            var args=new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,delta);
            args.RoutedEvent=UIElement.MouseWheelEvent;
            target.RaiseEvent(args);
        }
        static string Report(string surface,string variant,double px,double ms,string extra)
        {
            return string.Format("{0} {1}: scroll={2:F1}px ms={3:F2}{4}",surface,variant,px,ms,extra);
        }
        static double MeasureWheel(string surface,TextEditor editor,ScrollViewer scroll,int delta,UIElement target)
        {
            editor.ScrollToVerticalOffset(0);Pump();
            double before=editor.VerticalOffset;
            var watch=Stopwatch.StartNew();
            Wheel(target,delta);PumpRender();
            watch.Stop();
            double moved=editor.VerticalOffset-before;
            Console.WriteLine(Report(surface,"delta="+delta+" via="+target.GetType().Name,moved,watch.Elapsed.TotalMilliseconds,""));
            return moved;
        }
        static double MeasureNative(string surface,TextEditor editor,int delta,IntPtr hwnd,Point point)
        {
            editor.ScrollToVerticalOffset(0);Pump();
            double before=editor.VerticalOffset;
            var watch=Stopwatch.StartNew();
            NativeMove(hwnd,new Point(300,300));
            NativeWheel(hwnd,delta,point);PumpRender();
            watch.Stop();
            double moved=editor.VerticalOffset-before;
            Console.WriteLine(Report(surface,"native delta="+delta,moved,watch.Elapsed.TotalMilliseconds,""));
            return moved;
        }
        static double MeasureNativeScroll(string surface,ScrollViewer scroll,int delta,IntPtr hwnd,Point point)
        {
            scroll.ScrollToVerticalOffset(0);Pump();
            double before=scroll.VerticalOffset;
            var watch=Stopwatch.StartNew();
            NativeMove(hwnd,new Point(300,300));
            NativeWheel(hwnd,delta,point);PumpRender();
            watch.Stop();
            double moved=scroll.VerticalOffset-before;
            Console.WriteLine(Report(surface,"native delta="+delta,moved,watch.Elapsed.TotalMilliseconds,""));
            return moved;
        }
        [STAThread]
        public static int Main(string[] args)
        {
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var window=new MainWindow{WindowStartupLocation=WindowStartupLocation.Manual,Left=-16000,Top=0,ShowActivated=false,ShowInTaskbar=false};
            window.Show();Pump();
            string root=Path.Combine(Path.GetTempPath(),"qingye-scroll-probe-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Console.WriteLine("WheelScrollLines="+SystemParameters.WheelScrollLines);
                string file=Path.Combine(root,"scroll.txt");
                var text=new System.Text.StringBuilder();
                for(int i=0;i<6000;i++)text.Append("line ").Append(i).Append(" lorem ipsum dolor sit amet consectetur adipiscing elit\n");
                File.WriteAllText(file,text.ToString());
                Await(window.OpenPathsAsync(new[]{file},false));Pump();
                Console.WriteLine("after open: docs="+window.Session.Documents.Count+" current="+(window.Current==null?"null":window.Current.Title)+" busy="+window.busy+" notice="+window.TestHooks.Footer.notice.Text);
                window.SetViewMode("single");Pump();
                var editor=window.ActiveEditor;
                var scroll=Find<ScrollViewer>(editor);
                Console.WriteLine(string.Format("editor: lines={0} doc={1} offset={2:F1} viewport={3:F1} svViewport={4:F0} svExtent={5:F0} svScrollable={6:F0} canContentScroll={7} scrollOwner={8} visible={9}/{10}",editor.Document==null?-1:editor.Document.LineCount,editor.Document==null?-1:editor.Document.TextLength,editor.VerticalOffset,editor.ViewportHeight,scroll.ViewportHeight,scroll.ExtentHeight,scroll.ScrollableHeight,scroll.CanContentScroll,((IScrollInfo)editor.TextArea).ScrollOwner!=null,editor.IsVisible,editor.IsLoaded));
                editor.ScrollToVerticalOffset(200);Pump();Console.WriteLine("manual scroll to 200 -> "+editor.VerticalOffset+" sv="+scroll.VerticalOffset);editor.ScrollToVerticalOffset(0);Pump();
                var hwnd=new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Point editorPoint=editor.PointToScreen(new Point(Math.Min(240,editor.ActualWidth/2),Math.Min(260,editor.ActualHeight/2)));
                Console.WriteLine("hwnd="+hwnd+" editorPoint="+editorPoint);
                foreach(int delta in new[]{120,60,30,15})MeasureNative("editor",editor,delta,hwnd,editorPoint);
                // Burst: 20 notches delivered back to back, one render pass.
                editor.ScrollToVerticalOffset(0);Pump();
                double start=editor.VerticalOffset;var burst=Stopwatch.StartNew();
                for(int i=0;i<20;i++)NativeWheel(hwnd,120,editorPoint);
                PumpRender();burst.Stop();
                Console.WriteLine(Report("editor-burst20","native 120-each",editor.VerticalOffset-start,burst.Elapsed.TotalMilliseconds,""));
                {var probeArgs=new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,120);probeArgs.RoutedEvent=UIElement.MouseWheelEvent;editor.ScrollToVerticalOffset(0);Pump();scroll.RaiseEvent(probeArgs);Console.WriteLine("synthetic wheel handled="+probeArgs.Handled+" offset="+editor.VerticalOffset);}
                MeasureStep("editor-window",editor,10);

                string markdown=Path.Combine(root,"scroll.md");
                var md=new System.Text.StringBuilder();
                for(int i=0;i<1200;i++)md.Append("## Section ").Append(i).Append("\n\nParagraph ").Append(i).Append(" with some words to fill the line and a little more text.\n\n");
                File.WriteAllText(markdown,md.ToString());
                Await(window.OpenPathsAsync(new[]{markdown},false));Pump();
                window.SetViewMode("reader");Await(window.UpdatePreviewAsync());Pump();
                Console.WriteLine("layout="+window.Appearance.Layout);
                var preview=window.TestHooks.Preview;
                Console.WriteLine("preview visual nodes="+Count(preview));
                var viewer=Find<FlowDocumentScrollViewer>(preview);
                Console.WriteLine("viewer found="+(viewer!=null));
                if(viewer==null)
                {
                    var sv2=Find<ScrollViewer>(preview);
                    Console.WriteLine("fallback scrollviewer found="+(sv2!=null));
                }
                else
                {
                    var readerScroll=Find<ScrollViewer>(viewer);
                    Console.WriteLine(string.Format("reader: viewport={0:F0}px extent={1:F0}px",readerScroll.ViewportHeight,readerScroll.ScrollableHeight));
                    Point readerPoint=viewer.PointToScreen(new Point(Math.Min(240,viewer.ActualWidth/2),Math.Min(260,viewer.ActualHeight/2)));
                    Console.WriteLine("readerPoint="+readerPoint);
                    foreach(int delta in new[]{120,60,30,15})MeasureNativeScroll("reader",readerScroll,delta,hwnd,readerPoint);
                    readerScroll.ScrollToVerticalOffset(0);Pump();
                    double s=readerScroll.VerticalOffset;var b2=Stopwatch.StartNew();
                    for(int i=0;i<20;i++)NativeWheel(hwnd,120,readerPoint);
                    PumpRender();b2.Stop();
                    Console.WriteLine(Report("reader-burst20","native 120-each",readerScroll.VerticalOffset-s,b2.Elapsed.TotalMilliseconds,""));
                    MeasureStepScroll("reader-window",readerScroll,10,60);
                }
                return 0;
            }
            catch(Exception error)
            {
                Console.WriteLine("SCROLL PROBE FAIL: "+error.GetType().FullName+" "+error.Message);
                Console.WriteLine(error.StackTrace);
                return 1;
            }
            finally
            {
                try
                {
                    foreach(var doc in window.Session.Documents){doc.Modified=false;doc.MetadataModified=false;}
                    window.Close();Pump();
                    if(FileService.IsInsideRoot(AppDomain.CurrentDomain.BaseDirectory,root))Directory.Delete(root,true);
                }
                catch(Exception cleanup){Console.WriteLine("cleanup: "+cleanup.Message);}
                app.Shutdown();
            }
        }
    }
}
