using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Qingye.Core
{
    // Every agent path goes through this policy. A workspace bounds the agent to
    // one directory tree; a single open file bounds it to that file only.
    public sealed class AgentWorkspace
    {
        public string Root { get; private set; }
        public string CurrentFile { get; private set; }
        public bool IsSingleFile { get { return String.IsNullOrEmpty(Root); } }
        public bool CanCreate { get { return !IsSingleFile; } }
        readonly string[] protectedRoots;

        public AgentWorkspace(string root, string currentFile, params string[] protectedRoots)
        {
            Root = String.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            CurrentFile = String.IsNullOrWhiteSpace(currentFile) ? null : Path.GetFullPath(currentFile);
            this.protectedRoots = (protectedRoots ?? new string[0]).Where(path => !String.IsNullOrWhiteSpace(path)).Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)).ToArray();
        }

        public AgentWorkspace Protect(params string[] additionalRoots)
        {
            return new AgentWorkspace(Root, CurrentFile, protectedRoots.Concat(additionalRoots ?? new string[0]).ToArray());
        }

        public string Resolve(string path, bool forWrite)
        {
            string value = (path ?? "").Trim();
            if (value.Length == 0)
            {
                if (forWrite) throw new AgentException("写入时必须提供路径。");
                string target = Root ?? CurrentFile;
                if (target != null) RequireDirectPath(target);
                if (target != null && IsProtected(target)) throw new AgentException("该路径受保护。");
                return target;
            }
            if (IsSingleFile && (value == "." || value == "./"))
            {
                if (String.IsNullOrEmpty(CurrentFile)) throw new AgentException("当前没有可操作的文件。");
                RequireDirectPath(CurrentFile);
                if (IsProtected(CurrentFile)) throw new AgentException("该文件受保护，Agent 不能读取或修改。");
                return CurrentFile;
            }
            string full = Path.IsPathRooted(value)
                ? Path.GetFullPath(value)
                : Path.GetFullPath(Path.Combine(Root ?? Path.GetDirectoryName(CurrentFile ?? ""), value));
            if (IsSingleFile)
            {
                if (String.IsNullOrEmpty(CurrentFile)) throw new AgentException("当前没有可操作的文件。");
                if (!String.Equals(full, CurrentFile, StringComparison.OrdinalIgnoreCase))
                    throw new AgentException("只打开了一个文件，Agent 只能操作当前文件。");
            }
            else
            {
                if (!String.Equals(full, Root, StringComparison.OrdinalIgnoreCase) && !FileService.IsInsideRoot(Root, full))
                    throw new AgentException("路径不在当前工作区内：" + Relative(full));
                if (forWrite && !File.Exists(full) && !Directory.Exists(Path.GetDirectoryName(full)))
                    throw new AgentException("目标目录不存在，Agent 不会自动创建目录。");
            }
            RequireDirectPath(full);
            if (IsProtected(full)) throw new AgentException("该文件受保护，Agent 不能读取或修改：" + Relative(full));
            return full;
        }

        static void RequireDirectPath(string path)
        {
            string check = Directory.Exists(path) ? Path.Combine(path, ".qingye-scope-check") : path;
            if (!FileService.IsInsideRoot(Path.GetPathRoot(check), check))
                throw new AgentException("Agent 不访问文件或目录链接，请直接打开原路径。");
        }

        public string Relative(string full)
        {
            if (String.IsNullOrEmpty(full)) return "";
            if (IsSingleFile) return Path.GetFileName(full);
            string prefix = Root + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(prefix.Length).Replace('\\', '/')
                : Path.GetFileName(full);
        }

        public bool IsProtected(string full)
        {
            if (!String.IsNullOrEmpty(full)) {
                string resolved = Path.GetFullPath(full);
                if (protectedRoots.Any(root => String.Equals(root, resolved, StringComparison.OrdinalIgnoreCase) || resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return true;
            }
            string name = Path.GetFileName(full ?? "").ToLowerInvariant();
            if (name == "模型.txt" || name == "preferences.xml" || name == ".env" || name.StartsWith(".env.", StringComparison.Ordinal))
                return true;
            if (name == "id_rsa" || name == "id_ed25519" || name == "credentials.json" || name.StartsWith("secrets.", StringComparison.Ordinal))
                return true;
            string extension = Path.GetExtension(name);
            if (extension == ".pfx" || extension == ".p12" || extension == ".key" || extension == ".pem") return true;
            string[] parts = (full ?? "").Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Any(part => String.Equals(part, ".git", StringComparison.OrdinalIgnoreCase) || String.Equals(part, "agent-history", StringComparison.OrdinalIgnoreCase) || String.Equals(part, "agent-data", StringComparison.OrdinalIgnoreCase) || String.Equals(part, "harness-data", StringComparison.OrdinalIgnoreCase));
        }

        public static bool ShouldSkipDirectory(string name)
        {
            return String.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "agent-history", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "agent-data", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "harness-data", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, ".svn", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, ".venv", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "obj", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "packages", StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class AgentException : Exception
    {
        public AgentException(string message) : base(message) { }
    }
}
