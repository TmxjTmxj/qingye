using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Diagnostics;
using ICSharpCode.AvalonEdit.Document;
using Qingye.Core;

namespace Qingye.UI
{
    internal sealed class PaneLayout:Grid
    {
        readonly MainWindow owner;
        readonly TabStrip tabStrip;
        readonly Grid editorRegion=new Grid(),readerRegion=new Grid();
        Grid editorStack;
        internal PaneLayout(MainWindow window,TabStrip tabs)
        {
            owner=window;
            tabStrip=tabs;
            editorRegion.RowDefinitions.Add(new RowDefinition
            {
                Height=GridLength.Auto
            });
            editorRegion.RowDefinitions.Add(new RowDefinition());
            editorRegion.Children.Add(tabStrip);
        }
        internal void Apply(string layout,bool showSecondary,bool showPreview,bool oldPreview)
        {
            var oldTabsParent=tabStrip.Parent as Panel;
            if(oldTabsParent!=null)oldTabsParent.Children.Remove(tabStrip);
            readerRegion.Children.Clear();
            if(editorStack!=null)
            {
                editorRegion.Children.Remove(editorStack);
                editorStack.Children.Clear();
            }
            this.Children.Clear();
            this.RowDefinitions.Clear();
            this.ColumnDefinitions.Clear();
            editorStack=new Grid();
            editorRegion.Children.Add(editorStack);
            Grid.SetRow(editorStack,1);
            Grid.SetColumn(editorStack,0);
            editorStack.Children.Add(owner.primary);
            if(showSecondary)
            {
                if(owner.secondary.BoundDocument==null || !owner.Session.Documents.Contains(owner.secondary.BoundDocument))owner.secondary.Bind(owner.Session.Documents.FirstOrDefault(d=>d!=owner.primary.BoundDocument)??owner.primary.BoundDocument);
                if(owner.Appearance.EditorsVertical || owner.ActualWidth<720)
                {
                    editorStack.RowDefinitions.Add(new RowDefinition
                    {
                        Height=new GridLength(owner.Appearance.EditorRatio,GridUnitType.Star)
                    });
                    editorStack.RowDefinitions.Add(new RowDefinition
                    {
                        Height=new GridLength(5)
                    });
                    editorStack.RowDefinitions.Add(new RowDefinition
                    {
                        Height=new GridLength(1-owner.Appearance.EditorRatio,GridUnitType.Star)
                    });
                    Grid.SetRow(owner.primary,0);
                    Grid.SetColumn(owner.primary,0);
                    Grid.SetRow(owner.secondary,2);
                    Grid.SetColumn(owner.secondary,0);
                    editorStack.Children.Add(owner.secondary);
                    var splitter=Splitter(false);
                    Grid.SetRow(splitter,1);
                    editorStack.Children.Add(splitter);
                    splitter.DragCompleted+=delegate
                    {
                        owner.Appearance.EditorRatio=editorStack.RowDefinitions[0].ActualHeight/Math.Max(1,editorStack.ActualHeight-5);
                    };
                }
                else
                {
                    editorStack.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width=new GridLength(.5,GridUnitType.Star)
                    });
                    editorStack.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width=new GridLength(5)
                    });
                    editorStack.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width=new GridLength(.5,GridUnitType.Star)
                    });
                    Grid.SetRow(owner.primary,0);
                    Grid.SetColumn(owner.primary,0);
                    Grid.SetRow(owner.secondary,0);
                    Grid.SetColumn(owner.secondary,2);
                    editorStack.Children.Add(owner.secondary);
                    var splitter=Splitter(true);
                    Grid.SetColumn(splitter,1);
                    editorStack.Children.Add(splitter);
                }
            }
            else
            {
                Grid.SetRow(owner.primary,0);
                Grid.SetColumn(owner.primary,0);
            }
            if(layout=="wysiwyg")
            {
                readerRegion.RowDefinitions.Clear();
                readerRegion.RowDefinitions.Add(new RowDefinition
                {
                    Height=GridLength.Auto
                });
                readerRegion.RowDefinitions.Add(new RowDefinition());
                Grid.SetRow(tabStrip,0);
                readerRegion.Children.Add(tabStrip);
                Grid.SetRow(owner.RichEditor,1);
                Grid.SetColumn(owner.RichEditor,0);
                readerRegion.Children.Add(owner.RichEditor);
                Grid.SetRow(readerRegion,0);
                Grid.SetColumn(readerRegion,0);
                this.Children.Add(readerRegion);
                if(oldPreview)owner.previewClose=owner.preview.CloseAsync();
            }
            else if(!showPreview)
            {
                this.Children.Add(editorRegion);
                Grid.SetRow(editorRegion,0);
                Grid.SetColumn(editorRegion,0);
                if(oldPreview)owner.previewClose=owner.preview.CloseAsync();
            }
            else if(layout=="reader")
            {
                readerRegion.RowDefinitions.Clear();
                readerRegion.RowDefinitions.Add(new RowDefinition
                {
                    Height=GridLength.Auto
                });
                readerRegion.RowDefinitions.Add(new RowDefinition());
                Grid.SetRow(tabStrip,0);
                readerRegion.Children.Add(tabStrip);
                Grid.SetRow(owner.preview,1);
                Grid.SetColumn(owner.preview,0);
                readerRegion.Children.Add(owner.preview);
                Grid.SetRow(readerRegion,0);
                Grid.SetColumn(readerRegion,0);
                this.Children.Add(readerRegion);
                owner.Observe(owner.UpdatePreviewAsync());
            }
            else
            {
                bool bottom=owner.Appearance.PreviewPosition=="bottom" || owner.ActualWidth<720,left=owner.Appearance.PreviewPosition=="left";
                int editorPosition=left?2:0,previewPosition=left?0:2;
                if(bottom)
                {
                    this.RowDefinitions.Add(new RowDefinition
                    {
                        Height=new GridLength(1-owner.Appearance.PreviewRatio,GridUnitType.Star)
                    });
                    this.RowDefinitions.Add(new RowDefinition
                    {
                        Height=new GridLength(5)
                    });
                    this.RowDefinitions.Add(new RowDefinition
                    {
                        Height=new GridLength(owner.Appearance.PreviewRatio,GridUnitType.Star)
                    });
                    Grid.SetRow(editorRegion,0);
                    Grid.SetColumn(editorRegion,0);
                    Grid.SetRow(owner.preview,2);
                    Grid.SetColumn(owner.preview,0);
                }
                else
                {
                    this.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width=new GridLength(left?owner.Appearance.PreviewRatio:1-owner.Appearance.PreviewRatio,GridUnitType.Star)
                    });
                    this.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width=new GridLength(5)
                    });
                    this.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width=new GridLength(left?1-owner.Appearance.PreviewRatio:owner.Appearance.PreviewRatio,GridUnitType.Star)
                    });
                    Grid.SetColumn(editorRegion,editorPosition);
                    Grid.SetRow(editorRegion,0);
                    Grid.SetColumn(owner.preview,previewPosition);
                    Grid.SetRow(owner.preview,0);
                }
                this.Children.Add(editorRegion);
                this.Children.Add(owner.preview);
                var splitter=Splitter(!bottom);
                if(bottom)Grid.SetRow(splitter,1);
                else Grid.SetColumn(splitter,1);
                this.Children.Add(splitter);
                splitter.DragCompleted+=delegate
                {
                    owner.Appearance.PreviewRatio=bottom?owner.preview.ActualHeight/Math.Max(1,this.ActualHeight-5):owner.preview.ActualWidth/Math.Max(1,this.ActualWidth-5);
                };
                owner.Observe(owner.UpdatePreviewAsync());
            }
            if(layout!="reader" && layout!="wysiwyg")
            {
                Grid.SetRow(tabStrip,0);
                editorRegion.Children.Add(tabStrip);
            }
            owner.primary.ShowHeader(showSecondary);
            owner.secondary.ShowHeader(showSecondary);
            owner.primary.Apply(owner.Appearance);
            owner.secondary.Apply(owner.Appearance);
            owner.UpdateStatus();
        }
        GridSplitter Splitter(bool vertical)
        {
            var splitter=new GridSplitter
            {
                HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch,ResizeDirection=vertical?GridResizeDirection.Columns:GridResizeDirection.Rows,ResizeBehavior=GridResizeBehavior.PreviousAndNext,ShowsPreview=false
            };
            splitter.SetResourceReference(Control.BackgroundProperty,"LineBrush");
            splitter.ToolTip="拖动调整比例";
            return splitter;
        }
    }
}
