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
    internal sealed class DocumentBar : UserControl
    {
        readonly MainWindow owner;
        Document displayedDocument;string displayedTitle,displayedPath,displayedLayout,displayedIcon;DocumentKind displayedKind;bool displayedDirty;
        readonly Grid documentBar;
        readonly DockPanel fileInfo;
        readonly StackPanel viewActions;
        readonly TextBlock documentName=new TextBlock(),documentType=new TextBlock();
        readonly ContentControl documentIcon=new ContentControl();
        internal readonly Button readViewButton,sourceViewButton,splitViewButton,wysiwygButton,systemOpenButton,sideButton;
        internal DocumentBar(MainWindow window)
        {
            owner=window;
            HorizontalContentAlignment=HorizontalAlignment.Stretch;
            documentBar=new Grid
            {
                Margin=new Thickness(20,8,16,8)
            };
            documentBar.ColumnDefinitions.Add(new ColumnDefinition());
            documentBar.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width=GridLength.Auto
            });
            documentBar.RowDefinitions.Add(new RowDefinition
            {
                Height=GridLength.Auto
            });
            documentBar.RowDefinitions.Add(new RowDefinition
            {
                Height=GridLength.Auto
            });
            Content=documentBar;
            fileInfo=new DockPanel
            {
                Margin=new Thickness(0,0,16,0)
            };
            sideButton=Theme.Button("panel-left","",delegate
            {
                owner.ToggleSidePanel();
            });
            sideButton.Width=30;
            sideButton.Height=30;
            sideButton.MinHeight=0;
            sideButton.Padding=new Thickness(0);
            sideButton.Margin=new Thickness(0,0,12,0);
            sideButton.ToolTip="文件与大纲  Ctrl+Shift+B";
            System.Windows.Automation.AutomationProperties.SetName(sideButton,"文件与大纲");
            Theme.Tint(sideButton,"MutedBrush");
            DockPanel.SetDock(sideButton,Dock.Left);
            fileInfo.Children.Add(sideButton);
            documentIcon.Width=22;
            documentIcon.Margin=new Thickness(0,0,12,0);
            documentIcon.VerticalAlignment=VerticalAlignment.Center;
            fileInfo.Children.Add(documentIcon);
            var fileText=new StackPanel
            {
                VerticalAlignment=VerticalAlignment.Center
            };
            documentName.FontSize=14;
            documentName.FontWeight=FontWeights.SemiBold;
            documentName.TextTrimming=TextTrimming.CharacterEllipsis;
            documentType.FontSize=11;
            documentType.Margin=new Thickness(0,3,0,0);
            documentType.TextTrimming=TextTrimming.CharacterEllipsis;
            documentType.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");
            fileText.Children.Add(documentName);
            fileText.Children.Add(documentType);
            fileInfo.Children.Add(fileText);
            documentBar.Children.Add(fileInfo);
            viewActions=new StackPanel
            {
                Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center
            };
            Grid.SetColumn(viewActions,1);
            documentBar.Children.Add(viewActions);
            var segments=new StackPanel
            {
                Orientation=Orientation.Horizontal
            };
            var segmentBorder=new Border
            {
                Child=segments,CornerRadius=new CornerRadius(6),Padding=new Thickness(3)
            };
            segmentBorder.SetResourceReference(Border.BackgroundProperty,"HoverBrush");
            viewActions.Children.Add(segmentBorder);
            readViewButton=Theme.Button(null,"阅读",delegate
            {
                owner.SetViewMode("reader");
            });
            sourceViewButton=Theme.Button(null,"源码",delegate
            {
                owner.SetViewMode("single");
            });
            splitViewButton=Theme.Button(null,"对照",delegate
            {
                owner.SetViewMode("preview");
            });
            wysiwygButton=Theme.Button(null,"编辑",delegate
            {
                owner.SetViewMode("wysiwyg");
            });
            wysiwygButton.ToolTip="原位编辑（Markdown）Ctrl+Shift+E";
            foreach(var button in new[]
            {
                readViewButton,sourceViewButton,splitViewButton,wysiwygButton
            }
            )
            {
                button.MinHeight=30;
                button.Padding=new Thickness(12,4,12,4);
                button.Margin=new Thickness(1,0,1,0);
                button.FontSize=12;
                segments.Children.Add(button);
            }
            owner.RegisterName("ReadViewButton",readViewButton);
            owner.RegisterName("SourceViewButton",sourceViewButton);
            owner.RegisterName("SplitViewButton",splitViewButton);
            systemOpenButton=Theme.Button("arrow-up-right","",delegate
            {
                if(owner.Current!=null && owner.Current.Path!=null && File.Exists(owner.Current.Path))try
                {
                    Process.Start(owner.Current.Path);
                }
                catch(Exception ex)
                {
                    owner.SetNotice("无法用系统软件打开："+ex.Message);
                }
            });
            systemOpenButton.ToolTip="使用系统关联软件打开";
            systemOpenButton.Margin=new Thickness(10,0,0,0);
            System.Windows.Automation.AutomationProperties.SetName(systemOpenButton,"使用系统关联软件打开");
            viewActions.Children.Add(systemOpenButton);
        }
        internal object FileIcon{get{return documentIcon.Content;}}
        internal void Update(Document doc)
        {
            var Appearance=owner.Appearance;
            if(Object.ReferenceEquals(displayedDocument,doc)&&displayedTitle==doc.Title&&displayedPath==doc.Path&&displayedKind==doc.Kind&&displayedDirty==doc.IsDirty&&displayedLayout==Appearance.Layout)return;
            displayedDocument=doc;displayedTitle=doc.Title;displayedPath=doc.Path;displayedKind=doc.Kind;displayedDirty=doc.IsDirty;displayedLayout=Appearance.Layout;
            var profile=FilePresentation.For(doc);
            documentName.Text=doc.Title+(doc.IsDirty?"  •":"");
            documentName.ToolTip=doc.Path??"未保存文档";
            documentType.Text=profile.Name+"  /  "+profile.Description;
            documentType.ToolTip=documentType.Text;
            if(displayedIcon!=profile.Icon){var icon=new IconView(profile.Icon,22);icon.SetResourceReference(Control.ForegroundProperty,"AccentBrush");documentIcon.Content=icon;displayedIcon=profile.Icon;}
            readViewButton.Content=profile.ReaderLabel??"阅读";
            readViewButton.IsEnabled=profile.CanRead;
            splitViewButton.IsEnabled=profile.CanRead && doc.Kind==DocumentKind.Text;
            wysiwygButton.IsEnabled=profile.CanWysiwyg;
            sourceViewButton.Content=profile.Prose || doc.Kind==DocumentKind.Xlsx?"文本":doc.Kind==DocumentKind.Binary?"十六进制":"源码";
            sourceViewButton.IsEnabled=doc.Kind!=DocumentKind.Image && doc.Kind!=DocumentKind.Pdf && doc.Kind!=DocumentKind.Rtf && doc.Kind!=DocumentKind.Epub;
            System.Windows.Automation.AutomationProperties.SetName(readViewButton,(readViewButton.Content as string)??"阅读");
            System.Windows.Automation.AutomationProperties.SetName(sourceViewButton,(sourceViewButton.Content as string)??"源码");
            systemOpenButton.IsEnabled=doc.Path!=null;
            foreach(var button in new[]
            {
                readViewButton,sourceViewButton,splitViewButton,wysiwygButton
            }
            )
            {
                bool selected=button==readViewButton?Appearance.Layout=="reader":button==splitViewButton?Appearance.Layout=="preview":button==wysiwygButton?Appearance.Layout=="wysiwyg":Appearance.Layout=="single" || Appearance.Layout=="compare";
                button.SetResourceReference(Control.BackgroundProperty,selected?"SurfaceBrush":"HoverBrush");
                button.FontWeight=selected?FontWeights.SemiBold:FontWeights.Normal;
                System.Windows.Automation.AutomationProperties.SetHelpText(button,selected?"当前视图":"切换视图");
            }
        }
        internal void Resize(double width)
        {
            bool stacked=width<620;
            documentBar.Margin=stacked?new Thickness(12,6,12,8):new Thickness(20,8,16,8);
            Grid.SetColumnSpan(fileInfo,stacked?2:1);
            Grid.SetRow(viewActions,stacked?1:0);
            Grid.SetColumn(viewActions,stacked?0:1);
            Grid.SetColumnSpan(viewActions,stacked?2:1);
            viewActions.HorizontalAlignment=stacked?HorizontalAlignment.Left:HorizontalAlignment.Right;
            viewActions.Margin=new Thickness(0,stacked?6:0,0,0);
            documentType.Visibility=width<480?Visibility.Collapsed:Visibility.Visible;
            systemOpenButton.Visibility=width<320?Visibility.Collapsed:Visibility.Visible;
            wysiwygButton.Visibility=width<420?Visibility.Collapsed:Visibility.Visible;
            foreach(var button in new[]
            {
                readViewButton,sourceViewButton,splitViewButton,wysiwygButton
            }
            )button.Padding=new Thickness(width<320?8:12,4,width<320?8:12,4);
        }
    }
}
