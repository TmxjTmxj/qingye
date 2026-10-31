using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
using Qingye.Core;

namespace Qingye.UI
{
    internal static class HarnessBrowserCleanup
    {
        internal static async Task CloseAsync(WebView2 view, Grid host, string root, string directory)
        {
            var identities = new Dictionary<int,DateTime>();
            if (view != null) {
                try {
                    if (view.CoreWebView2 != null) foreach (int id in view.CoreWebView2.Environment.GetProcessInfos().Select(info => info.ProcessId)) {
                        try { using (var process = Process.GetProcessById(id)) if (process.ProcessName == "msedgewebview2") identities[id] = process.StartTime.ToUniversalTime(); } catch (ArgumentException) { }
                    }
                } catch (InvalidOperationException) { }
                view.Dispose();
                host.Children.Remove(view);
            }
            if (directory == null) return;
            string target = Path.GetFullPath(directory);
            ValidateRunDirectory(root, target);
            await Task.Run(delegate {
                foreach (var entry in identities) try {
                    using (var process = Process.GetProcessById(entry.Key)) {
                        if (!process.WaitForExit(250) && process.ProcessName == "msedgewebview2" && process.StartTime.ToUniversalTime() == entry.Value) { process.Kill(); process.WaitForExit(1000); }
                    }
                } catch (ArgumentException) { }
                catch (InvalidOperationException) { /* An owned browser process finished between checks. */ }
                for (int attempt = 0; attempt < 20; attempt++) {
                    try {
                        if (Directory.Exists(target)) {
                            AssertNoLinks(target);
                            Directory.Delete(target, true);
                        }
                        return;
                    } catch (IOException) { if (attempt == 19) throw; System.Threading.Thread.Sleep(100); }
                    catch (UnauthorizedAccessException) { if (attempt == 19) throw; System.Threading.Thread.Sleep(100); }
                }
            });
        }

        internal static void ValidateRunDirectory(string root, string directory)
        {
            string permitted = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            string target = Path.GetFullPath(directory);
            Guid identity;
            if (!String.Equals(Path.GetDirectoryName(target), permitted, StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(Path.GetFileName(target), out identity) || !FileService.IsInsideRoot(Path.GetPathRoot(target), target))
                throw new IOException("临时 Harness 目录不在预期范围内或包含目录链接。");
            if (Directory.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new IOException("临时 Harness 目录不可是链接。");
        }

        static void AssertNoLinks(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("临时 Harness 目录包含链接，拒绝递归清理。");
            foreach (string child in Directory.EnumerateFileSystemEntries(directory)) {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("临时 Harness 目录包含链接，拒绝递归清理。");
                if ((attributes & FileAttributes.Directory) != 0) AssertNoLinks(child);
            }
        }
    }
}
