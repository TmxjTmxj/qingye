using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Qingye.Core
{
    public enum HarnessFilePermission { ReadOnly, Confirm, Automatic }
    // The Harness owns the agent loop. This adapter only supplies editor-aware file tools.
    public sealed class HarnessNativeTools
    {
        readonly string scopeDirectory;
        readonly Func<AgentWorkspace> scopeProvider;
        readonly Func<string, string> openText;
        readonly Func<string, bool> openDirty;
        readonly Action<WriteProposal> applied;
        readonly bool persistScopes;
        readonly string[] protectedRoots;
        readonly Dictionary<string, AgentTools> sessions = new Dictionary<string, AgentTools>();
        readonly Dictionary<string, AgentWorkspace> scopes = new Dictionary<string, AgentWorkspace>();
        readonly Dictionary<string, PendingWrite> proposals = new Dictionary<string, PendingWrite>();
        readonly object proposalLock = new object();
        long proposalSequence;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        readonly object policyLock = new object();
        HarnessFilePermission permission;
        public HarnessFilePermission Permission
        {
            get { lock (policyLock) return permission; }
            set { lock (policyLock) permission = value; }
        }

        sealed class PendingWrite
        {
            internal string SessionId;
            internal string ToolName;
            internal long Sequence;
            internal AgentTools Tools;
            internal WriteProposal Proposal;
            internal HarnessFilePermission Permission;
        }

        public HarnessNativeTools(string home, Func<AgentWorkspace> scopeProvider, Func<string,string> openText,
            Func<string,bool> openDirty, Action<WriteProposal> applied, bool persistScopes = true, params string[] protectedRoots)
        {
            scopeDirectory = Path.Combine(home, "qingye-scopes");
            this.scopeProvider = scopeProvider;
            this.openText = openText;
            this.openDirty = openDirty;
            this.applied = applied;
            this.persistScopes = persistScopes;
            this.protectedRoots = new List<string>(protectedRoots ?? new string[0]) { home }.ToArray();
            Permission = HarnessFilePermission.Confirm;
        }

        AgentTools ForSession(string sessionId, string requestedDirectory)
        {
            if (String.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 256) throw new AgentException("会话标识无效。");
            AgentTools tools;
            if (sessions.TryGetValue(sessionId, out tools)) return tools;
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sessionId))).Replace("-", "");
            string path = Path.Combine(scopeDirectory, hash + ".json");
            AgentWorkspace scope;
            if (persistScopes && File.Exists(path))
            {
                if (new FileInfo(path).Length > 32768) throw new AgentException("会话范围文件超限。");
                var saved = json.Deserialize<Dictionary<string,string>>(File.ReadAllText(path, Encoding.UTF8));
                scope = new AgentWorkspace(saved["root"], saved["file"]);
            }
            else
            {
                scope = scopeProvider();
                if (scope == null || scope.Root == null && scope.CurrentFile == null) throw new AgentException("请先打开文件或导入工作区，再创建新对话。");
                if (!String.IsNullOrWhiteSpace(requestedDirectory)) {
                    string expected = scope.Root ?? Path.GetDirectoryName(scope.CurrentFile);
                    if (!String.Equals(Path.GetFullPath(requestedDirectory).TrimEnd(Path.DirectorySeparatorChar), expected.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                        throw new AgentException("Harness 会话的工作区与轻页当前工作区不同。请在 Harness 中选择当前工作区后新建会话。");
                }
                if (persistScopes) {
                    Directory.CreateDirectory(scopeDirectory);
                    File.WriteAllText(path, json.Serialize(new Dictionary<string,string> { { "root", scope.Root }, { "file", scope.CurrentFile } }), new UTF8Encoding(false));
                }
            }
            scope = scope.Protect(protectedRoots);
            tools = new AgentTools(scope, openText, openDirty);
            sessions.Add(sessionId, tools);
            scopes.Add(sessionId, scope);
            return tools;
        }

        public object Execute(string operation, string sessionId, string name, Dictionary<string,object> args, string proposalId)
        {
            if (operation == "discard")
            {
                PendingWrite discarded;
                lock (proposalLock)
                {
                    if (proposalId != null && proposals.TryGetValue(proposalId, out discarded) && discarded.SessionId == sessionId) proposals.Remove(proposalId);
                }
                return "已释放修改提案。";
            }
            if (operation == "apply")
            {
                PendingWrite pending;
                lock (proposalLock)
                {
                    if (proposalId == null || !proposals.TryGetValue(proposalId, out pending) || pending.SessionId != sessionId) pending = null;
                    else proposals.Remove(proposalId);
                }
                if (pending == null) throw new AgentException("修改提案已失效或不属于当前会话。");
                RequireWritePermission(sessionId);
                if (pending.Permission == HarnessFilePermission.Automatic && Permission != HarnessFilePermission.Automatic)
                    throw new AgentException("自动编辑权限已收回，请重新提出修改并等待确认。");
                pending.Tools.ApplyWrite(pending.Proposal, publish => {
                    // File reads and editor callbacks happen before this lock.
                    // Revocation and the final rename are serialized without
                    // holding a lock while waiting for the WPF dispatcher.
                    lock (policyLock)
                    {
                        if (permission == HarnessFilePermission.ReadOnly ||
                            pending.Permission == HarnessFilePermission.Automatic && permission != HarnessFilePermission.Automatic)
                            throw new AgentException("文件权限已撤销，修改未写入。");
                        publish();
                    }
                });
                if (applied != null) applied(pending.Proposal);
                return "已写入 " + pending.Proposal.RelativePath;
            }
            object cwd;
            string directory = args != null && args.TryGetValue("cwd", out cwd) ? Convert.ToString(cwd) : null;
            var tools = ForSession(sessionId, directory);
            if (operation == "pin") return "会话文件范围已固定。";
            if (operation != "tool") throw new AgentException("未知原生操作。");
            if (name == "write_file" || name == "edit_file" || name == "create_file") RequireWritePermission(sessionId);
            var result = tools.Execute(name, args ?? new Dictionary<string,object>());
            if (result.Write == null) return result.Json;
            lock (proposalLock)
            {
                if (proposals.Count >= 16) throw new AgentException("等待确认的修改过多，请先处理现有提案。");
                string id = Guid.NewGuid().ToString("N");
                proposals.Add(id, new PendingWrite
                {
                    SessionId = sessionId, ToolName = name, Sequence = ++proposalSequence,
                    Tools = tools, Proposal = result.Write, Permission = Permission
                });
                bool ask = Permission != HarnessFilePermission.Automatic || scopes[sessionId].Root == null;
                return new Dictionary<string,object> { { "id", id }, { "diff", DiffPreview(result.Write) }, { "requiresApproval", ask } };
            }
        }

        // The TCP bridge answers an explicit approval request from the node plugin:
        // the pending proposal created by the preceding tool call is projected back
        // to the caller so the native diff dialog shows the exact pending write.
        internal bool TryGetProposal(string sessionId, string proposalId, out WriteProposal proposal)
        {
            proposal = null;
            lock (proposalLock)
            {
                PendingWrite pending;
                if (String.IsNullOrEmpty(proposalId) || !proposals.TryGetValue(proposalId, out pending) || pending.SessionId != sessionId) return false;
                proposal = pending.Proposal;
                return true;
            }
        }

        // Fallback for a bridge approval that omits the proposal id: the most
        // recent pending write of that session and tool, if any.
        internal bool TryFindLatestProposal(string sessionId, string toolName, out WriteProposal proposal)
        {
            proposal = null;
            lock (proposalLock)
            {
                PendingWrite latest = null;
                foreach (var pair in proposals)
                {
                    PendingWrite pending = pair.Value;
                    if (pending.SessionId != sessionId) continue;
                    if (!String.IsNullOrEmpty(toolName) && pending.ToolName != toolName) continue;
                    if (latest == null || pending.Sequence > latest.Sequence) latest = pending;
                }
                if (latest == null) return false;
                proposal = latest.Proposal;
                return true;
            }
        }

        void RequireWritePermission(string sessionId)
        {
            if (Permission == HarnessFilePermission.ReadOnly) throw new AgentException("当前为只读权限。请在轻页权限菜单中启用编辑。");
            AgentWorkspace pinned = scopes[sessionId];
            AgentWorkspace active = scopeProvider();
            bool matches = active != null && (pinned.Root != null
                ? String.Equals(pinned.Root, active.Root, StringComparison.OrdinalIgnoreCase)
                : String.Equals(pinned.CurrentFile, active.CurrentFile, StringComparison.OrdinalIgnoreCase));
            if (!matches) throw new AgentException("请在轻页中打开该会话原文件或导入原工作区，再允许编辑。");
        }

        // Approval cards show a bounded +/- line diff instead of the whole file:
        // the model already holds the complete text, the reader needs the change.
        const int MaxDiffLines = 60;
        const int MaxChangedLines = 20;
        const int MaxDiffCharacters = 8000;
        const int MaxDiffLineCharacters = 300;
        const int DiffContextLines = 3;

        static string DiffPreview(WriteProposal proposal)
        {
            string[] oldLines = SplitLines(proposal.OldText);
            string[] newLines = SplitLines(proposal.NewText);
            var body = new StringBuilder();
            int lines = 0;
            int characters = 0;
            bool omitted = false;
            if (proposal.IsNew)
            {
                int displayLines = newLines.Length;
                if (displayLines > 0 && newLines[displayLines - 1].Length == 0) displayLines--;
                for (int index = 0; index < displayLines; index++)
                {
                    if (!AppendDiffLine(body, "+", newLines[index], ref lines, ref characters)) { omitted = true; break; }
                }
                return ComposeDiff("新建文件：" + proposal.RelativePath + "（" + displayLines + " 行）", body, omitted);
            }
            int prefix = 0;
            while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix]) prefix++;
            int suffix = 0;
            while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix
                && oldLines[oldLines.Length - 1 - suffix] == newLines[newLines.Length - 1 - suffix]) suffix++;
            int oldMiddle = oldLines.Length - prefix - suffix;
            int newMiddle = newLines.Length - prefix - suffix;
            var head = new StringBuilder();
            head.Append("修改文件：").Append(proposal.RelativePath).Append("（-").Append(oldMiddle).Append(" +").Append(newMiddle).Append(" 行）").AppendLine();
            head.Append("@@ 第 ").Append(prefix + 1).Append(" 行 @@");
            int contextStart = Math.Max(0, prefix - DiffContextLines);
            for (int index = contextStart; index < prefix; index++) AppendDiffLine(body, " ", oldLines[index], ref lines, ref characters);
            int removed = 0;
            for (int index = prefix; index < prefix + oldMiddle && removed < MaxChangedLines; index++, removed++)
            {
                if (!AppendDiffLine(body, "-", oldLines[index], ref lines, ref characters)) { omitted = true; break; }
            }
            if (removed < oldMiddle) omitted = true;
            int added = 0;
            for (int index = prefix; index < prefix + newMiddle && added < MaxChangedLines; index++, added++)
            {
                if (!AppendDiffLine(body, "+", newLines[index], ref lines, ref characters)) { omitted = true; break; }
            }
            if (added < newMiddle) omitted = true;
            int afterStart = prefix + oldMiddle;
            for (int index = afterStart; index < Math.Min(oldLines.Length, afterStart + DiffContextLines); index++)
                AppendDiffLine(body, " ", oldLines[index], ref lines, ref characters);
            return ComposeDiff(head.ToString(), body, omitted);
        }

        static bool AppendDiffLine(StringBuilder body, string prefix, string line, ref int lines, ref int characters)
        {
            if (lines >= MaxDiffLines || characters >= MaxDiffCharacters) return false;
            string value = line ?? "";
            if (value.Length > MaxDiffLineCharacters) value = value.Substring(0, MaxDiffLineCharacters) + "…";
            string row = prefix + value;
            body.AppendLine(row);
            lines++;
            characters += row.Length + 2;
            return true;
        }

        static string ComposeDiff(string header, StringBuilder body, bool omitted)
        {
            var preview = new StringBuilder();
            preview.AppendLine(header);
            preview.Append(body.ToString().TrimEnd());
            if (omitted) preview.AppendLine().Append("… 修改内容较长，已截取显示；请在工具参数中核对完整内容。");
            return preview.ToString();
        }

        static string[] SplitLines(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }
    }
}
