using System;
using System.IO;
using System.Globalization;
using System.Collections.Generic;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Qingye.UI
{
    // Render the original Lucide SVG paths; no glyph-font metrics or hand-drawn replacements.
    public sealed class IconView : Control
    {
        static readonly Dictionary<string,Geometry> cache=new Dictionary<string,Geometry>();
        readonly Geometry geometry;
        public IconView(string name,double size){Width=size;Height=size;IsHitTestVisible=false;Focusable=false;VerticalAlignment=VerticalAlignment.Center;geometry=Load(ResolveName(name));}
        static string ResolveName(string value)
        {
            switch(value){case "\uE7C3":return "file-plus";case "\uE8E5":return "folder-open";case "\uE8B5":return "folder-input";case "\uE74E":return "save";case "\uE721":return "search";case "\uE7F4":return "panel-right";case "\uE713":return "sliders-horizontal";case "\uE712":return "ellipsis";case "\uE72C":return "rotate-cw";case "\uE8EA":return "smartphone";case "\uE8A7":return "arrow-up-right";case "\uE8BB":return "x";case "\uE710":return "plus";case "\uE784":return "grip-vertical";case "\uE8A5":return "leaf";case "\uE921":return "minus";case "\uE922":return "square";default:return value;}
        }
        static double Number(XElement e,string name,double fallback=0){var value=(string)e.Attribute(name);return value==null?fallback:Double.Parse(value,CultureInfo.InvariantCulture);}
        static Geometry Load(string name)
        {
            Geometry result;if(cache.TryGetValue(name,out result))return result;
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Qingye.Icons."+name+".svg"))
            {
                if(stream==null)throw new InvalidDataException("Missing icon asset: "+name);
                using(var reader=XmlReader.Create(stream,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null}))
                {
                    var document=XDocument.Load(reader);var group=new GeometryGroup();
                    foreach(var e in document.Root.Elements())
                    {
                        switch(e.Name.LocalName){case "path":group.Children.Add(Geometry.Parse((string)e.Attribute("d")));break;case "circle":group.Children.Add(new EllipseGeometry(new Point(Number(e,"cx"),Number(e,"cy")),Number(e,"r"),Number(e,"r")));break;case "rect":group.Children.Add(new RectangleGeometry(new Rect(Number(e,"x"),Number(e,"y"),Number(e,"width"),Number(e,"height")),Number(e,"rx"),Number(e,"ry",Number(e,"rx"))));break;case "line":group.Children.Add(new LineGeometry(new Point(Number(e,"x1"),Number(e,"y1")),new Point(Number(e,"x2"),Number(e,"y2"))));break;case "polyline":group.Children.Add(Geometry.Parse("M"+(string)e.Attribute("points")));break;case "polygon":group.Children.Add(Geometry.Parse("M"+(string)e.Attribute("points")+" Z"));break;}
                    }
                    group.Freeze();result=group;cache[name]=result;return result;
                }
            }
        }
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);var pen=new Pen(Foreground,1.8){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};drawing.PushTransform(new ScaleTransform(ActualWidth/24,ActualHeight/24));drawing.DrawGeometry(null,pen,geometry);drawing.Pop();
        }
    }
}
