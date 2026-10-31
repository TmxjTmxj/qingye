using System;
using System.Windows;
using ICSharpCode.AvalonEdit.Document;
using Qingye.Core;

namespace Qingye.UI
{
    internal sealed class DocumentSaver
    {
        readonly MainWindow owner;

        internal DocumentSaver(MainWindow owner) { this.owner = owner; }

        internal bool Save(Document doc, bool saveAs)
        {
            if (owner.richEdit.IsEditing(doc)) owner.richEdit.Commit();
            if (doc.IsReadOnly)
            {
                if (saveAs) { owner.Exports.Export(doc); return true; }
                owner.SetNotice("当前内容是只读查看"); return false;
            }
            string target = saveAs || doc.Path == null ? Dialogs.PickSave(owner, doc.Path ?? doc.Title + ".txt") : doc.Path;
            if (target == null) return false;
            TextDocument buffer;
            if (owner.buffers.TryGetValue(doc, out buffer)) { bool wasDirty = doc.Modified; doc.Text = buffer.Text; doc.Modified = wasDirty || doc.Modified; }
            try
            {
                FileService.Save(doc, target, false);
                if (buffer != null) buffer.UndoStack.MarkAsOriginalFile();
                if (owner.primary.BoundDocument == doc) { owner.primary.Bind(doc); owner.primary.Apply(owner.Appearance); }
                if (owner.secondary.BoundDocument == doc) { owner.secondary.Bind(doc); owner.secondary.Apply(owner.Appearance); }
                owner.RefreshTabs(); owner.UpdateStatus(); owner.SetNotice("已保存"); return true;
            }
            catch (FileConflictException ex)
            {
                var answer = MessageBox.Show(owner, ex.Message + "\n\n是否明确覆盖原路径？选择“否”可另存为。", "文件发生变化", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel) return false;
                if (answer == MessageBoxResult.No) return Save(doc, true);
                try
                {
                    FileService.Save(doc, target, true);
                    if (buffer != null) buffer.UndoStack.MarkAsOriginalFile();
                    owner.RefreshTabs(); owner.UpdateStatus(); return true;
                }
                catch (Exception error) { owner.SetNotice("保存失败：" + error.Message); return false; }
            }
            catch (Exception ex) { owner.SetNotice("保存失败，内容仍保留：" + ex.Message); return false; }
        }
    }
}
