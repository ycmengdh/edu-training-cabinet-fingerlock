using System.Windows;
using System.Windows.Controls;

namespace FingerprintLockManager
{
    /// <summary>
    /// 批量分配柜子权限：选在线柜 + 锁位 → 写权限覆盖 → 绑定 → 全量同步。
    /// 从用户列表页拆出，保证「调权限」是独立、可复现的场景入口。
    /// </summary>
    public partial class BatchAssignPermissionWindow : BorderlessWindow
    {
        private readonly IReadOnlyList<User> _users;
        private bool _busy;

        public int SuccessCount { get; private set; }
        public int FailCount { get; private set; }
        public BroadcastCommandResult? SyncResult { get; private set; }

        public BatchAssignPermissionWindow(IEnumerable<User> users)
        {
            InitializeComponent();
            _users = users?.Where(u => u != null).ToList() ?? new List<User>();
            SummaryText.Text = _users.Count == 0
                ? "未选择用户"
                : $"已选 {_users.Count} 个用户：{string.Join("、", _users.Take(5).Select(u => u.Name))}" +
                  (_users.Count > 5 ? "…" : "");
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Lock0Check.IsEnabled = App.CurrentUser?.Role == "admin";
            if (!Lock0Check.IsEnabled) Lock0Check.IsChecked = false;

            try
            {
                List<Device> cabinets = await Task.Run(() => App.DeviceService.GetAllDevices()
                    .Where(d => d.IsOnline && !DeviceService.IsTrueRoot(d) &&
                                !string.IsNullOrWhiteSpace(d.DeviceId))
                    .OrderBy(d => d.DeviceId)
                    .ToList());

                CabinetList.Items.Clear();
                foreach (var cabinet in cabinets)
                {
                    CabinetList.Items.Add(new ListBoxItem
                    {
                        Content = $"{cabinet.DeviceName}  ·  {cabinet.DeviceId}",
                        Tag = cabinet.DeviceId,
                        Padding = new Thickness(10, 8, 10, 8)
                    });
                }

                StatusText.Text = cabinets.Count == 0
                    ? "当前没有在线柜子，请确认 Mesh 链路后再试"
                    : $"共 {cabinets.Count} 台在线柜子，按住 Ctrl 可多选";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"读取柜子列表失败：{ex.Message}";
            }
        }

        private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _users.Count == 0) return;

            var targetDevices = CabinetList.SelectedItems.OfType<ListBoxItem>()
                .Select(item => item.Tag?.ToString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .ToList();
            if (targetDevices.Count == 0)
            {
                MessageBox.Show("请至少选择一个在线柜子", "提示");
                return;
            }

            bool[] perms =
            {
                Lock0Check.IsChecked == true,
                Lock1Check.IsChecked == true,
                Lock2Check.IsChecked == true,
                Lock3Check.IsChecked == true
            };

            SetBusy(true, $"正在为 {_users.Count} 个用户写入权限…");
            try
            {
                int success = 0, fail = 0;
                foreach (var user in _users)
                {
                    try
                    {
                        var dict = new Dictionary<int, bool>
                        {
                            { 0, perms[0] }, { 1, perms[1] }, { 2, perms[2] }, { 3, perms[3] }
                        };
                        bool ok = await Task.Run(() => App.PermissionService.SetUserPermissions(user.UserId, dict));
                        if (ok)
                        {
                            foreach (string deviceId in targetDevices)
                                App.CabinetBindingService.Assign(deviceId, user.UserId);
                            success++;
                        }
                        else fail++;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        fail++;
                    }
                }

                StatusText.Text = "权限已写入，正在同步到柜子…";
                BroadcastCommandResult syncResult = await Task.Run(App.CabinetSyncService.SyncAllPermissions);
                SuccessCount = success;
                FailCount = fail;
                SyncResult = syncResult;

                string msg =
                    $"批量分配完成：成功 {success}，失败 {fail}\n" +
                    CabinetSyncService.FormatSyncResult(syncResult,
                        "所有在线柜子均已确认权限更新。",
                        "在线柜子未全部确认权限更新，未确认设备仍使用原有缓存。");
                MessageBox.Show(msg, "完成", MessageBoxButton.OK,
                    fail == 0 && syncResult.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("批量分配异常：" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            ApplyButton.IsEnabled = !busy;
            CabinetList.IsEnabled = !busy;
            Lock0Check.IsEnabled = !busy && App.CurrentUser?.Role == "admin";
            Lock1Check.IsEnabled = !busy;
            Lock2Check.IsEnabled = !busy;
            Lock3Check.IsEnabled = !busy;
            if (!string.IsNullOrEmpty(status)) StatusText.Text = status;
        }
    }
}
