using System;
using System.Collections.Generic;
using System.IO;

namespace Qingye.Core
{
    public static class LegacyAgentArchives
    {
        public static void Clear(string applicationDirectory)
        {
            string root = Path.GetFullPath(applicationDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var targets = new List<string>();
            foreach (string name in new[] { "agent-history", "agent-data" })
            {
                string target = Path.GetFullPath(Path.Combine(root, name));
                if (Path.GetDirectoryName(target) != root || !FileService.IsInsideRoot(Path.GetPathRoot(target), Path.Combine(target, ".cleanup-check")))
                    throw new IOException("旧存档目录包含链接或不在程序目录内。");
                if (Directory.Exists(target)) targets.Add(target);
            }
            // Validate every target before any deletion. A linked child must
            // never turn a user-approved archive cleanup into an outside delete.
            int visited = 0;
            foreach (string target in targets) ValidateTree(target, ref visited);
            foreach (string target in targets) Directory.Delete(target, true);
        }

        static void ValidateTree(string directory, ref int visited)
        {
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("旧存档包含链接，未执行清理。");
                foreach (string child in Directory.EnumerateFileSystemEntries(current))
                {
                    if (++visited > 100000) throw new IOException("旧存档项目过多，未执行自动清理。");
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("旧存档包含链接，未执行清理。");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
                }
            }
        }
    }
}
