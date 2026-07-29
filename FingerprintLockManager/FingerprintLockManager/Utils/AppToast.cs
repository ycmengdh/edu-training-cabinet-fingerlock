using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FingerprintLockManager
{
    /// <summary>
    /// 轻量全局 Toast：不阻断操作，2.5s 后自动关闭。
    /// 优先附着到当前主窗口右上角。
    /// </summary>
    public static class AppToast
    {
        public enum Kind
        {
            Info,
            Success,
            Warning,
            Error
        }

        public static void Info(string message) => Show(message, Kind.Info);
        public static void Success(string message) => Show(message, Kind.Success);
        public static void Warning(string message) => Show(message, Kind.Warning);
        public static void Error(string message) => Show(message, Kind.Error);

        public static void Show(string message, Kind kind = Kind.Info, int durationMs = 2600)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            Application? app = Application.Current;
            if (app == null) return;

            void ShowCore()
            {
                Window? owner = app.Windows.OfType<Window>()
                    .FirstOrDefault(w => w.IsActive && w.IsVisible)
                    ?? app.MainWindow
                    ?? app.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible);

                var toast = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    ShowInTaskbar = false,
                    Topmost = true,
                    ResizeMode = ResizeMode.NoResize,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    ShowActivated = false,
                    Owner = owner
                };

                string bgKey = kind switch
                {
                    Kind.Success => "SuccessBrush",
                    Kind.Warning => "WarningBrush",
                    Kind.Error => "DangerBrush",
                    _ => "PrimaryBrush"
                };
                Brush accent = TryBrush(owner, bgKey) ?? new SolidColorBrush(Color.FromRgb(37, 99, 235));
                Brush card = TryBrush(owner, "CardBrush") ?? Brushes.White;
                Brush text = TryBrush(owner, "TextBrush") ?? Brushes.Black;
                Brush border = TryBrush(owner, "BorderBrush") ?? new SolidColorBrush(Color.FromRgb(226, 232, 240));

                var root = new Border
                {
                    Background = card,
                    BorderBrush = border,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(14, 12, 16, 12),
                    MinWidth = 220,
                    MaxWidth = 420,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        BlurRadius = 16,
                        ShadowDepth = 2,
                        Opacity = 0.22,
                        Direction = 270
                    }
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var bar = new Border
                {
                    Background = accent,
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(0, 2, 10, 2)
                };
                Grid.SetColumn(bar, 0);
                var label = new TextBlock
                {
                    Text = message.Trim(),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12.5,
                    Foreground = text,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(label, 1);
                grid.Children.Add(bar);
                grid.Children.Add(label);
                root.Child = grid;
                toast.Content = root;

                Position(toast, owner);
                toast.Show();

                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1200, durationMs)) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    try { toast.Close(); } catch { }
                };
                timer.Start();
            }

            if (app.Dispatcher.CheckAccess()) ShowCore();
            else app.Dispatcher.BeginInvoke(ShowCore);
        }

        private static Brush? TryBrush(Window? owner, string key)
        {
            try
            {
                object? res = owner?.TryFindResource(key) ?? Application.Current?.TryFindResource(key);
                return res as Brush;
            }
            catch
            {
                return null;
            }
        }

        private static void Position(Window toast, Window? owner)
        {
            toast.Loaded += (_, _) =>
            {
                try
                {
                    toast.UpdateLayout();
                    if (owner != null && owner.IsVisible)
                    {
                        double left = owner.Left + owner.ActualWidth - toast.ActualWidth - 24;
                        double top = owner.Top + 56;
                        if (owner.WindowState == WindowState.Maximized)
                        {
                            left = SystemParameters.WorkArea.Right - toast.ActualWidth - 24;
                            top = SystemParameters.WorkArea.Top + 24;
                        }
                        toast.Left = Math.Max(8, left);
                        toast.Top = Math.Max(8, top);
                    }
                    else
                    {
                        toast.Left = SystemParameters.WorkArea.Right - toast.ActualWidth - 24;
                        toast.Top = SystemParameters.WorkArea.Top + 24;
                    }
                }
                catch { }
            };
        }
    }
}
