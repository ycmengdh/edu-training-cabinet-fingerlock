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
            Loaded += async (s, e) =>
            {
                if (LockSelectBox.Items.Count > 0)
                {
                    LockSelectBox.SelectedIndex = 1; // 默认 Lock1
                }
                await LoadDevicesAsync();
            };
        }

        /// <summary>加载设备列表</summary>
        private async Task LoadDevicesAsync()
        {
            RefreshButton.IsEnabled = false;
            PageStatusText.Text = "正在读取根节点数据";
            try
            {
                var devices = await Task.Run(App.DeviceService.GetAllDevices);
                DeviceDataGrid.ItemsSource = devices.Where(d => !d.IsRoot).ToList();
                PageStatusText.Text = $"共 {DeviceDataGrid.Items.Count} 个柜子节点";
            }
            catch (RootDataUnavailableException ex)
            {
                DeviceDataGrid.ItemsSource = null;
                PageStatusText.Text = ex.Message;
            }
            finally
            {
                RefreshButton.IsEnabled = true;
            }
        }

        /// <summary>刷新按钮</summary>
        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadDevicesAsync();
        }

        /// <summary>远程开锁：向选中设备发送 CONTROL_LOCK 命令</summary>
        private async void RemoteUnlockButton_Click(object sender, RoutedEventArgs e)
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

            // 检查设备是否在线（通过 Mesh 桥接器的在线设备列表判断）
            bool meshOnline = false;
            foreach (var dc in App.MeshBridge.GetOnlineDevices())
            {
                if (dc.DeviceId == selected.DeviceId && dc.IsOnline)
                {
                    meshOnline = true;
                    break;
                }
            }

            if (!meshOnline)
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

            // 构造并发送控制命令（经 Mesh 桥接器转发到目标设备）
            var data = new Dictionary<string, object>
            {
                ["lock_id"] = lockId,
                ["action"] = "open",
                ["operator"] = App.CurrentUser?.UserId ?? "system"
            };
            var msg = Message.Create(Protocol.CmdControlLock, selected.DeviceId, data);
            RemoteUnlockButton.IsEnabled = false;
            var result = await App.CommandService.SendAsync(selected.DeviceId, msg);
            RemoteUnlockButton.IsEnabled = true;
            if (!result.Success)
            {
                MessageBox.Show(result.ErrorMessage, "开锁失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 开锁日志由柜子记录并上报根节点，上位机不重复写日志表。
            MessageBox.Show($"设备「{selected.DeviceName}」已确认 Lock {lockId} 开锁", "开锁完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
