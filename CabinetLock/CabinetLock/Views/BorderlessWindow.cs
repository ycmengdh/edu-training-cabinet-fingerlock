using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CabinetLock
{
    [TemplatePart(Name = MinimizeButtonPartName, Type = typeof(Button))]
    [TemplatePart(Name = MaximizeButtonPartName, Type = typeof(Button))]
    [TemplatePart(Name = CloseButtonPartName, Type = typeof(Button))]
    public class BorderlessWindow : Window
    {
        private const string MinimizeButtonPartName = "PART_MinimizeButton";
        private const string MaximizeButtonPartName = "PART_MaximizeButton";
        private const string CloseButtonPartName = "PART_CloseButton";
        private const int GetMinMaxInfoMessage = 0x0024;
        private const int SetIconMessage = 0x0080;
        private const int IconSmall = 0;
        private const int IconBig = 1;
        private const uint ImageIcon = 1;
        private const uint LoadFromFile = 0x0010;
        private const uint DefaultToNearestMonitor = 0x00000002;
        private static readonly Uri AppIconUri = new("pack://application:,,,/Resources/logo.ico");
        private static readonly object IconCacheLock = new();
        private static string? _cachedIconPath;
        private static ImageSource? _cachedWpfIcon;

        private Button? _minimizeButton;
        private Button? _maximizeButton;
        private Button? _closeButton;
        private HwndSource? _windowSource;

        public BorderlessWindow()
        {
            SetResourceReference(StyleProperty, typeof(BorderlessWindow));
            try
            {
                // 标题栏 / Alt-Tab 用 WPF Icon；任务栏在 SourceInitialized 再补 Win32 图标
                Icon = GetOrLoadWpfIcon();
            }
            catch
            {
                // 资源缺失时保持系统默认图标，不阻断启动
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            _windowSource = HwndSource.FromHwnd(hwnd);
            _windowSource?.AddHook(WindowProcedure);
            ApplyCaptionButtonVisibility();
            // WindowStyle=None 时仅设 Window.Icon 往往不够，任务栏仍可能是默认图标
            ApplyNativeTaskbarIcon(hwnd);
        }

        /// <summary>加载并缓存 WPF ImageSource（用于 Window.Icon）。</summary>
        private static ImageSource GetOrLoadWpfIcon()
        {
            if (_cachedWpfIcon != null) return _cachedWpfIcon;
            lock (IconCacheLock)
            {
                if (_cachedWpfIcon != null) return _cachedWpfIcon;
                // OnLoad：立即解码；忽略缓存色偏，确保 ico 多尺寸可用
                BitmapDecoder decoder = BitmapDecoder.Create(
                    AppIconUri,
                    BitmapCreateOptions.None,
                    BitmapCacheOption.OnLoad);
                // 优先取接近 32px 的帧，任务栏观感更好
                BitmapFrame? best = null;
                int bestScore = int.MaxValue;
                foreach (BitmapFrame frame in decoder.Frames)
                {
                    int score = Math.Abs(frame.PixelWidth - 32) + Math.Abs(frame.PixelHeight - 32);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = frame;
                    }
                }
                _cachedWpfIcon = best ?? decoder.Frames[0];
                return _cachedWpfIcon;
            }
        }

        /// <summary>
        /// 将 pack 内 logo.ico 落到临时文件，经 LoadImage + WM_SETICON 写入 HWND，
        /// 修复无边框窗口任务栏不显示应用图标的问题（启动页尤为明显）。
        /// </summary>
        private static void ApplyNativeTaskbarIcon(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                string path = EnsureIconFileOnDisk();
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

                // 小图标（任务栏）与大图标（Alt-Tab）
                IntPtr small = LoadImage(IntPtr.Zero, path, ImageIcon, 16, 16, LoadFromFile);
                IntPtr big = LoadImage(IntPtr.Zero, path, ImageIcon, 32, 32, LoadFromFile);
                if (small != IntPtr.Zero)
                    SendMessage(hwnd, SetIconMessage, new IntPtr(IconSmall), small);
                if (big != IntPtr.Zero)
                    SendMessage(hwnd, SetIconMessage, new IntPtr(IconBig), big);
                // 若 16 失败则用 32 顶上
                if (small == IntPtr.Zero && big != IntPtr.Zero)
                    SendMessage(hwnd, SetIconMessage, new IntPtr(IconSmall), big);
            }
            catch
            {
                // 图标失败不影响窗口功能
            }
        }

        private static string EnsureIconFileOnDisk()
        {
            lock (IconCacheLock)
            {
                if (!string.IsNullOrEmpty(_cachedIconPath) && File.Exists(_cachedIconPath))
                    return _cachedIconPath;

                // 1) 优先用输出目录旁的 logo（发布/调试均常见）
                try
                {
                    string baseDir = AppContext.BaseDirectory;
                    string beside = Path.Combine(baseDir, "Resources", "logo.ico");
                    if (File.Exists(beside))
                    {
                        _cachedIconPath = beside;
                        return _cachedIconPath;
                    }
                    string flat = Path.Combine(baseDir, "logo.ico");
                    if (File.Exists(flat))
                    {
                        _cachedIconPath = flat;
                        return _cachedIconPath;
                    }
                }
                catch { }

                // 2) 从 pack URI 抽出到 %TEMP%
                var resource = Application.GetResourceStream(AppIconUri);
                if (resource?.Stream == null)
                    return "";

                string temp = Path.Combine(Path.GetTempPath(), "CabinetLock_logo.ico");
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    resource.Stream.CopyTo(fs);
                }
                _cachedIconPath = temp;
                return _cachedIconPath;
            }
        }

        public override void OnApplyTemplate()
        {
            DetachCaptionButtonHandlers();
            base.OnApplyTemplate();

            _minimizeButton = GetTemplateChild(MinimizeButtonPartName) as Button;
            _maximizeButton = GetTemplateChild(MaximizeButtonPartName) as Button;
            _closeButton = GetTemplateChild(CloseButtonPartName) as Button;

            if (_minimizeButton != null)
                _minimizeButton.Click += MinimizeButton_Click;
            if (_maximizeButton != null)
                _maximizeButton.Click += MaximizeButton_Click;
            if (_closeButton != null)
                _closeButton.Click += CloseButton_Click;

            ApplyCaptionButtonVisibility();
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == ResizeModeProperty || e.Property == WindowStateProperty)
                ApplyCaptionButtonVisibility();
        }

        /// <summary>
        /// 按 ResizeMode 控制标题栏按钮：NoResize 仅关闭；CanMinimize 隐藏最大化。
        /// 与模板触发器双保险，避免样式触发器未生效时仍显示最大化。
        /// </summary>
        private void ApplyCaptionButtonVisibility()
        {
            if (_minimizeButton != null)
            {
                _minimizeButton.Visibility = ResizeMode == ResizeMode.NoResize
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            if (_maximizeButton != null)
            {
                bool canMaximize = ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;
                _maximizeButton.Visibility = canMaximize ? Visibility.Visible : Visibility.Collapsed;
                if (canMaximize)
                {
                    // Segoe MDL2: E922 maximize, E923 restore
                    _maximizeButton.Content = WindowState == WindowState.Maximized
                        ? ""
                        : "";
                    _maximizeButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            DetachCaptionButtonHandlers();
            _windowSource?.RemoveHook(WindowProcedure);
            _windowSource = null;
            base.OnClosed(e);
        }

        private void DetachCaptionButtonHandlers()
        {
            if (_minimizeButton != null)
                _minimizeButton.Click -= MinimizeButton_Click;
            if (_maximizeButton != null)
                _maximizeButton.Click -= MaximizeButton_Click;
            if (_closeButton != null)
                _closeButton.Click -= CloseButton_Click;
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
            SystemCommands.MinimizeWindow(this);

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
                SystemCommands.RestoreWindow(this);
            else
                SystemCommands.MaximizeWindow(this);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private IntPtr WindowProcedure(
            IntPtr windowHandle,
            int message,
            IntPtr wordParameter,
            IntPtr longParameter,
            ref bool handled)
        {
            if (message != GetMinMaxInfoMessage)
                return IntPtr.Zero;

            ApplyMonitorWorkArea(windowHandle, longParameter);
            handled = true;
            return IntPtr.Zero;
        }

        private static void ApplyMonitorWorkArea(IntPtr windowHandle, IntPtr infoPointer)
        {
            MinMaxInfo minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(infoPointer);
            IntPtr monitorHandle = MonitorFromWindow(windowHandle, DefaultToNearestMonitor);
            if (monitorHandle == IntPtr.Zero)
                return;

            MonitorInfo monitorInfo = new()
            {
                Size = Marshal.SizeOf<MonitorInfo>()
            };
            if (!GetMonitorInfo(monitorHandle, ref monitorInfo))
                return;

            minMaxInfo.MaxPosition.X = Math.Abs(monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left);
            minMaxInfo.MaxPosition.Y = Math.Abs(monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top);
            minMaxInfo.MaxSize.X = Math.Abs(monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left);
            minMaxInfo.MaxSize.Y = Math.Abs(monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top);
            Marshal.StructureToPtr(minMaxInfo, infoPointer, true);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadImage(
            IntPtr instance, string name, uint type, int desiredX, int desiredY, uint load);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public NativePoint Reserved;
            public NativePoint MaxSize;
            public NativePoint MaxPosition;
            public NativePoint MinTrackSize;
            public NativePoint MaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect MonitorArea;
            public NativeRect WorkArea;
            public uint Flags;
        }
    }
}
