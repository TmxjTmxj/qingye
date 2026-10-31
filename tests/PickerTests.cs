using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Runtime.InteropServices;
using Qingye.UI;

namespace Qingye.Tests
{
    public static class PickerTests
    {
        static int pid;
        delegate bool EnumWindow(IntPtr handle,IntPtr parameter);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback,IntPtr value);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent,EnumWindow callback,IntPtr value);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr handle,out uint process);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr handle,System.Text.StringBuilder name,int maximum);
        [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr handle);
        [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr handle,int id);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr handle,uint message,IntPtr wParam,string value);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
        static AutomationElement Dialog()
        {
            var until=DateTime.UtcNow.AddSeconds(10);
            while(DateTime.UtcNow<until){IntPtr found=IntPtr.Zero;EnumWindows(delegate(IntPtr h,IntPtr p){uint current;GetWindowThreadProcessId(h,out current);var name=new System.Text.StringBuilder(128);GetClassName(h,name,128);if(current==pid && IsWindowVisible(h) && name.ToString()=="#32770"){found=h;return false;}return true;},IntPtr.Zero);if(found!=IntPtr.Zero)return AutomationElement.FromHandle(found);Thread.Sleep(40);}throw new Exception("Owned native dialog did not open");
        }
        static void Fill(string value)
        {
            AutomationElement dialog=null;
            try
            {
                Thread.Sleep(180);dialog=Dialog();var handle=new IntPtr(dialog.Current.NativeWindowHandle);Thread.Sleep(200);IntPtr edit=GetDlgItem(handle,1152);if(edit==IntPtr.Zero){var combo=GetDlgItem(handle,1148);if(combo!=IntPtr.Zero)EnumChildWindows(combo,delegate(IntPtr h,IntPtr p){var name=new System.Text.StringBuilder(64);GetClassName(h,name,64);if(name.ToString()=="Edit"){edit=h;return false;}return true;},IntPtr.Zero);}
                if(edit==IntPtr.Zero)EnumChildWindows(handle,delegate(IntPtr h,IntPtr p){var name=new System.Text.StringBuilder(64);GetClassName(h,name,64);if(name.ToString()=="Edit" && GetDlgCtrlID(h)==1001){edit=h;return false;}return true;},IntPtr.Zero);
                if(edit==IntPtr.Zero){EnumChildWindows(handle,delegate(IntPtr h,IntPtr p){var name=new System.Text.StringBuilder(64);GetClassName(h,name,64);if(name.ToString()=="Edit" || name.ToString()=="ComboBox")Console.WriteLine("Dialog field: "+name+" id="+GetDlgCtrlID(h));return true;},IntPtr.Zero);throw new Exception("Filename/path edit not found");}
                SendMessage(edit,0x0c,IntPtr.Zero,value);PostMessage(GetDlgItem(handle,1),0xf5,IntPtr.Zero,IntPtr.Zero);
            }
            catch{if(dialog!=null)PostMessage(new IntPtr(dialog.Current.NativeWindowHandle),0x10,IntPtr.Zero,IntPtr.Zero);throw;}
        }
        [STAThread] public static int Main()
        {
            pid=Process.GetCurrentProcess().Id;var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var owner=new Window{Title="轻页选择器回归测试",Width=400,Height=180,ShowActivated=false};owner.Show();string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"picker-data-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                string one=Path.Combine(root,"first 中文.txt"),two=Path.Combine(root,"second file.md");File.WriteAllText(one,"one");File.WriteAllText(two,"two");
                var fill=Task.Run(delegate{Fill("\""+one+"\"");});var selected=Dialogs.PickFiles(owner,false);fill.GetAwaiter().GetResult();if(selected.Length!=1 || selected[0]!=one)throw new Exception("Single-file selection incorrect");Console.WriteLine("PASS Actual native open selects Unicode filename");
                fill=Task.Run(delegate{Fill("\""+one+"\" \""+two+"\"");});selected=Dialogs.PickFiles(owner,true);fill.GetAwaiter().GetResult();if(selected.Length!=2 || !selected.Contains(one) || !selected.Contains(two))throw new Exception("Multi-file selection incorrect");Console.WriteLine("PASS Actual native import returns both filenames");
                string save=Path.Combine(root,"saved 中文.txt");fill=Task.Run(delegate{Fill(save);});var target=Dialogs.PickSave(owner,"draft.txt");fill.GetAwaiter().GetResult();if(target!=save)throw new Exception("Save path incorrect");Console.WriteLine("PASS Actual native Save As returns chosen path");
                fill=Task.Run(delegate{Fill(root);});var folder=Dialogs.PickFolder(owner);fill.GetAwaiter().GetResult();if(folder!=root)throw new Exception("Workspace folder incorrect");Console.WriteLine("PASS Actual workspace chooser accepts direct folder path");return 0;
            }
            catch(Exception error){Console.WriteLine("PICKER FAIL: "+error);return 1;}
            finally{owner.Close();app.Shutdown();if(Qingye.Core.FileService.IsInsideRoot(AppDomain.CurrentDomain.BaseDirectory,root))Directory.Delete(root,true);}
        }
    }
}
