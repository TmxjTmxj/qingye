using System;
namespace Qingye.UI
{
    // Ctrl+wheel / Ctrl+= / Ctrl+- zoom lives in a partial file so the window
    // overview stays inside the size budget enforced by the UI tests.
    public partial class MainWindow
    {
        internal double TextZoom=1;
        internal void AdjustTextZoom(int notches)
        {
            if(notches==0)return;
            TextZoom=Math.Max(0.6,Math.Min(2.5,TextZoom*Math.Pow(1.1,notches)));
            ApplyTextZoom();
            SetNotice("字号缩放 "+Math.Round(TextZoom*100)+"%（Ctrl+0 重置）");
        }
        internal void ResetTextZoom()
        {
            TextZoom=1;
            ApplyTextZoom();
            SetNotice("字号已恢复 100%");
        }
        void ApplyTextZoom()
        {
            primary.Zoom=TextZoom;secondary.Zoom=TextZoom;
            primary.Apply(Appearance);secondary.Apply(Appearance);
        }
    }
}
