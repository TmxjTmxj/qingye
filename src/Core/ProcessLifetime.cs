using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Qingye.Core
{
    // Windows closes the job with the editor, including after an abnormal exit.
    internal sealed class ProcessLifetime : IDisposable
    {
        IntPtr job;
        [StructLayout(LayoutKind.Sequential)] struct BasicLimits
        {
            internal long ProcessTime, JobTime;
            internal uint Flags;
            internal UIntPtr Minimum, Maximum;
            internal uint ActiveProcesses;
            internal UIntPtr Affinity;
            internal uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] struct IoCounters
        {
            internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        }
        [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits
        {
            internal BasicLimits Basic;
            internal IoCounters Io;
            internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int type, ref ExtendedLimits limits, uint size);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

        internal ProcessLifetime(Process process)
        {
            job = CreateJobObject(IntPtr.Zero, null);
            var limits = new ExtendedLimits();
            limits.Basic.Flags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (job == IntPtr.Zero || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(limits)) || !AssignProcessToJobObject(job, process.Handle)) {
                Dispose();
                process.Kill();
                throw new InvalidOperationException("无法建立 终端 进程生命周期。请重试或检查系统策略。");
            }
        }
        public void Dispose()
        {
            if (job == IntPtr.Zero) return;
            CloseHandle(job);
            job = IntPtr.Zero;
        }
    }
}
