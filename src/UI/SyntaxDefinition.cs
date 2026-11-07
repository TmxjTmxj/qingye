using System;
using System.IO;
using System.Xml;
using System.Linq;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
namespace Qingye.UI
{
    static class SyntaxDefinition
    {
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IHighlightingDefinition,object> themed=new System.Runtime.CompilerServices.ConditionalWeakTable<IHighlightingDefinition,object>();
        sealed class ResourceBrush : HighlightingBrush
        {
            readonly string token;
            public ResourceBrush(string resourceToken) { token = resourceToken; }
            public override Brush GetBrush(ITextRunConstructionContext context)
            {
                return context == null ? Brushes.Transparent : context.TextView.TryFindResource(token) as Brush ?? Brushes.Transparent;
            }
        }
        public static void UseTheme(IHighlightingDefinition definition)
        {
            if (definition == null) return;object applied;if(themed.TryGetValue(definition,out applied))return;
            foreach (var color in definition.NamedHighlightingColors)
            {
                string name = (color.Name ?? "").ToLowerInvariant();
                string token = name.Contains("heading") ? "SyntaxHeading" : name.Contains("comment") ? "SyntaxComment" :
                    name.Contains("string") ? "SyntaxString" : name.Contains("number") || name.Contains("digit") ? "SyntaxNumber" :
                    name.Contains("keyword") || name.Contains("tag") ? "SyntaxKeyword" : name.Contains("attribute") || name.Contains("key") ? "SyntaxAttribute" : "SyntaxText";
                color.FontSize = null;
                color.Foreground = new ResourceBrush(token);
                if (color.Background != null) color.Background = new ResourceBrush("CodeBackground");
            }
            themed.Add(definition,new object());
        }
        static IHighlightingDefinition json,config;
        static IHighlightingDefinition Load(string name,string rules)
        {
            string xml="<SyntaxDefinition name='"+name+"' xmlns='http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008'><Color name='Comment'/><Color name='String'/><Color name='Number'/><Color name='Keyword'/><Color name='Key'/><RuleSet>"+rules+"</RuleSet></SyntaxDefinition>";
            using(var reader=XmlReader.Create(new StringReader(xml)))return HighlightingLoader.Load(reader,HighlightingManager.Instance);
        }
        public static IHighlightingDefinition For(string extension)
        {
            extension=extension.ToLowerInvariant();
            if(extension==".json" || extension==".jsonc")return json??(json=Load("JSON","<Span color='String'><Begin>&quot;</Begin><End>&quot;</End><RuleSet><Span begin='\\\\.' end=''/></RuleSet></Span><Keywords color='Keyword'><Word>true</Word><Word>false</Word><Word>null</Word></Keywords><Rule color='Number'>-?\\b[0-9]+(\\.[0-9]+)?([eE][+-]?[0-9]+)?\\b</Rule>"));
            if(new[]{".yaml",".yml",".ini",".toml",".cfg",".conf",".env",".properties"}.Contains(extension))return config??(config=Load("Configuration","<Span color='Comment' begin='(^|\\s)[#;]' /><Span color='String' begin='&quot;' end='&quot;'/><Span color='String' begin='&apos;' end='&apos;'/><Rule color='Key'>^[ \\t]*[A-Za-z_][A-Za-z0-9_.-]*(?=\\s*[:=])</Rule><Keywords color='Keyword'><Word>true</Word><Word>false</Word><Word>null</Word></Keywords><Rule color='Number'>\\b[0-9]+(\\.[0-9]+)?\\b</Rule>"));
            if(extension==".ts" || extension==".tsx" || extension==".jsx" || extension==".mjs" || extension==".cjs")extension=".js";
            if(extension==".h" || extension==".hpp" || extension==".cc" || extension==".cxx")extension=".cpp";
            if(extension==".xaml" || extension==".config" || extension==".csproj" || extension==".svg")extension=".xml";
            return HighlightingManager.Instance.GetDefinitionByExtension(extension);
        }
    }
}
