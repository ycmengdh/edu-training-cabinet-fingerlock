using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FingerprintLockManager
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
        private const uint DefaultToNearestMonitor = 0x00000002;
        private static readonly Uri AppIconUri = new("pack://application:,,,/Resources/logo.ico");

        private Button? _minimizeButton;
        private Button? _maximizeButton;
        private Button? _closeButton;
        private HwndSource? _windowSource;

        public BorderlessWindow()
        {
            SetResourceReference(StyleProperty, typeof(BorderlessWindow));
            try
            {
                // 所有 Borderless 窗口统一使用应用 logo（任务栏缩略图 / Alt-Tab）
                Icon = BitmapFrame.Create(AppIconUri);
            }
            catch
            {
                // 资源缺失时保持系统默认图标，不阻断启动
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _windowSource?.AddHook(WindowProcedure);
            ApplyCaptionButtonVisibility();
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
