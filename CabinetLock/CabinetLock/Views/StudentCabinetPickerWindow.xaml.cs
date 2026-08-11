using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CabinetLock
{
    public partial class StudentCabinetPickerWindow : BorderlessWindow
    {
        private readonly List<StudentCabinetPickRow> _rows;
        private readonly ICollectionView _view;

        public StudentCabinetPickerWindow(
            IEnumerable<Device> devices,
            IReadOnlyCollection<string> assignedDeviceIds)
        {
            InitializeComponent();
            HashSet<string> assigned = assignedDeviceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _rows = devices
                .Select(device => new StudentCabinetPickRow(device, assigned.Contains(device.DeviceId)))
                .OrderBy(row => row.IsAssigned)
                .ThenByDescending(row => row.IsOnline)
                .ThenBy(row => row.DeviceNumber)
                .ThenBy(row => row.DeviceName)
                .ToList();
            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = MatchesFilter;
            CabinetGrid.ItemsSource = _view;
            OnlineFilterCombo.SelectedIndex = 0;
            RefreshFilter();
        }

        public string SelectedDeviceId { get; private set; } = "";

        private void FilterChanged(object sender, RoutedEventArgs e) => RefreshFilter();

        private void RefreshFilter()
        {
            if (_view == null) return;
            _view.Refresh();
            int visible = _rows.Count(MatchesFilter);
            ResultCountText.Text = $"显示 {visible} / {_rows.Count} 台柜子";
        }

        private bool MatchesFilter(object item)
        {
            if (item is not StudentCabinetPickRow row) return false;
            if (AvailableOnlyCheckBox?.IsChecked == true && row.IsAssigned) return false;

            string status = (OnlineFilterCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
            if (status == "online" && !row.IsOnline) return false;
            if (status == "offline" && row.IsOnline) return false;

            string keyword = SearchBox?.Text.Trim() ?? "";
            if (keyword.Length == 0) return true;
            return row.DeviceNumber.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                   row.DeviceName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                   row.DeviceId.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                   row.IpAddress.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                   row.FirmwareVersion.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e) => ConfirmSelection();

        private void ConfirmSelection()
        {
            if (CabinetGrid.SelectedItem is not StudentCabinetPickRow row)
            {
                AppToast.Info("请先选择一台柜子");
                return;
            }
            if (row.IsAssigned)
            {
                AppToast.Info("该柜子已授权，可在授权列表中直接修改");
                return;
            }
            SelectedDeviceId = row.DeviceId;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }

    public sealed class StudentCabinetPickRow
    {
        public StudentCabinetPickRow(Device device, bool isAssigned)
        {
            DeviceId = device.DeviceId;
            DeviceNumber = string.IsNullOrWhiteSpace(device.DeviceNumber) ? "未编号" : device.DeviceNumber;
            DeviceName = string.IsNullOrWhiteSpace(device.DeviceName) ? device.DeviceId : device.DeviceName;
            IpAddress = string.IsNullOrWhiteSpace(device.IpAddress) ? "-" : device.IpAddress;
            FirmwareVersion = device.FirmwareVersionText;
            IsOnline = device.IsOnline;
            IsAssigned = isAssigned;
        }

        public string DeviceId { get; }
        public string DeviceNumber { get; }
        public string DeviceName { get; }
        public string IpAddress { get; }
        public string FirmwareVersion { get; }
        public bool IsOnline { get; }
        public bool IsAssigned { get; }
        public string OnlineText => IsOnline ? "在线" : "离线";
        public string AssignmentText => IsAssigned ? "已授权" : "未授权";
    }
}
