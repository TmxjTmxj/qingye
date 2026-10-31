using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Runtime.Serialization.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace Qingye.UI
{
    public static class StructuredReader
    {
        sealed class Budget{public int Count;}
        public static XElement Parse(string text,bool json)
        {
            if(text.Length>512*1024)throw new InvalidDataException("结构视图限 512 KiB，请在源码中查看完整内容。");
            if(json)
            {
                byte[] bytes=Encoding.UTF8.GetBytes(text);
                using(var reader=JsonReaderWriterFactory.CreateJsonReader(bytes,new XmlDictionaryReaderQuotas{MaxDepth=64,MaxStringContentLength=512*1024,MaxArrayLength=512*1024,MaxNameTableCharCount=512*1024}))return XElement.Load(reader);
            }
            using(var reader=XmlReader.Create(new StringReader(text),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=512*1024}))return XDocument.Load(reader,LoadOptions.PreserveWhitespace).Root;
        }
        public static TreeView Create(XElement root,bool json)
        {
            var budget=new Budget();var tree=new TreeView{BorderThickness=new Thickness(0),Padding=new Thickness(24,20,24,24),FontFamily=new FontFamily("Consolas, Microsoft YaHei UI"),FontSize=14};
            tree.SetResourceReference(Control.BackgroundProperty,"SurfaceBrush");tree.SetResourceReference(Control.ForegroundProperty,"TextBrush");
            var item=Node(root,json,json?"根":root.Name.LocalName,0,budget);tree.Items.Add(item);item.IsExpanded=true;
            System.Windows.Automation.AutomationProperties.SetName(tree,json?"JSON 结构":"XML 结构");return tree;
        }
        static TreeViewItem Node(XElement node,bool json,string name,int depth,Budget budget)
        {
            budget.Count++;string type=(string)node.Attribute("type");bool container=node.HasElements;string value=json && (type=="array" || type=="object")?(type=="array"?"[ "+node.Elements().Count()+" 项 ]":"{ "+node.Elements().Count()+" 个字段 }"):container?"<"+node.Name.LocalName+">":json && type=="null"?"null":json && type=="string"?"\""+node.Value+"\"":node.Value;
            var header=new TextBlock{Text=name+"  "+Short(value),TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=1000};
            var item=new TreeViewItem{Header=header,Padding=new Thickness(4,5,4,5),ToolTip=(name+"\n"+Short(value,1000))};
            if(!container && json && type!="string")header.SetResourceReference(TextBlock.ForegroundProperty,"SyntaxNumber");
            if(depth>=32 || budget.Count>=2000){if(container)item.Items.Add(new TreeViewItem{Header="更多内容请在源码中查看",IsEnabled=false});return item;}
            // Build the next level only when it is requested; large arrays stay responsive.
            if(container || !json && node.HasAttributes)
            {
                item.Items.Add(new TreeViewItem{Header=""});
                item.Expanded+=delegate(object sender,RoutedEventArgs e)
                {
                    if(e.OriginalSource!=item || item.Tag!=null)return;item.Tag=true;item.Items.Clear();int children=0;
                    if(!json)foreach(var a in node.Attributes().Take(100))item.Items.Add(new TreeViewItem{Header="@"+a.Name.LocalName+" = "+Short(a.Value),Padding=new Thickness(4,5,4,5)});
                    foreach(var child in node.Elements())
                    {
                        if(children>=500 || budget.Count>=2000){item.Items.Add(new TreeViewItem{Header="其余项目请在源码中查看",IsEnabled=false});break;}
                        string key=json && type=="array"?"["+children+"]":json?((string)child.Attribute("item")??child.Name.LocalName):child.Name.LocalName;item.Items.Add(Node(child,json,key,depth+1,budget));children++;
                    }
                    if(!json && container)foreach(var text in node.Nodes().OfType<XText>().Where(t=>!String.IsNullOrWhiteSpace(t.Value)).Take(100))item.Items.Add(new TreeViewItem{Header="#text  "+Short(text.Value),Padding=new Thickness(4,5,4,5)});
                };
            }
            return item;
        }
        static string Short(string value,int max=160){value=value.Replace("\r","\\r").Replace("\n","\\n").Replace("\t","\\t");return value.Length>max?value.Substring(0,max)+"…":value;}
    }
}
