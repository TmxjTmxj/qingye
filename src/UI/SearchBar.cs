using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
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
    internal sealed class SearchBar : UserControl
    {
        readonly MainWindow owner;
        internal readonly WrapPanel searchStrip;
        internal readonly TextBox query=new TextBox
        {
            Width=180
        };
        readonly TextBox replacement=new TextBox
        {
            Width=160
        };
        readonly CheckBox regexChoice=new CheckBox
        {
            Content="正则"
        },
        caseChoice=new CheckBox
        {
            Content="区分大小写"
        },
        workspaceChoice=new CheckBox
        {
            Content="工作区",Visibility=Visibility.Collapsed
        };
        readonly Popup resultsPopup=new Popup
        {
            StaysOpen=false,AllowsTransparency=true,Placement=PlacementMode.Bottom
        };
        readonly ListBox resultsList=new ListBox
        {
            Width=560,MaxHeight=320
        };
        CancellationTokenSource searchCancel;
        internal SearchBar(MainWindow window)
        {
            owner=window;
            HorizontalContentAlignment=HorizontalAlignment.Stretch;
            searchStrip=new WrapPanel
            {
                Orientation=Orientation.Horizontal,Margin=new Thickness(14,8,14,8),Visibility=Visibility.Collapsed
            };
            Content=searchStrip;
            query.ToolTip="查找文本";
            replacement.ToolTip="替换为";
            searchStrip.Children.Add(query);
            searchStrip.Children.Add(Theme.Button(null,"上一个",delegate
            {
                Find(false);
            }
            ));
            searchStrip.Children.Add(Theme.Button(null,"下一个",delegate
            {
                Find(true);
            }
            ));
            replacement.Margin=new Thickness(8,0,0,0);
            searchStrip.Children.Add(replacement);
            searchStrip.Children.Add(Theme.Button(null,"替换全部",delegate
            {
                try
                {
                    int count=ReplaceAll(query.Text,replacement.Text,regexChoice.IsChecked==true,caseChoice.IsChecked==true);
                    owner.SetNotice("已替换 "+count+" 处");
                }
                catch(Exception ex)
                {
                    owner.SetNotice(ex.Message);
                }
            }
            ));
            searchStrip.Children.Add(regexChoice);
            searchStrip.Children.Add(caseChoice);
            searchStrip.Children.Add(workspaceChoice);
            searchStrip.Children.Add(Theme.Button("\uE8BB","",ToggleSearch));
            resultsList.BorderThickness=new Thickness(0);
            resultsList.Background=owner.TryFindResource("SurfaceBrush") as Brush ?? Brushes.White;
            resultsList.Foreground=owner.TryFindResource("TextBrush") as Brush ?? Brushes.Black;
            resultsList.ItemContainerStyle=ResultStyle();
            var resultBorder=new Border{Child=resultsList,CornerRadius=new CornerRadius(7),BorderThickness=new Thickness(1),Margin=new Thickness(0,2,0,0)};
            resultBorder.Background=owner.TryFindResource("SurfaceBrush") as Brush ?? Brushes.White;
            resultBorder.BorderBrush=owner.TryFindResource("LineBrush") as Brush ?? Brushes.Gray;
            resultsPopup.Child=resultBorder;
            resultsPopup.PlacementTarget=query;
            resultsList.SelectionChanged+=delegate
            {
                var item=resultsList.SelectedItem as ListBoxItem;
                if(item==null || !(item.Tag is SearchHit))return;
                var hit=(SearchHit)item.Tag;resultsList.SelectedItem=null;resultsPopup.IsOpen=false;OpenHit(hit);
            };
            query.KeyDown+=delegate(object sender,KeyEventArgs e)
            {
                if(e.Key==Key.Enter)
                {
                    Find(true);
                    e.Handled=true;
                }
            };
        }
        internal void ToggleSearch()
        {
            searchStrip.Visibility=searchStrip.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;
            if(searchStrip.Visibility==Visibility.Visible)
            {
                RefreshContext();
                if(owner.Appearance.Layout=="reader" && owner.SourceViewButton.IsEnabled)owner.SetViewMode("single");
                query.Focus();
                if(owner.ActiveEditor.SelectionLength>0 && owner.ActiveEditor.SelectionLength<200)query.Text=owner.ActiveEditor.SelectedText;
                query.SelectAll();
            }
            else { resultsPopup.IsOpen=false; owner.ActiveEditor.Focus(); }
        }
        internal void RefreshContext()
        {
            workspaceChoice.Visibility=owner.WorkspaceRoot==null?Visibility.Collapsed:Visibility.Visible;
            if(owner.WorkspaceRoot==null){workspaceChoice.IsChecked=false;resultsPopup.IsOpen=false;}
        }
        static Style ResultStyle()
        {
            var style=new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(10,6,10,6)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));
            return style;
        }
        Regex SearchExpression(string text,bool useRegex,bool matchCase)
        {
            return new Regex(useRegex?text:Regex.Escape(text),matchCase?RegexOptions.None:RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(250));
        }
        void Find(bool forward)
        {
            if(String.IsNullOrEmpty(query.Text) || owner.ActiveEditor==null)return;
            if(workspaceChoice.IsChecked==true && owner.WorkspaceRoot!=null){SearchWorkspace();return;}
            try
            {
                var pattern=SearchExpression(query.Text,regexChoice.IsChecked==true,caseChoice.IsChecked==true);
                var matches=pattern.Matches(owner.ActiveEditor.Text).Cast<Match>().Take(10000).ToArray();
                if(matches.Length==0)
                {
                    owner.SetNotice("未找到");
                    return;
                }
                int start=owner.ActiveEditor.SelectionStart;
                Match selected=forward?matches.FirstOrDefault(m=>m.Index>=start+Math.Max(1,owner.ActiveEditor.SelectionLength)):matches.LastOrDefault(m=>m.Index<start);
                selected=selected??(forward?matches.First():matches.Last());
                owner.ActiveEditor.Select(selected.Index,selected.Length);
                owner.ActiveEditor.ScrollToLine(owner.ActiveEditor.Document.GetLineByOffset(selected.Index).LineNumber);
                owner.SetNotice("找到 "+matches.Length+" 处");
            }
            catch(Exception ex)
            {
                owner.SetNotice("搜索失败："+ex.Message);
            }
        }
        public int ReplaceAll(string text,string value,bool useRegex,bool matchCase)
        {
            if(String.IsNullOrEmpty(text) || owner.ActiveEditor==null || owner.ActiveEditor.IsReadOnly)return 0;
            var pattern=SearchExpression(text,useRegex,matchCase);
            int count=0;
            string result=pattern.Replace(owner.ActiveEditor.Text,delegate(Match match)
            {
                count++;
                return useRegex?match.Result(value):value;
            });
            if(count>0)owner.ActiveEditor.Document.Replace(0,owner.ActiveEditor.Document.TextLength,result);
            return count;
        }
        async void SearchWorkspace()
        {
            if(searchCancel!=null)searchCancel.Cancel();
            searchCancel=new CancellationTokenSource();
            var token=searchCancel.Token;
            string root=owner.WorkspaceRoot,text=query.Text;
            owner.SetNotice("正在搜索工作区…");
            try
            {
                var hits=await Task.Run(delegate{return WorkspaceSearch.Search(root,text,500,200,token);});
                if(token.IsCancellationRequested)return;
                resultsList.Items.Clear();
                foreach(var hit in hits)
                {
                    var panel=new StackPanel();
                    var head=new TextBlock{Text=hit.RelativePath+":"+hit.Line,FontWeight=FontWeights.SemiBold,FontSize=12};
                    var body=new TextBlock{Text=hit.LineText,FontSize=11,TextTrimming=TextTrimming.CharacterEllipsis};
                    body.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");
                    panel.Children.Add(head);panel.Children.Add(body);
                    resultsList.Items.Add(new ListBoxItem{Content=panel,Tag=hit});
                }
                if(hits.Count==0){owner.SetNotice("工作区中未找到");resultsPopup.IsOpen=false;return;}
                resultsPopup.IsOpen=true;
                owner.SetNotice("工作区找到 "+hits.Count+" 处");
            }
            catch(OperationCanceledException){}
            catch(Exception ex){owner.SetNotice("工作区搜索失败："+ex.Message);}
        }
        async void OpenHit(SearchHit hit)
        {
            try
            {
                await owner.OpenPathsAsync(new[]{hit.Path},true);
                var doc=owner.Session.Documents.FirstOrDefault(item=>item.Path!=null && String.Equals(Path.GetFullPath(item.Path),Path.GetFullPath(hit.Path),StringComparison.OrdinalIgnoreCase));
                if(doc==null)return;
                owner.SelectDocument(doc,null);
                var editor=owner.ActiveEditor;if(editor.Document==null)return;
                int line=Math.Max(1,Math.Min(hit.Line,editor.Document.LineCount));
                editor.ScrollToLine(line);editor.TextArea.Caret.Line=line;editor.Focus();
            }
            catch(Exception ex){owner.SetNotice("打开搜索结果失败："+ex.Message);}
        }
        internal bool WorkspaceOptionVisible { get { return workspaceChoice.Visibility == Visibility.Visible; } }
        internal int WorkspaceResultCount { get { return resultsList.Items.Count; } }
        internal void RunWorkspaceSearchForTest(string text) { query.Text = text; workspaceChoice.IsChecked = true; SearchWorkspace(); }
        internal void ShowGoToLine()
        {
            var dialog=new Window
            {
                Owner=owner,Title="跳转行",Width=280,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false
            };
            Theme.Install(dialog);
            Theme.Apply(dialog,owner.Appearance);
            var content=new StackPanel
            {
                Margin=new Thickness(18)
            };
            var number=new TextBox
            {
                Text=owner.ActiveEditor.TextArea.Caret.Line.ToString()
            };
            content.Children.Add(number);
            content.Children.Add(Theme.Button(null,"跳转",delegate
            {
                int line;
                if(Int32.TryParse(number.Text,out line))
                {
                    line=Math.Max(1,Math.Min(line,owner.ActiveEditor.Document.LineCount));
                    owner.ActiveEditor.ScrollToLine(line);
                    owner.ActiveEditor.TextArea.Caret.Line=line;
                    dialog.Close();
                }
            }
            ));
            dialog.Content=content;
            dialog.Loaded+=delegate
            {
                number.Focus();
                number.SelectAll();
            };
            dialog.ShowDialog();
        }
    }
}
