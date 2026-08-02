using System.ComponentModel;
using System.Windows;
using CabinetLock.Controls;

namespace CabinetLock
{
    public partial class TeacherEditWindow : BorderlessWindow
    {
        private readonly List<ClassPickItem> _classes;

        public TeacherEditWindow(User? existing, IEnumerable<ClassInfo> classes)
        {
            InitializeComponent();
            bool isCreate = existing == null;
            Title = isCreate ? "添加教师" : "编辑教师";
            UserIdBox.Text = existing?.DisplayId ?? "";
            NameBox.Text = existing?.Name ?? "";
            PasswordPanel.Visibility = isCreate ? Visibility.Visible : Visibility.Collapsed;
            PasswordRequirementText.Text = PasswordHelper.PasswordRequirement;
            HashSet<string> selected = existing?.GetResponsibleClassIds()
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();
            _classes = classes.OrderBy(item => item.Name).ThenBy(item => item.ClassId)
                .Select(item => new ClassPickItem(item, selected.Contains(item.ClassId)))
                .ToList();
            foreach (ClassPickItem item in _classes)
                item.PropertyChanged += (_, _) => UpdateSelectionText();
            ClassPicker.SetItems(_classes);
            UpdateSelectionText();
        }

        public string TeacherCode { get; private set; } = "";
        public string TeacherName { get; private set; } = "";
        public string Password { get; private set; } = "";
        public IReadOnlyList<string> SelectedClassIds { get; private set; } = Array.Empty<string>();

        private void SelectEnabledButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (ClassPickItem item in _classes) item.IsSelected = item.Enabled;
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (ClassPickItem item in _classes) item.IsSelected = false;
        }

        private void UpdateSelectionText() =>
            SelectionText.Text = $"已选择 {_classes.Count(item => item.IsSelected)} 个班级";

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(UserIdBox.Text) || string.IsNullOrWhiteSpace(NameBox.Text))
            {
                AppToast.Info("教师 ID 和姓名不能为空");
                return;
            }
            TeacherCode = UserIdBox.Text.Trim();
            TeacherName = NameBox.Text.Trim();
            Password = PasswordBox.Password;
            SelectedClassIds = _classes.Where(item => item.IsSelected)
                .Select(item => item.ClassId).ToList();
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private sealed class ClassPickItem : ISearchableMultiSelectItem
        {
            private bool _isSelected;

            public ClassPickItem(ClassInfo info, bool selected)
            {
                ClassId = info.ClassId;
                Name = info.Name;
                Enabled = info.Enabled;
                _isSelected = selected;
            }

            public string ClassId { get; }
            public string Name { get; }
            public bool Enabled { get; }
            public string PrimaryText => Name;
            public string SecondaryText => ClassId;
            public string StatusText => Enabled ? "启用" : "停用";
            public bool IsAvailable => Enabled;
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
