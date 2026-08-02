using System.ComponentModel;
using System.Windows;

namespace CabinetLock
{
    public partial class StudentCabinetConfigWindow : BorderlessWindow
    {
        private readonly List<FingerprintOption> _fingerprints;

        public StudentCabinetConfigWindow(
            Device device,
            IEnumerable<FingerprintTemplate> templates,
            IEnumerable<int> selectedFingerprintIds,
            IReadOnlyList<bool> lockPermissions)
        {
            InitializeComponent();
            ArgumentNullException.ThrowIfNull(device);
            HashSet<int> selected = selectedFingerprintIds.ToHashSet();
            _fingerprints = templates.Where(item => item.Enabled && item.FingerprintId > 0)
                .GroupBy(item => item.FingerprintId)
                .Select(group => group.First())
                .OrderBy(item => item.FingerIndex)
                .ThenBy(item => item.FingerprintId)
                .Select(item => new FingerprintOption(item, selected.Contains(item.FingerprintId)))
                .ToList();

            CabinetNameText.Text = string.IsNullOrWhiteSpace(device.DeviceName)
                ? device.DeviceId : device.DeviceName;
            CabinetMetaText.Text = $"设备编号：{(string.IsNullOrWhiteSpace(device.DeviceNumber) ? "未编号" : device.DeviceNumber)}  ·  {(device.IsOnline ? "在线" : "离线")}";
            FingerprintCountText.Text = $"共 {_fingerprints.Count} 枚";
            FingerprintList.ItemsSource = _fingerprints;
            Lock1CheckBox.IsChecked = lockPermissions.ElementAtOrDefault(1);
            Lock2CheckBox.IsChecked = lockPermissions.ElementAtOrDefault(2);
            Lock3CheckBox.IsChecked = lockPermissions.ElementAtOrDefault(3);
        }

        public IReadOnlyList<int> SelectedFingerprintIds { get; private set; } = Array.Empty<int>();
        public IReadOnlyList<int> SelectedLockIds { get; private set; } = Array.Empty<int>();

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            int[] fingerprints = _fingerprints.Where(item => item.IsSelected)
                .Select(item => item.FingerprintId).ToArray();
            if (fingerprints.Length == 0)
            {
                AppToast.Info("请至少选择一枚指纹");
                return;
            }
            int[] locks = new[]
            {
                Lock1CheckBox.IsChecked == true ? 1 : -1,
                Lock2CheckBox.IsChecked == true ? 2 : -1,
                Lock3CheckBox.IsChecked == true ? 3 : -1
            }.Where(id => id >= 0).ToArray();
            if (locks.Length == 0)
            {
                AppToast.Info("请至少选择一个柜门权限");
                return;
            }
            SelectedFingerprintIds = fingerprints;
            SelectedLockIds = locks;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private sealed class FingerprintOption : INotifyPropertyChanged
        {
            private bool _isSelected;

            public FingerprintOption(FingerprintTemplate template, bool selected)
            {
                FingerprintId = template.FingerprintId;
                FingerName = template.FingerDisplayName;
                BackupStatusText = template.BackupStatusText;
                _isSelected = selected;
            }

            public int FingerprintId { get; }
            public string FingerName { get; }
            public string BackupStatusText { get; }
            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }
    }
}
