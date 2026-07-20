using System.Windows;
using System.Windows.Controls;

namespace FingerprintLockManager
{
    /// <summary>
    /// 简单的文本输入对话框（V2.7 副指纹删除等场景使用）。
    /// WPF 没有内置 InputBox，这里用 Window + TextBox 实现。
    /// </summary>
    public static class PromptDialog
    {
        /// <summary>
        /// 显示文本输入对话框。返回用户输入的字符串；用户取消返回 null。
        /// </summary>
        public static string? Show(string prompt, string title, string defaultValue = "")
        {
            var window = new Window
            {
                Title = title,
                Width = 420,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush")
            };

            var stack = new StackPanel { Margin = new Thickness(20) };
            var label = new TextBlock
            {
                Text = prompt,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            var textBox = new TextBox { Text = defaultValue };
            textBox.Loaded += (_, _) => { textBox.Focus(); textBox.SelectAll(); };
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var okBtn = new Button { Content = "确定", Padding = new Thickness(20, 4, 20, 4), Margin = new Thickness(8, 0, 0, 0) };
            var cancelBtn = new Button
            {
                Content = "取消",
                Padding = new Thickness(20, 4, 20, 4),
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)Application.Current.FindResource("SecondaryButton")
            };
            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(okBtn);
            stack.Children.Add(label);
            stack.Children.Add(textBox);
            stack.Children.Add(btnPanel);
            window.Content = stack;

            string? result = null;
            okBtn.Click += (_, _) => { result = textBox.Text; window.DialogResult = true; window.Close(); };
            cancelBtn.Click += (_, _) => { window.DialogResult = false; window.Close(); };

            bool? dialogResult = window.ShowDialog();
            return dialogResult == true ? result : null;
        }
    }
}
