using System;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using Qingye.Core;

namespace Qingye.UI
{
    // Applies an agent write without rebuilding the window or the session's
    // document list. A clean open buffer receives the disk text through a
    // single undoable replace while caret, selection and scroll stay where the
    // reader left them; a dirty buffer is never overwritten.
    internal static class DocumentSync
    {
        internal static string Apply(MainWindow owner, WriteProposal proposal)
        {
            if (owner == null || proposal == null || String.IsNullOrEmpty(proposal.Path)) return "写入结果无效。";
            owner.RefreshWorkspace();
            var document = owner.Session.Documents.FirstOrDefault(candidate => candidate.Path != null &&
                String.Equals(Path.GetFullPath(candidate.Path), Path.GetFullPath(proposal.Path), StringComparison.OrdinalIgnoreCase));
            string message;
            if (document == null) message = "Agent 已写入 " + proposal.RelativePath + (proposal.IsNew ? "（新建文件）" : "") + "。";
            else if (owner.richEdit.IsEditing(document)) message = "Agent 已写入磁盘；当前文件正在原位编辑，请退出原位编辑后再核对内容。";
            else if (document.IsDirty) message = "Agent 已写入磁盘；你没有保存的修改仍保留在编辑器缓冲区，请核对后保存。";
            else
            {
                try { message = ReplaceBuffer(owner, document, proposal); }
                catch (Exception error) { message = "Agent 已写入，但编辑器缓冲更新失败：" + error.Message; }
            }
            owner.RefreshTabs();
            owner.UpdateStatus();
            owner.SetNotice(message);
            return message;
        }

        static string ReplaceBuffer(MainWindow owner, Document document, WriteProposal proposal)
        {
            Document loaded = FileService.Read(proposal.Path);
            string text = loaded.Text ?? "";
            document.Version = loaded.Version;
            document.ByteLength = loaded.ByteLength;
            TextDocument buffer;
            if (!owner.buffers.TryGetValue(document, out buffer) || buffer == null)
            {
                document.SetLoadedText(text);
                return "Agent 已更新 " + proposal.RelativePath + "。";
            }
            TextEditor editor = owner.primary.BoundDocument == document ? owner.primary.Editor
                : owner.secondary.BoundDocument == document ? owner.secondary.Editor : null;
            int caret = editor == null ? 0 : editor.CaretOffset;
            int selectionStart = editor == null ? 0 : editor.SelectionStart;
            int selectionLength = editor == null ? 0 : editor.SelectionLength;
            double vertical = editor == null ? 0 : editor.VerticalOffset;
            double horizontal = editor == null ? 0 : editor.HorizontalOffset;
            if (buffer.Text != text)
            {
                buffer.UndoStack.StartUndoGroup();
                try { buffer.Replace(0, buffer.TextLength, text); }
                finally { buffer.UndoStack.EndUndoGroup(); }
            }
            // The buffer now matches the file just written, so the tab is clean;
            // the replace remains available as one undo step.
            document.Text = text;
            document.AcceptChanges();
            buffer.UndoStack.MarkAsOriginalFile();
            if (editor != null)
            {
                int length = buffer.TextLength;
                int start = Math.Min(selectionStart, length);
                editor.CaretOffset = Math.Min(caret, length);
                editor.Select(start, Math.Min(selectionLength, length - start));
                editor.ScrollToVerticalOffset(vertical);
                editor.ScrollToHorizontalOffset(horizontal);
                editor.TextArea.TextView.Redraw();
                var dispatcher = editor.Dispatcher;
                if (dispatcher != null) dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
                {
                    editor.ScrollToVerticalOffset(vertical);
                    editor.ScrollToHorizontalOffset(horizontal);
                }));
            }
            return "Agent 已更新 " + proposal.RelativePath + "；可用撤销恢复写入前的内容。";
        }
    }
}
