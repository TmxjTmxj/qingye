using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Globalization;
namespace Qingye.Core
{
    public class TableContent
    {
        public IList<string[]> Rows=new List<string[]>();
        public bool Truncated;
        public string Notice;
    }
    public static partial class ContentReaders
    {
        public static TableContent ParseTable(string text,char delimiter)
        {
            var result=new TableContent();var row=new List<string>();var cell=new StringBuilder();bool quoted=false;
            for(int i=0;i<text.Length;i++)
            {
                char c=text[i];
                if(c=='"'){if(quoted && i+1<text.Length && text[i+1]=='"'){if(cell.Length<8192)cell.Append('"');i++;}else if(quoted || cell.Length==0)quoted=!quoted;else if(cell.Length<8192)cell.Append(c);}
                else if(!quoted && c==delimiter){if(row.Count<50)row.Add(cell.ToString());else result.Truncated=true;cell.Clear();}
                else if(!quoted && (c=='\r' || c=='\n'))
                {
                    if(c=='\r' && i+1<text.Length && text[i+1]=='\n')i++;if(row.Count<50)row.Add(cell.ToString());else result.Truncated=true;cell.Clear();result.Rows.Add(row.ToArray());row.Clear();
                    if(result.Rows.Count>=500){result.Truncated=result.Truncated || i<text.Length-1;break;}
                }
                else if(cell.Length<8192)cell.Append(c);else result.Truncated=true;
            }
            if(result.Rows.Count<500 && (cell.Length>0 || row.Count>0)){if(row.Count<50)row.Add(cell.ToString());result.Rows.Add(row.ToArray());}
            result.Notice=result.Truncated?"内容预览 · 最多 500 行、50 列，单元格最多 8192 字符":"内容预览 · CSV 原始文本仍可在编辑区修改";return result;
        }
        static XDocument Xml(ZipArchive zip,string entryName,ref long total)
        {
            var entry=zip.GetEntry(entryName);if(entry==null)return null;
            if(entry.Length>16L*1024*1024 || total+entry.Length>64L*1024*1024)throw new InvalidDataException("Office 内容超过安全预览大小。");total+=entry.Length;
            using(var stream=entry.Open())using(var reader=XmlReader.Create(stream,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=16L*1024*1024}))return XDocument.Load(reader);
        }
        public static string ReadDocx(string path)
        {
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))
            {
                long total=0;var xml=Xml(zip,"word/document.xml",ref total);if(xml==null)throw new InvalidDataException("没有找到 DOCX 文档正文。");var result=new StringBuilder();
                foreach(var paragraph in xml.Descendants().Where(x=>x.Name.LocalName=="p"))
                {
                    foreach(var node in paragraph.Descendants()){if(node.Name.LocalName=="t")result.Append(node.Value);else if(node.Name.LocalName=="tab")result.Append('\t');else if(node.Name.LocalName=="br")result.AppendLine();}result.AppendLine();
                }
                return result.ToString();
            }
        }
        public static XElement ReadDocxBody(string path)
        {
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))
            {long total=0;var xml=Xml(zip,"word/document.xml",ref total);if(xml==null)throw new InvalidDataException("没有找到 DOCX 文档正文。");return xml.Root.Elements().FirstOrDefault(e=>e.Name.LocalName=="body");}
        }
        static XElement[] Sheets(XDocument workbook){return workbook==null?new XElement[0]:workbook.Descendants().Where(e=>e.Name.LocalName=="sheet").ToArray();}
        public static string[] SheetNames(string path)
        {
            using(var stream=File.OpenRead(path))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read)){long total=0;return Sheets(Xml(zip,"xl/workbook.xml",ref total)).Select(e=>(string)e.Attribute("name")??"工作表").ToArray();}
        }
        public static TableContent ReadXlsx(string path,int sheet=0)
        {
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))
            {
                long total=0;var workbook=Xml(zip,"xl/workbook.xml",ref total);var sheets=Sheets(workbook);if(sheets.Length==0)throw new InvalidDataException("未找到工作表。");sheet=Math.Max(0,Math.Min(sheet,sheets.Length-1));
                bool date1904=workbook.Descendants().Any(e=>e.Name.LocalName=="workbookPr" && new[]{"1","true"}.Contains((string)e.Attribute("date1904")));
                var stylesXml=Xml(zip,"xl/styles.xml",ref total);int[] formats=stylesXml==null?new int[0]:stylesXml.Descendants().Where(e=>e.Name.LocalName=="cellXfs").Elements().Select(e=>{int formatId;return Int32.TryParse((string)e.Attribute("numFmtId"),out formatId)?formatId:0;}).ToArray();
                string id=sheets[sheet].Attributes().Where(a=>a.Name.LocalName=="id").Select(a=>a.Value).FirstOrDefault();var relationships=Xml(zip,"xl/_rels/workbook.xml.rels",ref total);
                string target=relationships==null?null:relationships.Descendants().Where(e=>(string)e.Attribute("Id")==id).Select(e=>(string)e.Attribute("Target")).FirstOrDefault();if(target==null)throw new InvalidDataException("工作表引用无效。");
                target=target.Replace('\\','/');if(target.Contains("..") || target.Contains(":") || target.StartsWith("//"))throw new InvalidDataException("工作表路径不受支持。");target=target.StartsWith("/")?target.TrimStart('/'):("xl/"+target);
                var sharedXml=Xml(zip,"xl/sharedStrings.xml",ref total);string[] shared=sharedXml==null?new string[0]:sharedXml.Descendants().Where(e=>e.Name.LocalName=="si").Take(50000).Select(e=>String.Concat(e.Descendants().Where(t=>t.Name.LocalName=="t").Select(t=>t.Value))).ToArray();
                var xml=Xml(zip,target,ref total);if(xml==null)throw new InvalidDataException("工作表内容缺失。");var result=new TableContent();
                foreach(var row in xml.Descendants().Where(e=>e.Name.LocalName=="row"))
                {
                    int rowIndex;string rowNumber=(string)row.Attribute("r");if(!Int32.TryParse(rowNumber,out rowIndex))rowIndex=result.Rows.Count+1;
                    if(rowIndex>500 || result.Rows.Count>=500){result.Truncated=true;break;}
                    while(result.Rows.Count<rowIndex-1)result.Rows.Add(new string[0]);var cells=new string[50];int width=0;
                    foreach(var cell in row.Elements().Where(e=>e.Name.LocalName=="c"))
                    {
                        string reference=(string)cell.Attribute("r")??"A";int column=0;foreach(char c in reference){if(c<'A'||c>'Z')break;column=column*26+c-'A'+1;if(column>50)break;}column--;if(column<0 || column>=50){result.Truncated=true;continue;}
                        string type=(string)cell.Attribute("t");string value=cell.Elements().Where(e=>e.Name.LocalName=="v").Select(e=>e.Value).FirstOrDefault()??"";
                        if(type=="s"){int index;value=Int32.TryParse(value,out index)&&index>=0&&index<shared.Length?shared[index]:"";}
                        else if(type=="inlineStr")value=String.Concat(cell.Descendants().Where(e=>e.Name.LocalName=="t").Select(e=>e.Value));
                        else if(type=="b")value=value=="1"?"TRUE":value=="0"?"FALSE":value;
                        else if(type==null || type=="n"){int style;if(Int32.TryParse((string)cell.Attribute("s"),out style) && style>=0 && style<formats.Length)value=FormatCell(value,formats[style],date1904);}
                        if(value=="" && cell.Elements().Any(e=>e.Name.LocalName=="f"))value="[公式无缓存值]";
                        if(value.Length>8192){value=value.Substring(0,8192)+"…";result.Truncated=true;}cells[column]=value;width=Math.Max(width,column+1);
                    }
                    result.Rows.Add(cells.Take(width).Select(v=>v??"").ToArray());
                }
                result.Notice="工作表："+((string)sheets[sheet].Attribute("name")??"工作表")+" · 缓存值，不重算公式 · 常用内置数字格式"+(result.Truncated?" · 已截断到前 500 行 / 50 列":"");return result;
            }
        }
        static string FormatCell(string raw,int format,bool date1904)
        {
            decimal number;if(!Decimal.TryParse(raw,NumberStyles.Float,CultureInfo.InvariantCulture,out number))return raw;
            try
            {
                if(format==1 || format==3)return number.ToString(format==1?"0":"#,##0",CultureInfo.InvariantCulture);
                if(format==2 || format==4)return number.ToString(format==2?"0.00":"#,##0.00",CultureInfo.InvariantCulture);
                if(format==9 || format==10)return (number*100).ToString(format==9?"0":"0.00",CultureInfo.InvariantCulture)+"%";
                if(format==11)return number.ToString("0.00E+00",CultureInfo.InvariantCulture);
                if(format>=14 && format<=22 || format>=45 && format<=47)
                {
                    double serial=(double)number;if(serial<0 || serial>2958465)return raw;
                    if(!date1904 && Math.Floor(serial)==60 && (format<18 || format==22))return "1900-02-29"+(format==22?" "+DateTime.FromOADate(serial).ToString("HH:mm",CultureInfo.InvariantCulture):"")+" (Excel)";
                    double adjusted=serial+(date1904?1462:serial<60?1:0);var date=DateTime.FromOADate(adjusted);
                    string pattern=format>=18 && format<=21?"HH:mm:ss":format==22?"yyyy-MM-dd HH:mm":format==45 || format==47?"mm:ss":format==46?null:"yyyy-MM-dd";
                    return pattern==null?((long)(serial*24)).ToString(CultureInfo.InvariantCulture)+":"+date.ToString("mm:ss",CultureInfo.InvariantCulture):date.ToString(pattern,CultureInfo.InvariantCulture);
                }
            }
            catch(ArgumentException){}catch(OverflowException){}
            return raw;
        }
    }
}
