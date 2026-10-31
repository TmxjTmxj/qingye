using System;
using System.Runtime.InteropServices;

namespace Qingye.UI
{
    public static class FolderPicker
    {
        [ComImport,Guid("D57C7288-D4AD-4768-BE02-9D969532D960"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count,IntPtr filters);void SetFileTypeIndex(uint index);void GetFileTypeIndex(out uint index);void Advise(IntPtr events,out uint cookie);void Unadvise(uint cookie);
            void SetOptions(uint options);void GetOptions(out uint options);void SetDefaultFolder(IShellItem item);void SetFolder(IShellItem item);void GetFolder(out IShellItem item);void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)]string name);void GetFileName(out IntPtr name);void SetTitle([MarshalAs(UnmanagedType.LPWStr)]string title);void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)]string label);void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)]string label);
            void GetResult(out IShellItem item);void AddPlace(IShellItem item,uint position);void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)]string extension);void Close(int result);void SetClientGuid(ref Guid guid);void ClearClientData();void SetFilter(IntPtr filter);
            void GetResults(out IntPtr array);void GetSelectedItems(out IntPtr array);
        }
        [ComImport,Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr bind,ref Guid handler,ref Guid id,out IntPtr result);void GetParent(out IShellItem item);void GetDisplayName(uint kind,out IntPtr name);void GetAttributes(uint mask,out uint attributes);void Compare(IShellItem item,uint hint,out int order);
        }
        public static string Pick(IntPtr owner)
        {
            object instance=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")));IShellItem item=null;IntPtr name=IntPtr.Zero;
            try
            {
                var dialog=(IFileOpenDialog)instance;uint options;dialog.GetOptions(out options);dialog.SetOptions(options|0x20|0x40|0x800|0x8|0x02000000);dialog.SetTitle("导入工作区（本次使用，退出即清空）");dialog.SetOkButtonLabel("导入此文件夹");int result=dialog.Show(owner);if(result==unchecked((int)0x800704c7))return null;if(result<0)Marshal.ThrowExceptionForHR(result);
                dialog.GetResult(out item);item.GetDisplayName(0x80058000,out name);return Marshal.PtrToStringUni(name);
            }
            finally{if(name!=IntPtr.Zero)Marshal.FreeCoTaskMem(name);if(item!=null)Marshal.FinalReleaseComObject(item);Marshal.FinalReleaseComObject(instance);}
        }
    }
}
