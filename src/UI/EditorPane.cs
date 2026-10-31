using System;
using System.IO;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using Qingye.Core;
namespace Qingye.UI
{
    public class EditorPane : Border
    {
        public TextEditor Editor { get; private set; }
        public Document BoundDocument { get; private set; }
        public string PaneId { get; private set; }
        public event Action<EditorPane> Activated;
        readonly TextBlock title;
        readonly TextBlock language;
        readonly ContentControl fileSymbol=new ContentControl{Width=18,Height=20,Margin=new Thickness(0,0,10,0),VerticalAlignment=VerticalAlignment.Center};
        readonly Dictionary<Document,TextDocument> buffers;
        readonly Action changed;
        readonly RowDefinition headerRow;
        readonly FrameworkElement headerElement,headerLine;
        Point dragStart;bool pressed;
        public EditorPane(string paneId,Dictionary<Document,TextDocument> shared,Action onChanged,Action<string> placement)
        {
            PaneId=paneId;buffers=shared;changed=onChanged;BorderThickness=new Thickness(0);SetResourceReference(BorderBrushProperty,"LineBrush");SetResourceReference(BackgroundProperty,"SurfaceBrush");
            var grid=new Grid();headerRow=new RowDefinition{Height=new GridLength(40)};grid.RowDefinitions.Add(headerRow);grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});grid.RowDefinitions.Add(new RowDefinition());Child=grid;
            var header=new DockPanel{LastChildFill=true,Margin=new Thickness(10,0,6,0),Tag=paneId};Grid.SetRow(header,0);grid.Children.Add(header);
            headerElement=header;
            var grip=Theme.Icon("\uE784",16);grip.Margin=new Thickness(2,0,11,0);grip.SetResourceReference(Control.ForegroundProperty,"MutedBrush");header.Children.Add(grip);
            header.Children.Add(fileSymbol);
            var menu=Theme.Button("\uE712","",delegate{});menu.Padding=new Thickness(8,5,8,5);DockPanel.SetDock(menu,Dock.Right);header.Children.Add(menu);
            var context=new ContextMenu();context.Items.Add(Theme.Menu("代码上下排列",delegate{placement("vertical");}));context.Items.Add(Theme.Menu("代码左右排列",delegate{placement("horizontal");}));context.Items.Add(Theme.Menu("交换两个文件",delegate{placement("swap");}));context.Items.Add(Theme.Menu("只保留一栏",delegate{placement("single");}));menu.Click+=delegate{context.PlacementTarget=menu;context.IsOpen=true;};
            language=new TextBlock{FontSize=11,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,0,0)};language.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");DockPanel.SetDock(language,Dock.Right);header.Children.Add(language);
            title=new TextBlock{Text="未命名",VerticalAlignment=VerticalAlignment.Center,FontWeight=FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis};header.Children.Add(title);
            var line=Theme.Line();headerLine=line;Grid.SetRow(line,1);grid.Children.Add(line);
            Editor=new TextEditor{FontSize=15,ShowLineNumbers=true,WordWrap=true,Padding=new Thickness(16,12,16,10),HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            Editor.SetResourceReference(Control.BackgroundProperty,"SurfaceBrush");Editor.SetResourceReference(Control.ForegroundProperty,"TextBrush");Editor.SetResourceReference(TextEditor.LineNumbersForegroundProperty,"MutedBrush");
            Editor.Options.EnableHyperlinks=false;Editor.Options.EnableEmailHyperlinks=false;Editor.Options.IndentationSize=4;Editor.Options.ConvertTabsToSpaces=false;
            Editor.TextArea.SelectionCornerRadius=2;Editor.TextArea.Caret.PositionChanged+=delegate{if(Activated!=null && Editor.IsKeyboardFocusWithin)Activated(this);};
            Editor.TextArea.SelectionChanged+=delegate{if(Activated!=null && Editor.IsKeyboardFocusWithin)Activated(this);};
            Editor.GotKeyboardFocus+=delegate{if(Activated!=null)Activated(this);};Grid.SetRow(Editor,2);grid.Children.Add(Editor);
            header.MouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e){dragStart=e.GetPosition(header);pressed=true;};
            header.MouseLeftButtonUp+=delegate{pressed=false;};
            header.MouseMove+=delegate(object sender,MouseEventArgs e){if(pressed && e.LeftButton==MouseButtonState.Pressed && (e.GetPosition(header)-dragStart).Length>7){pressed=false;DragDrop.DoDragDrop(header,new DataObject("QingyePane",paneId),DragDropEffects.Move);}};
            header.ToolTip="拖动标题调整位置；右侧菜单可切换排列";
        }
        public void Bind(Document doc)
        {
            BoundDocument=doc;TextDocument buffer;
            if(!buffers.TryGetValue(doc,out buffer))
            {
                buffer=new TextDocument(doc.Text);buffer.UndoStack.MarkAsOriginalFile();buffers.Add(doc,buffer);
                buffer.TextChanged+=delegate{doc.Modified=!buffer.UndoStack.IsOriginalFile;changed();};
                buffer.UndoStack.PropertyChanged+=delegate(object sender,System.ComponentModel.PropertyChangedEventArgs e){if(e.PropertyName=="IsOriginalFile"){doc.Modified=!buffer.UndoStack.IsOriginalFile;changed();}};
            }
            Editor.Document=buffer;Editor.IsReadOnly=doc.IsReadOnly;UpdateTitle();
            string extension=FilePresentation.Extension(doc);
            fileSymbol.Content=new IconView(FilePresentation.For(doc).Icon,18){Foreground=Theme.Brush("#55776A")};
            Editor.SyntaxHighlighting=doc.ByteLength>20L*1024*1024 || doc.Kind!=DocumentKind.Text?null:SyntaxDefinition.For(extension);
            SyntaxDefinition.UseTheme(Editor.SyntaxHighlighting);
            language.Text=doc.Kind==DocumentKind.Text?FilePresentation.For(doc).Name:"只读";
        }
        public void UpdateTitle(){if(BoundDocument!=null)title.Text=BoundDocument.Title+(BoundDocument.IsDirty?"  •":"");}
        public void Unbind(){BoundDocument=null;Editor.Document=null;Editor.IsReadOnly=true;title.Text="";language.Text="";fileSymbol.Content=null;}
        public void Apply(Settings settings)
        {
            bool prose=settings.FileAwareDefaults && BoundDocument!=null && FilePresentation.For(BoundDocument).Prose;string family=prose?"Segoe UI":settings.FontFamily;
            var font=new System.Windows.Media.FontFamily();font.FamilyMaps.Add(new System.Windows.Media.FontFamilyMap{Unicode="0000-007F",Target=family});font.FamilyMaps.Add(new System.Windows.Media.FontFamilyMap{Unicode="0080-10FFFF",Target=family=="Consolas" || family=="Cascadia Mono" || family=="Segoe UI"?"Microsoft YaHei UI":family});Editor.FontSize=settings.FontSize+(prose?1:0);Editor.FontFamily=font;
            Editor.WordWrap=(settings.FileAwareDefaults?prose:settings.Wrap) && (BoundDocument==null || BoundDocument.ByteLength<=20L*1024*1024 && (Editor.Document.TextLength<65536 || Editor.Document.GetText(0,65536).IndexOf('\n')>=0));Editor.ShowLineNumbers=settings.LineNumbers && !prose;
            Editor.Padding=prose?new Thickness(32,24,32,24):new Thickness(20,18,20,18);
            Editor.TextArea.SelectionBrush=TryFindResource("HoverBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Transparent;
            Editor.TextArea.SelectionForeground=TryFindResource("TextBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Transparent;
            Editor.TextArea.TextView.Redraw();
        }
        public void ShowHeader(bool visible){headerRow.Height=new GridLength(visible?40:0);headerElement.Visibility=headerLine.Visibility=visible?Visibility.Visible:Visibility.Collapsed;}
    }
}
