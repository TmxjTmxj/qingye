using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Qingye.Core;

namespace Qingye.UI
{
    internal static class PromptDialog
    {
        internal static string Ask(Window owner, string title, string label, string initial)
        {
            var dialog = new Window
            {
                Owner = owner,
                Title = title,
                Width = 440,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };
            Theme.Install(dialog);
            Theme.Apply(dialog, new Settings());
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8) });
            var input = new TextBox { Text = initial ?? "" };
            panel.Children.Add(input);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            string result = null;
            actions.Children.Add(Theme.Button(null, "取消", dialog.Close));
            var confirm = Theme.Button(null, "确定", delegate { result = input.Text; dialog.Close(); });
            actions.Children.Add(confirm);
            panel.Children.Add(actions);
            dialog.Content = panel;
            input.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { result = input.Text; dialog.Close(); e.Handled = true; }
                else if (e.Key == Key.Escape) dialog.Close();
            };
            dialog.Loaded += delegate { input.Focus(); input.SelectAll(); };
            dialog.ShowDialog();
            return result;
        }
    }
}
