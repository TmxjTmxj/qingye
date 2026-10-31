using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Qingye.Core
{
    public sealed class WriteProposal
    {
        public string Path;
        public string RelativePath;
        public string OldText;
        public string NewText;
        public bool IsNew;
        public string OriginalVersion;
    }

    public sealed class AgentToolResult
    {
        public string Json;
        public WriteProposal Write;
    }

    // Tools are deliberately small: list, read, search and a write proposal.
    // There is no shell, no deletion, no rename and no network access.
    public sealed class AgentTools
    {
        public const int MaxReadCharacters = 256 * 1024;
        public const int MaxWriteCharacters = 512 * 1024;
        public const int MaxListEntries = 200;
        public const int MaxSearchFiles = 200;
        public const int MaxSearchResults = 50;
        public const long MaxSearchBytes = 1024 * 1024;

        readonly AgentWorkspace workspace;
        readonly Func<string, string> openText;
        readonly Func<string, bool> openDirty;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();

        public AgentTools(AgentWorkspace workspace, Func<string, string> openText, Func<string, bool> openDirty)
        {
            this.workspace = workspace;
            this.openText = openText;
            this.openDirty = openDirty;
        }

        public AgentToolResult Execute(string name, Dictionary<string, object> args)
        {
            if (name == "write_file") return new AgentToolResult { Write = ProposeWrite(args) };
            if (name == "create_file") return new AgentToolResult { Write = ProposeCreate(args) };
            if (name == "edit_file") return new AgentToolResult { Write = ProposeEdit(args) };
            if (name == "list_files") return new AgentToolResult { Json = ListFiles(args) };
            if (name == "read_file") return new AgentToolResult { Json = ReadFile(args) };
            if (name == "search_files") return new AgentToolResult { Json = SearchFiles(args) };
            throw new AgentException("未知工具：" + name);
        }

        public WriteProposal ProposeWrite(Dictionary<string, object> args)
        {
            string path = GetString(args, "path");
            string content = GetString(args, "content");
            if (String.IsNullOrEmpty(path)) throw new AgentException("write_file 需要 path。");
            if (content.Length > MaxWriteCharacters) throw new AgentException("写入内容超过 512 KiB，Agent 不会一次写这么大的文件。");
            string full = workspace.Resolve(path, true);
            bool exists = File.Exists(full);
            string version = null;
            if (!exists && !workspace.CanCreate) throw new AgentException("只打开单个文件时禁止新建文件。");
            if (exists)
            {
                var existing = FileService.Read(full);
                if (existing.Kind != DocumentKind.Text) throw new AgentException("Agent 只修改普通文本文件；大文件分段、Office/PDF/二进制只读。");
                version = existing.Version;
            }
            if (openDirty != null && openDirty(full)) throw new AgentException("该文件有未保存修改，请先保存后再让 Agent 修改。");
            string old = exists ? (openText == null ? null : openText(full)) : "";
            if (old == null && exists) old = ReadText(full);
            return new WriteProposal { Path = full, RelativePath = workspace.Relative(full), OldText = old ?? "", NewText = content, IsNew = !exists, OriginalVersion = version };
        }

        // create_file is the explicit "new file" proposal: it never overwrites and
        // stays unavailable while only a single file is open.
        public WriteProposal ProposeCreate(Dictionary<string, object> args)
        {
            string path = GetString(args, "path");
            string content = GetString(args, "content");
            if (String.IsNullOrEmpty(path)) throw new AgentException("create_file 需要 path。");
            if (content.Length > MaxWriteCharacters) throw new AgentException("写入内容超过 512 KiB，Agent 不会一次写这么大的文件。");
            string full = workspace.Resolve(path, true);
            if (!workspace.CanCreate) throw new AgentException("只打开单个文件时禁止新建文件。");
            if (File.Exists(full)) throw new AgentException("文件已存在：" + workspace.Relative(full) + "。修改现有文件请使用 edit_file 或 write_file。");
            if (Directory.Exists(full)) throw new AgentException("同名目录已存在：" + workspace.Relative(full) + "。");
            return new WriteProposal { Path = full, RelativePath = workspace.Relative(full), OldText = "", NewText = content, IsNew = true, OriginalVersion = null };
        }

        // edit_file matches exact text (never a fuzzy or line-based guess), so the
        // model sees a clear error instead of a silent wrong-place edit.
        public WriteProposal ProposeEdit(Dictionary<string, object> args)
        {
            string path = GetString(args, "path");
            string oldText = GetString(args, "old_text");
            string newText = GetString(args, "new_text");
            bool replaceAll = GetBool(args, "replace_all");
            if (String.IsNullOrEmpty(path)) throw new AgentException("edit_file 需要 path。");
            if (String.IsNullOrEmpty(oldText)) throw new AgentException("edit_file 的 old_text 不能为空；要整体重写请使用 write_file。");
            if (oldText == newText) throw new AgentException("edit_file 的 old_text 与 new_text 相同，没有可修改的内容。");
            if (newText.Length > MaxWriteCharacters) throw new AgentException("修改后的内容超过 512 KiB，Agent 不会一次写这么大的文件。");
            string full = workspace.Resolve(path, true);
            if (!File.Exists(full)) throw new AgentException("文件不存在：" + workspace.Relative(full) + "。新建文件请使用 create_file 或 write_file。");
            var existing = FileService.Read(full);
            if (existing.Kind != DocumentKind.Text) throw new AgentException("Agent 只修改普通文本文件；大文件分段、Office/PDF/二进制只读。");
            if (openDirty != null && openDirty(full)) throw new AgentException("该文件有未保存修改，请先保存后再让 Agent 修改。");
            string current = openText == null ? null : openText(full);
            if (current == null) current = ReadText(full);
            int count = CountOccurrences(current, oldText);
            if (count > 1 && !replaceAll) throw new AgentException(AmbiguousMatchMessage(count));
            string updated = count > 0
                ? (replaceAll ? current.Replace(oldText, newText) : ReplaceFirst(current, oldText, newText))
                : ReplaceNormalizedNewLines(current, oldText, newText, replaceAll);
            if (updated.Length > MaxWriteCharacters) throw new AgentException("修改后的文件超过 512 KiB，Agent 不会一次写这么大的文件。");
            return new WriteProposal { Path = full, RelativePath = workspace.Relative(full), OldText = current, NewText = updated, IsNew = false, OriginalVersion = existing.Version };
        }

        static string NotFoundMatchMessage()
        {
            return "edit_file 未找到 old_text 的精确匹配。请先用 read_file 核对原文（注意空格、缩进与换行符），再重试。";
        }

        static string AmbiguousMatchMessage(int count)
        {
            return "edit_file 的 old_text 在文件中出现了 " + count + " 次，无法确定修改位置。请提供更长的唯一上下文，或设置 replace_all=true 全部替换。";
        }

        // read_file returns LF-normalized text, so an edit copied from it must still
        // match files saved with Windows (CRLF) newlines; the replacement keeps the
        // file's own newline style instead of silently converting the whole file.
        static string ReplaceNormalizedNewLines(string current, string oldText, string newText, bool replaceAll)
        {
            int[] map;
            string normalizedCurrent = NormalizeNewLines(current, out map);
            string normalizedOld = NormalizeNewLines(oldText);
            int count = CountOccurrences(normalizedCurrent, normalizedOld);
            if (count == 0) throw new AgentException(NotFoundMatchMessage());
            if (count > 1 && !replaceAll) throw new AgentException(AmbiguousMatchMessage(count));
            string newline = NewLineOf(current);
            string replacement = newline == "\n" ? NormalizeNewLines(newText) : NormalizeNewLines(newText).Replace("\n", newline);
            var builder = new StringBuilder(current.Length + replacement.Length);
            int position = 0;
            int search = 0;
            while (search <= normalizedCurrent.Length)
            {
                int index = normalizedCurrent.IndexOf(normalizedOld, search, StringComparison.Ordinal);
                if (index < 0) break;
                int start = map[index];
                int end = index + normalizedOld.Length < normalizedCurrent.Length ? map[index + normalizedOld.Length] : current.Length;
                builder.Append(current, position, start - position);
                builder.Append(replacement);
                position = end;
                if (!replaceAll) break;
                search = index + normalizedOld.Length;
            }
            builder.Append(current, position, current.Length - position);
            return builder.ToString();
        }

        static string NormalizeNewLines(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        }

        static string NormalizeNewLines(string text, out int[] map)
        {
            text = text ?? "";
            var builder = new StringBuilder(text.Length);
            var positions = new List<int>(text.Length);
            for (int index = 0; index < text.Length; index++)
            {
                char value = text[index];
                if (value == '\r')
                {
                    positions.Add(index);
                    if (index + 1 < text.Length && text[index + 1] == '\n') index++;
                    builder.Append('\n');
                    continue;
                }
                positions.Add(index);
                builder.Append(value);
            }
            map = positions.ToArray();
            return builder.ToString();
        }

        static string NewLineOf(string text)
        {
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] == '\r') return index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r";
                if (text[index] == '\n') return "\n";
            }
            return "\n";
        }

        static int CountOccurrences(string text, string value)
        {
            if (String.IsNullOrEmpty(value)) return 0;
            int count = 0;
            for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal)) count++;
            return count;
        }

        static string ReplaceFirst(string text, string oldValue, string newValue)
        {
            int index = text.IndexOf(oldValue, StringComparison.Ordinal);
            return index < 0 ? text : text.Substring(0, index) + newValue + text.Substring(index + oldValue.Length);
        }

        public void ApplyWrite(WriteProposal proposal, Action<Action> publishGuard = null)
        {
            string full = workspace.Resolve(proposal.Path, true);
            if (openDirty != null && openDirty(full)) throw new AgentException("确认期间文件出现了未保存修改，已取消写入；请先保存后重新提出修改。");
            if (proposal.IsNew == File.Exists(full)) throw new AgentException("确认期间文件的存在状态发生变化，已取消写入。");
            if (proposal.IsNew && !workspace.CanCreate) throw new AgentException("只打开单个文件时禁止新建文件。");
            string directory = Path.GetDirectoryName(full);
            if (String.IsNullOrEmpty(directory) || !Directory.Exists(directory)) throw new AgentException("目标目录不存在。");
            if (!proposal.IsNew && (FileService.Read(full).Text != proposal.OldText || proposal.OriginalVersion != null && FileService.HashFile(full) != proposal.OriginalVersion))
                throw new AgentException("文件在确认前发生变化，已取消写入。");
            string encodingName = "utf-8";
            bool bom = false;
            if (File.Exists(full))
            {
                try
                {
                    var existing = FileService.Read(full);
                    if (existing.Kind == DocumentKind.Text) { encodingName = existing.EncodingName; bom = existing.HasBom; }
                }
                catch { }
            }
            var encoding = FileService.GetEncoding(encodingName, bom);
            byte[] body = encoding.GetBytes(proposal.NewText ?? "");
            byte[] prefix = bom ? encoding.GetPreamble() : new byte[0];
            string temp = Path.Combine(directory, ".qingye-agent-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(prefix, 0, prefix.Length);
                    output.Write(body, 0, body.Length);
                    output.Flush(true);
                }
                if (openDirty != null && openDirty(full)) throw new AgentException("写入前出现未保存修改，已取消写入。");
                Action publish = delegate {
                    if (proposal.IsNew) File.Move(temp, full);
                    else
                    {
                        if (!File.Exists(full) || proposal.OriginalVersion != null && FileService.HashFile(full) != proposal.OriginalVersion) throw new AgentException("写入前原文件发生变化，已取消写入。");
                        File.Replace(temp, full, null);
                    }
                };
                if (publishGuard == null) publish();
                else publishGuard(publish);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        string ListFiles(Dictionary<string, object> args)
        {
            string path = GetString(args, "path");
            int maxDepth = ListDepth(args, GetBool(args, "recursive"));
            string full = workspace.Resolve(String.IsNullOrEmpty(path) ? "." : path, false);
            var entries = new List<string>();
            bool truncated = false;
            if (File.Exists(full))
            {
                entries.Add(workspace.Relative(full));
            }
            else if (Directory.Exists(full))
            {
                Walk(full, entries, ref truncated, maxDepth, 0);
            }
            else throw new AgentException("路径不存在：" + workspace.Relative(full));
            return json.Serialize(new Dictionary<string, object> { { "root", workspace.IsSingleFile ? workspace.Relative(workspace.CurrentFile) : "." }, { "depth", Math.Min(maxDepth, 6) }, { "entries", entries }, { "truncated", truncated } });
        }

        // depth is the number of directory levels to include: 1 lists only the
        // requested directory, 6 descends at most five levels.
        static int ListDepth(Dictionary<string, object> args, bool recursive)
        {
            int depth = GetInt(args, "depth", 0);
            if (depth == 0) return recursive ? 7 : 1;
            if (depth < 1 || depth > 6) throw new AgentException("list_files 的 depth 必须是 1 到 6 之间的整数。");
            return depth;
        }

        void Walk(string directory, List<string> entries, ref bool truncated, int maxDepth, int level)
        {
            IEnumerable<string> children;
            try { children = Directory.EnumerateFileSystemEntries(directory).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(); }
            catch (Exception error) { entries.Add("! " + workspace.Relative(directory) + " (" + error.Message + ")"); return; }
            foreach (string child in children)
            {
                if (entries.Count >= MaxListEntries) { truncated = true; return; }
                string name = Path.GetFileName(child);
                bool directoryChild = Directory.Exists(child);
                if (directoryChild && AgentWorkspace.ShouldSkipDirectory(name)) continue;
                if (directoryChild && IsReparse(child)) continue;
                if (workspace.IsProtected(child)) continue;
                entries.Add(workspace.Relative(child) + (directoryChild ? "/" : ""));
                if (directoryChild && level + 1 < maxDepth) Walk(child, entries, ref truncated, maxDepth, level + 1);
            }
        }

        string ReadFile(Dictionary<string, object> args)
        {
            string path = GetString(args, "path");
            if (String.IsNullOrEmpty(path)) throw new AgentException("read_file 需要 path。");
            string full = workspace.Resolve(path, false);
            if (Directory.Exists(full)) throw new AgentException("这是目录，请使用 list_files。");
            string text = ReadText(full);
            bool truncated = text.Length > MaxReadCharacters;
            if (truncated) text = text.Substring(0, MaxReadCharacters);
            int start = Math.Max(1, GetInt(args, "start_line", 1));
            int count = Math.Max(1, Math.Min(5000, GetInt(args, "max_lines", 2000)));
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (start > lines.Length) start = 1;
            string body = String.Join("\n", lines.Skip(start - 1).Take(count));
            if (start - 1 + count < lines.Length) truncated = true;
            return json.Serialize(new Dictionary<string, object> {
                { "path", workspace.Relative(full) }, { "start_line", start }, { "content", body }, { "truncated", truncated }
            });
        }

        string SearchFiles(Dictionary<string, object> args)
        {
            string query = GetString(args, "query");
            if (String.IsNullOrEmpty(query)) throw new AgentException("search_files 需要 query。");
            bool useRegex = GetBool(args, "regex");
            int context = GetInt(args, "context", 0);
            if (context < 0 || context > 10) throw new AgentException("search_files 的 context 必须是 0 到 10 之间的整数。");
            Regex pattern = null;
            if (useRegex)
            {
                if (query.Length > 500) throw new AgentException("正则表达式过长，请控制在 500 个字符以内。");
                try { pattern = new Regex(query, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)); }
                catch (ArgumentException error) { throw new AgentException("正则表达式无效：" + error.Message); }
            }
            string path = GetString(args, "path");
            string full = workspace.Resolve(String.IsNullOrEmpty(path) ? "." : path, false);
            var files = new List<string>();
            if (File.Exists(full)) files.Add(full);
            else if (Directory.Exists(full)) CollectFiles(full, files, 0);
            var matches = new List<object>();
            bool timedOut = false;
            foreach (string file in files)
            {
                if (matches.Count >= MaxSearchResults || timedOut) break;
                try
                {
                    if (workspace.IsProtected(file) || new FileInfo(file).Length > MaxSearchBytes) continue;
                    string text = ReadText(file);
                    string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                    for (int index = 0; index < lines.Length && matches.Count < MaxSearchResults; index++)
                    {
                        bool hit;
                        if (pattern == null) hit = lines[index].IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                        else
                        {
                            try { hit = pattern.IsMatch(lines[index]); }
                            catch (RegexMatchTimeoutException) { timedOut = true; break; }
                        }
                        if (!hit) continue;
                        var entry = new Dictionary<string, object> { { "path", workspace.Relative(file) }, { "line", index + 1 }, { "text", ClipSnippet(lines[index]) } };
                        if (context > 0)
                        {
                            var before = new List<string>();
                            for (int offset = Math.Max(0, index - context); offset < index; offset++) before.Add(ClipSnippet(lines[offset]));
                            var after = new List<string>();
                            for (int offset = index + 1; offset <= Math.Min(lines.Length - 1, index + context); offset++) after.Add(ClipSnippet(lines[offset]));
                            entry["before"] = before;
                            entry["after"] = after;
                        }
                        matches.Add(entry);
                    }
                }
                catch { }
            }
            if (timedOut) throw new AgentException("正则表达式匹配超时，请简化后重试。");
            return json.Serialize(matches);
        }

        static string ClipSnippet(string line)
        {
            string snippet = (line ?? "").Trim();
            return snippet.Length <= 240 ? snippet : snippet.Substring(0, 240) + "…";
        }

        void CollectFiles(string directory, List<string> files, int depth)
        {
            if (files.Count >= MaxSearchFiles || depth > 6) return;
            try
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (files.Count >= MaxSearchFiles) return;
                    string name = Path.GetFileName(child);
                    if (Directory.Exists(child))
                    {
                        if (AgentWorkspace.ShouldSkipDirectory(name) || IsReparse(child) || workspace.IsProtected(child)) continue;
                        CollectFiles(child, files, depth + 1);
                    }
                    else if (!workspace.IsProtected(child)) files.Add(child);
                }
            }
            catch { }
        }

        string ReadText(string full)
        {
            if (openText != null)
            {
                string open = openText(full);
                if (open != null) return open;
            }
            if (!File.Exists(full)) throw new AgentException("文件不存在：" + workspace.Relative(full));
            if (new FileInfo(full).Length > 2 * 1024 * 1024) throw new AgentException("文件超过 2 MiB，Agent 不读取。");
            var doc = FileService.Read(full);
            if (doc.Kind != DocumentKind.Text && doc.Kind != DocumentKind.PagedText) throw new AgentException("不是可读文本文件：" + workspace.Relative(full));
            return doc.Text;
        }

        static bool IsReparse(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }

        static string GetString(Dictionary<string, object> args, string name)
        {
            object value;
            return args != null && args.TryGetValue(name, out value) && value != null ? Convert.ToString(value) : "";
        }

        static bool GetBool(Dictionary<string, object> args, string name)
        {
            object value;
            if (args == null || !args.TryGetValue(name, out value) || value == null) return false;
            bool result;
            return Boolean.TryParse(Convert.ToString(value), out result) && result;
        }

        static int GetInt(Dictionary<string, object> args, string name, int fallback)
        {
            object value;
            int result;
            return args != null && args.TryGetValue(name, out value) && value != null && Int32.TryParse(Convert.ToString(value), out result) ? result : fallback;
        }

        public static string ToolDefinitions()
        {
            var tools = new List<object>();
            tools.Add(Tool("list_files", "列出工作区或当前目录内的文件与目录。depth 1-6 指定递归层数（1 只列当前目录）；recursive 等同于深度递归。",
                new Dictionary<string, object> { { "path", StringSchema("相对路径；省略表示工作区根目录") }, { "recursive", BoolSchema("是否递归到子目录") }, { "depth", IntegerSchema("递归层数 1-6，默认沿用 recursive 语义") } }, new string[0]));
            tools.Add(Tool("read_file", "读取一个文本文件。返回内容可能被截断；不要读取密钥或配置文件。",
                new Dictionary<string, object> { { "path", StringSchema("相对路径") }, { "start_line", IntegerSchema("起始行，默认 1") }, { "max_lines", IntegerSchema("最多返回行数，默认 2000") } }, new[] { "path" }));
            tools.Add(Tool("search_files", "在当前工作区或文件内搜索文本：默认不区分大小写，regex=true 时按正则匹配，context 返回上下文行。",
                new Dictionary<string, object> { { "query", StringSchema("要搜索的文本或正则表达式") }, { "path", StringSchema("相对路径；省略表示根目录") }, { "regex", BoolSchema("是否把 query 当作正则表达式") }, { "context", IntegerSchema("匹配行前后各返回的上下文行数 0-10") } }, new[] { "query" }));
            tools.Add(Tool("write_file", "提出完整文件内容（新建或整体覆盖）。用户会先看到 diff 并确认；单文件模式禁止新建。修改已有文件请优先使用 edit_file。",
                new Dictionary<string, object> { { "path", StringSchema("相对路径") }, { "content", StringSchema("完整的新文件内容") } }, new[] { "path", "content" }));
            tools.Add(Tool("edit_file", "精确修改已有文本文件：old_text 必须在文件中唯一匹配，replace_all=true 时替换全部。用户确认后才写入，其余内容保持不变。",
                new Dictionary<string, object> { { "path", StringSchema("相对路径") }, { "old_text", StringSchema("要替换的原文，必须唯一精确匹配") }, { "new_text", StringSchema("替换后的文本；可为空表示删除") }, { "replace_all", BoolSchema("是否替换全部匹配，默认 false") } }, new[] { "path", "old_text", "new_text" }));
            tools.Add(Tool("create_file", "新建文件（仅工作区模式）：目标不能已存在。用户确认后才写入；单文件模式不可用。",
                new Dictionary<string, object> { { "path", StringSchema("相对路径") }, { "content", StringSchema("完整的新文件内容") } }, new[] { "path", "content" }));
            return new JavaScriptSerializer().Serialize(tools);
        }

        static Dictionary<string, object> Tool(string name, string description, Dictionary<string, object> properties, string[] required)
        {
            return new Dictionary<string, object> {
                { "type", "function" },
                { "function", new Dictionary<string, object> {
                    { "name", name }, { "description", description },
                    { "parameters", new Dictionary<string, object> {
                        { "type", "object" }, { "properties", properties }, { "required", required }
                    } }
                } }
            };
        }

        static Dictionary<string, object> StringSchema(string description) { return new Dictionary<string, object> { { "type", "string" }, { "description", description } }; }
        static Dictionary<string, object> IntegerSchema(string description) { return new Dictionary<string, object> { { "type", "integer" }, { "description", description } }; }
        static Dictionary<string, object> BoolSchema(string description) { return new Dictionary<string, object> { { "type", "boolean" }, { "description", description } }; }
    }
}
