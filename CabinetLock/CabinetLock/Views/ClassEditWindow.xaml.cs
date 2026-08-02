using System.ComponentModel;
using System.Windows;
using CabinetLock.Controls;

namespace CabinetLock
{
    public partial class ClassEditWindow : BorderlessWindow
    {
        private readonly List<TeacherPickItem> _teachers;

        public ClassEditWindow(ClassInfo? existing, IEnumerable<User> teachers)
        {
            InitializeComponent();
            bool isCreate = existing == null;
            Title = isCreate ? "添加班级" : "编辑班级";
            ClassIdBox.Text = existing?.ClassId ?? "";
            ClassIdBox.IsEnabled = isCreate;
            NameBox.Text = existing?.Name ?? "";
            string? classId = existing?.ClassId;
            _teachers = teachers.OrderBy(item => item.Name).ThenBy(item => item.DisplayId)
                .Select(item => new TeacherPickItem(item,
                    !string.IsNullOrWhiteSpace(classId) && item.IsResponsibleForClass(classId)))
                .ToList();
            foreach (TeacherPickItem item in _teachers)
                item.PropertyChanged += (_, _) => UpdateSelectionText();
            TeacherPicker.SetItems(_teachers);
            UpdateSelectionText();
        }

        public string ClassId { get; private set; } = "";
        public string ClassName { get; private set; } = "";
        public IReadOnlyList<string> SelectedTeacherIds { get; private set; } = Array.Empty<string>();

        private void SelectEnabledButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (TeacherPickItem item in _teachers) item.IsSelected = item.Enabled;
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (TeacherPickItem item in _teachers) item.IsSelected = false;
        }

        private void UpdateSelectionText() =>
            SelectionText.Text = $"已选择 {_teachers.Count(item => item.IsSelected)} 名教师";

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ClassIdBox.Text) || string.IsNullOrWhiteSpace(NameBox.Text))
            {
                AppToast.Info("班级 ID 和名称不能为空");
                return;
            }
            ClassId = ClassIdBox.Text.Trim();
            ClassName = NameBox.Text.Trim();
            SelectedTeacherIds = _teachers.Where(item => item.IsSelected)
                .Select(item => item.UserId).ToList();
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private sealed class TeacherPickItem : ISearchableMultiSelectItem
        {
            private bool _isSelected;

            public TeacherPickItem(User user, bool selected)
            {
                UserId = user.UserId;
                UserCode = user.DisplayId;
                Name = user.Name;
                Enabled = user.Enabled;
                _isSelected = selected;
            }

            public string UserId { get; }
            public string UserCode { get; }
            public string Name { get; }
            public bool Enabled { get; }
            public string PrimaryText => Name;
            public string SecondaryText => UserCode;
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
