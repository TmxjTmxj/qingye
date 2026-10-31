using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Qingye.Core;

namespace Qingye.UI
{
    internal static class ModelSettingsDialog
    {
        internal static void Show(Window owner, string path, Action saved)
        {
            var config = ModelConfig.Load(path);
            var dialog = new Window
            {
                Owner = owner,
                Title = "模型配置",
                Width = 560,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize
            };
            Theme.Install(dialog);
            Theme.Apply(dialog, new Settings());
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = "Agent 模型配置", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            var note = new TextBlock
            {
                Text = "密钥只保存在本机模型文件，不写入外观偏好，也不会进入便携包。Agent 会把读取的文件内容发送到这个端点。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 16)
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.Children.Add(note);

            panel.Children.Add(Label("API 地址（OpenAI 兼容）"));
            var baseUrl = new TextBox { Text = config.BaseUrl, Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(baseUrl);
            panel.Children.Add(Label("模型名"));
            var model = new TextBox { Text = config.ModelName, Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(model);
            panel.Children.Add(Label("API Key"));
            var key = new PasswordBox { Password = config.ApiKey, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 12) };
            key.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
            key.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            panel.Children.Add(key);

            var pathLabel = new TextBlock { Text = "配置文件：" + path, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            pathLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.Children.Add(pathLabel);
            var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.Children.Add(status);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var test = Theme.Button(null, "测试连接", delegate { });
            var save = Theme.Button(null, "保存", delegate { });
            actions.Children.Add(test);
            actions.Children.Add(save);
            actions.Children.Add(Theme.Button(null, "取消", dialog.Close));
            panel.Children.Add(actions);
            dialog.Content = panel;

            Func<ModelConfig> build = delegate
            {
                return new ModelConfig { BaseUrl = baseUrl.Text.Trim(), ApiKey = key.Password.Trim(), ModelName = model.Text.Trim() };
            };
            test.Click += async delegate
            {
                var candidate = build();
                if (!candidate.IsValid) { status.Text = "请填写 https 地址、模型名和 API Key。"; return; }
                test.IsEnabled = false;
                status.Text = "正在连接…";
                try
                {
                    using (var client = new AgentClient(candidate))
                    using (var source = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    {
                        string reply = await client.TestAsync(source.Token);
                        status.Text = String.IsNullOrWhiteSpace(reply) ? "连接成功。" : "连接成功：" + ModelConfig.Redact(reply, candidate.ApiKey);
                    }
                }
                catch (Exception error) { status.Text = "连接失败：" + ModelConfig.Redact(error.Message, candidate.ApiKey); }
                finally { test.IsEnabled = true; }
            };
            save.Click += delegate
            {
                var candidate = build();
                if (!candidate.IsValid) { status.Text = "请填写 https 地址、模型名和 API Key。"; return; }
                try
                {
                    candidate.Save(path);
                    if (saved != null) saved();
                    dialog.Close();
                }
                catch (Exception error) { status.Text = "保存失败：" + error.Message; }
            };
            dialog.Loaded += delegate { baseUrl.Focus(); };
            dialog.ShowDialog();
        }

        static TextBlock Label(string text)
        {
            var label = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 5) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            return label;
        }
    }
}
