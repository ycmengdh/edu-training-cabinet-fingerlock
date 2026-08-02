using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace CabinetLock
{
    public static class AppToast
    {
        private const int MaxVisibleToasts = 4;
        private static readonly List<ToastEntry> ActiveToasts = new();

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
                    .FirstOrDefault(window => window.IsActive && window.IsVisible)
                    ?? app.MainWindow
                    ?? app.Windows.OfType<Window>().FirstOrDefault(window => window.IsVisible);

                ToastPalette palette = CreatePalette(owner, kind);
                var scale = new ScaleTransform(0.22, 0.78);
                var translate = new TranslateTransform(0, 14);
                var transforms = new TransformGroup();
                transforms.Children.Add(scale);
                transforms.Children.Add(translate);

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
                    Focusable = false,
                    Opacity = 0,
                    Owner = owner
                };

                var root = new Border
                {
                    Background = palette.Surface,
                    BorderBrush = palette.Border,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10, 16, 10),
                    MinWidth = 280,
                    MaxWidth = 500,
                    SnapsToDevicePixels = true,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = transforms,
                    Effect = new DropShadowEffect
                    {
                        BlurRadius = 20,
                        ShadowDepth = 4,
                        Opacity = ThemeManager.Current == AppTheme.Dark ? 0.42 : 0.18,
                        Direction = 270,
                        Color = ThemeManager.Current == AppTheme.Dark
                            ? Colors.Black
                            : Color.FromRgb(15, 23, 42)
                    }
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var iconSurface = new Border
                {
                    Width = 28,
                    Height = 28,
                    CornerRadius = new CornerRadius(7),
                    Background = palette.IconSurface,
                    VerticalAlignment = VerticalAlignment.Center
                };
                iconSurface.Child = new TextBlock
                {
                    Text = palette.Glyph,
                    FontFamily = TryResource(owner, "AppIconFont") as FontFamily
                        ?? new FontFamily("Segoe Fluent Icons"),
                    FontSize = 14,
                    Foreground = palette.Accent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var label = new TextBlock
                {
                    Text = message.Trim(),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    LineHeight = 20,
                    Foreground = TryBrush(owner, "TextBrush") ?? Brushes.Black,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(label, 1);
                grid.Children.Add(iconSurface);
                grid.Children.Add(label);
                root.Child = grid;
                toast.Content = root;

                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(Math.Max(1400, durationMs))
                };
                var entry = new ToastEntry(toast, owner, timer, scale, translate);
                timer.Tick += (_, _) => BeginExit(entry);
                toast.Closed += (_, _) => Remove(entry);
                toast.Loaded += (_, _) =>
                {
                    PositionStack(owner, animate: false);
                    BeginEntrance(entry);
                };

                ActiveToasts.Add(entry);
                toast.Show();

                ToastEntry? overflow = ActiveToasts
                    .Where(item => !item.IsClosing)
                    .SkipLast(MaxVisibleToasts)
                    .FirstOrDefault();
                if (overflow != null)
                    BeginExit(overflow);
            }

            if (app.Dispatcher.CheckAccess()) ShowCore();
            else app.Dispatcher.BeginInvoke(ShowCore);
        }

        private static void BeginEntrance(ToastEntry entry)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var opacity = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190))
            {
                EasingFunction = ease
            };
            var scaleX = new DoubleAnimation(0.22, 1, TimeSpan.FromMilliseconds(340))
            {
                EasingFunction = ease
            };
            var scaleY = new DoubleAnimation(0.78, 1, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = ease
            };
            var rise = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = ease
            };
            rise.Completed += (_, _) =>
            {
                if (!entry.IsClosing && entry.Window.IsVisible)
                    entry.Timer.Start();
            };

            entry.Window.BeginAnimation(UIElement.OpacityProperty, opacity);
            entry.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
            entry.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
            entry.Translate.BeginAnimation(TranslateTransform.YProperty, rise);
        }

        private static void BeginExit(ToastEntry entry)
        {
            if (entry.IsClosing) return;
            entry.IsClosing = true;
            entry.Timer.Stop();

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var opacity = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = ease
            };
            var rise = new DoubleAnimation(0, -20, TimeSpan.FromMilliseconds(360))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var shrink = new DoubleAnimation(1, 0.96, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = ease
            };
            opacity.Completed += (_, _) =>
            {
                try { entry.Window.Close(); } catch { }
            };

            entry.Window.BeginAnimation(UIElement.OpacityProperty, opacity);
            entry.Translate.BeginAnimation(TranslateTransform.YProperty, rise);
            entry.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            entry.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }

        private static void Remove(ToastEntry entry)
        {
            entry.Timer.Stop();
            ActiveToasts.Remove(entry);
            PositionStack(entry.Owner, animate: true);
        }

        private static void PositionStack(Window? owner, bool animate)
        {
            List<ToastEntry> entries = ActiveToasts
                .Where(entry => ReferenceEquals(entry.Owner, owner) && entry.Window.IsVisible)
                .ToList();
            if (entries.Count == 0) return;

            double centerX;
            double top;
            if (owner?.IsVisible == true && owner.WindowState != WindowState.Minimized)
            {
                centerX = owner.Left + owner.ActualWidth / 2;
                top = owner.Top + 64;
            }
            else
            {
                Rect workArea = SystemParameters.WorkArea;
                centerX = workArea.Left + workArea.Width / 2;
                top = workArea.Top + 28;
            }

            double virtualLeft = SystemParameters.VirtualScreenLeft + 8;
            double virtualRight = SystemParameters.VirtualScreenLeft
                + SystemParameters.VirtualScreenWidth - 8;
            foreach (ToastEntry entry in entries)
            {
                entry.Window.UpdateLayout();
                double width = Math.Max(entry.Window.ActualWidth, entry.Window.DesiredSize.Width);
                double height = Math.Max(entry.Window.ActualHeight, entry.Window.DesiredSize.Height);
                double left = Math.Clamp(centerX - width / 2, virtualLeft,
                    Math.Max(virtualLeft, virtualRight - width));

                entry.Window.Left = left;
                MoveTop(entry.Window, top, animate);
                top += height + 10;
            }
        }

        private static void MoveTop(Window window, double target, bool animate)
        {
            if (!animate || double.IsNaN(window.Top) || Math.Abs(window.Top - target) < 0.5)
            {
                window.BeginAnimation(Window.TopProperty, null);
                window.Top = target;
                return;
            }

            double current = window.Top;
            window.BeginAnimation(Window.TopProperty, null);
            window.Top = target;
            var movement = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            window.BeginAnimation(Window.TopProperty, movement);
        }

        private static ToastPalette CreatePalette(Window? owner, Kind kind)
        {
            string accentKey = kind switch
            {
                Kind.Success => "SuccessBrush",
                Kind.Warning => "WarningBrush",
                Kind.Error => "DangerBrush",
                _ => "PrimaryBrush"
            };
            string glyph = kind switch
            {
                Kind.Success => "\uE73E",
                Kind.Warning => "\uE7BA",
                Kind.Error => "\uEA39",
                _ => "\uE946"
            };

            Brush accent = TryBrush(owner, accentKey)
                ?? new SolidColorBrush(Color.FromRgb(15, 118, 110));
            Brush card = TryBrush(owner, "CardBrush") ?? Brushes.White;
            Brush baseBorder = TryBrush(owner, "BorderBrush")
                ?? new SolidColorBrush(Color.FromRgb(221, 227, 234));
            Brush surface = kind switch
            {
                Kind.Info => TryBrush(owner, "PrimaryLightBrush") ?? Blend(card, accent, 0.09),
                Kind.Error => TryBrush(owner, "DangerSurfaceBrush") ?? Blend(card, accent, 0.09),
                _ => Blend(card, accent, ThemeManager.Current == AppTheme.Dark ? 0.14 : 0.08)
            };

            return new ToastPalette(
                accent,
                surface,
                Blend(baseBorder, accent, ThemeManager.Current == AppTheme.Dark ? 0.28 : 0.18),
                Blend(surface, accent, ThemeManager.Current == AppTheme.Dark ? 0.20 : 0.12),
                glyph);
        }

        private static Brush Blend(Brush background, Brush foreground, double foregroundWeight)
        {
            if (background is not SolidColorBrush baseBrush || foreground is not SolidColorBrush tintBrush)
                return background;

            double weight = Math.Clamp(foregroundWeight, 0, 1);
            byte Mix(byte from, byte to) =>
                (byte)Math.Round(from + (to - from) * weight);
            return new SolidColorBrush(Color.FromRgb(
                Mix(baseBrush.Color.R, tintBrush.Color.R),
                Mix(baseBrush.Color.G, tintBrush.Color.G),
                Mix(baseBrush.Color.B, tintBrush.Color.B)));
        }

        private static object? TryResource(Window? owner, string key)
        {
            try
            {
                return owner?.TryFindResource(key) ?? Application.Current?.TryFindResource(key);
            }
            catch
            {
                return null;
            }
        }

        private static Brush? TryBrush(Window? owner, string key) =>
            TryResource(owner, key) as Brush;

        private sealed class ToastEntry
        {
            public ToastEntry(
                Window window,
                Window? owner,
                DispatcherTimer timer,
                ScaleTransform scale,
                TranslateTransform translate)
            {
                Window = window;
                Owner = owner;
                Timer = timer;
                Scale = scale;
                Translate = translate;
            }

            public Window Window { get; }
            public Window? Owner { get; }
            public DispatcherTimer Timer { get; }
            public ScaleTransform Scale { get; }
            public TranslateTransform Translate { get; }
            public bool IsClosing { get; set; }
        }

        private readonly record struct ToastPalette(
            Brush Accent,
            Brush Surface,
            Brush Border,
            Brush IconSurface,
            string Glyph);
    }
}
