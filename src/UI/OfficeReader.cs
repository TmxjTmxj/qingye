using System;
using System.Linq;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using W=System.Windows.Documents;
namespace Qingye.UI
{
    public static class OfficeReader
    {
        sealed class Budget{public int Blocks,Runs,Inlines,Characters;public bool Limited;}
        public static FlowDocumentScrollViewer Create(XElement body)
        {
            var flow=NativeMarkdown.EmptyDocument();var budget=new Budget();if(body!=null)AddBlocks(body,flow.Blocks,budget,0);
            if(budget.Limited)flow.Blocks.Add(new W.Paragraph(new W.Run("部分内容超出基础预览范围，请用系统软件查看完整文档。")){FontSize=12});
            if(flow.Blocks.Count==0)flow.Blocks.Add(new W.Paragraph(new W.Run("此文档没有可预览的正文，请用系统关联软件打开。")));
            return NativeMarkdown.Viewer(flow);
        }
        static void AddBlocks(XElement source,W.BlockCollection target,Budget budget,int depth)
        {
            if(depth>12){budget.Limited=true;return;}var word=source.Name.Namespace;
            foreach(var block in source.Elements())
            {
                if(++budget.Blocks>1500 || budget.Characters>=512*1024){budget.Limited=true;break;}
                if(block.Name==word+"p")target.Add(Paragraph(block,budget));
                else if(block.Name==word+"tbl")
                {
                    var table=new W.Table{CellSpacing=0,FontSize=14,Margin=new Thickness(0,8,0,20)};var group=new W.TableRowGroup();table.RowGroups.Add(group);
                    int rows=0;foreach(var row in block.Elements(word+"tr")){if(++rows>500 || budget.Blocks>=1500){budget.Limited=true;break;}var output=new W.TableRow();int columns=0;foreach(var cell in row.Elements(word+"tc")){if(++columns>50 || ++budget.Blocks>=1500){budget.Limited=true;break;}var c=new W.TableCell{Padding=new Thickness(10),BorderThickness=new Thickness(0,0,0,1)};c.SetResourceReference(W.TextElement.ForegroundProperty,"TextBrush");c.SetResourceReference(W.TableCell.BorderBrushProperty,"Rule");AddBlocks(cell,c.Blocks,budget,depth+1);if(c.Blocks.Count==0)c.Blocks.Add(new W.Paragraph());output.Cells.Add(c);}if(output.Cells.Count>0)group.Rows.Add(output);}if(group.Rows.Count>0)target.Add(table);
                }
                else if(new[]{"sdt","sdtContent","customXml","ins","moveTo"}.Contains(block.Name.LocalName))AddBlocks(block,target,budget,depth+1);
                else if(block.Name.LocalName!="sectPr" && block.Name.LocalName!="sdtPr" && block.Name.LocalName!="tcPr")budget.Limited=true;
            }
        }
        static W.Paragraph Paragraph(XElement source,Budget budget)
        {
            var word=source.Name.Namespace;var p=new W.Paragraph{Margin=new Thickness(0,0,0,14)};var properties=source.Element(word+"pPr");string style=properties==null?"":(string)(properties.Element(word+"pStyle")??new XElement("x")).Attribute(word+"val")??"";
            int level;string digits=new string(style.Where(Char.IsDigit).ToArray());if(style.IndexOf("heading",StringComparison.OrdinalIgnoreCase)>=0 || style.Contains("标题")){if(!Int32.TryParse(digits,out level))level=2;p.FontSize=level==1?30:level==2?24:19;p.FontWeight=FontWeights.SemiBold;p.Margin=new Thickness(0,16,0,12);}
            if(style.Equals("Title",StringComparison.OrdinalIgnoreCase)){p.FontSize=32;p.FontWeight=FontWeights.SemiBold;}
            if(properties!=null){string align=(string)(properties.Element(word+"jc")??new XElement("x")).Attribute(word+"val");if(align=="center")p.TextAlignment=TextAlignment.Center;else if(align=="right")p.TextAlignment=TextAlignment.Right;}
            foreach(var run in source.Descendants(word+"r"))
            {
                if(++budget.Runs>20000 || budget.Characters>=512*1024){budget.Limited=true;break;}
                var span=new W.Span();var format=run.Element(word+"rPr");
                if(format!=null){if(On(format.Element(word+"b")))span.FontWeight=FontWeights.Bold;if(On(format.Element(word+"i")))span.FontStyle=FontStyles.Italic;if(On(format.Element(word+"u")))span.TextDecorations=TextDecorations.Underline;}
                foreach(var node in run.Elements()){if(++budget.Inlines>20000 || budget.Characters>=512*1024){budget.Limited=true;break;}if(node.Name==word+"t"){int length=Math.Min(node.Value.Length,Math.Max(0,512*1024-budget.Characters));span.Inlines.Add(new W.Run(node.Value.Substring(0,length)));budget.Characters+=length;if(length<node.Value.Length)budget.Limited=true;}else if(node.Name==word+"tab")span.Inlines.Add(new W.Run("\t"));else if(node.Name==word+"br")span.Inlines.Add(new W.LineBreak());}p.Inlines.Add(span);
            }
            return p;
        }
        static bool On(XElement e){if(e==null)return false;string value=(string)e.Attribute(e.Name.Namespace+"val");return value!="false" && value!="0" && value!="none";}
    }
}
