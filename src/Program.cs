using System;
using System.Windows;
namespace Qingye { public static class Program {
[STAThread] public static void Main(string[] args) {
 var app=new Application();var window=new UI.MainWindow();
 window.Loaded+=delegate{if(args.Length>0)window.Observe(window.OpenPathsAsync(args,args.Length>1));};
 app.DispatcherUnhandledException+=delegate(object sender,System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e){MessageBox.Show(window,"操作未完成："+e.Exception.Message,"轻页");e.Handled=true;};
 app.Run(window);
} } }
