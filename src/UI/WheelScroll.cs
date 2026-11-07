using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;

namespace Qingye.UI
{
    // Wheel scrolling that keeps the OS line setting but treats the wheel as a
    // continuous device: fractional high-resolution deltas accumulate instead
    // of being rounded away, and a quick spin gets a modest boost so fast
    // wheeling covers ground instead of feeling sticky. Ctrl+wheel is left to
    // the zoom paths, Shift+wheel scrolls sideways like Windows does.
    internal static class WheelScroll
    {
        internal const double MaxBoost=2.2;
        internal static double LinesPerNotch()
        {
            double lines=SystemParameters.WheelScrollLines;
            return lines<=0?3:lines;
        }
        // delta is in Windows wheel units (120 per notch). lastTick/boost carry
        // the per-surface spin state so held-over acceleration stays local.
        internal static double Pixels(int delta,double lineHeight,long now,ref long lastTick,ref double boost)
        {
            long gap=lastTick==0?1000:now-lastTick;
            lastTick=now;
            if(gap<80)boost=Math.Min(MaxBoost,boost+0.18);
            else if(gap<140)boost=Math.Min(MaxBoost,boost+0.04);
            else if(gap>300)boost=1;
            double height=Math.Max(12,lineHeight);
            return delta/120.0*LinesPerNotch()*height*boost;
        }
        internal static void Attach(TextEditor editor)
        {
            if(editor==null)return;
            long lastTick=0;double boost=1;
            editor.TextArea.PreviewMouseWheel+=delegate(object sender,MouseWheelEventArgs e)
            {
                if((Keyboard.Modifiers&ModifierKeys.Control)!=0)return;
                double amount=Pixels(e.Delta,editor.TextArea.TextView.DefaultLineHeight,Environment.TickCount,ref lastTick,ref boost);
                if((Keyboard.Modifiers&ModifierKeys.Shift)!=0)editor.ScrollToHorizontalOffset(editor.HorizontalOffset-amount);
                else editor.ScrollToVerticalOffset(editor.VerticalOffset-amount);
                e.Handled=true;
            };
        }
        internal static void Attach(FlowDocumentScrollViewer viewer)
        {
            if(viewer==null)return;
            ScrollViewer scroll=null;long lastTick=0;double boost=1;
            viewer.PreviewMouseWheel+=delegate(object sender,MouseWheelEventArgs e)
            {
                if((Keyboard.Modifiers&ModifierKeys.Control)!=0)return;
                if(scroll==null)scroll=FindScroll(viewer);
                if(scroll==null)return;
                double lineHeight=viewer.Document!=null && viewer.Document.LineHeight>0?viewer.Document.LineHeight:24;
                double amount=Pixels(e.Delta,lineHeight,Environment.TickCount,ref lastTick,ref boost);
                if((Keyboard.Modifiers&ModifierKeys.Shift)!=0)scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset-amount);
                else scroll.ScrollToVerticalOffset(scroll.VerticalOffset-amount);
                e.Handled=true;
            };
        }
        static ScrollViewer FindScroll(DependencyObject root)
        {
            var own=root as ScrollViewer;
            if(own!=null)return own;
            int count=VisualTreeHelper.GetChildrenCount(root);
            for(int i=0;i<count;i++)
            {
                var found=FindScroll(VisualTreeHelper.GetChild(root,i));
                if(found!=null)return found;
            }
            return null;
        }
    }
}
