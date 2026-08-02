using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CabinetLock
{
    public partial class CabinetStudentPickerWindow : BorderlessWindow
    {
        private readonly List<StudentPickItem> _students = new();

        public CabinetStudentPickerWindow(ClassCabinetOverviewRow cabinet,
            IEnumerable<ClassStudentRow> students)
        {
            InitializeComponent();
            CabinetText.Text = $"{cabinet.DeviceNumber} · {cabinet.DeviceName} · {cabinet.AvailabilityText}";
            _students.AddRange(students.Select(row => new StudentPickItem(row, cabinet.DeviceId))
                .OrderBy(item => item.IsAlreadyAssigned)
                .ThenBy(item => item.StudentNo)
                .ToList());
            foreach (StudentPickItem item in _students)
                item.PropertyChanged += (_, _) => UpdateSelectionText();
            ApplyFilter();
            UpdateSelectionText();
        }

        public IReadOnlyList<User> SelectedUsers => _students.Where(item => item.IsSelected)
            .Select(item => item.User).ToList();

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void ApplyFilter()
        {
            string keyword = SearchBox?.Text?.Trim() ?? "";
            StudentList.ItemsSource = string.IsNullOrWhiteSpace(keyword)
                ? _students
                : _students.Where(item => item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    item.StudentNo.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private void SelectUnassignedButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (StudentPickItem item in _students)
                item.IsSelected = !item.IsAlreadyAssigned;
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (StudentPickItem item in _students) item.IsSelected = false;
        }

        private void UpdateSelectionText()
        {
            int selected = _students.Count(item => item.IsSelected);
            int repeated = _students.Count(item => item.IsSelected && item.IsAlreadyAssigned);
            SelectionText.Text = repeated == 0
                ? $"已选 {selected} 名学生"
                : $"已选 {selected} 名，其中 {repeated} 名已在此柜";
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedUsers.Count == 0)
            {
                AppToast.Info("请至少选择一名学生");
                return;
            }
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private sealed class StudentPickItem : INotifyPropertyChanged
        {
            private bool _isSelected;

            public StudentPickItem(ClassStudentRow row, string deviceId)
            {
                User = row.User;
                IsAlreadyAssigned = row.BoundCabinetIds.Contains(deviceId,
                    StringComparer.OrdinalIgnoreCase);
            }

            public User User { get; }
            public string Name => User.Name;
            public string StudentNo => User.DisplayId;
            public bool IsAlreadyAssigned { get; }
            public string AssignmentText => IsAlreadyAssigned ? "已在此柜 · 重复分配" : "可分配";
            public Brush AssignmentBrush => IsAlreadyAssigned
                ? (Brush)Application.Current.FindResource("WarningBrush")
                : (Brush)Application.Current.FindResource("SuccessBrush");
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
