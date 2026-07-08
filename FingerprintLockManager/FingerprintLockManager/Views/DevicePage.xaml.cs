using System.Windows;
using System.Windows.Controls;

namespace FingerprintLockManager
{
    /// <summary>
    /// 设备管理页面
    /// 设备列表展示（在线绿点/离线红点）、远程开锁、刷新
    /// </summary>
    public partial class DevicePage : Page
    {
        public DevicePage()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                if (LockSelectBox.Items.Count > 0)
                {
                    LockSelectBox.SelectedIndex = 1; // 默认 Lock1
                }
                LoadDevices();
            };
        }

        /// <summary>加载设备列表</summary>
        private void LoadDevices()
        {
            var devices = App.DeviceService.GetAllDevices();
            DeviceDataGrid.ItemsSource = devices;
        }

        /// <summary>刷新按钮</summary>
        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadDevices();
        }

        /// <summary>远程开锁：向选中设备发送 CONTROL_LOCK 命令</summary>
        private void RemoteUnlockButton_Click(object sender, RoutedEventArgs e)
        {
            if (DeviceDataGrid.SelectedItem is not Device selected)
            {
                MessageBox.Show("请先选择要开锁的设备", "提示");
                return;
            }

            // 获取锁号
            int lockId = 1;
            if (LockSelectBox.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                int.TryParse(item.Tag.ToString(), out lockId);
            }

            // 检查设备是否在线（通过 TCP 连接列表判断）
            bool tcpOnline = false;
            foreach (var dc in App.TcpServer.GetOnlineDevices())
            {
                if (dc.DeviceId == selected.DeviceId && dc.IsOnline)
                {
                    tcpOnline = true;
                    break;
                }
            }

            if (!tcpOnline)
            {
                MessageBox.Show($"设备「{selected.DeviceName}」当前未连接，无法远程开锁", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 权限检查：非管理员不允许操作系统锁
            if (lockId == 0 && App.CurrentUser?.Role != "admin")
            {
                MessageBox.Show("系统锁(Lock0)仅管理员可远程开启", "权限不足",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 构造并发送控制命令
            var data = new Dictionary<string, object>
            {
                ["lock_id"] = lockId,
                ["action"] = "open",
                ["operator"] = App.CurrentUser?.UserId ?? "system"
            };
            var msg = Message.Create(Protocol.CmdControlLock, selected.DeviceId, data);
            App.TcpServer.SendToDevice(selected.DeviceId, msg);

            // 记录日志
            App.LogService.AddLog(new LogEntry
            {
                DeviceId = selected.DeviceId,
                UserId = App.CurrentUser?.UserId ?? "",
                LockId = lockId,
                Action = "remote_open",
                Result = "success",
                Reason = "",
                CreateTime = DateTime.Now
            });

            MessageBox.Show($"已向设备「{selected.DeviceName}」发送 Lock{lockId} 开锁指令", "成功",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
