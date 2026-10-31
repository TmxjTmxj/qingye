using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Diagnostics;
using ICSharpCode.AvalonEdit.Document;
using Qingye.Core;

namespace Qingye.UI
{
    internal sealed class WorkspaceTree : UserControl
    {
        readonly MainWindow owner;
        int workspaceGeneration;
        sealed class FolderState
        {
            public string Path;
            public int DirectoryOffset,FileOffset;
            public bool Loading,Loaded,DirectoriesDone,FilesDone;
        }
        internal readonly TreeView folderTree=new TreeView();
        internal string Root
        {
            get;
            private set;
        }
        internal Task LoadTask
        {
            get;
            private set;
        }
        internal WorkspaceTree(MainWindow window)
        {
            owner=window;
            LoadTask=Task.FromResult(0);
            HorizontalContentAlignment=HorizontalAlignment.Stretch;
            var border=new Border
            {
                BorderThickness=new Thickness(0),Child=folderTree
            };
            border.SetResourceReference(Border.BackgroundProperty,"PanelBrush");
            border.SetResourceReference(Border.BorderBrushProperty,"LineBrush");
            Content=border;
            folderTree.Background=Brushes.Transparent;
            folderTree.BorderThickness=new Thickness(0);
            folderTree.Padding=new Thickness(8,12,6,10);
            folderTree.SetResourceReference(Control.ForegroundProperty,"TextBrush");
            folderTree.SelectedItemChanged+=delegate
            {
                var item=folderTree.SelectedItem as TreeViewItem;
                if(item!=null && item.Tag is string && File.Exists((string)item.Tag))owner.Observe(owner.OpenPathsAsync(new[]
                {
                    (string)item.Tag
                },
                true));
            };
            VirtualizingPanel.SetIsVirtualizing(folderTree,true);
            VirtualizingPanel.SetVirtualizationMode(folderTree,VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(folderTree,true);
        }
        public void ImportWorkspace(string path)
        {
            if(!Directory.Exists(path) || owner.IsClosing)return;
            workspaceGeneration++;
            Root=Path.GetFullPath(path);
            folderTree.Items.Clear();
            var root=FolderItem(Root);
            folderTree.Items.Add(root);
            root.IsExpanded=true;
            owner.UpdateToolbar();
        }
        // Re-lists the imported root after an agent write so new files appear
        // without rebuilding the whole window. Expanded subfolders collapse with
        // the root, which is acceptable for a single external change.
        internal Task RefreshAsync()
        {
            if(Root==null || owner.IsClosing)return Task.FromResult(0);
            workspaceGeneration++;
            folderTree.Items.Clear();
            var root=FolderItem(Root);
            folderTree.Items.Add(root);
            root.IsExpanded=true;
            return LoadTask;
        }
        internal void ClearWorkspace()
        {
            workspaceGeneration++;
            Root=null;
            folderTree.Items.Clear();
            LoadTask=Task.FromResult(0);
        }
        TreeViewItem FolderItem(string path)
        {
            var state=new FolderState
            {
                Path=path
            };
            var item=new TreeViewItem
            {
                Header=Theme.FileLabel(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),true),Tag=state,Padding=new Thickness(4,5,0,5)
            };
            item.Items.Add(new TreeViewItem
            {
                Header=""
            });
            item.Expanded+=delegate(object sender,RoutedEventArgs e)
            {
                if(e.OriginalSource==item && !state.Loaded && !state.Loading)LoadTask=LoadDirectoryAsync(item,state);
            };
            return item;
        }
        async Task LoadDirectoryAsync(TreeViewItem item,FolderState state)
        {
            if(state.Loading || owner.IsClosing)return;
            state.Loading=true;
            int generation=workspaceGeneration;
            if(!state.Loaded)item.Items.Clear();
            else
            {
                foreach(var button in item.Items.OfType<Button>().ToArray())item.Items.Remove(button);
                foreach(var hint in item.Items.OfType<TreeViewItem>().Where(x=>x.Tag==null).ToArray())item.Items.Remove(hint);
            }
            var loading=new TreeViewItem
            {
                Header="加载中…",IsEnabled=false,FontSize=12
            };
            item.Items.Add(loading);
            try
            {
                var page=await Task.Run(delegate
                {
                    return DirectoryPage.Read(state.Path,state.DirectoryOffset,state.FileOffset,!state.DirectoriesDone,!state.FilesDone);
                });
                if(generation!=workspaceGeneration || owner.IsClosing)return;
                item.Items.Remove(loading);
                foreach(string directory in page.Directories)item.Items.Add(FolderItem(directory));
                foreach(string file in page.Files)item.Items.Add(new TreeViewItem
                {
                    Header=Theme.FileLabel(Path.GetFileName(file)),Tag=file,Padding=new Thickness(4,5,0,5)
                });
                state.DirectoryOffset+=page.Directories.Length;
                state.FileOffset+=page.Files.Length;
                state.DirectoriesDone=!page.MoreDirectories;
                state.FilesDone=!page.MoreFiles;
                state.Loaded=true;
                if(page.MoreDirectories || page.MoreFiles)
                {
                    var more=Theme.Button(null,"加载更多…",delegate
                    {
                        LoadTask=LoadDirectoryAsync(item,state);
                    });
                    more.FontSize=12;
                    more.ToolTip="继续读取此目录，文件列表只保留在本次内存";
                    item.Items.Add(more);
                }
                else if(item.Items.Count==0)item.Items.Add(new TreeViewItem
                {
                    Header="空目录",IsEnabled=false
                });
            }
            catch(Exception error)
            {
                if(generation==workspaceGeneration && !owner.IsClosing)
                {
                    item.Items.Remove(loading);
                    item.Items.Add(new TreeViewItem
                    {
                        Header="目录暂时无法读取",IsEnabled=false,FontSize=12
                    });
                    var retry=Theme.Button(null,"重试加载",delegate
                    {
                        LoadTask=LoadDirectoryAsync(item,state);
                    });
                    retry.FontSize=12;
                    item.Items.Add(retry);
                    owner.SetNotice(error.Message);
                }
            }
            finally
            {
                state.Loading=false;
            }
        }
    }
}
