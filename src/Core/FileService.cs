using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
namespace Qingye.Core
{
    public static class FileService
    {
        public const long MaxEditableBytes = 32L * 1024 * 1024;
        public const int PageBytes = 1024 * 1024;
        public static Document Read(string path, int page = 0, string overrideEncoding = null,int pageSizeHint=0)
        {
            path = System.IO.Path.GetFullPath(path);
            var info = new FileInfo(path);
            var doc = new Document { Path = path, Title = info.Name, ByteLength = info.Length };
            string ext = info.Extension.ToLowerInvariant();
            if (new[]{".png",".jpg",".jpeg",".bmp",".gif",".tif",".tiff",".ico"}.Contains(ext)) doc.Kind = DocumentKind.Image;
            else if(ext==".pdf") doc.Kind = DocumentKind.Pdf;
            else if(ext==".docx") doc.Kind = DocumentKind.Docx;
            else if(ext==".pptx") doc.Kind = DocumentKind.Pptx;
            else if(ext==".xlsx") doc.Kind = DocumentKind.Xlsx;
            else if(ext==".rtf") doc.Kind = DocumentKind.Rtf;
            else if(ext==".epub") doc.Kind = DocumentKind.Epub;
            if(doc.Kind != DocumentKind.Text)
            {
                doc.IsReadOnly=true; doc.Notice="内容查看 · 原文件不会被改写";
                doc.SetLoadedText(doc.Title + "\r\n\r\n" + FormatSize(doc.ByteLength) + "\r\n\r\n打开预览查看文件内容。");
                if(doc.Kind==DocumentKind.Docx){doc.SetLoadedText(ContentReaders.ReadDocx(path));doc.Notice="DOCX 正文内容 · 不包含完整排版";}
                if(doc.Kind==DocumentKind.Pptx){var presentation=ContentReaders.ReadPptx(path);doc.SetLoadedText(String.Join("\r\n",presentation.Body.Descendants().Where(element=>element.Name.LocalName=="p").Select(paragraph=>String.Concat(paragraph.Descendants().Where(element=>element.Name.LocalName=="t").Select(element=>element.Value)))));doc.Notice=presentation.Notice;}
                if(doc.Kind==DocumentKind.Xlsx){var table=ContentReaders.ReadXlsx(path);doc.SetLoadedText(String.Join("\r\n",table.Rows.Select(row=>String.Join("\t",row))));doc.Notice=table.Notice;}
                if(doc.Kind==DocumentKind.Epub)doc.Notice="EPUB 阅读 · 原文件只读";
                return doc;
            }
            byte[] sample;
            using(var input = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) sample=ReadBytes(input, Math.Min(65536L,input.Length));
            string encodingName="utf-8"; int bom=0;
            if(sample.Length>=4 && sample[0]==0xff && sample[1]==0xfe && sample[2]==0 && sample[3]==0){encodingName="utf-32";bom=4;}
            else if(sample.Length>=4 && sample[0]==0 && sample[1]==0 && sample[2]==0xfe && sample[3]==0xff){encodingName="utf-32BE";bom=4;}
            else if(sample.Length>=3 && sample[0]==0xef && sample[1]==0xbb && sample[2]==0xbf){encodingName="utf-8";bom=3;}
            else if(sample.Length>=2 && sample[0]==0xff && sample[1]==0xfe){encodingName="utf-16";bom=2;}
            else if(sample.Length>=2 && sample[0]==0xfe && sample[1]==0xff){encodingName="unicodeFFFE";bom=2;}
            else if(sample.Length>=4)
            {
                int odd=0,even=0; for(int i=0;i<sample.Length;i++)if(sample[i]==0){if(i%2==0)even++;else odd++;}
                if(odd>sample.Length/5 && even<sample.Length/20) encodingName="utf-16";
                else if(even>sample.Length/5 && odd<sample.Length/20)encodingName="unicodeFFFE";
                else if(sample.Contains((byte)0)) return MakeBinary(doc,sample);
            }
            else if(sample.Contains((byte)0)) return MakeBinary(doc,sample);
            if(overrideEncoding!=null){if(GetEncoding(overrideEncoding,false).CodePage!=GetEncoding(encodingName,false).CodePage)bom=0;encodingName=overrideEncoding;}
            Encoding encoding=GetEncoding(encodingName, bom>0);
            if(bom==0 && encodingName=="utf-8" && overrideEncoding==null)
            {
                try { DecodePartial(sample,encoding,0,false); }
                catch(DecoderFallbackException){encodingName="gb18030";encoding=GetEncoding(encodingName,false);}
            }
            doc.EncodingName=encodingName;doc.HasBom=bom>0;
            string head;try{head=DecodePartial(sample,encoding,bom,false);}catch(DecoderFallbackException){return MakeBinary(doc,sample);}
            bool longLine=pageSizeHint==65536 || HasLongLine(head);
            bool paged;long start;
            byte[] bytes;
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            {
                doc.ByteLength=input.Length;paged=doc.ByteLength>MaxEditableBytes || longLine && doc.ByteLength>256*1024;
                doc.PageByteSize=longLine?65536:PageBytes;doc.PageCount=paged?(int)Math.Min(Int32.MaxValue,(doc.ByteLength+doc.PageByteSize-1)/doc.PageByteSize):1;doc.Page=Math.Max(0,Math.Min(page,doc.PageCount-1));start=paged?(long)doc.Page*doc.PageByteSize:0;
                input.Seek(start,SeekOrigin.Begin);bytes=ReadBytes(input,paged?Math.Min(doc.PageByteSize,input.Length-start):input.Length);
            }
            try
            {
                int skip=start==0?bom:0;
                string value=paged?DecodePage(path,bytes,encoding,start,skip,start+bytes.Length>=doc.ByteLength,doc.PageByteSize):encoding.GetString(bytes,skip,bytes.Length-skip);
                if(doc.ByteLength>256*1024 && doc.PageByteSize!=65536 && HasLongLine(value))return Read(path,(int)(start/65536),overrideEncoding,65536);
                int controls=value.Take(65536).Count(c=>c<32 && c!='\t' && c!='\r' && c!='\n' && c!='\f');
                if(controls>Math.Max(1, Math.Min(value.Length,65536)/100))return MakeBinary(doc,sample);
                doc.SetLoadedText(value);
            }
            catch(DecoderFallbackException)
            {
                doc.Notice="无法可靠识别编码 · 可在编码菜单重新读取"; return MakeBinary(doc,sample);
            }
            doc.Kind=paged?DocumentKind.PagedText:DocumentKind.Text;
            doc.IsReadOnly=paged || (info.Attributes & FileAttributes.ReadOnly)!=0;
            Match newline=Regex.Match(doc.Text,"\r\n|\r|\n");doc.NewLine=newline.Success?newline.Value:"\r\n";
            if(!paged)doc.Version=Hash(bytes);
            if(paged)doc.Notice=(longLine?"超长行分段只读":"大文件分段只读")+" · 第 "+(doc.Page+1)+" / "+doc.PageCount+" 段 · "+FormatSize(doc.PageByteSize)+" / 段";
            else if(doc.ByteLength>20L*1024*1024)doc.Notice="大文件模式 · 已关闭语法高亮";
            return doc;
        }
        static bool HasLongLine(string text){int run=0;foreach(char value in text){if(value=='\r' || value=='\n')run=0;else if(++run>=16384)return true;}return false;}
        static byte[] ReadBytes(Stream stream,long count)
        {
            int length=(int)count;byte[] data=new byte[length];int read=0;
            while(read<length){int part=stream.Read(data,read,length-read);if(part==0)break;read+=part;}
            if(read!=length)Array.Resize(ref data,read);return data;
        }
        static string DecodePartial(byte[] bytes,Encoding encoding,int skip,bool alignStart)
        {
            for(int extra=0;extra<=(alignStart?3:0);extra++)
            {
                try
                {
                    int offset=Math.Min(bytes.Length,skip+extra);char[] chars=new char[encoding.GetMaxCharCount(bytes.Length-offset)];
                    int usedBytes,usedChars;bool complete;
                    encoding.GetDecoder().Convert(bytes,offset,bytes.Length-offset,chars,0,chars.Length,false,out usedBytes,out usedChars,out complete);
                    return new string(chars,0,usedChars);
                }
                catch(DecoderFallbackException){if(extra==(alignStart?3:0))throw;}
            }
            return "";
        }
        static string DecodePage(string path,byte[] bytes,Encoding encoding,long start,int skip,bool last,int pageSize)
        {
            var decoder=encoding.GetDecoder();
            long checkpoint = encoding.CodePage == 54936 ? Gb18030PageIndex.Before(path,start,pageSize) : 0;
            if(start>0)
            {
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                {
                    if(encoding.CodePage==54936)
                    {
                        // Reconstruct only the at-most-three pending bytes for an indexed page.
                        stream.Position=checkpoint;
                        byte[] prefix=new byte[65536];char[] ignored=new char[encoding.GetMaxCharCount(prefix.Length)];long remaining=start-checkpoint;
                        while(remaining>0){int count=stream.Read(prefix,0,(int)Math.Min(prefix.Length,remaining));if(count==0)break;decoder.GetChars(prefix,0,count,ignored,0,false);remaining-=count;}
                    }
                    else if(encoding.CodePage==65001 || encoding.CodePage==1200 || encoding.CodePage==1201)
                    {
                        stream.Position=Math.Max(0,start-4);byte[] prefix=ReadBytes(stream,start-stream.Position);int offset=0;
                        if(encoding.CodePage==65001){while(offset<prefix.Length && (prefix[offset]&0xc0)==0x80)offset++;}
                        else if(prefix.Length>=2){int value=encoding.CodePage==1200?prefix[0]+(prefix[1]<<8):(prefix[0]<<8)+prefix[1];if(value>=0xdc00 && value<=0xdfff)offset=2;}
                        decoder.GetChars(prefix,offset,prefix.Length-offset,new char[16],0,false);
                    }
                }
            }
            char[] characters=new char[encoding.GetMaxCharCount(bytes.Length-skip)+4];int length=decoder.GetChars(bytes,skip,bytes.Length-skip,characters,0,last);return new string(characters,0,length);
        }
        static Document MakeBinary(Document doc,byte[] bytes)
        {
            doc.Kind=DocumentKind.Binary;doc.IsReadOnly=true;
            var text=new StringBuilder("二进制 / 编码未识别 · 只读\r\n"+FormatSize(doc.ByteLength)+" · 最多查看前 64 KB\r\n\r\n");
            for(int i=0;i<bytes.Length;i+=16)
            {
                text.Append(i.ToString("X8")).Append("  ");
                for(int j=0;j<16;j++)text.Append(i+j<bytes.Length?bytes[i+j].ToString("X2")+" ":"   ");
                text.Append(" ");for(int j=0;j<16 && i+j<bytes.Length;j++)text.Append(bytes[i+j]>=32 && bytes[i+j]<127?(char)bytes[i+j]:'.');text.Append("\r\n");
            }
            doc.SetLoadedText(text.ToString());return doc;
        }
        public static Encoding GetEncoding(string name,bool bom)
        {
            if(name=="utf-8")return new UTF8Encoding(bom,true);
            if(name=="utf-16")return new UnicodeEncoding(false,bom,true);
            if(name=="unicodeFFFE")return new UnicodeEncoding(true,bom,true);
            if(name=="utf-32")return new UTF32Encoding(false,bom,true);
            if(name=="utf-32BE")return new UTF32Encoding(true,bom,true);
            return Encoding.GetEncoding(name,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
        }
        static string Hash(byte[] bytes){using(var hash=SHA256.Create())return Convert.ToBase64String(hash.ComputeHash(bytes));}
        internal static string HashFile(string path){using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var hash=SHA256.Create())return Convert.ToBase64String(hash.ComputeHash(stream));}
        public static void Save(Document doc, string target, bool force)
        {
            if(doc.IsReadOnly || doc.Kind!=DocumentKind.Text)throw new InvalidOperationException("此内容为只读，请使用适合该格式的软件编辑。");
            target=System.IO.Path.GetFullPath(target);
            bool original=doc.Path!=null && String.Equals(target,System.IO.Path.GetFullPath(doc.Path),StringComparison.OrdinalIgnoreCase);
            if(original && !force)
            {
                if(!File.Exists(target))throw new FileConflictException("原文件已被删除。请选择另存为，或明确重新保存到原路径。");
                if(doc.Version!=HashFile(target))throw new FileConflictException("原文件在外部被修改，尚未覆盖。请选择另存为、重新读取或明确覆盖。");
            }
            Encoding encoding=GetEncoding(doc.EncodingName,doc.HasBom);byte[] body=encoding.GetBytes(doc.Text);byte[] prefix=doc.HasBom?encoding.GetPreamble():new byte[0];
            string directory=System.IO.Path.GetDirectoryName(target);
            if(!Directory.Exists(directory))throw new DirectoryNotFoundException("保存目录不存在。");
            string temp=System.IO.Path.Combine(directory,".qingye-"+Guid.NewGuid().ToString("N")+".tmp");
            try
            {
                using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    output.Write(prefix,0,prefix.Length);output.Write(body,0,body.Length);output.Flush(true);
                }
                if(original && !force && (!File.Exists(target) || HashFile(target)!=doc.Version))throw new FileConflictException("保存期间原文件发生变化，未执行覆盖。");
                if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);
                doc.Path=target;doc.Title=System.IO.Path.GetFileName(target);doc.Version=HashFile(target);doc.ByteLength=prefix.Length+body.Length;doc.AcceptChanges();
            }
            finally { if(File.Exists(temp))File.Delete(temp); }
        }
        public static string NormalizeNewLines(string text, string newLine) { return Regex.Replace(text,"\r\n|\r|\n",newLine); }
        public static bool IsInsideRoot(string root, string path)
        {
            try
            {
                string allowed=System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar)+System.IO.Path.DirectorySeparatorChar;
                string full=System.IO.Path.GetFullPath(path);
                if(!full.StartsWith(allowed,StringComparison.OrdinalIgnoreCase))return false;
                var directory=new DirectoryInfo(System.IO.Path.GetDirectoryName(full));
                while(directory!=null && directory.FullName.Length>=allowed.Length-1)
                {
                    if(directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint)!=0)return false;
                    directory=directory.Parent;
                }
                return !File.Exists(full) || (File.GetAttributes(full)&FileAttributes.ReparsePoint)==0;
            }
            catch{return false;}
        }
        public static string FormatSize(long bytes){if(bytes<1024)return bytes+" B";if(bytes<1024*1024)return (bytes/1024.0).ToString("0.0")+" KB";return (bytes/1024.0/1024.0).ToString("0.0")+" MB";}
    }
}
