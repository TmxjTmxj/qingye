using System;
using Markdig;
namespace Qingye.UI
{
    public static class WebContent
    {
        public static string MarkdownHtml(string text)
        {
            var pipeline=new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
            string body=Markdown.ToHtml(text,pipeline);
            return "<!doctype html><html><head><meta charset='utf-8'><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; img-src https: http: data:; style-src 'unsafe-inline'; script-src 'none'; font-src 'none'\"><style>"+
                "body{font:16px/1.85 'Segoe UI','Microsoft YaHei UI',sans-serif;color:#263d38;margin:32px auto;padding:0 32px;max-width:760px;background:#fff}h1{font-size:30px;line-height:1.3}h2{margin-top:1.6em}a{color:#237f69}pre{background:#eff7f4;padding:18px;overflow:auto;border-radius:6px}code{font-family:Consolas,monospace;font-size:14px}blockquote{margin-left:0;padding-left:18px;border-left:3px solid #237f69;color:#667c72}table{border-collapse:collapse;width:100%}th,td{border-bottom:1px solid #dfe9e4;text-align:left;padding:8px}img{max-width:100%;height:auto}hr{border:0;border-top:1px solid #dfe9e4;margin:26px 0}</style></head><body>"+body+"</body></html>";
        }
    }
}
