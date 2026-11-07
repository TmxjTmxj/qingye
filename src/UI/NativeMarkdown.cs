using System;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Qingye.Core;
using W = System.Windows.Documents;

namespace Qingye.UI
{
    public sealed class MarkdownContent
    {
        public MarkdownDocument Tree;
        public string Source;
        public bool Truncated;
    }

    public sealed class NativeMarkdown
    {
        public const int MaxCharacters=512*1024;
        static readonly MarkdownPipeline pipeline=new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
        readonly string directory;
        readonly Action<string> openFile;
        string sourceText;
        int blocks,inlines,images;
        public bool Truncated {get;private set;}
        NativeMarkdown(string path,Action<string> open){directory=path==null?null:Path.GetDirectoryName(path);openFile=open;}

        public static MarkdownContent Parse(string text)
        {
            text=text??"";bool limited=text.Length>MaxCharacters;
            if(limited){int length=MaxCharacters;if(Char.IsHighSurrogate(text[length-1]))length--;text=text.Substring(0,length);}
            return new MarkdownContent{Tree=Markdown.Parse(text,pipeline),Source=text,Truncated=limited};
        }

        static readonly System.Text.RegularExpressions.Regex htmlBlockLine=new System.Text.RegularExpressions.Regex(@"(?m)^ {0,3}</?[A-Za-z][A-Za-z0-9-]*(\s[^>\r\n]*)?>");
        static readonly System.Text.RegularExpressions.Regex fencedBlock=new System.Text.RegularExpressions.Regex(@"(?ms)^[ \t]*(```|~~~)[^\r\n]*\r?\n.*?^[ \t]*\1[ \t]*\r?$");
        public static bool HasHtmlBlocks(string text){if(String.IsNullOrEmpty(text))return false;return htmlBlockLine.IsMatch(fencedBlock.Replace(text,""));}

