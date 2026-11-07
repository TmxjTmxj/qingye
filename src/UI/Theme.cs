using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Automation;
using Qingye.Core;
namespace Qingye.UI
{
    public static class Theme
    {
        public static Brush Brush(string hex){var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));brush.Freeze();return brush;}
        public static void Install(Window window)
        {
            const string templates=@"<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
<Style TargetType='Button'><Setter Property='FontSize' Value='14'/><Setter Property='Padding' Value='12,7'/><Setter Property='Margin' Value='2,0'/><Setter Property='Background' Value='Transparent'/><Setter Property='Foreground' Value='{DynamicResource TextBrush}'/><Setter Property='BorderThickness' Value='0'/><Setter Property='Cursor' Value='Hand'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'><Border x:Name='root' Background='{TemplateBinding Background}' BorderBrush='Transparent' BorderThickness='1' CornerRadius='6' Padding='{TemplateBinding Padding}'><ContentPresenter VerticalAlignment='Center' HorizontalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='root' Property='Background' Value='{DynamicResource HoverBrush}'/></Trigger><Trigger Property='IsPressed' Value='True'><Setter TargetName='root' Property='Background' Value='{DynamicResource PanelBrush}'/></Trigger><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='root' Property='BorderBrush' Value='{DynamicResource AccentBrush}'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType='TextBox'><Setter Property='FontSize' Value='14'/><Setter Property='Padding' Value='8,6'/><Setter Property='BorderBrush' Value='{DynamicResource LineBrush}'/><Setter Property='BorderThickness' Value='1'/><Setter Property='Background' Value='{DynamicResource SurfaceBrush}'/><Setter Property='Foreground' Value='{DynamicResource TextBrush}'/></Style>
<Style TargetType='ComboBox'><Setter Property='FontSize' Value='13'/><Setter Property='Padding' Value='7,4'/><Setter Property='MinHeight' Value='30'/><Setter Property='MinWidth' Value='90'/><Setter Property='BorderBrush' Value='{DynamicResource LineBrush}'/><Setter Property='BorderThickness' Value='1'/><Setter Property='Background' Value='{DynamicResource SurfaceBrush}'/><Setter Property='Foreground' Value='{DynamicResource TextBrush}'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBox'><Grid><Border x:Name='root' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='5'><Grid><ContentPresenter x:Name='ContentSite' IsHitTestVisible='False' Margin='8,0,24,0' VerticalAlignment='Center' HorizontalAlignment='Left' Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}' ContentStringFormat='{TemplateBinding SelectionBoxItemStringFormat}'/><Path Data='M 0 0 L 4 4 L 8 0' Stroke='{DynamicResource MutedBrush}' StrokeThickness='1.4' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,9,0'/><ToggleButton x:Name='toggle' Focusable='False' ClickMode='Press' IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'><ToggleButton.Template><ControlTemplate TargetType='ToggleButton'><Border Background='Transparent'/></ControlTemplate></ToggleButton.Template></ToggleButton></Grid></Border><Popup x:Name='PART_Popup' AllowsTransparency='True' Focusable='False' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' PopupAnimation='Fade' StaysOpen='False'><Border Background='{DynamicResource SurfaceBrush}' BorderBrush='{DynamicResource LineBrush}' BorderThickness='1' CornerRadius='6' Margin='0,2,0,0' MinWidth='{TemplateBinding ActualWidth}' MaxHeight='{TemplateBinding MaxDropDownHeight}'><ScrollViewer SnapsToDevicePixels='True'><ItemsPresenter/></ScrollViewer></Border></Popup></Grid><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='root' Property='Background' Value='{DynamicResource HoverBrush}'/></Trigger><Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='root' Property='BorderBrush' Value='{DynamicResource AccentBrush}'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType='ComboBoxItem'><Setter Property='Padding' Value='9,5'/><Setter Property='Foreground' Value='{DynamicResource TextBrush}'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBoxItem'><Border x:Name='root' Background='Transparent' CornerRadius='4' Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'><Setter TargetName='root' Property='Background' Value='{DynamicResource HoverBrush}'/></Trigger><Trigger Property='IsSelected' Value='True'><Setter TargetName='root' Property='Background' Value='{DynamicResource PanelBrush}'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType='ListBoxItem'><Setter Property='Foreground' Value='{DynamicResource TextBrush}'/><Setter Property='HorizontalContentAlignment' Value='Stretch'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBoxItem'><Border x:Name='item' Background='Transparent' CornerRadius='5' Padding='{TemplateBinding Padding}' Margin='2,1'><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='item' Property='Background' Value='{DynamicResource HoverBrush}'/></Trigger><Trigger Property='IsSelected' Value='True'><Setter TargetName='item' Property='Background' Value='{DynamicResource HoverBrush}'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType='ScrollBar'><Setter Property='Width' Value='11'/><Setter Property='Background' Value='Transparent'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'><Grid Background='{TemplateBinding Background}'><Track x:Name='PART_Track' IsDirectionReversed='True'><Track.DecreaseRepeatButton><RepeatButton x:Name='decrease' Command='ScrollBar.PageUpCommand' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton><Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'><Border Background='{DynamicResource MutedBrush}' Opacity='0.35' Margin='2' CornerRadius='3'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb><Track.IncreaseRepeatButton><RepeatButton x:Name='increase' Command='ScrollBar.PageDownCommand' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton></Track></Grid><ControlTemplate.Triggers><Trigger Property='Orientation' Value='Horizontal'><Setter Property='Width' Value='Auto'/><Setter Property='Height' Value='11'/><Setter TargetName='PART_Track' Property='IsDirectionReversed' Value='False'/><Setter TargetName='decrease' Property='Command' Value='ScrollBar.PageLeftCommand'/><Setter TargetName='increase' Property='Command' Value='ScrollBar.PageRightCommand'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType='CheckBox'><Setter Property='FontSize' Value='13'/><Setter Property='Margin' Value='4,6'/><Setter Property='Foreground' Value='{DynamicResource TextBrush}'/></Style>
<Style TargetType='ContextMenu'><Setter Property='FontSize' Value='14'/><Setter Property='Background' Value='{DynamicResource SurfaceBrush}'/><Setter Property='Foreground' Value='{DynamicResource TextBrush}'/></Style>
<Style TargetType='MenuItem'><Setter Property='Padding' Value='12,7'/></Style>
</ResourceDictionary>";
            window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(templates));
            window.FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI");window.FontSize=14;window.UseLayoutRounding=true;
        }
        public static void Apply(Window window,Settings settings)
        {
            string accent="#14786F",panel="#F3F7F8",hover="#E4EFED",text="#25373F",line="#DFE7EA",muted="#61747D",surface="#FFFFFF";
            if(settings.Theme=="warm"){accent="#875526";panel="#F6F4EF";hover="#ECE8DF";text="#35332D";line="#E6E2DA";muted="#70695D";surface="#FFFEFB";}
            if(settings.Theme=="lilac"){accent="#69569D";panel="#F4F3F8";hover="#EAE6F1";text="#34313F";line="#E5E1EB";muted="#6D6579";}
            if(settings.Theme=="dark"){accent="#6ACCB5";panel="#151F26";hover="#293B43";text="#E6EFF2";line="#34464F";muted="#9BAEB7";surface="#1D2A32";}
            if(!String.IsNullOrEmpty(settings.Accent))accent=settings.Accent;
            window.Resources["AccentBrush"]=Brush(accent);window.Resources["PanelBrush"]=Brush(panel);window.Resources["HoverBrush"]=Brush(hover);
            window.Resources["TextBrush"]=Brush(text);window.Resources["LineBrush"]=Brush(line);window.Resources["MutedBrush"]=Brush(muted);window.Resources["SurfaceBrush"]=Brush(surface);
            string[] tokens = { "CodeBackground", "TableHeader", "Rule", "QuoteBorder", "QuoteText", "SyntaxComment", "SyntaxString", "SyntaxNumber", "SyntaxKeyword", "SyntaxAttribute", "SyntaxHeading", "EditorLineHighlight" };
            string[] content = { "#EFF7F4", "#EFF7F4", "#DFE9E4", "#80A995", "#677C72", "#63746A", "#377452", "#97552D", "#52609A", "#77608F", "#466A5A", "#F1F8F4" };
            if (settings.Theme == "warm")
                content = new[] { "#F5F0E7", "#F3EDE2", "#E6DED1", "#B89B75", "#766652", "#796D5B", "#79602F", "#9A512C", "#74538D", "#476E82", "#775B38", "#F7F3E9" };
            if (settings.Theme == "lilac")
                content = new[] { "#F0EDF7", "#EDE8F5", "#E2DBEC", "#A08AB9", "#756780", "#786A83", "#766397", "#995977", "#655594", "#476D89", "#6B5489", "#F3F0F9" };
            if (settings.Theme == "dark")
                content = new[] { "#2A3430", "#2A3430", "#3A4741", "#6FA98E", "#A9BDB3", "#8FA79B", "#7FC29A", "#E0A06A", "#9DA8E8", "#C19BD8", "#9CC7B2", "#24323A" };
            for (int index = 0; index < tokens.Length; index++)
                window.Resources[tokens[index]] = Brush(content[index]);
            window.Resources["SyntaxText"] = Brush(text);
            string[] diff = settings.Theme == "dark" ? new[] { "#223E30", "#462D32", "#9AD8AE", "#F2AFB5" }
                : settings.Theme == "warm" ? new[] { "#EDF1E5", "#F7ECE5", "#3E6840", "#963F34" }
                : settings.Theme == "lilac" ? new[] { "#E9F1EC", "#F5EAF0", "#34684F", "#943F65" }
                : new[] { "#E8F3EC", "#FAEEEE", "#2A6547", "#963B3B" };
            string[] diffTokens = { "DiffAddedBackground", "DiffRemovedBackground", "DiffAddedText", "DiffRemovedText" };
            for (int index = 0; index < diffTokens.Length; index++) window.Resources[diffTokens[index]] = Brush(diff[index]);
            window.SetResourceReference(Window.BackgroundProperty,"PanelBrush");window.SetResourceReference(Window.ForegroundProperty,"TextBrush");
        }
        public static T ContentToken<T>(T element,DependencyProperty property,string token) where T:FrameworkContentElement
        {
            element.SetResourceReference(property,token);
            return element;
        }
        public static IconView Icon(string glyph,double size=20)
        {
            return new IconView(glyph,size);
        }
        public static Button Button(string glyph,string label,Action click)
        {
            bool labelled=!String.IsNullOrEmpty(label);var content=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};if(glyph!=null){var icon=Icon(glyph,18);icon.Margin=new Thickness(0,0,labelled?8:0,0);content.Children.Add(icon);}
            if(labelled)content.Children.Add(new TextBlock{Text=label,Tag="ButtonLabel",VerticalAlignment=VerticalAlignment.Center,FontWeight=FontWeights.Medium});
            var button=new Button{Content=content,MinHeight=32};if(!labelled && glyph!=null){button.Width=30;button.Height=30;button.Padding=new Thickness(0);button.Margin=new Thickness(2,0,2,0);}if(labelled){button.ToolTip=label;AutomationProperties.SetName(button,label);}button.Click+=delegate{click();};return button;
        }
        public static void Compact(Button button,bool compact){var content=button.Content as StackPanel;if(content==null)return;foreach(var child in content.Children){var text=child as TextBlock;if(text!=null && (string)text.Tag=="ButtonLabel")text.Visibility=compact?Visibility.Collapsed:Visibility.Visible;var icon=child as IconView;if(icon!=null)icon.Margin=new Thickness(0,0,compact?0:9,0);}button.Padding=compact?new Thickness(10,7,10,7):new Thickness(12,7,12,7);}
        public static void Tint(Button button,string token){var content=button.Content as StackPanel;if(content==null)return;foreach(var child in content.Children){var icon=child as IconView;if(icon!=null)icon.SetResourceReference(Control.ForegroundProperty,token);}}
        public static FrameworkElement FileLabel(string name,bool folder=false)
        {
            string extension=System.IO.Path.GetExtension(name).ToLowerInvariant();string symbol=folder?"folder":extension==".css"?"hash":extension==".json"?"braces":extension==".md" || extension==".txt"?"file-text":"file-code";
            string color=folder?"#8AA39A":extension==".py"?"#B8874E":extension==".css"?"#5E93CE":extension==".md"?"#7F8F99":"#289A7B";var panel=new Grid{VerticalAlignment=VerticalAlignment.Center};panel.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(27)});panel.ColumnDefinitions.Add(new ColumnDefinition());panel.Children.Add(new IconView(symbol,18){Foreground=Brush(color),HorizontalAlignment=HorizontalAlignment.Left});var text=new TextBlock{Text=name,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};Grid.SetColumn(text,1);panel.Children.Add(text);return panel;
        }
        public static Border Line(bool vertical=false){var result=new Border{Width=vertical?1:Double.NaN,Height=vertical?Double.NaN:1};result.SetResourceReference(Border.BackgroundProperty,"LineBrush");return result;}
        public static MenuItem Menu(string label,Action action){var item=new MenuItem{Header=label};item.Click+=delegate{action();};return item;}
    }
}
