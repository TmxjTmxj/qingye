using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Qingye.Core
{
    // Checkpoints live only for the current scope. Text strings are immutable;
    // merging repeated edits retains the first original and latest result.
    public sealed class AgentChangeSet
    {
        const int MaximumCharacters = 4 * 1024 * 1024;
        readonly Dictionary<string, WriteProposal> changes = new Dictionary<string, WriteProposal>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> order = new List<string>();
        public IList<WriteProposal> Entries { get { return order.Select(path => changes[path]).ToList(); } }

        public void Record(WriteProposal proposal)
        {
            string path = Path.GetFullPath(proposal.Path);
            WriteProposal previous;
            bool combine = changes.TryGetValue(path, out previous) && previous.NewText == proposal.OldText;
            var saved = new WriteProposal {
                Path = path, RelativePath = proposal.RelativePath,
                OldText = combine ? previous.OldText : proposal.OldText,
                NewText = proposal.NewText, IsNew = combine ? previous.IsNew : proposal.IsNew,
                OriginalVersion = File.Exists(path) ? FileService.HashFile(path) : null
            };
            if (!changes.ContainsKey(path)) order.Add(path);
            changes[path] = saved;
            while (order.Count > 32 || changes.Values.Sum(change => (long)(change.OldText ?? "").Length + (change.NewText ?? "").Length) > MaximumCharacters)
            {
                changes.Remove(order[0]);
                order.RemoveAt(0);
            }
        }

        public bool Revert(string path, AgentWorkspace scope, Func<string, bool> dirty, Action<WriteProposal> applied)
        {
            string full = scope.Resolve(path, true);
            WriteProposal saved;
            if (!changes.TryGetValue(full, out saved)) throw new AgentException("撤销记录已清除。");
            if (dirty != null && dirty(full)) throw new AgentException("文件有未保存修改，请先保存或放弃后再撤销。");
            if (!File.Exists(full) || FileService.HashFile(full) != saved.OriginalVersion)
                throw new AgentException("文件在 Agent 修改后又发生变化，已保留当前内容。");
            if (saved.IsNew)
            {
                // Only the user's explicit UI action reaches this branch. The
                // model still has no deletion tool, and contents must match.
                File.Delete(full);
                changes.Remove(full);
                order.Remove(full);
                return true;
            }
            var reverse = new WriteProposal {
                Path = full, RelativePath = saved.RelativePath, OldText = saved.NewText,
                NewText = saved.OldText, OriginalVersion = saved.OriginalVersion
            };
            new AgentTools(scope, null, dirty).ApplyWrite(reverse);
            changes.Remove(full);
            order.Remove(full);
            if (applied != null) applied(reverse);
            return false;
        }

        public void Clear() { changes.Clear(); order.Clear(); }
    }
}
