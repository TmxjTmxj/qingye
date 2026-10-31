using System;
using System.IO;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Qingye.UI;

public static class IconGenerator
{
    [STAThread] public static int Main(string[] args)
    {
        string target=Path.GetFullPath(args[0]);Directory.CreateDirectory(Path.GetDirectoryName(target));var sizes=new[]{16,24,32,48,64,128,256};var images=new List<byte[]>();
        foreach(int size in sizes)
        {
            var root=new Grid{Width=size,Height=size};root.Children.Add(new Border{Background=new SolidColorBrush(Color.FromRgb(229,247,239)),CornerRadius=new CornerRadius(size*.22)});root.Children.Add(new IconView("leaf",size*.68){Foreground=new SolidColorBrush(Color.FromRgb(18,132,108)),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center});root.Measure(new Size(size,size));root.Arrange(new Rect(0,0,size,size));root.UpdateLayout();var bitmap=new RenderTargetBitmap(size,size,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var png=new MemoryStream()){encoder.Save(png);images.Add(png.ToArray());}
        }
        using(var file=File.Create(target))using(var writer=new BinaryWriter(file)){writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)sizes.Length);int offset=6+16*sizes.Length;for(int i=0;i<sizes.Length;i++){writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)0);writer.Write((byte)0);writer.Write((ushort)1);writer.Write((ushort)32);writer.Write(images[i].Length);writer.Write(offset);offset+=images[i].Length;}foreach(var bytes in images)writer.Write(bytes);}
        File.WriteAllBytes(Path.ChangeExtension(target,".png"),images[images.Count-1]);Console.WriteLine("Generated multi-resolution application icon: "+target);return 0;
    }
}
