using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Qingye.Core
{
    public sealed class SearchHit
    {
        public string Path;
        public string RelativePath;
        public string LineText;
        public int Line;
        public int Column;
    }

    // On-demand workspace text search. No index, no cached results, bounded work.
    public static class WorkspaceSearch
    {
        public static IList<SearchHit> Search(string root, string query, int maxFiles, int maxResults, CancellationToken token)
        {
            var hits = new List<SearchHit>();
            if (String.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || String.IsNullOrWhiteSpace(query)) return hits;
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            var files = new List<string>();
            Collect(normalizedRoot, files, Math.Max(1, maxFiles), 0, token);
            foreach (string file in files)
            {
                token.ThrowIfCancellationRequested();
                if (hits.Count >= maxResults) break;
                try
                {
                    if (new FileInfo(file).Length > 1024 * 1024) continue;
                    byte[] bytes;
                    using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        bytes = new byte[stream.Length];
                        int read = 0;
                        while (read < bytes.Length) { int part = stream.Read(bytes, read, bytes.Length - read); if (part == 0) break; read += part; }
                        if (read != bytes.Length) Array.Resize(ref bytes, read);
                    }
                    if (bytes.Take(8192).Any(value => value == 0)) continue;
                    string text;
                    try { text = new UTF8Encoding(false, true).GetString(bytes); }
                    catch { try { text = Encoding.GetEncoding("gb18030").GetString(bytes); } catch { continue; } }
                    string[] lines = text.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                    for (int index = 0; index < lines.Length && hits.Count < maxResults; index++)
                    {
                        int column = lines[index].IndexOf(query, StringComparison.OrdinalIgnoreCase);
                        if (column < 0) continue;
                        string snippet = lines[index].Trim();
                        if (snippet.Length > 240) snippet = snippet.Substring(0, 240) + "…";
                        hits.Add(new SearchHit
                        {
                            Path = file,
                            RelativePath = file.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? file.Substring(normalizedRoot.Length + 1) : Path.GetFileName(file),
                            Line = index + 1,
                            Column = column + 1,
                            LineText = snippet
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            return hits;
        }

        static void Collect(string directory, List<string> files, int maxFiles, int depth, CancellationToken token)
        {
            if (files.Count >= maxFiles || depth > 8) return;
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(directory).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                {
                    if (files.Count >= maxFiles) return;
                    string name = Path.GetFileName(child);
                    if (Directory.Exists(child))
                    {
                        if (Skip(name) || IsReparse(child)) continue;
                        Collect(child, files, maxFiles, depth + 1, token);
                    }
                    else if (!IsReparse(child)) files.Add(child);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        static bool Skip(string name)
        {
            return String.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, ".svn", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, ".venv", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "obj", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "packages", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsReparse(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }
    }
}
