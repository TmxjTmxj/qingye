using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using System.Text.RegularExpressions;
namespace Qingye.Core
{
    public class Settings
    {
        public string Theme = "mint";
        public string Accent = "";
        public double FontSize = 15;
        public string FontFamily = "Consolas";
        public bool Wrap = true;
        public bool LineNumbers = true;
        public bool FileAwareDefaults = true;
        public bool RememberAppearance = false;
        public string Layout = "single";
        public string PreviewPosition = "right";
        public bool EditorsVertical = true;
        public double EditorRatio = .54;
        public double PreviewRatio = .48;
        public double TerminalSize = 260;
        public string TerminalDock = "bottom";
        public static Settings Load(string path)
        {
            try
            {
                if(!File.Exists(path) || new FileInfo(path).Length>65536)return new Settings();
                using(var reader=XmlReader.Create(path,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null}))
                {
                    var value=(Settings)new XmlSerializer(typeof(Settings)).Deserialize(reader);
                    if(!value.RememberAppearance)return new Settings();
                    if(value.Theme!="mint" && value.Theme!="warm" && value.Theme!="lilac" && value.Theme!="dark")value.Theme="mint";
                    if(value.Layout!="single" && value.Layout!="compare" && value.Layout!="preview")value.Layout="single";
                    if(value.PreviewPosition!="left" && value.PreviewPosition!="right" && value.PreviewPosition!="bottom")value.PreviewPosition="right";
                    if(!Regex.IsMatch(value.Accent??"","^#[0-9A-Fa-f]{6}$"))value.Accent="";
                    value.FontSize=Math.Max(10,Math.Min(40,Double.IsNaN(value.FontSize)?15:value.FontSize));
                    value.EditorRatio=Math.Max(.2,Math.Min(.8,value.EditorRatio));value.PreviewRatio=Math.Max(.2,Math.Min(.7,value.PreviewRatio));
                    value.TerminalSize=Math.Max(140,Math.Min(620,Double.IsNaN(value.TerminalSize)?260:value.TerminalSize));
                    if(value.TerminalDock!="right")value.TerminalDock="bottom";
                    return value;
                }
            }
            catch{return new Settings();}
        }
        public void Save(string path)
        {
            if(!RememberAppearance){if(File.Exists(path))File.Delete(path);return;}
            string temp=path+".tmp";
            try
            {
                using(var writer=XmlWriter.Create(temp,new XmlWriterSettings{Indent=true,Encoding=new System.Text.UTF8Encoding(false)}))new XmlSerializer(typeof(Settings)).Serialize(writer,this);
                if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
            }
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
    }
}
