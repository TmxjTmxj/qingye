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
    internal sealed class TabStrip : UserControl
    {
        readonly MainWindow owner;
        readonly Dictionary<Document,Button> selectors=new Dictionary<Document,Button>();
        readonly Dictionary<Document,Border> containers=new Dictionary<Document,Border>();
        Document[] previousDocuments=new Document[0];
        string[] previousLabels=new string[0];
        internal readonly StackPanel tabs=new StackPanel
        {
            Orientation=Orientation.Horizontal
        };
        internal readonly Border tabsBorder;
        internal TabStrip(MainWindow window)
        {
            owner=window;
            HorizontalContentAlignment=HorizontalAlignment.Stretch;
            tabsBorder=new Border
            {
                BorderThickness=new Thickness(0,0,0,1)
            };
            tabsBorder.SetResourceReference(Border.BorderBrushProperty,"LineBrush");
            tabsBorder.SetResourceReference(Border.BackgroundProperty,"PanelBrush");
            tabsBorder.Child=new ScrollViewer
            {
                Content=tabs,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled
            };
            Content=tabsBorder;
        }
        internal void Refresh()
        {
            var documents=owner.Session.Documents.ToArray();
            var labels=documents.Select(doc=>doc.Title+(doc.IsDirty?" •":"")).ToArray();
            owner.primary.UpdateTitle();owner.secondary.UpdateTitle();
            if(previousDocuments.SequenceEqual(documents) && previousLabels.SequenceEqual(labels))
            {
                UpdateSelection();
                return;
            }
            previousDocuments=documents;previousLabels=labels;selectors.Clear();containers.Clear();
            tabs.Children.Clear();
            tabsBorder.Visibility=owner.Session.Documents.Count>1?Visibility.Visible:Visibility.Collapsed;
            foreach(var doc in owner.Session.Documents)
            {
                var item=new StackPanel
                {
                    Orientation=Orientation.Horizontal,Margin=new Thickness(0)
                };
                owner.DisplayedDirty[doc]=doc.IsDirty;
                var select=Theme.Button(null,doc.Title+(doc.IsDirty?" •":""),delegate
                {
                    owner.SelectDocument(doc,null);
                });
                select.Content=Theme.FileLabel(doc.Title+(doc.IsDirty?" •":""));
                select.MinHeight=36;
                select.MaxWidth=210;
                select.Padding=new Thickness(10,5,8,5);
                select.ToolTip=(doc.Path??"未保存文档")+"\n切换当前编辑栏；右键可指定另一栏";
                item.Children.Add(select);
                var close=Theme.Button("\uE8BB","",delegate
                {
                    owner.CloseTab(doc);
                });
                close.Width=26;
                close.Height=26;
                close.Padding=new Thickness(0);
                close.ToolTip="关闭文件  Ctrl+W";
                item.Children.Add(close);
                var container=new Border{Child=item,Margin=new Thickness(3,3,0,0),Padding=new Thickness(2,0,4,1),CornerRadius=new CornerRadius(6,6,0,0),BorderThickness=new Thickness(0,0,0,2)};
                selectors[doc]=select;containers[doc]=container;
                tabs.Children.Add(container);
                var menu=new ContextMenu();
                menu.Items.Add(Theme.Menu("在第一栏打开",delegate
                {
                    owner.SelectDocument(doc,"primary");
                }
                ));
                menu.Items.Add(Theme.Menu("在第二栏打开",delegate
                {
                    owner.SelectDocument(doc,"secondary");
                }
                ));
                menu.Items.Add(new Separator());
                menu.Items.Add(Theme.Menu("关闭其他文件",delegate
                {
                    if(owner.CloseDocuments(owner.Session.Documents.Where(d=>d!=doc).ToArray()))
                    {
                        owner.primary.Bind(doc);
                        owner.secondary.Bind(doc);
                        owner.RefreshTabs();
                        owner.UpdateStatus();
                    }
                }
                ));
                menu.Items.Add(Theme.Menu("关闭全部",delegate
                {
                    if(owner.CloseDocuments(owner.Session.Documents.ToArray()))
                    {
                        owner.ClearWorkspace();
                        owner.NewDocument(true);
                        owner.SetLayout("single");
                    }
                }
                ));
                menu.Items.Add(new Separator());
                menu.Items.Add(Theme.Menu("复制完整路径",()=>{if(doc.Path!=null)Clipboard.SetText(doc.Path);}));
                menu.Items.Add(Theme.Menu("复制相对工作区路径",()=>{if(doc.Path!=null){string root=owner.WorkspaceRoot;Clipboard.SetText(root!=null&&FileService.IsInsideRoot(root,doc.Path)?doc.Path.Substring(root.TrimEnd('\\','/').Length+1):Path.GetFileName(doc.Path));}}));
                menu.Items.Add(Theme.Menu("重新打开刚关闭的文件  Ctrl+Shift+T",()=>owner.Observe(owner.Usage.ReopenAsync())));
                menu.Items.Add(Theme.Menu("在资源管理器中定位",delegate
                {
                    if(doc.Path!=null && File.Exists(doc.Path))Process.Start("explorer.exe","/select,\""+doc.Path+"\"");
                }
                ));
                menu.Items.Add(Theme.Menu("使用系统关联软件打开",delegate
                {
                    if(doc.Path!=null && File.Exists(doc.Path))Process.Start(doc.Path);
                }
                ));
                select.ContextMenu=menu;
            }
            tabs.Children.Add(Theme.Button("\uE710","",delegate
            {
                owner.NewDocument(true);
            }
            ));
            UpdateSelection();
        }
        void UpdateSelection()
        {
            foreach(var pair in containers)
            {
                bool selected=pair.Key==owner.Current;
                pair.Value.SetResourceReference(Border.BackgroundProperty,selected?"SurfaceBrush":"PanelBrush");
                pair.Value.SetResourceReference(Border.BorderBrushProperty,selected?"AccentBrush":"PanelBrush");
                selectors[pair.Key].FontWeight=selected?FontWeights.SemiBold:FontWeights.Normal;
            }
        }
    }
}
