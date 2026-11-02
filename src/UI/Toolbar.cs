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
    internal sealed class Toolbar : UserControl
    {
        readonly MainWindow owner;
        readonly Grid windowHeader;
        readonly TextBlock brandName;
        readonly List<Button> windowButtons=new List<Button>();
        internal readonly List<Button> toolButtons=new List<Button>();
        readonly List<Border> separators=new List<Border>();
        internal readonly Button appearanceButton,saveButton,moreTools,terminalButton;
        internal Toolbar(MainWindow window)
        {
            owner=window;
            HorizontalContentAlignment=HorizontalAlignment.Stretch;
            var header=new Grid
            {
                Margin=new Thickness(20,0,8,0)
            };
            header.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width=new GridLength(132)
            });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width=new GridLength(138)
            });
            Content=header;
            windowHeader=header;
            var chrome=new StackPanel
            {
                Orientation=Orientation.Horizontal
            };
            Grid.SetColumn(chrome,2);
            header.Children.Add(chrome);
            foreach(var entry in new[]
            {
                new[]
                {
                    "\uE921","最小化"
                },
                new[]
                {
                    "\uE922","最大化"
                },
                new[]
                {
                    "\uE8BB","关闭"
                }
            }
            )
            {
                string action=entry[1];
                var control=Theme.Button(entry[0],"",delegate
                {
                    if(action=="最小化")owner.WindowState=WindowState.Minimized;
                    else if(action=="最大化")owner.WindowState=owner.WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
                    else owner.Close();
                });
                control.Width=46;
                control.Height=38;
                control.MinHeight=0;
                control.Padding=new Thickness(0);
                control.Margin=new Thickness(0);
                control.ToolTip=action;
                WindowChrome.SetIsHitTestVisibleInChrome(control,true);
                chrome.Children.Add(control);
                windowButtons.Add(control);
            }
            var brand=new StackPanel
            {
                Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(2,0,14,0)
            };
            var logo=new Image{Source=owner.Icon,Width=30,Height=30};
            logo.Margin=new Thickness(0,0,11,0);

            brand.Children.Add(logo);
            brandName=new TextBlock
            {
                Text="轻页",FontSize=19,FontWeight=FontWeights.SemiBold
            };
            brand.Children.Add(brandName);
            header.Children.Add(brand);
            var tools=new StackPanel
            {
                Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center
            };
            Grid.SetColumn(tools,1);
            header.Children.Add(tools);
            AddTool(tools,Theme.Button("\uE7C3","新建",delegate
            {
                owner.NewDocument(true);
            }
            ));
            AddTool(tools,Theme.Button("\uE8E5","打开",delegate
            {
                var files=Dialogs.PickFiles(owner,false);
                owner.Observe(owner.OpenPathsAsync(files,false));
            }
            ));
            var import=Theme.Button("\uE8B5","导入",delegate
            {
            });
            var importMenu=new ContextMenu();
            importMenu.Items.Add(Theme.Menu("导入更多文件  Ctrl+Shift+O",delegate
            {
                owner.Observe(owner.OpenPathsAsync(Dialogs.PickFiles(owner,true),true));
            }
            ));
            importMenu.Items.Add(Theme.Menu("导入工作区  Ctrl+K",delegate
            {
                var folder=Dialogs.PickFolder(owner);
                if(folder!=null)owner.ImportWorkspace(folder);
            }
            ));
            importMenu.Items.Add(new Separator());
            importMenu.Items.Add(Theme.Menu("清空工作区与本次文件",delegate
            {
                if(owner.CloseDocuments(owner.Session.Documents.ToArray()))
                {
                    owner.ClearWorkspace();
                    owner.NewDocument(true);
                }
            }
            ));
            import.Click+=delegate
            {
                importMenu.PlacementTarget=import.IsVisible?import:moreTools;
                importMenu.IsOpen=true;
            };
            AddTool(tools,import);
            AddSeparator(tools);
            saveButton=Theme.Button("\uE74E","保存",delegate
            {
                if(owner.Current!=null)owner.SaveDocument(owner.Current,false);
            });
            saveButton.SetResourceReference(Control.ForegroundProperty,"AccentBrush");
            AddTool(tools,saveButton);
            var saveContent=saveButton.Content as StackPanel;
            if(saveContent!=null)saveContent.Children.Add(new TextBlock{Text="▾",Tag="ButtonLabel",FontSize=10,Margin=new Thickness(4,0,0,0),VerticalAlignment=VerticalAlignment.Center});
            AddSeparator(tools);
            var saveMenu=new ContextMenu();
            saveMenu.Items.Add(Theme.Menu("另存为  Ctrl+Shift+S",delegate
            {
                if(owner.Current!=null)owner.SaveDocument(owner.Current,true);
            }
            ));
            saveMenu.Items.Add(Theme.Menu("导出为其他格式…",delegate
            {
                if(owner.Current!=null)owner.Exports.Export(owner.Current);
            }
            ));
            saveMenu.Items.Add(Theme.Menu("打印当前阅读内容  Ctrl+Alt+P",delegate
            {
                owner.PrintCurrent();
            }
            ));
            saveMenu.Items.Add(Theme.Menu("保存所有修改",delegate
            {
                foreach(var doc in owner.Session.Documents.Where(d=>d.IsDirty).ToArray())if(!owner.SaveDocument(doc,false))break;
            }
            ));
            saveButton.ContextMenu=saveMenu;
            AddTool(tools,Theme.Button("\uE721","搜索",owner.ToggleSearch));
            appearanceButton=Theme.Button("\uE713","外观与布局",delegate
            {
            });
            appearanceButton.Click+=delegate
            {
                owner.ShowAppearance(appearanceButton);
            };
            AddTool(tools,appearanceButton);
            AddSeparator(tools);
            terminalButton=Theme.Button("terminal","终端",owner.ToggleTerminal);
            terminalButton.ToolTip="CMD  Ctrl+`";
            System.Windows.Automation.AutomationProperties.SetName(terminalButton,"终端");
            AddTool(tools,terminalButton);
            var newMenu=new ContextMenu();newMenu.Items.Add(Theme.Menu("新建文件  Ctrl+N",()=>owner.NewDocument(true)));newMenu.Items.Add(Theme.Menu("新建独立窗口  Ctrl+Shift+N",owner.NewWindow));toolButtons[0].ContextMenu=newMenu;
            owner.RegisterName("AppearanceButton",appearanceButton);
            moreTools=Theme.Button("ellipsis","",delegate
            {
            });
            moreTools.ToolTip="更多工具";
            System.Windows.Automation.AutomationProperties.SetName(moreTools,"更多工具");
            moreTools.MinHeight=36;
            moreTools.Width=36;
            moreTools.Visibility=Visibility.Collapsed;
            WindowChrome.SetIsHitTestVisibleInChrome(moreTools,true);
            tools.Children.Add(moreTools);
            owner.RegisterName("MoreToolsButton",moreTools);
            moreTools.Click+=delegate
            {
                var menu=new ContextMenu();
                foreach(var tool in toolButtons.Where(b=>b.Visibility==Visibility.Collapsed))
                {
                    var button=tool;
                    var item=Theme.Menu(button.ToolTip as string,delegate
                    {
                        if(button==appearanceButton)owner.ShowAppearance(moreTools);
                        else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    });
                    item.IsEnabled=button.IsEnabled;
                    menu.Items.Add(item);
                }
                moreTools.ContextMenu=menu;
                menu.PlacementTarget=moreTools;
                menu.IsOpen=true;
            };
        }
        void AddTool(StackPanel tools,Button button)
        {
            WindowChrome.SetIsHitTestVisibleInChrome(button,true);
            button.Margin=new Thickness(3,0,3,0);
            button.MinHeight=36;
            toolButtons.Add(button);
            tools.Children.Add(button);
        }
        void AddSeparator(StackPanel tools)
        {
            var line=new Border{Width=1,Height=22,Margin=new Thickness(5,0,5,0),VerticalAlignment=VerticalAlignment.Center};
            line.SetResourceReference(Border.BackgroundProperty,"LineBrush");
            separators.Add(line);
            tools.Children.Add(line);
        }
        internal void Resize(double width)
        {
            bool narrow=width<720,tiny=width<420,stacked=width<620;
            foreach(var button in toolButtons)
            {
                Theme.Compact(button,width<1080);
                bool keep=!narrow || button==toolButtons[1] && width>=260 || button==saveButton && !tiny;
                button.Visibility=keep?Visibility.Visible:Visibility.Collapsed;
                button.Margin=narrow?new Thickness(1,0,1,0):new Thickness(3,0,3,0);
                if(narrow)button.Padding=new Thickness(7);
            }
            moreTools.Visibility=narrow?Visibility.Visible:Visibility.Collapsed;
            foreach(var line in separators)line.Visibility=narrow?Visibility.Collapsed:Visibility.Visible;
            brandName.Visibility=tiny?Visibility.Collapsed:Visibility.Visible;
            windowHeader.Margin=new Thickness(narrow?10:20,0,8,0);
            windowHeader.ColumnDefinitions[0].Width=new GridLength(width<260?0:tiny?44:narrow?108:132);
            ((FrameworkElement)brandName.Parent).Visibility=width<260?Visibility.Collapsed:Visibility.Visible;
            foreach(var button in windowButtons)
            {
                button.Width=narrow?36:46;
                button.Visibility=width<300 && button!=windowButtons.Last()?Visibility.Collapsed:Visibility.Visible;
            }
            windowHeader.ColumnDefinitions[2].Width=new GridLength(width<300?36:narrow?108:138);
        }
    }
}
