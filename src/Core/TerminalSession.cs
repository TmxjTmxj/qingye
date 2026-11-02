using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Qingye.Core
{
    public sealed class TerminalSession : IDisposable
    {
        IntPtr console; FileStream input; Process process; ProcessLifetime lifetime;
        readonly BlockingCollection<byte[]> inputQueue=new BlockingCollection<byte[]>(256); int disposed,processId;
        public event Action<string> Output;
        public event Action Exited;
        public int ProcessId {get{return processId;}}
        public bool IsRunning {get{try{return process!=null&&!process.HasExited&&disposed==0;}catch{return false;}}}
        [StructLayout(LayoutKind.Sequential)] struct Coord {public short X,Y;public Coord(int x,int y){X=(short)x;Y=(short)y;}}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Startup {public int cb;public string reserved,desktop,title;public int x,y,cx,cy,xChars,yChars,fill,flags;public short show,reserved2;public IntPtr reservedPtr,stdin,stdout,stderr;}
        [StructLayout(LayoutKind.Sequential)] struct StartupEx {public Startup info;public IntPtr attributes;}
        [StructLayout(LayoutKind.Sequential)] struct ProcessInfo {public IntPtr process,thread;public int pid,tid;}
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool CreatePipe(out IntPtr read,out IntPtr write,IntPtr attributes,int size);
        [DllImport("kernel32.dll")] static extern int CreatePseudoConsole(Coord size,IntPtr input,IntPtr output,uint flags,out IntPtr console);
        [DllImport("kernel32.dll")] static extern int ResizePseudoConsole(IntPtr console,Coord size);
        [DllImport("kernel32.dll")] static extern void ClosePseudoConsole(IntPtr console);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref IntPtr size);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr attribute,IntPtr value,IntPtr size,IntPtr previous,IntPtr returned);
        [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcess(string app,StringBuilder command,IntPtr pa,IntPtr ta,bool inherit,uint flags,IntPtr environment,string cwd,ref StartupEx startup,out ProcessInfo info);
        [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        static void Require(bool value){if(!value)throw new Win32Exception(Marshal.GetLastWin32Error());}
        public void Start(string directory,int columns,int rows)
        {
            if(process!=null)throw new InvalidOperationException("终端已启动。");
            IntPtr inRead=IntPtr.Zero,inWrite=IntPtr.Zero,outRead=IntPtr.Zero,outWrite=IntPtr.Zero,attributes=IntPtr.Zero;
            var pi=new ProcessInfo();
            try
            {
                Require(CreatePipe(out inRead,out inWrite,IntPtr.Zero,0));Require(CreatePipe(out outRead,out outWrite,IntPtr.Zero,0));
                int result=CreatePseudoConsole(new Coord(Math.Max(2,columns),Math.Max(1,rows)),inRead,outWrite,0,out console);
                Marshal.ThrowExceptionForHR(result);
                IntPtr size=IntPtr.Zero;InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref size);attributes=Marshal.AllocHGlobal(size);
                Require(InitializeProcThreadAttributeList(attributes,1,0,ref size));
                Require(UpdateProcThreadAttribute(attributes,0,new IntPtr(0x20016),console,new IntPtr(IntPtr.Size),IntPtr.Zero,IntPtr.Zero));
                var startup=new StartupEx();startup.info.cb=Marshal.SizeOf(typeof(StartupEx));startup.attributes=attributes;startup.info.flags=0x100; // Explicit null std handles prevent redirected parent handle inheritance.
                string cmd=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe");
                Require(CreateProcess(cmd,new StringBuilder("\""+cmd+"\" /d"),IntPtr.Zero,IntPtr.Zero,false,0x80000|4,IntPtr.Zero,directory,ref startup,out pi));
                processId=pi.pid;process=Process.GetProcessById(pi.pid);lifetime=new ProcessLifetime(process);
                input=new FileStream(new SafeFileHandle(inWrite,true),FileAccess.Write,4096,false);inWrite=IntPtr.Zero;
                var writer=input;
                Task.Run(delegate{try{foreach(var bytes in inputQueue.GetConsumingEnumerable()){writer.Write(bytes,0,bytes.Length);writer.Flush();}}catch(IOException){}catch(ObjectDisposedException){} });
                var output=new FileStream(new SafeFileHandle(outRead,true),FileAccess.Read,4096,false);outRead=IntPtr.Zero;
                // Drain before resuming: ConPTY can block startup waiting for its output consumer.
                Task.Run(delegate
                {
                    try{using(output){var decoder=new UTF8Encoding(false).GetDecoder();var bytes=new byte[8192];var chars=new char[8192];int count;while((count=output.Read(bytes,0,bytes.Length))>0){int length=decoder.GetChars(bytes,0,count,chars,0,false);var handler=Output;if(handler!=null&&length>0)handler(new string(chars,0,length));}}}
                    catch(IOException){}catch(ObjectDisposedException){}
                    finally{var handler=Exited;if(handler!=null)handler();}
                });
                // Natural shell exit must close HPCON so the output reader receives EOF.
                process.Exited+=delegate{Dispose();};process.EnableRaisingEvents=true;
                ResumeThread(pi.thread);
            }
            catch{Dispose();throw;}
            finally
            {
                foreach(var h in new[]{inRead,inWrite,outRead,outWrite,pi.process,pi.thread})if(h!=IntPtr.Zero)CloseHandle(h);
                if(attributes!=IntPtr.Zero){DeleteProcThreadAttributeList(attributes);Marshal.FreeHGlobal(attributes);}
            }
        }
        public void Send(string text){SendBytes(Encoding.UTF8.GetBytes(text));}
        public void SendBytes(byte[] bytes)
        {
            if(disposed!=0||input==null)return;
            if(bytes.Length>1048576)throw new InvalidOperationException("一次输入过长，请分段粘贴。");
            try{if(!inputQueue.TryAdd(bytes))throw new InvalidOperationException("终端输入繁忙，请稍后重试。");}catch(ObjectDisposedException){}catch(InvalidOperationException){if(disposed==0)throw;}
        }
        public void Resize(int columns,int rows){if(console!=IntPtr.Zero&&disposed==0)ResizePseudoConsole(console,new Coord(Math.Max(2,Math.Min(1000,columns)),Math.Max(1,Math.Min(500,rows))));}
        public void Dispose()
        {
            if(Interlocked.Exchange(ref disposed,1)!=0)return;
            if(lifetime!=null)lifetime.Dispose();
            inputQueue.CompleteAdding();if(input!=null){input.Dispose();input=null;}
            IntPtr handle=console;console=IntPtr.Zero;
            // Keep the output reader alive while ConPTY completes its shutdown handshake.
            if(handle!=IntPtr.Zero)Task.Run(()=>ClosePseudoConsole(handle));
            if(process!=null)process.Dispose();
        }
    }
}
