using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using Qingye.UI;
namespace Qingye.Tests {
 public static class PerformanceProbe {
  static void Pump(){var f=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>f.Continue=false));Dispatcher.PushFrame(f);}
  static void Await(Task task){var end=DateTime.UtcNow.AddSeconds(30);while(!task.IsCompleted){if(DateTime.UtcNow>end)throw new Exception("Timeout");Pump();Thread.Sleep(1);}task.GetAwaiter().GetResult();Pump();}
  static TextEditor Editor(DependencyObject root){var text=root as TextEditor;if(text!=null)return text;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var result=Editor(VisualTreeHelper.GetChild(root,i));if(result!=null)return result;}return null;}
  static long Allocated{get{return AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;}}
  static void Metric(string label,Action action){long before=Allocated;var watch=Stopwatch.StartNew();action();watch.Stop();Console.WriteLine(label+"_ms="+watch.Elapsed.TotalMilliseconds.ToString("F2",System.Globalization.CultureInfo.InvariantCulture));Console.WriteLine(label+"_allocated_MiB="+((Allocated-before)/1048576.0).ToString("F3",System.Globalization.CultureInfo.InvariantCulture));}
  [STAThread]public static int Main(string[] args){AppDomain.MonitoringIsEnabled=true;var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));MainWindow window=null;try{
    string folder=Path.GetFullPath(args[0]);Directory.CreateDirectory(folder);string first=Path.Combine(folder,"first.py"),second=Path.Combine(folder,"second.py");string text=String.Join("\n",Enumerable.Range(0,2400).Select(i=>"value_"+i+" = \"performance fixture\" # line "+i));File.WriteAllText(first,text);File.WriteAllText(second,text);
    Metric("window_create",delegate{window=new MainWindow{ShowActivated=false,Left=-16000,Top=0};window.Show();window.UpdateLayout();Pump();});
    for(int i=0;i<20;i++){Thread.Sleep(15);Pump();}var process=Process.GetCurrentProcess();process.Refresh();Console.WriteLine("blank_working_set_MiB="+(process.WorkingSet64/1048576.0).ToString("F2",System.Globalization.CultureInfo.InvariantCulture));Console.WriteLine("blank_private_MiB="+(process.PrivateMemorySize64/1048576.0).ToString("F2",System.Globalization.CultureInfo.InvariantCulture));Console.WriteLine("markdig_loaded="+AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name=="Markdig"));
    Metric("open_two",()=>Await(window.OpenPathsAsync(new[]{first,second},true)));window.SetLayout("single");var docs=window.Session.Documents.Where(d=>d.Path!=null).ToArray();for(int i=0;i<6;i++){window.SelectDocument(docs[i%2],null);Pump();}
    Metric("switch_120",delegate{for(int i=0;i<120;i++){window.SelectDocument(docs[i%2],null);Pump();}});
    var editor=Editor(window);editor.Focus();Metric("type_400",delegate{for(int i=0;i<400;i++){editor.Document.Insert(editor.CaretOffset,"x");editor.CaretOffset++;if(i%10==0)Pump();}Pump();});editor.Document.UndoStack.MarkAsOriginalFile();
    Metric("caret_1000",delegate{for(int i=0;i<1000;i++){editor.CaretOffset=i%editor.Document.TextLength;if(i%20==0)Pump();}Pump();});
    window.Close();for(int i=0;i<100&&app.Windows.Count>0;i++){Pump();Thread.Sleep(5);}return 0;
  }catch(Exception e){Console.WriteLine("FAIL "+e);return 1;}finally{if(window!=null&&window.IsVisible){var editor=Editor(window);if(editor!=null)editor.Document.UndoStack.MarkAsOriginalFile();window.Close();for(int i=0;i<100&&app.Windows.Count>0;i++){Pump();Thread.Sleep(5);}}app.Shutdown();}}
 }
}
