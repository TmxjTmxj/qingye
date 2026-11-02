using System;
using System.IO;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public static class IconGenerator
{
    static Brush Color(string value){var brush=new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(value));brush.Freeze();return brush;}
    [STAThread]public static int Main(string[] args)
    {
        string target=Path.GetFullPath(args[0]);Directory.CreateDirectory(Path.GetDirectoryName(target));var sizes=new[]{16,24,32,48,64,128,256};var images=new List<byte[]>();
        foreach(int size in sizes)
        {
            var visual=new DrawingVisual();using(var drawing=visual.RenderOpen())
            {
                drawing.PushTransform(new ScaleTransform(size/256.0,size/256.0));
                drawing.DrawRoundedRectangle(Color("#14786F"),null,new Rect(12,12,232,232),54,54);
                drawing.DrawGeometry(Color("#F6FFFC"),null,Geometry.Parse("M 72,46 L 154,46 L 190,82 L 190,193 Q 190,210 173,210 L 72,210 Q 58,210 58,196 L 58,60 Q 58,46 72,46 Z"));
                drawing.DrawGeometry(Color("#B6E9DA"),null,Geometry.Parse("M 150,46 L 150,74 Q 150,86 162,86 L 190,86 Z"));
                drawing.DrawGeometry(Color("#218D78"),null,Geometry.Parse("M 88,160 C 87,118 112,105 161,105 C 161,151 140,177 111,172 L 95,191 L 84,184 L 105,160 C 119,146 133,132 145,119 C 121,134 107,149 88,160 Z"));
                drawing.Pop();
            }
            var bitmap=new RenderTargetBitmap(size,size,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var png=new MemoryStream()){encoder.Save(png);images.Add(png.ToArray());}
        }
        using(var file=File.Create(target))using(var writer=new BinaryWriter(file)){writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)sizes.Length);int offset=6+16*sizes.Length;for(int i=0;i<sizes.Length;i++){writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)0);writer.Write((byte)0);writer.Write((ushort)1);writer.Write((ushort)32);writer.Write(images[i].Length);writer.Write(offset);offset+=images[i].Length;}foreach(var bytes in images)writer.Write(bytes);}
        File.WriteAllBytes(Path.ChangeExtension(target,".png"),images[images.Count-1]);Console.WriteLine("Generated paper-and-leaf icon, 16–256 px: "+target);return 0;
    }
}
