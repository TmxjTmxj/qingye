using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Qingye.Core;
using Qingye.UI;
namespace Qingye.Tests
{
    public static partial class UiTests
    {
        internal static void RunZoomAndColorTests(MainWindow window,string root)
        {
            Test("Ctrl+wheel zoom scales the code editor and Ctrl+0 resets it",delegate{
                string file=Path.Combine(root,"zoom-code.py");File.WriteAllText(file,"def f():\n    return 1\n");
                Await(window.OpenPathsAsync(new[]{file},false));window.SetViewMode("single");Pump();
                var editor=window.ActiveEditor;
                double baseSize=window.Appearance.FontSize;
                Check(Math.Abs(editor.FontSize-baseSize)<0.5,"Code font does not start at the configured size");
                window.AdjustTextZoom(2);
                Check(editor.FontSize>baseSize*1.15,"Wheel zoom did not grow the code font");
                Check(window.TextZoom>1.15,"Zoom state was not recorded");
                window.ResetTextZoom();
                Check(Math.Abs(editor.FontSize-baseSize)<0.5,"Ctrl+0 did not restore the base font size");
                Check(Math.Abs(window.TextZoom-1)<0.001,"Zoom state was not reset");
            });
            Test("Code fonts keep a CJK fallback for every family choice",delegate{
                var consolas=Qingye.UI.EditorFonts.Compose("Consolas");
                Check(consolas.FamilyMaps.Count==2 && consolas.FamilyMaps[1].Target=="Microsoft YaHei UI","Consolas lost its CJK fallback");
                var jetbrains=Qingye.UI.EditorFonts.Compose("JetBrains Mono");
                Check(jetbrains.FamilyMaps[1].Target=="Microsoft YaHei UI","New mono families do not fall back to YaHei for CJK");
                var yahei=Qingye.UI.EditorFonts.Compose("Microsoft YaHei UI");
                Check(yahei.FamilyMaps[1].Target=="Microsoft YaHei UI","CJK family was remapped unexpectedly");
                Check(Qingye.UI.EditorFonts.Choices.Contains("Cascadia Code") && Qingye.UI.EditorFonts.Choices.Contains("Fira Code"),"Font choices were not extended");
            });
            Test("Python files get code conventions and syntax highlighting",delegate{
                string file=Path.Combine(root,"conventions.py");File.WriteAllText(file,"def f():\n    return 1\n");
                Await(window.OpenPathsAsync(new[]{file},false));window.SetViewMode("single");Pump();
                var editor=window.ActiveEditor;
                Check(editor.SyntaxHighlighting!=null && editor.SyntaxHighlighting.Name=="Python","Python had no syntax highlighting");
                Check(editor.Options.ConvertTabsToSpaces && editor.Options.IndentationSize==4,"Python is not using 4-space indentation");
                Check(editor.ShowLineNumbers && !editor.WordWrap,"Code conventions lost line numbers or wrap control");
                var strategy=editor.TextArea.IndentationStrategy;
                Check(strategy!=null && strategy.GetType().Name.IndexOf("Python",StringComparison.OrdinalIgnoreCase)>=0,"Python indentation strategy is not installed");
                window.SetTheme("mint");Pump();
            });
            Test("Syntax colors stay readable and the current line follows every theme",delegate{
                var lineColors=new List<Color>();
                foreach(string theme in new[]{"mint","warm","lilac","dark"})
                {
                    window.SetTheme(theme);Pump();
                    Color surface=((SolidColorBrush)window.FindResource("SurfaceBrush")).Color;
                    foreach(string token in new[]{"SyntaxComment","SyntaxString","SyntaxNumber","SyntaxKeyword","SyntaxAttribute","SyntaxHeading"})
                    {
                        Brush brush=window.FindResource(token) as Brush;
                        Check(brush!=null,"Missing syntax token "+token);
                        Check(Contrast(((SolidColorBrush)brush).Color,surface)>=4.5,token+" is unreadable on the "+theme+" theme");
                    }
                    Brush line=window.FindResource("EditorLineHighlight") as Brush;
                    Check(line!=null,"Current-line highlight token is missing");
                    lineColors.Add(((SolidColorBrush)line).Color);
                }
                Check(lineColors.Distinct().Count()>=3,"Current-line highlight did not follow the theme");
                window.SetTheme("mint");Pump();
                var fresh=new MainWindow{ShowActivated=false,Left=-16000,Top=0};fresh.Show();Pump();
                try
                {
                    string file=Path.Combine(root,"theme-code.py");
                    File.WriteAllText(file,"import math\n\ndef area(r):\n    # 圆面积\n    return math.pi * r * r\n\nfor radius in (1, 2, 3):\n    print(area(radius))\n");
                    Await(fresh.OpenPathsAsync(new[]{file},false));fresh.SetViewMode("single");Pump();
                    Check(fresh.ActiveEditor.TextArea.TextView.CurrentLineBackground!=null,"Editor did not apply the current-line highlight");
                    Check(fresh.ActiveEditor.TextArea.TextView.CurrentLineBackground==(Brush)fresh.FindResource("EditorLineHighlight"),"Current-line highlight does not use its theme token");
                    string output=Path.GetFullPath(Path.Combine(root,"..","..","..","artifacts","ui"));Directory.CreateDirectory(output);
                    fresh.SetTheme("dark");Pump();
                    fresh.ActiveEditor.TextArea.Caret.Line=4;fresh.ActiveEditor.TextArea.Caret.Column=5;Pump();
                    CaptureElement(fresh,Path.Combine(output,"0.16-code-dark.png"));
                }
                finally
                {
                    foreach(var doc in fresh.Session.Documents){doc.Modified=false;doc.MetadataModified=false;}
                    fresh.Appearance.RememberAppearance=false;fresh.Close();
                    for(int attempt=0;attempt<400;attempt++){Pump();if(!Application.Current.Windows.OfType<Window>().Contains(fresh))break;System.Threading.Thread.Sleep(5);}
                    Pump();
                }
            });
        }
        static double Contrast(Color first,Color second)
        {
            double lighter=Luminance(first),darker=Luminance(second);
            if(lighter<darker){double swap=lighter;lighter=darker;darker=swap;}
            return (lighter+0.05)/(darker+0.05);
        }
        static double Luminance(Color color)
        {
            return 0.2126*Channel(color.R)+0.7152*Channel(color.G)+0.0722*Channel(color.B);
        }
        static double Channel(byte value)
        {
            double channel=value/255.0;
            return channel<=0.03928?channel/12.92:Math.Pow((channel+0.055)/1.055,2.4);
        }
    }
}
