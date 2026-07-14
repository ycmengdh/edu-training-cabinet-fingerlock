using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FingerprintLockManager
{
    /// <summary>
    /// 班级管理页面（需求 4）
    ///
    /// 管理员和老师均可创建班级，创建时必须指定负责老师（TeacherUserId）。
    /// 老师只能管理自己负责的班级数据。
    /// 选中班级后底部展开该班级的学生列表。
    /// </summary>
    public partial class ClassManagePage : Page
    {
        /// <summary>当前选中的班级</summary>
        private ClassInfo? _selectedClass;

        public ClassManagePage()
        {
            InitializeComponent();
            Loaded += (s, e) => LoadClasses();
        }

        /// <summary>加载班级列表（按角色过滤）</summary>
        private void LoadClasses()
        {
            List<ClassInfo> classes;
            var currentUser = App.CurrentUser;
            if (currentUser == null)
            {
                classes = new List<ClassInfo>();
            }
            else if (currentUser.Role == "teacher")
            {
                // 老师只能看自己负责的班级
                classes = App.ClassService.GetClassesByTeacher(currentUser.UserId);
            }
            else
            {
                // 管理员看所有班级
                classes = App.ClassService.GetClasses();
            }
            ClassDataGrid.ItemsSource = classes;
        }

        /// <summary>刷新按钮</summary>
        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadClasses();
        }

        /// <summary>新建班级</summary>
        private void AddClassButton_Click(object sender, RoutedEventArgs e)
        {
            // 弹出对话框输入班级名称、负责老师、备注
            if (!ShowAddClassDialog(out string className, out string teacherUserId, out string description))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(className))
            {
                MessageBox.Show("请输入班级名称", "提示");
                return;
            }

            if (string.IsNullOrEmpty(teacherUserId))
            {
                MessageBox.Show("必须选择负责老师", "提示");
                return;
            }

            // 操作前自动备份（需求 11）
            App.BackupService.BackupBeforeAction($"新建班级 {className}", App.CurrentUser?.UserId);

            var cls = new ClassInfo
            {
                ClassId = "",
                ClassName = className.Trim(),
                TeacherUserId = teacherUserId,
                Description = description?.Trim() ?? ""
            };

            string? err = App.ClassService.AddClass(cls);
            if (err == null)
            {
                MessageBox.Show($"班级创建成功！\n班级ID：{cls.ClassId}", "成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                LoadClasses();
            }
            else
            {
                MessageBox.Show($"创建失败：{err}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>查看班级学生</summary>
        private void ViewStudentsButton_Click(object sender, RoutedEventArgs e)
        {
            if (ClassDataGrid.SelectedItem is not ClassInfo cls)
            {
                MessageBox.Show("请先选择要查看的班级", "提示");
                return;
            }

            // 老师只能查看自己班级
            if (!CanManageClass(cls))
            {
                MessageBox.Show("您只能管理自己负责的班级", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _selectedClass = cls;
            SelectedClassText.Text = $"- {cls.ClassName}（{cls.ClassId}）";
            StudentDataGrid.ItemsSource = App.ClassService.GetStudentsByClass(cls.ClassId);
            StudentSection.Visibility = Visibility.Visible;
        }

        /// <summary>删除班级</summary>
        private void DeleteClassButton_Click(object sender, RoutedEventArgs e)
        {
            if (ClassDataGrid.SelectedItem is not ClassInfo cls)
            {
                MessageBox.Show("请先选择要删除的班级", "提示");
                return;
            }

            if (!CanManageClass(cls))
            {
                MessageBox.Show("您只能管理自己负责的班级", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int studentCount = App.ClassService.GetStudentsByClass(cls.ClassId).Count;
            string warn = studentCount > 0
                ? $"\n\n该班级下还有 {studentCount} 名学生，删除班级前请先处理学生（转班或删除）。"
                : "";

            var result = MessageBox.Show($"确认删除班级「{cls.ClassName}（{cls.ClassId}」？{warn}",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            // 操作前自动备份（需求 11）
            App.BackupService.BackupBeforeAction($"删除班级 {cls.ClassId}", App.CurrentUser?.UserId);

            string? err = App.ClassService.DeleteClass(cls.ClassId);
            if (err == null)
            {
                MessageBox.Show("删除成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                StudentSection.Visibility = Visibility.Collapsed;
                LoadClasses();
            }
            else
            {
                MessageBox.Show($"删除失败：{err}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>判断当前用户是否有权管理该班级</summary>
        private bool CanManageClass(ClassInfo cls)
        {
            var user = App.CurrentUser;
            if (user == null) return false;
            if (user.Role == "admin") return true;
            if (user.Role == "teacher") return cls.TeacherUserId == user.UserId;
            return false;
        }

        // ===== 代码构建的对话框 =====

        /// <summary>显示新建班级对话框</summary>
        private bool ShowAddClassDialog(out string className, out string teacherUserId, out string description)
        {
            className = "";
            teacherUserId = "";
            description = "";

            var dlg = new Window
            {
                Title = "新建班级",
                Width = 360,
                Height = 320,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = FindResource("BackgroundBrush") as Brush
            };

            var panel = new StackPanel { Margin = new Thickness(20) };

            panel.Children.Add(new TextBlock { Text = "班级名称", Margin = new Thickness(0, 0, 0, 6) });
            var nameBox = new TextBox { Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(nameBox);

            panel.Children.Add(new TextBlock { Text = "负责老师（必选）", Margin = new Thickness(0, 0, 0, 6) });
            var teacherCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 16) };
            // 加载所有老师
            var teachers = App.UserService.GetUsersByRole("teacher");
            foreach (var t in teachers)
            {
                teacherCombo.Items.Add(new ComboBoxItem { Content = $"{t.Name}（{t.UserId}）", Tag = t.UserId });
            }
            // 老师新建班级时默认选自己
            if (App.CurrentUser?.Role == "teacher")
            {
                for (int i = 0; i < teacherCombo.Items.Count; i++)
                {
                    if (teacherCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == App.CurrentUser.UserId)
                    {
                        teacherCombo.SelectedIndex = i;
                        break;
                    }
                }
                // 老师新建班级时不能改负责老师
                teacherCombo.IsEnabled = false;
            }
            panel.Children.Add(teacherCombo);

            panel.Children.Add(new TextBlock { Text = "备注", Margin = new Thickness(0, 0, 0, 6) });
            var descBox = new TextBox { Margin = new Thickness(0, 0, 0, 16), Height = 60, AcceptsReturn = true };
            panel.Children.Add(descBox);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var okBtn = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0) };
            var cancelBtn = new Button { Content = "取消", Width = 70, Style = FindResource("SecondaryButton") as Style };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            panel.Children.Add(btnPanel);

            dlg.Content = panel;

            bool confirmed = false;
            string localName = "";
            string localTeacher = "";
            string localDesc = "";
            okBtn.Click += (s, e) =>
            {
                localName = nameBox.Text;
                if (teacherCombo.SelectedItem is ComboBoxItem item)
                {
                    localTeacher = item.Tag?.ToString() ?? "";
                }
                localDesc = descBox.Text;
                confirmed = true;
                dlg.Close();
            };
            cancelBtn.Click += (s, e) => dlg.Close();

            dlg.ShowDialog();
            if (confirmed)
            {
                className = localName;
                teacherUserId = localTeacher;
                description = localDesc;
            }
            return confirmed;
        }
    }
}
