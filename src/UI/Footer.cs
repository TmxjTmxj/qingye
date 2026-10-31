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
    internal sealed class Footer : UserControl
    {
        readonly MainWindow owner;
        internal readonly TextBlock position=new TextBlock(),notice=new TextBlock(),sessionHint=new TextBlock(),breadcrumb=new TextBlock();
        internal readonly Button encodingButton,newlineButton,readOnlyButton;
        readonly StackPanel pageControls=new StackPanel
        {
            Orientation=Orientation.Horizontal,Visibility=Visibility.Collapsed
        };
        internal Footer(MainWindow window)
        {
            owner=window;
            HorizontalContentAlignment=HorizontalAlignment.Stretch;
            var footer=new DockPanel
            {
                LastChildFill=true,Margin=new Thickness(14,0,12,0)
            };
            var footerBorder=new Border
            {
                BorderThickness=new Thickness(0,1,0,0),Child=footer
            };
            footerBorder.SetResourceReference(Border.BorderBrushProperty,"LineBrush");
            Content=footerBorder;
            sessionHint.Text="本次阅读 · 不保留文件列表";
            sessionHint.FontSize=11;
            sessionHint.VerticalAlignment=VerticalAlignment.Center;
            sessionHint.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");
            sessionHint.ToolTip="默认打开一个文件。文件与工作区列表退出即清空。";
            DockPanel.SetDock(sessionHint,Dock.Right);
            footer.Children.Add(sessionHint);
            breadcrumb.FontSize=12;
            breadcrumb.VerticalAlignment=VerticalAlignment.Center;
            breadcrumb.MaxWidth=360;
            breadcrumb.TextTrimming=TextTrimming.CharacterEllipsis;
            breadcrumb.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");
            footer.Children.Add(breadcrumb);
            encodingButton=Theme.Button(null,"UTF-8",ShowEncoding);
            encodingButton.Padding=new Thickness(6,3,6,3);
            encodingButton.FontSize=11;
            DockPanel.SetDock(encodingButton,Dock.Right);
            footer.Children.Add(encodingButton);
            newlineButton=Theme.Button(null,"CRLF",ShowNewlines);
            newlineButton.Padding=new Thickness(6,3,6,3);
            newlineButton.FontSize=11;
            DockPanel.SetDock(newlineButton,Dock.Right);
            footer.Children.Add(newlineButton);
            position.FontSize=11;
            position.VerticalAlignment=VerticalAlignment.Center;
            position.Margin=new Thickness(10,0,16,0);
            position.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");
            DockPanel.SetDock(position,Dock.Right);
            footer.Children.Add(position);
            var readOnly=Theme.Button(null,"锁定",delegate
            {
                if(owner.Current!=null && owner.Current.Kind==DocumentKind.Text)
                {
                    owner.Current.IsReadOnly=!owner.Current.IsReadOnly;
                    owner.primary.Editor.IsReadOnly=owner.primary.BoundDocument.IsReadOnly;
                    if(owner.secondary.BoundDocument!=null)owner.secondary.Editor.IsReadOnly=owner.secondary.BoundDocument.IsReadOnly;
                    owner.RichEditor.IsEnabled=!owner.Current.IsReadOnly;
                    owner.UpdateStatus();
                }
            });
            readOnly.FontSize=11;
            readOnly.Padding=new Thickness(6,3,6,3);
            readOnly.ToolTip="切换文本只读 / 可编辑";
            footer.Children.Add(readOnly);
            readOnlyButton=readOnly;
            pageControls.Children.Add(Theme.Button(null,"上一段",delegate
            {
                owner.Observe(owner.ChangePageAsync(-1));
            }
            ));
            pageControls.Children.Add(Theme.Button(null,"下一段",delegate
            {
                owner.Observe(owner.ChangePageAsync(1));
            }
            ));
            footer.Children.Add(pageControls);
            notice.FontSize=11;
            notice.VerticalAlignment=VerticalAlignment.Center;
            notice.TextTrimming=TextTrimming.CharacterEllipsis;
            notice.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");
            footer.Children.Add(notice);
        }
        internal void Update(Document doc,EditorPane active,string queueNotice)
        {
            breadcrumb.Text=(owner.WorkspaceRoot==null?"本次阅读":Path.GetFileName(owner.WorkspaceRoot))+"  ›  "+doc.Title;
            encodingButton.Content=doc.EncodingName.ToUpperInvariant()+(doc.HasBom?" BOM":"");
            newlineButton.Content=doc.NewLine=="\n"?"LF":doc.NewLine=="\r"?"CR":"CRLF";
            position.Text=owner.Appearance.Layout=="reader"?(doc.IsReadOnly?"只读查看":"阅读视图 · 切换源码可编辑"):owner.Appearance.Layout=="wysiwyg"?(doc.IsReadOnly?"只读查看":"原位编辑 · 保存时按支持的 Markdown 子集整理格式"):"行 "+active.Editor.TextArea.Caret.Line+"，列 "+active.Editor.TextArea.Caret.Column+" · "+(active.Editor.Document==null?0:active.Editor.Document.TextLength)+" 字符"+(active.Editor.SelectionLength>0?" · 选中 "+active.Editor.SelectionLength:"")+(doc.IsReadOnly?" · 只读":"");
            owner.SaveButton.IsEnabled=!doc.IsReadOnly;
            bool textFile=doc.Kind==DocumentKind.Text || doc.Kind==DocumentKind.PagedText;
            encodingButton.Visibility=newlineButton.Visibility=textFile?Visibility.Visible:Visibility.Collapsed;
            readOnlyButton.Content=doc.IsReadOnly?"只读":"可编辑";
            readOnlyButton.IsEnabled=doc.Kind==DocumentKind.Text;
            notice.Text=queueNotice??doc.Notice??(doc.ByteLength>0?FileService.FormatSize(doc.ByteLength):"直接输入或打开文件");
            pageControls.Visibility=doc.Kind==DocumentKind.PagedText?Visibility.Visible:Visibility.Collapsed;
            Resize(owner.ActualWidth);
        }
        internal void Resize(double width)
        {
            sessionHint.Visibility=width<1080?Visibility.Collapsed:Visibility.Visible;
            breadcrumb.MaxWidth=Math.Max(40,Math.Min(360,width*.25));
            position.Visibility=width<720?Visibility.Collapsed:Visibility.Visible;
            notice.Visibility=width<560?Visibility.Collapsed:Visibility.Visible;
            if(owner.Current!=null)
            {
                bool textFile=owner.Current.Kind==DocumentKind.Text || owner.Current.Kind==DocumentKind.PagedText;
                encodingButton.Visibility=newlineButton.Visibility=textFile && width>=560?Visibility.Visible:Visibility.Collapsed;
            }
        }
        internal void SetNotice(string message)
        {
            notice.Text=message;
            notice.ToolTip=message;
        }
        void ShowEncoding()
        {
            var menu=new ContextMenu();
            foreach(var item in new[]{new[]{"utf-8","UTF-8"},new[]{"utf-16","UTF-16 LE"},new[]{"unicodeFFFE","UTF-16 BE"},new[]{"gb18030","GB18030"},new[]{"windows-1252","Windows-1252"}})
            {
                string name=item[0];var group=new MenuItem{Header=item[1]};
                group.Items.Add(Theme.Menu("以此编码重新读取",delegate{owner.Observe(owner.ReloadEncodingAsync(name));}));
                group.Items.Add(Theme.Menu("以此编码保存",delegate{if(owner.Current!=null && !owner.Current.IsReadOnly){owner.Current.EncodingName=name;owner.Current.HasBom=name=="utf-16" || name=="unicodeFFFE";owner.Current.MetadataModified=true;owner.UpdateStatus();owner.RefreshTabs();}}));
                menu.Items.Add(group);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Theme.Menu("切换 UTF-8 BOM",delegate{if(owner.Current!=null && !owner.Current.IsReadOnly){owner.Current.EncodingName="utf-8";owner.Current.HasBom=!owner.Current.HasBom;owner.Current.MetadataModified=true;owner.UpdateStatus();owner.RefreshTabs();}}));
            menu.PlacementTarget=encodingButton;menu.IsOpen=true;
        }
        void ShowNewlines()
        {
            var menu=new ContextMenu();
            foreach(var entry in new[]{new[]{"\r\n","Windows CRLF"},new[]{"\n","Unix LF"},new[]{"\r","Classic CR"}})
            {
                string newline=entry[0];
                menu.Items.Add(Theme.Menu(entry[1],delegate{if(owner.Current!=null && !owner.Current.IsReadOnly){owner.ActiveEditor.Document.Replace(0,owner.ActiveEditor.Document.TextLength,FileService.NormalizeNewLines(owner.ActiveEditor.Text,newline));owner.Current.NewLine=newline;owner.Current.Modified=true;owner.UpdateStatus();}}));
            }
            menu.PlacementTarget=newlineButton;menu.IsOpen=true;
        }
    }
}
