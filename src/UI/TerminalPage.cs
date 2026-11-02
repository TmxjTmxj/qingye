using System;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Win32;
namespace Qingye.UI
{
    internal static class TerminalPage
    {
        static string Read(string name){using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Qingye.Terminal."+name)){if(stream==null)throw new FileNotFoundException("终端资源缺失："+name);using(var reader=new StreamReader(stream,Encoding.UTF8))return reader.ReadToEnd();}}
        internal static string Build()
        {
            int build; if(!Int32.TryParse(Convert.ToString(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion","CurrentBuildNumber","19041")),out build))build=19041;
            var html=new StringBuilder("<!doctype html><html><head><meta charset='utf-8'><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data: blob:; connect-src 'none'; font-src 'none';\"><style>");
            html.Append(Read("xterm.css"));html.Append("html,body{width:100%;height:100%;margin:0;overflow:hidden;background:#172229}#terminal{position:absolute;inset:10px 12px 8px 12px}.xterm{height:100%}.xterm-viewport{overscroll-behavior:contain;overflow-x:hidden!important;background:var(--terminal-background,#172229)!important}.xterm .composition-view{background:var(--terminal-background,#172229);color:var(--terminal-foreground,#e4edf1)}::selection{background:#3b5966}</style></head><body><div id='terminal'></div>");
            html.Append("<script>window.onerror=function(message){window.chrome.webview.postMessage({type:'error',text:String(message)});};</script>");
            foreach(string name in new[]{"xterm.js","addon-fit.js","addon-unicode-graphemes.js","addon-webgl.js","terminal-client.js"})html.Append("<script>").Append(Read(name).Replace("__WINDOWS_BUILD__",build.ToString()).Replace("</script","<\\/script")).Append("</script>");
            return html.Append("</body></html>").ToString();
        }
    }
}
