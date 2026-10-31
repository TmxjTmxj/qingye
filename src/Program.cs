using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
namespace Qingye
{
    public static class Program
    {
        [STAThread] public static void Main(string[] args)
        {
            string executable=Path.GetFullPath(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string identity;using(var hash=SHA256.Create())identity=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(executable.ToUpperInvariant()+Environment.UserName))).Replace("-","");
            bool first;using(var mutex=new Mutex(true,"Local\\Qingye-"+identity,out first))
            {
                if(!first){Forward(executable,args);return;}
                try
                {
                    var app=new Application();var window=new UI.MainWindow();
                    window.SourceInitialized+=delegate{HwndSource.FromHwnd(new WindowInteropHelper(window).Handle).AddHook(delegate(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled){if(message!=0x4a)return IntPtr.Zero;var data=(CopyData)Marshal.PtrToStructure(lParam,typeof(CopyData));if(data.Tag!=new IntPtr(0x5147) || data.Length<2 || data.Length>131072)return IntPtr.Zero;string payload=Marshal.PtrToStringUni(data.Pointer,data.Length/2-1);handled=true;window.Dispatcher.BeginInvoke(new Action(delegate{if(window.WindowState==WindowState.Minimized)window.WindowState=WindowState.Normal;window.Show();window.Activate();if(payload.Length>0){var paths=payload.Split('\n');Observe(window.OpenPathsAsync(paths,paths.Length>1),window);}}));return new IntPtr(1);});};
                    window.Loaded+=delegate{if(args.Length>0)Observe(window.OpenPathsAsync(args,args.Length>1),window);};
                    app.DispatcherUnhandledException+=delegate(object sender,System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e){MessageBox.Show(window,"操作未完成："+e.Exception.Message,"轻页");e.Handled=true;};
                    app.Run(window);
                }
                finally{mutex.ReleaseMutex();}
            }
        }
        static void Observe(System.Threading.Tasks.Task task,Window window){task.ContinueWith(t=>window.Dispatcher.BeginInvoke(new Action(delegate{MessageBox.Show(window,t.Exception.GetBaseException().Message,"轻页");})),System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);}
        [StructLayout(LayoutKind.Sequential)] struct CopyData{public IntPtr Tag;public int Length;public IntPtr Pointer;}
        [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SendMessageTimeout(IntPtr hwnd,int message,IntPtr wParam,ref CopyData data,uint flags,uint timeout,out IntPtr result);
        static void Forward(string executable,string[] args)
        {
            string payload=String.Join("\n",args.Select(Path.GetFullPath));IntPtr memory=Marshal.StringToHGlobalUni(payload);
            try{var data=new CopyData{Tag=new IntPtr(0x5147),Length=(payload.Length+1)*2,Pointer=memory};for(int attempt=0;attempt<30;attempt++){foreach(var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))using(process){try{if(process.Id==Process.GetCurrentProcess().Id || process.MainWindowHandle==IntPtr.Zero || !String.Equals(process.MainModule.FileName,executable,StringComparison.OrdinalIgnoreCase))continue;IntPtr result;if(SendMessageTimeout(process.MainWindowHandle,0x4a,IntPtr.Zero,ref data,2,1500,out result)!=IntPtr.Zero && result==new IntPtr(1))return;}catch{}}Thread.Sleep(50);}}
            finally{Marshal.FreeHGlobal(memory);}
        }
    }
}
