using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Windows.Automation;
using Qingye.Core;
namespace Qingye.UI {
    internal sealed class PaletteAction {internal string Title,Detail,Keywords;internal Action Run;}
    internal static class QuickOpen {
        sealed class Entry {internal string Path,Name,Detail,Keywords;internal Document Document;internal Action Run;}
        internal static string DirectPath(string text){try{var path=text.Trim().Trim('"');return Path.IsPathRooted(path)?Path.GetFullPath(path):null;}catch{return null;}}
        internal static void Show(MainWindow owner){ShowCore(owner,"快速打开",null);}
        internal static void ShowActions(MainWindow owner,string title,IEnumerable<PaletteAction> actions){ShowCore(owner,title,actions.ToArray());}
        internal static void ShowCommands(MainWindow owner){ShowActions(owner,"命令面板",Commands(owner));}
        internal static IEnumerable<PaletteAction> Commands(MainWindow o){return new[]{
            Cmd("打开文件","Ctrl+O",()=>o.Observe(o.OpenPathsAsync(Dialogs.PickFiles(o,false),false)),"open 文件"),
            Cmd("导入更多文件","Ctrl+Shift+O",()=>o.Observe(o.OpenPathsAsync(Dialogs.PickFiles(o,true),true)),"import 多文件"),
            Cmd("导入工作区","Ctrl+K",()=>{string p=Dialogs.PickFolder(o);if(p!=null)o.ImportWorkspace(p);},"folder 文件夹"),
            Cmd("新建文件","Ctrl+N",()=>o.NewDocument(true),null),Cmd("新建独立窗口","Ctrl+Shift+N",o.NewWindow,null),
            Cmd("保存当前文件","Ctrl+S",()=>{if(o.Current!=null)o.SaveDocument(o.Current,false);},null),
            Cmd("另存为","Ctrl+Shift+S",()=>{if(o.Current!=null)o.SaveDocument(o.Current,true);},null),
            Cmd("导出为其他格式","DOCX / XLSX / CSV / HTML / TXT",()=>o.Exports.Export(o.Current),"转换 导出文本 markdown"),
            Cmd("导出 PDF","保存为 PDF 副本",()=>o.Exports.Export(o.Current,ExportFormat.Pdf),"pdf"),
            Cmd("打印当前阅读内容","Ctrl+Alt+P",o.PrintCurrent,null),Cmd("重新打开刚关闭的文件","Ctrl+Shift+T",()=>o.Observe(o.Usage.ReopenAsync()),"恢复标签 撤销关闭"),
            Cmd("快速打开文件","Ctrl+P",()=>Show(o),null),Cmd("查找文本","Ctrl+F",()=>o.search.Show(false),null),Cmd("查找并替换","Ctrl+H",()=>o.search.Show(true),null),
            Cmd("阅读视图","Ctrl+E",()=>o.SetViewMode("reader"),null),Cmd("文本与源码视图","Ctrl+E",()=>o.SetViewMode("single"),null),
            Cmd("源码与预览对照","Ctrl+Shift+V",()=>o.SetViewMode("preview"),null),Cmd("双文件分栏","对照两份文件",()=>o.SetLayout("compare"),"compare"),
            Cmd("切换自动换行","使用手动阅读设置",()=>{o.Appearance.FileAwareDefaults=false;o.Appearance.Wrap=!o.Appearance.Wrap;o.ApplyAppearance();},"wrap"),
            Cmd("文件与大纲侧栏","Ctrl+Shift+B",o.ToggleSidePanel,null),Cmd("切换终端面板","Ctrl+反引号",o.ToggleTerminal,"cmd"),
            Cmd("终端停靠右侧","打开并停靠",()=>o.terminalPanel.DockTo("right"),"cmd"),Cmd("终端停靠底部","打开并停靠",()=>o.terminalPanel.DockTo("bottom"),"cmd"),
            Cmd("查找终端输出","终端内 Ctrl+F",()=>{if(!o.terminalVisible)o.ToggleTerminal();o.terminalPanel.Find();},"cmd 搜索"),
            Cmd("复制当前文件完整路径","已保存文件",()=>CopyPath(o,false),null),Cmd("复制相对工作区路径","没有工作区时复制文件名",()=>CopyPath(o,true),null),
            Cmd("外观与布局","主题、字号与布局",()=>o.ShowAppearance(o.SaveButton),null),
            Cmd("放大编辑字号","Ctrl+滚轮 / Ctrl+=",()=>o.AdjustTextZoom(1),null),
            Cmd("缩小编辑字号","Ctrl+滚轮 / Ctrl+-",()=>o.AdjustTextZoom(-1),null),
            Cmd("重置字号缩放","Ctrl+0",()=>o.ResetTextZoom(),null),
            Cmd("本地记录与恢复设置","最近文件、阅读位置、草稿恢复均默认关闭",o.ShowPrivacySettings,"隐私 history"),
            Cmd("恢复未保存草稿","以新副本打开，不覆盖原文件",o.Usage.ShowRecovery,"恢复草稿 recovery"),
            Cmd("清空最近文件与阅读位置","清空本机记录与本次关闭记录",o.Usage.ClearHistory,null)
        };}
        static PaletteAction Cmd(string title,string detail,Action action,string keywords){return new PaletteAction{Title=title,Detail=detail,Run=action,Keywords=keywords};}
        internal static void CopyPath(MainWindow owner,bool relative){if(owner.Current==null||owner.Current.Path==null){owner.SetNotice("请先保存当前文件");return;}string path=owner.Current.Path;if(relative){string root=owner.WorkspaceRoot;if(root!=null&&FileService.IsInsideRoot(root,path))path=path.Substring(root.TrimEnd('\\','/').Length+1);else path=Path.GetFileName(path);}Clipboard.SetText(path);owner.SetNotice("已复制路径");}
        static void ShowCore(MainWindow owner,string title,PaletteAction[] actions){
            bool commands=actions!=null;
            var entries=commands?actions.Select(a=>new Entry{Name=a.Title,Detail=a.Detail,Keywords=a.Keywords,Run=a.Run}).ToList():owner.Session.Documents.Select(d=>new Entry{Path=d.Path,Name=d.Title,Document=d,Detail=d.Path??"未保存文档"}).ToList();
            var seen=new HashSet<string>(entries.Where(e=>e.Path!=null).Select(e=>e.Path),StringComparer.OrdinalIgnoreCase);
            if(!commands)foreach(string path in owner.Usage.Recent)if(seen.Add(path))entries.Add(new Entry{Path=path,Name=Path.GetFileName(path),Detail=path});
            var cancellation=new CancellationTokenSource();string workspace=owner.WorkspaceRoot;bool scanning=!commands&&workspace!=null,limited=false;
            var dialog=new Window{Owner=owner,Title=title,Width=Math.Max(240,Math.Min(590,owner.ActualWidth-24)),Height=Math.Max(200,Math.Min(460,owner.ActualHeight-24)),WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false,ResizeMode=ResizeMode.NoResize};Theme.Install(dialog);Theme.Apply(dialog,owner.Appearance);
            var root=new DockPanel{Margin=new Thickness(16)};dialog.Content=root;
            var heading=new TextBlock{Text=title,FontSize=18,FontWeight=FontWeights.SemiBold,Margin=new Thickness(2,0,0,5)};DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);
            var hint=new TextBlock{Text=commands?"输入操作名称，无需记住菜单位置":"本次文件 / 最近文件 / 导入工作区；也可粘贴完整路径直接打开",FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(2,0,0,12)};hint.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");DockPanel.SetDock(hint,Dock.Top);root.Children.Add(hint);
            var search=new TextBox{FontSize=15,Padding=new Thickness(10,8,10,8)};AutomationProperties.SetName(search,commands?"搜索命令":"搜索文件或输入完整路径");DockPanel.SetDock(search,Dock.Top);root.Children.Add(search);
            var status=new TextBlock{Margin=new Thickness(4,8,4,0),FontSize=11,TextWrapping=TextWrapping.Wrap};status.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");DockPanel.SetDock(status,Dock.Bottom);root.Children.Add(status);
            var list=new ListBox{Margin=new Thickness(0,8,0,0),BorderThickness=new Thickness(0),Background=Brushes.Transparent};ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);list.SetResourceReference(Control.ForegroundProperty,"TextBrush");
            list.ItemContainerStyle=(Style)XamlReader.Parse(@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ListBoxItem'><Setter Property='HorizontalContentAlignment' Value='Stretch'/><Setter Property='Margin' Value='0,2'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBoxItem'><Border x:Name='row' Padding='{TemplateBinding Padding}' Background='Transparent' BorderBrush='Transparent' BorderThickness='1' CornerRadius='6'><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='row' Property='Background' Value='{DynamicResource HoverBrush}'/></Trigger><Trigger Property='IsSelected' Value='True'><Setter TargetName='row' Property='Background' Value='{DynamicResource HoverBrush}'/><Setter TargetName='row' Property='BorderBrush' Value='{DynamicResource AccentBrush}'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");root.Children.Add(list);
            Action refresh=delegate{string query=search.Text.Trim();var previous=list.SelectedItem as ListBoxItem;var selected=previous==null?null:previous.Tag as Entry;list.Items.Clear();var matches=entries.Where(e=>QuickOpenFiles.Matches(e.Name,(e.Path??"")+" "+e.Keywords,query)).Take(81).ToList();string path=commands?null:DirectPath(query);if(path!=null&&!matches.Any(e=>String.Equals(e.Path,path,StringComparison.OrdinalIgnoreCase)))matches.Insert(0,new Entry{Path=path,Name="打开此路径",Detail=path});foreach(var e in matches.Take(80)){var panel=new StackPanel();panel.Children.Add(new TextBlock{Text=e.Name+(e.Document!=null&&e.Document.IsDirty?" •":""),FontSize=13,FontWeight=FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis});var detail=new TextBlock{Text=e.Detail,FontSize=11,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(0,3,0,0)};detail.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");panel.Children.Add(detail);var row=new ListBoxItem{Content=panel,Tag=e,Padding=new Thickness(10,8,10,8),ToolTip=e.Detail};AutomationProperties.SetName(row,e.Name+" "+e.Detail);list.Items.Add(row);}if(list.Items.Count>0){list.SelectedIndex=0;for(int i=0;i<list.Items.Count;i++)if(Object.ReferenceEquals(((ListBoxItem)list.Items[i]).Tag,selected))list.SelectedIndex=i;}status.Text=scanning?"正在查找导入的工作区…":matches.Count==0?(commands?"没有匹配的操作":"没有匹配文件 · Ctrl+O 浏览，或输入完整路径"):(path!=null?"Enter 打开此路径 · 只读取指定目标":"↑↓ 选择   Enter "+(commands?"执行":"打开")+"   Esc 关闭");if(limited)status.Text+=" · 工作区最多 2000 个文件 / 6 层目录";};
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(70)};timer.Tick+=delegate{timer.Stop();refresh();};search.TextChanged+=delegate{timer.Stop();if(entries.Count<100)refresh();else timer.Start();};
            Action open=delegate{if(timer.IsEnabled){timer.Stop();refresh();}var row=list.SelectedItem as ListBoxItem;if(row==null)return;var e=(Entry)row.Tag;dialog.Close();owner.Dispatcher.BeginInvoke(new Action(delegate{if(e.Run!=null)e.Run();else if(e.Document!=null)owner.SelectDocument(e.Document,null);else owner.Observe(owner.OpenPathsAsync(new[]{e.Path},true));}));};
            list.MouseDoubleClick+=delegate{open();};search.KeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Down||e.Key==Key.Up){if(list.Items.Count>0){list.SelectedIndex=Math.Max(0,Math.Min(list.Items.Count-1,list.SelectedIndex+(e.Key==Key.Down?1:-1)));list.ScrollIntoView(list.SelectedItem);}e.Handled=true;}else if(e.Key==Key.Enter){open();e.Handled=true;}};list.KeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Enter){open();e.Handled=true;}};dialog.PreviewKeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Escape){dialog.Close();e.Handled=true;}};
            dialog.Closed+=delegate{timer.Stop();cancellation.Cancel();};dialog.Loaded+=async delegate{search.Focus();refresh();if(!scanning)return;try{var files=await Task.Run(()=>QuickOpenFiles.Collect(workspace,2000,cancellation.Token));if(cancellation.IsCancellationRequested)return;foreach(string file in files)if(seen.Add(file))entries.Add(new Entry{Path=file,Name=Path.GetFileName(file),Detail=file});limited=files.Count>=2000;scanning=false;refresh();}catch(OperationCanceledException){}catch{if(!cancellation.IsCancellationRequested){scanning=false;status.Text="无法读取工作区 · 可直接输入完整文件路径";}}};dialog.ShowDialog();
        }
    }
}
