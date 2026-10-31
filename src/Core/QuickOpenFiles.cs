using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Qingye.Core
{
    // A disposable, on-demand scan: no index or file contents are persisted.
    public static class QuickOpenFiles
    {
        public static IList<string> Collect(string root, int limit, CancellationToken token)
        {
            var files = new List<string>();
            if (String.IsNullOrEmpty(root) || limit <= 0 || !Directory.Exists(root)) return files;
            Walk(Path.GetFullPath(root), files, limit, 0, token);
            return files;
        }

        static void Walk(string directory, List<string> files, int limit, int depth, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 5 || files.Count >= limit || IsLink(directory)) return;
            try
            {
                // Files in the current folder are useful before deeply nested entries.
                foreach (string path in Directory.EnumerateFiles(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsLink(path)) files.Add(path);
                    if (files.Count >= limit) return;
                }
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (!AgentWorkspace.ShouldSkipDirectory(Path.GetFileName(child))) Walk(child, files, limit, depth + 1, token);
                    if (files.Count >= limit) return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static bool IsLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        public static bool Matches(string name, string path, string query)
        {
            if (String.IsNullOrWhiteSpace(query)) return true;
            query = query.Trim();
            if ((path ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            int position = 0;
            foreach (char character in name ?? "")
                if (Char.ToUpperInvariant(character) == Char.ToUpperInvariant(query[position]) && ++position == query.Length) return true;
            return false;
        }
    }
}
