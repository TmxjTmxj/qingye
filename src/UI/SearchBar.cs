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
        readonly WrapPanel replacementRow=new WrapPanel{Visibility=Visibility.Collapsed};
        readonly CheckBox wholeChoice=new CheckBox{Content="整词"};
        readonly TextBlock matchCounter=new TextBlock{Text="0/0",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6,0,6,0),MinWidth=34};
        readonly System.Windows.Threading.DispatcherTimer countTimer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(180)};
        int countVersion;ICSharpCode.AvalonEdit.Document.TextDocument countedDocument;
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
            replacementRow.Children.Add(replacement);
            replacementRow.Children.Add(Theme.Button(null,"替换全部",delegate
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
            searchStrip.Children.Add(wholeChoice);searchStrip.Children.Add(matchCounter);
            searchStrip.Children.Add(Theme.Button(null,"替换…",()=>replacementRow.Visibility=replacementRow.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible));
            searchStrip.Children.Add(workspaceChoice);searchStrip.Children.Add(replacementRow);workspaceChoice.ToolTip="工作区搜索使用不区分大小写的普通文本；替换仅作用于当前文件";workspaceChoice.Click+=delegate{bool local=workspaceChoice.IsChecked!=true;regexChoice.IsEnabled=caseChoice.IsEnabled=wholeChoice.IsEnabled=local;};
            System.Windows.Automation.AutomationProperties.SetName(matchCounter,"匹配位置与总数");
            countTimer.Tick+=async delegate{countTimer.Stop();int mine=++countVersion;if(workspaceChoice.IsChecked==true){matchCounter.Text="工作区";return;}if(owner.ActiveEditor==null||String.IsNullOrEmpty(query.Text)){matchCounter.Text="0/0";return;}var source=owner.ActiveEditor.Document;var snapshot=source.CreateSnapshot();string value=query.Text;bool regex=regexChoice.IsChecked==true,caseSensitive=caseChoice.IsChecked==true,whole=wholeChoice.IsChecked==true;try{int[] matches=await Task.Run(()=>Expression(value,regex,caseSensitive,whole).Matches(snapshot.Text).Cast<Match>().Take(10001).Select(m=>m.Index).ToArray());if(mine==countVersion&&Object.ReferenceEquals(source,owner.ActiveEditor.Document)){int index=owner.ActiveEditor.SelectionLength>0?Array.IndexOf(matches,owner.ActiveEditor.SelectionStart)+1:0;matchCounter.Text=matches.Length>10000?"10000+":index+"/"+matches.Length;}}catch{if(mine==countVersion)matchCounter.Text="表达式无效";}};
            Action recount=delegate{countVersion++;countTimer.Stop();if(searchStrip.Visibility==Visibility.Visible)countTimer.Start();};query.TextChanged+=delegate{recount();};regexChoice.Click+=delegate{recount();};caseChoice.Click+=delegate{recount();};wholeChoice.Click+=delegate{recount();};
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
        internal void Show(bool replace){if(searchStrip.Visibility!=Visibility.Visible)ToggleSearch();replacementRow.Visibility=replace?Visibility.Visible:Visibility.Collapsed;query.Focus();query.SelectAll();countTimer.Stop();countTimer.Start();}
        internal bool ReplaceVisible{get{return replacementRow.Visibility==Visibility.Visible;}}
        internal void Next(bool forward){if(searchStrip.Visibility!=Visibility.Visible)Show(false);Find(forward);}
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
            else { countTimer.Stop();countVersion++;resultsPopup.IsOpen=false; owner.ActiveEditor.Focus(); }
        }
        internal void RefreshMatches(bool changed=false){var doc=owner.ActiveEditor==null?null:owner.ActiveEditor.Document;if(!changed&&Object.ReferenceEquals(doc,countedDocument))return;countedDocument=doc;countVersion++;countTimer.Stop();if(searchStrip.Visibility==Visibility.Visible&&!String.IsNullOrEmpty(query.Text))countTimer.Start();}
        internal void RefreshContext()
        {
            workspaceChoice.Visibility=owner.WorkspaceRoot==null?Visibility.Collapsed:Visibility.Visible;
            if(owner.WorkspaceRoot==null){workspaceChoice.IsChecked=false;resultsPopup.IsOpen=false;}bool local=workspaceChoice.IsChecked!=true;regexChoice.IsEnabled=caseChoice.IsEnabled=wholeChoice.IsEnabled=local;
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
            return Expression(text,useRegex,matchCase,wholeChoice.IsChecked==true);
        }
        static Regex Expression(string text,bool regex,bool matchCase,bool whole){string pattern=regex?text:Regex.Escape(text);if(whole)pattern=@"(?<![\p{L}\p{N}_])(?:"+pattern+@")(?![\p{L}\p{N}_])";return new Regex(pattern,matchCase?RegexOptions.None:RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(250));}
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
                int number=Array.IndexOf(matches,selected)+1;matchCounter.Text=number+"/"+matches.Length;owner.SetNotice("第 "+number+" / "+matches.Length+" 处");
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
