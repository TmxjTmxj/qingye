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
    internal sealed class AppearancePopup : UserControl
    {
        readonly MainWindow owner;
        Settings Appearance
        {
            get
            {
                return owner.Appearance;
            }
        }
        string preferencesPath
        {
            get
            {
                return owner.PreferencesPath;
            }
        }
        internal Popup Popup
        {
            get;
            private set;
        }
        internal AppearancePopup(MainWindow window)
        {
            owner=window;
            HorizontalContentAlignment=HorizontalAlignment.Stretch;
        }
        internal void Show(Button anchor)
        {
            if(Popup!=null && Popup.IsOpen)
            {
                Popup.IsOpen=false;
                return;
            }
            if(Popup!=null)Popup.Child=null;
            var panel=new StackPanel
            {
                Margin=new Thickness(18)
            };
            var outer=new Border
            {
                Child=new ScrollViewer
                {
                    Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxHeight=Math.Max(360,SystemParameters.WorkArea.Height-82)
                },
                Width=392,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(9)
            };
            outer.SetResourceReference(Border.BackgroundProperty,"SurfaceBrush");
            outer.SetResourceReference(Border.BorderBrushProperty,"LineBrush");
            outer.Effect=new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius=18,ShadowDepth=4,Opacity=.13,Color=Colors.Black
            };
            panel.Children.Add(new TextBlock
            {
                Text="外观与布局",FontSize=20,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,16)
            });
            panel.Children.Add(Theme.Line());
            panel.Children.Add(SectionLabel("主题"));
            var themes=new UniformGrid
            {
                Columns=4
            };
            var themeCards=new List<KeyValuePair<string,Border>>();
            panel.Children.Add(themes);
            foreach(var pair in new[]
            {
                new[]
                {
                    "mint","薄荷","#DDF3E9"
                },
                new[]
                {
                    "warm","暖白","#F2E8D7"
                },
                new[]
                {
                    "lilac","浅紫","#E9E2F7"
                },
                new[]
                {
                    "dark","深色","#1D2421"
                }
            }
            )
            {
                string key=pair[0];
                var content=new StackPanel
                {
                    Margin=new Thickness(8,8,8,6)
                };
                content.Children.Add(new Border
                {
                    Width=54,Height=25,CornerRadius=new CornerRadius(4),Background=Theme.Brush(pair[2]),Margin=new Thickness(0,0,0,8)
                });
                content.Children.Add(new TextBlock
                {
                    Text=pair[1],FontSize=12,HorizontalAlignment=HorizontalAlignment.Center
                });
                var card=new Border
                {
                    Child=content,CornerRadius=new CornerRadius(6),BorderThickness=new Thickness(key==Appearance.Theme?1.5:1),Margin=new Thickness(4,0,4,0)
                };
                card.SetResourceReference(Border.BorderBrushProperty,key==Appearance.Theme?"AccentBrush":"LineBrush");
                themeCards.Add(new KeyValuePair<string,Border>(key,card));
                var button=Theme.Button(null,"",delegate
                {
                    owner.SetTheme(key);
                    foreach(var option in themeCards)
                    {
                        option.Value.BorderThickness=new Thickness(option.Key==Appearance.Theme?1.5:1);
                        option.Value.SetResourceReference(Border.BorderBrushProperty,option.Key==Appearance.Theme?"AccentBrush":"LineBrush");
                    }
                });
                button.Content=card;
                button.Padding=new Thickness(0);
                button.Margin=new Thickness(0);
                card.Width=82;
                button.ToolTip=pair[1]+"主题";
                themes.Children.Add(button);
            }
            panel.Children.Add(SectionLabel("强调色"));
            var accents=new StackPanel
            {
                Orientation=Orientation.Horizontal,Margin=new Thickness(2,0,0,0)
            };
            var accentRings=new List<KeyValuePair<string,Border>>();
            panel.Children.Add(accents);
            foreach(string value in new[]
            {
                "#12846C","#3286E7","#8C59DB","#F19138","#E44C60","#7F8D9B"
            }
            )
            {
                string color=value;
                var ring=new Border
                {
                    Width=26,Height=26,CornerRadius=new CornerRadius(13),BorderThickness=new Thickness(2),Padding=new Thickness(2),Child=new System.Windows.Shapes.Ellipse
                    {
                        Fill=Theme.Brush(color)
                    }
                };
                ring.BorderBrush=Appearance.Accent==color || String.IsNullOrEmpty(Appearance.Accent) && color=="#12846C"?Theme.Brush(color):Brushes.Transparent;
                accentRings.Add(new KeyValuePair<string,Border>(color,ring));
                var chip=Theme.Button(null,"",delegate
                {
                    Appearance.Accent=color;
                    owner.ApplyAppearance();
                    foreach(var option in accentRings)option.Value.BorderBrush=option.Key==color?Theme.Brush(color):Brushes.Transparent;
                });
                chip.Content=ring;
                chip.Width=36;
                chip.Height=34;
                chip.Padding=new Thickness(5);
                chip.ToolTip=color;
                accents.Children.Add(chip);
            }
            var custom=Theme.Button("sliders-horizontal","",delegate
            {
                using(var dialog=new System.Windows.Forms.ColorDialog())
                {
                    if(dialog.ShowDialog()==System.Windows.Forms.DialogResult.OK)
                    {
                        Appearance.Accent="#"+dialog.Color.R.ToString("X2")+dialog.Color.G.ToString("X2")+dialog.Color.B.ToString("X2");
                        owner.ApplyAppearance();
                    }
                }
            });
            custom.ToolTip="自定义强调色";
            accents.Children.Add(custom);
            panel.Children.Add(SectionLabel("布局"));
            var layouts=new UniformGrid
            {
                Columns=3
            };
            panel.Children.Add(layouts);
            var layoutCards=new List<KeyValuePair<string,Border>>();
            foreach(var pair in new[]
            {
                new[]
                {
                    "single","一栏","panel-left"
                },
                new[]
                {
                    "compare","对照","columns-2"
                },
                new[]
                {
                    "preview","预览","panel-right"
                }
            }
            )
            {
                string key=pair[0];
                var content=new StackPanel
                {
                    Margin=new Thickness(10,10,10,7)
                };
                var icon=Theme.Icon(pair[2],30);
                icon.HorizontalAlignment=HorizontalAlignment.Center;
                icon.SetResourceReference(Control.ForegroundProperty,"MutedBrush");
                content.Children.Add(icon);
                content.Children.Add(new TextBlock
                {
                    Text=pair[1],FontSize=12,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,7,0,0)
                });
                var card=new Border
                {
                    Child=content,CornerRadius=new CornerRadius(6),BorderThickness=new Thickness(Appearance.Layout==key?1.5:1),Margin=new Thickness(4,0,4,0)
                };
                card.SetResourceReference(Border.BorderBrushProperty,Appearance.Layout==key?"AccentBrush":"LineBrush");
                layoutCards.Add(new KeyValuePair<string,Border>(key,card));
                var button=Theme.Button(null,"",delegate
                {
                    owner.SetLayout(key);
                    foreach(var option in layoutCards)
                    {
                        option.Value.BorderThickness=new Thickness(option.Key==Appearance.Layout?1.5:1);
                        option.Value.SetResourceReference(Border.BorderBrushProperty,option.Key==Appearance.Layout?"AccentBrush":"LineBrush");
                    }
                });
                button.Content=card;
                button.Padding=new Thickness(0);
                button.Margin=new Thickness(0);
                card.Width=106;
                layouts.Children.Add(button);
            }
            var advanced=new StackPanel
            {
                Margin=new Thickness(0,10,0,0)
            };
            var fontLine=new DockPanel
            {
                Margin=new Thickness(0,0,0,8)
            };
            fontLine.Children.Add(new TextBlock
            {
                Text="字号",VerticalAlignment=VerticalAlignment.Center,Width=42
            });
            var slider=new Slider
            {
                Minimum=10,Maximum=30,Value=Appearance.FontSize,Width=208,TickFrequency=1,IsSnapToTickEnabled=true
            };
            fontLine.Children.Add(slider);
            var sizeLabel=new TextBlock
            {
                Text=Appearance.FontSize.ToString("0"),Margin=new Thickness(10,0,0,0)
            };
            fontLine.Children.Add(sizeLabel);
            slider.ValueChanged+=delegate
            {
                Appearance.FontSize=slider.Value;
                sizeLabel.Text=slider.Value.ToString("0");
                owner.ApplyAppearance();
            };
            advanced.Children.Add(fontLine);
            var automatic=new CheckBox
            {
                Content="按文件类型设置字体与换行",IsChecked=Appearance.FileAwareDefaults
            };
            advanced.Children.Add(automatic);
            var fonts=new ComboBox
            {
                ItemsSource=new[]
                {
                    "Consolas","Cascadia Mono","Microsoft YaHei UI","Segoe UI"
                },
                SelectedItem=Appearance.FontFamily,Margin=new Thickness(0,0,0,6)
            };
            fonts.SelectionChanged+=delegate
            {
                Appearance.FontFamily=(string)fonts.SelectedItem;
                owner.ApplyAppearance();
            };
            advanced.Children.Add(fonts);
            var wrap=new CheckBox
            {
                Content="自动换行",IsChecked=Appearance.Wrap
            };
            wrap.Click+=delegate
            {
                Appearance.Wrap=wrap.IsChecked==true;
                owner.ApplyAppearance();
            };
            advanced.Children.Add(wrap);
            var lines=new CheckBox
            {
                Content="显示行号",IsChecked=Appearance.LineNumbers
            };
            lines.Click+=delegate
            {
                Appearance.LineNumbers=lines.IsChecked==true;
                owner.ApplyAppearance();
            };
            advanced.Children.Add(lines);
            fonts.IsEnabled=wrap.IsEnabled=!Appearance.FileAwareDefaults;
            automatic.Click+=delegate
            {
                Appearance.FileAwareDefaults=automatic.IsChecked==true;
                fonts.IsEnabled=wrap.IsEnabled=!Appearance.FileAwareDefaults;
                owner.ApplyAppearance();
            };
            var remember=new CheckBox
            {
                Content="记住外观（不记录文件与工作区）",IsChecked=Appearance.RememberAppearance
            };
            remember.Click+=delegate
            {
                Appearance.RememberAppearance=remember.IsChecked==true;
                if(!Appearance.RememberAppearance)try
                {
                    Appearance.Save(preferencesPath);
                }
                catch(Exception ex)
                {
                    owner.SetNotice(ex.Message);
                }
            };
            advanced.Children.Add(remember);
            advanced.Children.Add(Theme.Button(null,"恢复默认布局",delegate
            {
                Appearance.EditorRatio=.54;
                Appearance.PreviewRatio=.48;
                Appearance.EditorsVertical=true;
                Appearance.PreviewPosition="right";
                owner.SetLayout("single");
            }
            ));
            panel.Children.Add(new Expander
            {
                Header="阅读设置",Content=advanced,Margin=new Thickness(4,16,4,12)
            });
            panel.Children.Add(Theme.Line());
            var help=new StackPanel
            {
                Orientation=Orientation.Horizontal,Margin=new Thickness(4,12,0,0)
            };
            help.Children.Add(Theme.Icon("grip-vertical",16));
            help.Children.Add(new TextBlock
            {
                Text="拖动标题调整位置",FontSize=12,Margin=new Thickness(8,0,0,0)
            });
            panel.Children.Add(help);
            Content=outer;
            Popup=new Popup
            {
                Child=this,PlacementTarget=owner,Placement=PlacementMode.Relative,HorizontalOffset=Math.Max(8,owner.ActualWidth-408),VerticalOffset=44,StaysOpen=false,AllowsTransparency=true,IsOpen=true
            };
        }
        TextBlock SectionLabel(string text)
        {
            var label=new TextBlock
            {
                Text=text,FontSize=13,FontWeight=FontWeights.Medium,Margin=new Thickness(4,16,0,9)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");
            return label;
        }
        internal void Close()
        {
            if(Popup!=null)Popup.IsOpen=false;
        }
    }
}
