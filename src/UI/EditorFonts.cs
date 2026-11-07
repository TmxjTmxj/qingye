using System;
using System.Windows.Media;
namespace Qingye.UI
{
    // Code/prose font composition. Every selected family keeps its own ASCII
    // glyphs, while CJK text always falls back to a family that actually has
    // those glyphs; otherwise monospace choices such as Consolas would render
    // Chinese comments as empty boxes.
    internal static class EditorFonts
    {
        internal static readonly string[] Choices =
        {
            "Cascadia Code","Cascadia Mono","Consolas","JetBrains Mono","Fira Code","Source Code Pro","Courier New","Microsoft YaHei UI","Segoe UI"
        };
        internal static bool IsCjkFamily(string family)
        {
            return family=="Microsoft YaHei UI" || family=="Microsoft YaHei" || family=="SimSun" || family=="Microsoft JhengHei" || family=="KaiTi" || family=="FangSong";
        }
        internal static FontFamily Compose(string family)
        {
            string ascii=String.IsNullOrEmpty(family)?"Consolas":family;
            var font=new FontFamily();
            font.FamilyMaps.Add(new FontFamilyMap{Unicode="0000-007F",Target=ascii});
            font.FamilyMaps.Add(new FontFamilyMap{Unicode="0080-10FFFF",Target=IsCjkFamily(ascii)?ascii:"Microsoft YaHei UI"});
            return font;
        }
    }
}
