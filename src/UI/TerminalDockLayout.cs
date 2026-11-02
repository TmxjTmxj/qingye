using System;
using System.Windows;
using System.Windows.Controls;
using Qingye.Core;
namespace Qingye.UI
{
    internal sealed class TerminalDockLayout
    {
        readonly Grid shell;readonly FrameworkElement document;readonly SidePanel navigation;readonly TerminalPanel terminal;readonly GridSplitter splitter;
        internal bool Right{get;private set;}
        internal TerminalDockLayout(Grid shell,FrameworkElement document,SidePanel navigation,TerminalPanel terminal,GridSplitter splitter){this.shell=shell;this.document=document;this.navigation=navigation;this.terminal=terminal;this.splitter=splitter;shell.RowDefinitions.Add(new RowDefinition());shell.RowDefinitions.Add(new RowDefinition{Height=new GridLength(0)});shell.RowDefinitions.Add(new RowDefinition{Height=new GridLength(0)});}
        internal void Apply(double width,double height,bool visible,bool showNavigation,Settings settings)
        {
            Right=settings.TerminalDock=="right"&&width>=820;bool nav=showNavigation&&width>=640;
            navigation.SetVisible(nav);Grid.SetRowSpan(navigation,3);
            shell.ColumnDefinitions[0].Width=new GridLength(nav?204:0);shell.ColumnDefinitions[1].Width=new GridLength(nav?8:0);
            shell.ColumnDefinitions[2].Width=new GridLength(1,GridUnitType.Star);
            shell.ColumnDefinitions[3].Width=new GridLength(visible&&Right?5:0);
            shell.ColumnDefinitions[4].Width=new GridLength(visible&&Right?Math.Max(240,Math.Min(settings.TerminalSize,width*.46)):0);
            shell.RowDefinitions[0].Height=new GridLength(1,GridUnitType.Star);
            shell.RowDefinitions[1].Height=new GridLength(visible&&!Right?5:0);
            shell.RowDefinitions[2].Height=new GridLength(visible&&!Right?Math.Max(110,Math.Min(settings.TerminalSize,Math.Max(110,height*.46))):0);
            Grid.SetRow(document,0);Grid.SetColumn(document,2);Grid.SetColumnSpan(document,Right?1:3);
            Grid.SetRow(terminal,Right?0:2);Grid.SetColumn(terminal,Right?4:2);Grid.SetColumnSpan(terminal,Right?1:3);
            Grid.SetRow(splitter,Right?0:1);Grid.SetColumn(splitter,Right?3:2);Grid.SetColumnSpan(splitter,Right?1:3);
            splitter.ResizeDirection=Right?GridResizeDirection.Columns:GridResizeDirection.Rows;
            terminal.Visibility=visible?Visibility.Visible:Visibility.Collapsed;splitter.Visibility=visible?Visibility.Visible:Visibility.Collapsed;
        }
    }
}
