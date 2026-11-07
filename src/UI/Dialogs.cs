using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Qingye.Core;
namespace Qingye.UI
{
    public static class Dialogs
    {
        static string InitialDirectory(Window owner){var window=owner as MainWindow;return window!=null?(window.WorkspaceRoot??(window.Current!=null&&window.Current.Path!=null?Path.GetDirectoryName(window.Current.Path):Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))):Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);}
        public static string[] ParseSelection(string raw){var parts=raw.Split(new[]{'\0'},StringSplitOptions.RemoveEmptyEntries);return parts.Length<=1?parts:parts.Skip(1).Select(name=>Path.Combine(parts[0],name)).ToArray();}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] class OpenFileName
        {
            public int size=Marshal.SizeOf(typeof(OpenFileName));public IntPtr owner,instance;public string filter;public string customFilter;public int maxCustomFilter,filterIndex=1;public IntPtr file;public int maxFile;public IntPtr fileTitle;public int maxFileTitle;public string initialDirectory,title;public int flags;public short fileOffset,extensionOffset;public string defaultExtension;public IntPtr customData,hook;public string template;public IntPtr reserved;public int reserved2,flagsEx;
        }
        [DllImport("comdlg32.dll",CharSet=CharSet.Unicode)] static extern bool GetOpenFileName(OpenFileName data);
        [DllImport("comdlg32.dll",CharSet=CharSet.Unicode)] static extern bool GetSaveFileName([In,Out] OpenFileName data);
        public static string[] PickFiles(Window owner,bool multiple)
        {
            IntPtr buffer=Marshal.AllocHGlobal(65536*2);Marshal.Copy(new byte[65536*2],0,buffer,65536*2);
            try{var data=new OpenFileName{owner=new WindowInteropHelper(owner).Handle,filter="所有文件\0*.*\0文本与代码\0*.txt;*.md;*.log;*.json;*.html;*.css;*.js;*.py;*.cs;*.csv;*.xml;*.yaml\0\0",file=buffer,maxFile=65536,initialDirectory=InitialDirectory(owner),title=multiple?"导入更多文件（仅本次）":"打开文件（替换当前）",flags=0x00080000|0x00001000|0x00000800|0x00000008|0x02000000|(multiple?0x00000200:0)};return GetOpenFileName(data)?ParseSelection(Marshal.PtrToStringUni(buffer,65536)):new string[0];}
            finally{Marshal.FreeHGlobal(buffer);}
        }
        public static string PickSave(Window owner,string filename)
        {
            IntPtr buffer=Marshal.AllocHGlobal(65536*2);Marshal.Copy(new byte[65536*2],0,buffer,65536*2);byte[] initial=Encoding.Unicode.GetBytes((Path.GetFileName(filename??"未命名.txt"))+"\0");Marshal.Copy(initial,0,buffer,initial.Length);
            try{var data=new OpenFileName{owner=new WindowInteropHelper(owner).Handle,filter="所有文件\0*.*\0\0",file=buffer,maxFile=65536,initialDirectory=String.IsNullOrEmpty(Path.GetDirectoryName(filename))?InitialDirectory(owner):Path.GetDirectoryName(filename),title="保存文件",flags=0x00080000|0x00000800|0x00000002|0x00000008|0x02000000};return GetSaveFileName(data)?Marshal.PtrToStringUni(buffer):null;}
            finally{Marshal.FreeHGlobal(buffer);}
        }
        public static string PickExport(Window owner,string filename,string filter)
        {
            IntPtr buffer=Marshal.AllocHGlobal(65536*2);Marshal.Copy(new byte[65536*2],0,buffer,65536*2);
            byte[] initial=Encoding.Unicode.GetBytes((Path.GetFileName(filename??"导出.txt"))+"\0");Marshal.Copy(initial,0,buffer,initial.Length);
            try
            {
                var data=new OpenFileName{owner=new WindowInteropHelper(owner).Handle,filter=filter+"\0\0",file=buffer,maxFile=65536,initialDirectory=InitialDirectory(owner),title="导出为",flags=0x00080000|0x00000002|0x00000008|0x02000000};
                if(!GetSaveFileName(data))return null;
                string path=Marshal.PtrToStringUni(buffer);
                if(!String.IsNullOrEmpty(path) && String.IsNullOrEmpty(Path.GetExtension(path)))
                {
                    string[] patterns=filter.Split('\0');int index=(Math.Max(1,data.filterIndex)-1)*2+1;
                    if(index<patterns.Length){string pattern=patterns[index].Replace("*","").Trim();if(pattern.StartsWith(".",StringComparison.Ordinal))path+=pattern;}
                }
                return path;
            }
            finally{Marshal.FreeHGlobal(buffer);}
        }
        public static string PickFolder(Window owner)
        {
            return FolderPicker.Pick(new WindowInteropHelper(owner).Handle);
        }
        class WindowHandle:System.Windows.Forms.IWin32Window{public IntPtr Handle{get;private set;}public WindowHandle(IntPtr handle){Handle=handle;}}
        public static CloseChoice ResolveClose(Window owner,IList<Document> docs,Func<Document,bool> save)
        {
            var dialog=new Window{Title="处理未保存文件",Owner=owner,Width=510,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false};Theme.Install(dialog);Theme.Apply(dialog,new Settings());
            var body=new StackPanel{Margin=new Thickness(24)};dialog.Content=body;
            body.Children.Add(new TextBlock{Text="这些文件有未保存修改",FontSize=21,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,8)});
            body.Children.Add(new TextBlock{Text="勾选要保存的文件。其余修改将随关闭清除。",Margin=new Thickness(0,0,0,14)});
            var list=new StackPanel();var checks=new List<CheckBox>();foreach(var doc in docs){var box=new CheckBox{Content=doc.Title,IsChecked=true,Tag=doc};checks.Add(box);list.Children.Add(box);}
            body.Children.Add(new ScrollViewer{Content=list,MaxHeight=300,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
            var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,20,0,0)};body.Children.Add(actions);CloseChoice result=CloseChoice.Cancel;
            actions.Children.Add(Theme.Button(null,"取消",delegate{dialog.Close();}));actions.Children.Add(Theme.Button(null,"全部放弃",delegate{result=CloseChoice.Discard;dialog.Close();}));
            var commit=Theme.Button(null,"保存选中并关闭",delegate{foreach(var box in checks)if(box.IsChecked==true && !save((Document)box.Tag))return;result=CloseChoice.Discard;dialog.Close();});commit.SetResourceReference(Control.BackgroundProperty,"HoverBrush");actions.Children.Add(commit);dialog.ShowDialog();return result;
        }
    }
}
