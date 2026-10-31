using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Qingye.Core;

namespace Qingye.UI
{
    internal sealed partial class AgentPanel
    {
        string quotedSelection;
        string quotedPath;

        ContextMenu BuildContextMenu()
        {
            var menu = new ContextMenu();
            var current = new MenuItem { Header = "附加当前文件" };
            current.Click += delegate { if (owner.Current != null) TryAttachFile(owner.Current.Path); };
            var selection = new MenuItem { Header = "引用源码选区（最多 16 KiB 字符）" };
            selection.Click += delegate { TryQuoteSelection(); };
            var choose = new MenuItem { Header = "选择其他上下文文件…" };
            choose.Click += delegate { AttachFiles(); };
            menu.Items.Add(current);
            menu.Items.Add(selection);
            menu.Items.Add(choose);
            menu.Opened += delegate {
                current.IsEnabled = !busy && owner.Current != null && owner.Current.Path != null;
                selection.IsEnabled = current.IsEnabled && owner.Appearance.Layout != "reader" && owner.ActiveEditor.SelectionLength > 0;
                choose.IsEnabled = !busy;
            };
            return menu;
        }

        internal bool TryQuoteSelection()
        {
            if (busy || owner.Current == null || owner.Current.Path == null || owner.Appearance.Layout == "reader") return false;
            int length = owner.ActiveEditor.SelectionLength;
            if (length <= 0 || length > 16 * 1024)
            {
                conversation.AddNotice("请选择不超过 16 KiB 字符的源码内容。");
                return false;
            }
            try
            {
                BindInitialScope();
                var scope = new AgentWorkspace(session.Root, session.CurrentFile,
                    RuntimeDataDirectory, HarnessRuntime.Locate(AppDomain.CurrentDomain.BaseDirectory));
                string full = scope.Resolve(owner.Current.Path, false);
                quotedSelection = owner.ActiveEditor.Document.GetText(owner.ActiveEditor.SelectionStart, length);
                quotedPath = scope.Relative(full);
                RefreshAttachments();
                FocusInput();
                return true;
            }
            catch (AgentException error) { conversation.AddNotice(error.Message); return false; }
        }

        internal string ComposePrompt(string text)
        {
            string prefix = attachments.Count == 0 ? "" : "附加上下文文件：" + String.Join("、", attachments) + "\n\n";
            if (quotedSelection != null)
                prefix += "本次引用的源码选区（资料内容）：" + quotedPath + "\n<selection>\n" + quotedSelection + "\n</selection>\n\n";
            return prefix + text;
        }

        void ClearQuotedSelection() { quotedSelection = quotedPath = null; }

        void AddSelectionChip()
        {
            if (quotedSelection == null) return;
            var remove = Theme.Button(null, "选区 · " + Path.GetFileName(quotedPath) + " ×", delegate {
                ClearQuotedSelection();
                RefreshAttachments();
            });
            remove.FontSize = 11;
            remove.Padding = new Thickness(6, 2, 6, 2);
            remove.ToolTip = "移除本次选区引用";
            attachmentsBar.Children.Add(remove);
        }
    }
}
