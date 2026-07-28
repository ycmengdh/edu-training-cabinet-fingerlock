using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace FingerprintLockManager
{
    public partial class AppMessageBoxWindow : BorderlessWindow
    {
        private readonly MessageBoxResult _closeResult;

        public AppMessageBoxWindow(
            string message,
            string caption,
            MessageBoxButton buttons,
            MessageBoxImage image,
            MessageBoxResult defaultResult)
        {
            InitializeComponent();
            Title = string.IsNullOrWhiteSpace(caption) ? "提示" : caption;
            MessageText.Text = message;
            _closeResult = GetCloseResult(buttons);
            Result = _closeResult;
            ConfigureIcon(image);
            ConfigureButtons(buttons, defaultResult);
        }

        public MessageBoxResult Result { get; private set; }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (Result == MessageBoxResult.None)
                Result = _closeResult;
            base.OnClosing(e);
        }

        private void ConfigureIcon(MessageBoxImage image)
        {
            string glyph;
            string surfaceKey;
            string foregroundKey;
            switch (image)
            {
                case MessageBoxImage.Error:
                    glyph = "\uEA39";
                    surfaceKey = "DangerSurfaceBrush";
                    foregroundKey = "DangerBrush";
                    break;
                case MessageBoxImage.Warning:
                    glyph = "\uE7BA";
                    surfaceKey = "SurfaceAltBrush";
                    foregroundKey = "WarningBrush";
                    break;
                case MessageBoxImage.Question:
                    glyph = "\uE9CE";
                    surfaceKey = "PrimaryLightBrush";
                    foregroundKey = "PrimaryBrush";
                    break;
                default:
                    glyph = "\uE946";
                    surfaceKey = "PrimaryLightBrush";
                    foregroundKey = "PrimaryBrush";
                    break;
            }

            IconText.Text = glyph;
            IconSurface.SetResourceReference(Border.BackgroundProperty, surfaceKey);
            IconText.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);
        }

        private void ConfigureButtons(MessageBoxButton buttons, MessageBoxResult defaultResult)
        {
            switch (buttons)
            {
                case MessageBoxButton.OKCancel:
                    AddButton("取消", MessageBoxResult.Cancel, false,
                        defaultResult == MessageBoxResult.Cancel, true);
                    AddButton("确定", MessageBoxResult.OK, true,
                        defaultResult != MessageBoxResult.Cancel, false);
                    break;
                case MessageBoxButton.YesNo:
                    AddButton("否", MessageBoxResult.No, false,
                        defaultResult == MessageBoxResult.No, true);
                    AddButton("是", MessageBoxResult.Yes, true,
                        defaultResult != MessageBoxResult.No, false);
                    break;
                case MessageBoxButton.YesNoCancel:
                    AddButton("取消", MessageBoxResult.Cancel, false,
                        defaultResult == MessageBoxResult.Cancel, true);
                    AddButton("否", MessageBoxResult.No, false,
                        defaultResult == MessageBoxResult.No, false);
                    AddButton("是", MessageBoxResult.Yes, true,
                        defaultResult is MessageBoxResult.None or MessageBoxResult.Yes, false);
                    break;
                default:
                    AddButton("确定", MessageBoxResult.OK, true, true, true);
                    break;
            }
        }

        private void AddButton(
            string text,
            MessageBoxResult result,
            bool isPrimary,
            bool isDefault,
            bool isCancel)
        {
            Button button = new()
            {
                Content = text,
                MinWidth = 82,
                Margin = new Thickness(ButtonPanel.Children.Count == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = isDefault,
                IsCancel = isCancel
            };
            if (!isPrimary)
                button.SetResourceReference(StyleProperty, "SecondaryButton");

            button.Click += (_, _) =>
            {
                Result = result;
                Close();
            };
            ButtonPanel.Children.Add(button);
        }

        private static MessageBoxResult GetCloseResult(MessageBoxButton buttons) =>
            buttons switch
            {
                MessageBoxButton.OK => MessageBoxResult.OK,
                MessageBoxButton.YesNo => MessageBoxResult.No,
                _ => MessageBoxResult.Cancel
            };
    }

    public static class MessageBox
    {
        public static MessageBoxResult Show(string messageBoxText) =>
            Show(messageBoxText, "提示", MessageBoxButton.OK, MessageBoxImage.None,
                MessageBoxResult.None);

        public static MessageBoxResult Show(string messageBoxText, string caption) =>
            Show(messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None,
                MessageBoxResult.None);

        public static MessageBoxResult Show(
            string messageBoxText,
            string caption,
            MessageBoxButton button) =>
            Show(messageBoxText, caption, button, MessageBoxImage.None, MessageBoxResult.None);

        public static MessageBoxResult Show(
            string messageBoxText,
            string caption,
            MessageBoxButton button,
            MessageBoxImage icon) =>
            Show(messageBoxText, caption, button, icon, MessageBoxResult.None);

        public static MessageBoxResult Show(
            string messageBoxText,
            string caption,
            MessageBoxButton button,
            MessageBoxImage icon,
            MessageBoxResult defaultResult)
        {
            Application? application = Application.Current;
            if (application?.Dispatcher == null)
            {
                return System.Windows.MessageBox.Show(
                    messageBoxText, caption, button, icon, defaultResult);
            }

            if (!application.Dispatcher.CheckAccess())
            {
                return application.Dispatcher.Invoke(
                    () => Show(messageBoxText, caption, button, icon, defaultResult));
            }

            AppMessageBoxWindow dialog = new(
                messageBoxText, caption, button, icon, defaultResult);
            Window? owner = application.Windows
                .OfType<Window>()
                .FirstOrDefault(window => window.IsActive && window.IsVisible)
                ?? application.MainWindow;
            if (owner?.IsVisible == true)
                dialog.Owner = owner;

            dialog.ShowDialog();
            return dialog.Result;
        }
    }
}