        public static FlowDocumentScrollViewer Create(MarkdownContent content,string path,Action<string> open)
        {
            var renderer=new NativeMarkdown(path,open){sourceText=content.Source};var flow=EmptyDocument();
            renderer.AddBlocks(content.Tree,flow.Blocks,0);
            if(HasHtmlBlocks(content.Source))
            {
                var note=Theme.ContentToken(new W.Paragraph(new W.Run("此文件包含网页代码（HTML），为安全起见按纯文本显示；需要网页效果请用系统关联软件打开。")){FontSize=12},W.TextElement.ForegroundProperty,"MutedBrush");
                if(flow.Blocks.FirstBlock!=null)flow.Blocks.InsertBefore(flow.Blocks.FirstBlock,note);else flow.Blocks.Add(note);
            }
            if(content.Truncated || renderer.Truncated)flow.Blocks.Add(Theme.ContentToken(new W.Paragraph(new W.Run("阅读预览已限量显示；其余内容请在编辑区继续阅读。")){FontSize=12},W.TextElement.ForegroundProperty,"MutedBrush"));
            return Viewer(flow);
        }
        public static W.FlowDocument EmptyDocument()
        {
            var flow=new W.FlowDocument{FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI"),FontSize=16,LineHeight=28,TextAlignment=TextAlignment.Left,PagePadding=new Thickness(24,24,24,32),ColumnWidth=Double.NaN,Background=Brushes.Transparent};flow.SetResourceReference(W.FlowDocument.ForegroundProperty,"TextBrush");return flow;
        }
        public static FlowDocumentScrollViewer Viewer(W.FlowDocument flow)
        {
            var viewer=new FlowDocumentScrollViewer{Document=flow,IsToolBarVisible=false,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=Brushes.Transparent,BorderThickness=new Thickness(0),HorizontalAlignment=HorizontalAlignment.Stretch};
            viewer.SizeChanged+=delegate{double inset=viewer.ActualWidth<480?16:24;flow.PagePadding=new Thickness(inset,24,inset,32);};WheelScroll.Attach(viewer);return viewer;
        }

        void AddBlocks(ContainerBlock source,W.BlockCollection target,int depth)
        {
            if(depth>12){Truncated=true;return;}
            foreach(var block in source)
            {
                if(++blocks>1500){Truncated=true;break;}
                var table=block as Markdig.Extensions.Tables.Table;
                if(table!=null){AddTable(table,target,depth);continue;}
                var list=block as ListBlock;
                if(list!=null)
                {
                    var result=new W.List{MarkerStyle=list.IsOrdered?TextMarkerStyle.Decimal:TextMarkerStyle.Disc,Margin=new Thickness(0,8,0,14),Padding=new Thickness(23,0,0,0)};int start;if(list.IsOrdered && Int32.TryParse(list.OrderedStart,out start))result.StartIndex=Math.Max(1,start);
                    foreach(var child in list){var item=new W.ListItem();var container=child as ContainerBlock;if(container!=null)AddBlocks(container,item.Blocks,depth+1);if(item.Blocks.Count==0)item.Blocks.Add(new W.Paragraph());result.ListItems.Add(item);if(blocks>1500)break;}target.Add(result);continue;
                }
                var quote=block as QuoteBlock;
                if(quote!=null)
                {
                    var section=new W.Section{BorderThickness=new Thickness(3,0,0,0),Padding=new Thickness(16,0,0,0),Margin=new Thickness(0,10,0,18)};
                    section.SetResourceReference(W.Section.BorderBrushProperty,"QuoteBorder");
                    section.SetResourceReference(W.TextElement.ForegroundProperty,"QuoteText");
                    AddBlocks(quote,section.Blocks,depth+1);
                    target.Add(section);
                    continue;
                }
                var code=block as CodeBlock;
                if(code!=null){string value=code.Lines.ToString();if(value.Length>32768){value=value.Substring(0,32768)+"\n…";Truncated=true;}target.Add(Theme.ContentToken(new W.Paragraph(new W.Run(value)){FontFamily=new FontFamily("Consolas, Microsoft YaHei UI"),FontSize=14,LineHeight=22,Padding=new Thickness(16),Margin=new Thickness(0,8,0,20),KeepTogether=false},W.TextElement.BackgroundProperty,"CodeBackground"));continue;}
                if(block is ThematicBreakBlock){target.Add(Theme.ContentToken(new W.Paragraph{BorderThickness=new Thickness(0,0,0,1),Margin=new Thickness(0,18,0,18)},W.Paragraph.BorderBrushProperty,"Rule"));continue;}
                var leaf=block as LeafBlock;
                if(leaf!=null)
                {
                    var paragraph=new W.Paragraph{Margin=new Thickness(0,0,0,16)};var heading=block as HeadingBlock;
                    if(heading!=null){paragraph.FontSize=new[]{30,24,20,18,16,14}[Math.Min(6,Math.Max(1,heading.Level))-1];paragraph.FontWeight=FontWeights.SemiBold;paragraph.LineHeight=paragraph.FontSize*1.4;paragraph.Margin=new Thickness(0,heading.Level==1?4:24,0,14);paragraph.Tag="line:"+(heading.Line+1);}
                    if(leaf.Inline!=null)AddInlines(leaf.Inline,paragraph.Inlines,0);else paragraph.Inlines.Add(new W.Run(leaf.Lines.ToString()));target.Add(paragraph);continue;
                }
                var other=block as ContainerBlock;if(other!=null)AddBlocks(other,target,depth+1);
            }
        }

        void AddTable(Markdig.Extensions.Tables.Table source,W.BlockCollection target,int depth)
        {
            var table=new W.Table{CellSpacing=0,FontSize=14,Margin=new Thickness(0,6,0,22)};var group=new W.TableRowGroup();table.RowGroups.Add(group);int rows=0;
            foreach(var block in source)
            {
                if(++rows>500){Truncated=true;break;}var row=block as Markdig.Extensions.Tables.TableRow;if(row==null)continue;var result=new W.TableRow{FontWeight=row.IsHeader?FontWeights.SemiBold:FontWeights.Normal,Background=Brushes.Transparent};int columns=0;
                if(row.IsHeader)result.SetResourceReference(W.TextElement.BackgroundProperty,"TableHeader");
                foreach(var blockCell in row){if(++columns>50){Truncated=true;break;}var cell=blockCell as ContainerBlock;if(cell==null)continue;var output=Theme.ContentToken(new W.TableCell{Padding=new Thickness(9),BorderThickness=new Thickness(0,0,0,1)},W.TableCell.BorderBrushProperty,"Rule");AddBlocks(cell,output.Blocks,depth+1);foreach(W.Block content in output.Blocks)content.Margin=new Thickness(0);if(output.Blocks.Count==0)output.Blocks.Add(new W.Paragraph());result.Cells.Add(output);}
                group.Rows.Add(result);if(blocks>1500)break;
            }
            target.Add(table);
        }

        void AddInlines(ContainerInline source,W.InlineCollection target,int depth)
        {
            if(depth>24){Truncated=true;return;}
            for(var inline=source.FirstChild;inline!=null;inline=inline.NextSibling)
            {
                if(++inlines>20000){Truncated=true;break;}
                var literal=inline as LiteralInline;if(literal!=null){target.Add(new W.Run(literal.Content.ToString()));continue;}
                var entity=inline as HtmlEntityInline;if(entity!=null){target.Add(new W.Run(entity.Transcoded.ToString()));continue;}
                var automatic=inline as AutolinkInline;if(automatic!=null){string url=automatic.IsEmail && !automatic.Url.StartsWith("mailto:",StringComparison.OrdinalIgnoreCase)?"mailto:"+automatic.Url:automatic.Url;var hyperlink=new W.Hyperlink(new W.Run(automatic.Url)){ToolTip=url};hyperlink.SetResourceReference(W.TextElement.ForegroundProperty,"AccentBrush");hyperlink.Click+=delegate{OpenLink(url);};target.Add(hyperlink);continue;}
                var code=inline as CodeInline;if(code!=null){target.Add(Theme.ContentToken(new W.Run(code.Content){FontFamily=new FontFamily("Consolas"),FontSize=14},W.TextElement.BackgroundProperty,"CodeBackground"));continue;}
                var line=inline as LineBreakInline;if(line!=null){if(line.IsHard)target.Add(new W.LineBreak());else target.Add(new W.Run(" "));continue;}
                var emphasis=inline as EmphasisInline;if(emphasis!=null){var span=new W.Span();if(emphasis.DelimiterChar=='~')span.TextDecorations=TextDecorations.Strikethrough;else{span.FontWeight=emphasis.DelimiterCount>=2?FontWeights.Bold:FontWeights.Normal;span.FontStyle=emphasis.DelimiterCount==1 || emphasis.DelimiterCount==3?FontStyles.Italic:FontStyles.Normal;}AddInlines(emphasis,span.Inlines,depth+1);target.Add(span);continue;}
                var link=inline as LinkInline;if(link!=null){if(link.IsImage && TryImage(link,target))continue;var hyperlink=new W.Hyperlink{ToolTip=link.Url};hyperlink.SetResourceReference(W.TextElement.ForegroundProperty,"AccentBrush");AddInlines(link,hyperlink.Inlines,depth+1);if(hyperlink.Inlines.Count==0)hyperlink.Inlines.Add(new W.Run(link.IsImage?"图片："+link.Url:link.Url??"链接"));string url=link.Url;hyperlink.Click+=delegate{OpenLink(url);};target.Add(hyperlink);continue;}
                var container=inline as ContainerInline;if(container!=null)AddInlines(container,target,depth+1);else if(inline.Span.Start>=0 && inline.Span.End>=inline.Span.Start && sourceText!=null && inline.Span.End<sourceText.Length)target.Add(new W.Run(sourceText.Substring(inline.Span.Start,inline.Span.End-inline.Span.Start+1)));
            }
        }

        string LocalPath(string url)
        {
            if(directory==null || String.IsNullOrEmpty(url) || url.StartsWith("#") || Uri.IsWellFormedUriString(url,UriKind.Absolute))return null;
            try{string path=Path.GetFullPath(Path.Combine(directory,Uri.UnescapeDataString(url.Split('#')[0]).Replace('/',Path.DirectorySeparatorChar)));return FileService.IsInsideRoot(directory,path)?path:null;}catch{return null;}
        }

        bool TryImage(LinkInline link,W.InlineCollection target)
        {
            string path=LocalPath(link.Url);if(path==null || !File.Exists(path) || ++images>12)return false;
            try
            {
                if(new FileInfo(path).Length>16L*1024*1024)return false;
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                {
                    var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);int width=decoder.Frames[0].PixelWidth,height=decoder.Frames[0].PixelHeight;if((long)width*height>20000000)return false;stream.Position=0;
                    var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=Math.Min(width,800);bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();target.Add(new W.InlineUIContainer(new Image{Source=bitmap,MaxWidth=640,MaxHeight=500,Stretch=Stretch.Uniform,Margin=new Thickness(0,10,0,10)}));return true;
                }
            }
            catch{return false;}
        }

        void OpenLink(string url)
        {
            Uri uri;try{if(Uri.TryCreate(url,UriKind.Absolute,out uri) && (uri.Scheme=="https" || uri.Scheme=="http" || uri.Scheme=="mailto")){Process.Start(uri.AbsoluteUri);return;}string path=LocalPath(url);if(path!=null && File.Exists(path) && openFile!=null)openFile(path);}catch{}
        }
    }
}
