using System;
using System.Windows;
namespace Qingye { public static class Program {
[STAThread] public static void Main(string[] args) {
 var app=new Application();var window=new UI.MainWindow();
 if(Environment.GetEnvironmentVariable("QINGYE_OFFSCREEN")=="1"){window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-16000;window.Top=-16000;window.ShowActivated=false;window.ShowInTaskbar=false;}
 window.Loaded+=delegate{if(args.Length>0)window.Observe(window.OpenPathsAsync(args,args.Length>1));};
 app.DispatcherUnhandledException+=delegate(object sender,System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e){MessageBox.Show(window,"操作未完成："+e.Exception.Message,"轻页");e.Handled=true;};
 app.Run(window);
} } }
