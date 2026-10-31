using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Qingye.Core;

namespace Qingye.UI
{
    internal sealed partial class AgentPanel
    {
        bool revertingChange;

        void ApplyOwnedEditorWrite(WriteProposal proposal, string ownerSession)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(new Action(() => ApplyOwnedEditorWrite(proposal, ownerSession)));
                return;
            }
            if (session.Id == ownerSession) ApplyEditorWrite(proposal);
            else DocumentSync.Apply(owner, proposal);
        }

        void RefreshChanges()
        {
            var entries = changes.Entries;
            changesList.Children.Clear();
            changesBox.Header = "本次改动 (" + entries.Count + ")";
            changesBox.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (WriteProposal change in entries)
            {
                var row = new DockPanel();
                var undo = Theme.Button(null, "撤销", () => UndoChange(change.Path, true));
                undo.FontSize = 11;
                undo.Padding = new Thickness(5, 2, 5, 2);
                undo.ToolTip = change.IsNew ? "撤销新建并删除此文件；有后续修改时拒绝删除" : "文件未被再次修改时，恢复本次 Agent 修改前的内容";
                DockPanel.SetDock(undo, Dock.Right);
                row.Children.Add(undo);
                var diff = Theme.Button(null, change.RelativePath + (change.IsNew ? "（新建）" : ""), () => DiffDialog.Show(Window.GetWindow(this), change, true));
                diff.FontSize = 11;
                diff.Padding = new Thickness(5, 2, 5, 2);
                diff.HorizontalContentAlignment = HorizontalAlignment.Left;
                diff.ToolTip = "查看本次差异 · " + change.Path;
                var menu = new ContextMenu();
                var open = new MenuItem { Header = "打开文件" };
                open.Click += delegate { owner.Observe(owner.OpenPathsAsync(new[] { change.Path }, true)); };
                menu.Items.Add(open);
                diff.ContextMenu = menu;
                row.Children.Add(diff);
                changesList.Children.Add(row);
            }
        }

        internal void UndoChange(string path, bool confirm)
        {
            if (busy) { conversation.AddNotice("停止当前任务后可撤销文件改动。"); return; }
            var entry = changes.Entries.FirstOrDefault(change => String.Equals(change.Path, path, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;
            if (entry.IsNew && confirm && MessageBox.Show(Window.GetWindow(this), "撤销新建并删除“" + entry.RelativePath + "”？文件有后续修改时会保留。", "撤销新建", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            try
            {
                var scope = new AgentWorkspace(owner.WorkspaceRoot, owner.Current == null ? null : owner.Current.Path,
                    RuntimeDataDirectory, HarnessRuntime.Locate(AppDomain.CurrentDomain.BaseDirectory));
                revertingChange = true;
                bool deleted = changes.Revert(path, scope, OpenDirty, reverse => DocumentSync.Apply(owner, reverse));
                if (deleted) owner.CloseDocuments(owner.Session.Documents.Where(document => document.Path != null && String.Equals(document.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray());
                RefreshChanges();
                conversation.AddNotice("已撤销 · " + entry.RelativePath);
                owner.RefreshWorkspace();
                owner.Observe(owner.UpdatePreviewAsync());
            }
            catch (Exception error) { conversation.AddNotice("未撤销：" + error.Message); }
            finally { revertingChange = false; }
        }

        void ClearLegacyArchives() { Observe(ClearLegacyArchivesAsync()); }

        async Task ClearLegacyArchivesAsync()
        {
            if (MessageBox.Show(Window.GetWindow(this), "删除旧版本保存在本机的对话、草稿和附件？不会撤销已经写入的文件。", "清理旧版存档", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            try
            {
                string directory = AppDomain.CurrentDomain.BaseDirectory;
                await Task.Run(() => LegacyAgentArchives.Clear(directory)).ConfigureAwait(true);
                conversation.AddNotice("旧版存档已清理；当前临时对话不受影响。");
            }
            catch (Exception error) { conversation.AddNotice("旧版存档清理失败：" + error.Message); }
        }
    }
}
