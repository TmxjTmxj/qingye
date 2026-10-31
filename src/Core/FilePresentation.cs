using System;
using System.IO;
using System.Linq;
namespace Qingye.Core
{
    // Presentation never changes the document's bytes, encoding, or newline conventions.
    public sealed class FilePresentation
    {
        public string Name, Description, Icon, ReaderLabel;
        public bool CanRead, PreferReader, Prose, CanWysiwyg;
        public static string Extension(Document doc){string name=Path.GetFileName(doc.Path??doc.Title??"").ToLowerInvariant();return name==".env" || name.StartsWith(".env.")?".env":name==".editorconfig"?".ini":Path.GetExtension(name);}
        public static FilePresentation For(Document doc)
        {
            string ext=Extension(doc);
            if(doc.Kind==DocumentKind.PagedText)return Make("分段文本","大文件只读 · 按段加载","file-text",null,false,false);
            if(doc.Kind==DocumentKind.Binary)return Make("二进制","十六进制 · 偏移 / 字节 / 字符","braces",null,false,false);
            if(doc.Kind==DocumentKind.Image)return Make("图片","适应窗口 · 原始尺寸","file-text","图片",true,false);
            if(doc.Kind==DocumentKind.Pdf)return Make("PDF 文档","页面阅读 · 搜索与缩放","file-text","页面",true,false);
            if(doc.Kind==DocumentKind.Docx)return Make("Word 文档","基础排版 · 完整版式请用系统软件","file-text","文档",true,true);
            if(doc.Kind==DocumentKind.Pptx)return Make("PowerPoint 演示文稿","页标题、正文和表格 · 原文件只读","file-text","幻灯片",true,true);
            if(doc.Kind==DocumentKind.Xlsx)return Make("Excel 工作簿","工作表内容 · 不计算公式、不还原完整格式","braces","表格",true,false);
            if(doc.Kind==DocumentKind.Rtf)return Make("RTF 文档","富文本阅读 · 原文件只读","file-text","文档",true,false);
            if(doc.Kind==DocumentKind.Epub)return Make("EPUB 电子书","章节阅读 · 原文件只读","file-text","阅读",true,false);
            if(new[]{".md",".markdown",".mdown",".mkd"}.Contains(ext)){var markdown=Make("Markdown 文档","标题、列表、表格与代码块","file-text","阅读",true,false);markdown.CanWysiwyg=true;return markdown;}
            if(ext==".csv" || ext==".tsv")return Make(ext==".csv"?"CSV 表格":"TSV 表格","行列阅读 · 原始文本可编辑","braces","表格",true,false);
            if(ext==".json" || ext==".jsonc")return Make(ext==".json"?"JSON 数据":"JSONC 配置","语法高亮 · 保留原始值与顺序","braces",ext==".json"?"结构":null,ext==".json",false);
            if(new[]{".xml",".xaml",".config",".csproj",".svg"}.Contains(ext))return Make(ext==".svg"?"SVG 图形":"XML 文档",ext==".svg"?"矢量预览 · 源码可编辑":"元素、属性与文本层级","file-code",ext==".svg"?"图形":"结构",true,false);
            if(ext==".html" || ext==".htm")return Make("HTML 页面","源码编辑 · 按需打开网页预览","file-code","网页",false,false);
            if(ext==".log")return Make("日志","等宽文本 · 原始行序","file-text",null,false,false);
            if(new[]{".yaml",".yml",".ini",".toml",".cfg",".conf",".env",".properties"}.Contains(ext) || (doc.Title??"")==".env")return Make("配置文件","键值与注释高亮 · 保留缩进","braces",null,false,false);
            if(String.IsNullOrEmpty(ext) || new[]{".txt",".text"}.Contains(ext))return Make("纯文本","自然换行 · 清晰阅读","file-text","阅读",false,true);
            if(new[]{".c",".cpp",".h",".hpp",".cc",".cxx",".cs",".js",".jsx",".ts",".tsx",".mjs",".cjs",".css",".scss",".less",".py",".pyw",".java",".kt",".kts",".go",".rs",".rb",".php",".sh",".bash",".ps1",".bat",".cmd",".sql",".r",".m",".swift",".vb",".vue",".svelte",".lua"}.Contains(ext))return Make(ext.TrimStart('.').ToUpperInvariant()+" 源文件","等宽字体 · 行号与语言高亮","file-code",null,false,false);
            return Make("文本（"+ext+"）","未知扩展名 · 保留原始文本","file-text",null,false,false);
        }
        static FilePresentation Make(string name,string description,string icon,string reader,bool prefer,bool prose)
        {return new FilePresentation{Name=name,Description=description,Icon=icon,ReaderLabel=reader,CanRead=reader!=null,PreferReader=prefer,Prose=prose};}
    }
}
