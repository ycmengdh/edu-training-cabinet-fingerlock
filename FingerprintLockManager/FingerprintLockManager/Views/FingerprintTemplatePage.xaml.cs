using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FingerprintLockManager
{
    /// <summary>
    /// 指纹模板库页面
    /// 展示本地缓存的指纹模板列表，支持上传到 SD、批量下发到柜子、关联用户、删除等操作。
    /// 设计原则：录入只是采集，模板存到 PC/SD 与用户关联；下发是后续的整理分配动作。
    /// </summary>
    public partial class FingerprintTemplatePage : Page
    {
        private List<FingerprintTemplate> _allTemplates = new();
        private readonly ListPager _pager = new(50);

        public FingerprintTemplatePage()
        {
            InitializeComponent();
            Loaded += async (s, e) => await LoadTemplatesAsync();
        }

        /// <summary>加载模板列表</summary>
        private async Task LoadTemplatesAsync(bool resetPage = true)
        {
            if (resetPage) _pager.Reset();
            SetBusy(true, "正在读取指纹模板列表");
            try
            {
                _allTemplates = await Task.Run(App.FingerprintTemplateService.GetAllTemplates);
                ApplyTemplatePage();
                PageStatusText.Text = App.SdStorageService.IsAvailable
                    ? "SD 卡可用，模板可上传到 SD 卡集中备份"
                    : "SD 不可用，模板仅保存在本地缓存";
            }
            catch (Exception ex)
            {
                PageStatusText.Text = $"加载失败：{ex.Message}";
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void ApplyTemplatePage()
        {
            var page = _pager.Slice(_allTemplates);
            TemplateDataGrid.ItemsSource = page;
            _pager.BindChrome(PrevPageButton, NextPageButton, PageInfoText);
            int unassigned = _allTemplates.Count(t => string.IsNullOrWhiteSpace(t.UserId));
            SummaryText.Text =
                $"{_pager.StatusText(page.Count)}，其中未关联用户 {unassigned} 个";
        }

        /// <summary>刷新按钮</summary>
        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadTemplatesAsync(resetPage: false);
        }

        private void PrevPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pager.Prev()) ApplyTemplatePage();
        }

        private void NextPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pager.Next()) ApplyTemplatePage();
        }

        private void TestFingerprintButton_Click(object sender, RoutedEventArgs e)
        {
            FingerprintTemplate? selected = TemplateDataGrid.SelectedItem as FingerprintTemplate;
            OpenTestWindow(selected);
        }

        private void TestOneButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not int fingerprintId) return;
            OpenTestWindow(_allTemplates.FirstOrDefault(item => item.FingerprintId == fingerprintId));
        }

        private void OpenTestWindow(FingerprintTemplate? template)
        {
            var window = new FingerprintTestWindow(
                template?.UserId, template?.FingerprintId, null)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
        }

        /// <summary>上传全部到 SD</summary>
        private async void UploadAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (!App.SdStorageService.IsAvailable)
            {
                MessageBox.Show("SD 不可用，模板仍在本地。\n请等根节点 SD 卡恢复后再上传。",
                    "SD 不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_allTemplates.Count == 0)
            {
                MessageBox.Show("当前没有可上传的本地模板", "提示");
                return;
            }

            SetBusy(true, "正在上传模板到 SD 卡");
            try
            {
                int success = await Task.Run(App.FingerprintTemplateService.UploadAllToSdAsync);
                MessageBox.Show($"已上传 {success} / {_allTemplates.Count} 个模板到 SD 卡",
                    success == _allTemplates.Count ? "上传完成" : "上传部分完成",
                    MessageBoxButton.OK,
                    success == _allTemplates.Count ? MessageBoxImage.Information : MessageBoxImage.Warning);
                await LoadTemplatesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"上传失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>批量下发选中模板到柜子</summary>
        private async void DistributeButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = TemplateDataGrid.SelectedItems.OfType<FingerprintTemplate>().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("请先选中要下发的模板", "提示");
                return;
            }

            var cabinets = GetOnlineCabinets();
            if (cabinets.Count == 0)
            {
                MessageBox.Show("当前没有在线柜子，无法下发", "无法下发",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var targets = ShowSelectCabinetsDialog(cabinets);
            if (targets == null || targets.Count == 0)
            {
                MessageBox.Show("未选择目标柜子，已取消下发", "提示");
                return;
            }

            SetBusy(true, $"正在下发 {selected.Count} 个模板到 {targets.Count} 个柜子");
            try
            {
                int totalSuccess = 0;
                int totalFail = 0;
                var failDetails = new List<string>();
                foreach (var template in selected)
                {
                    var result = await App.FingerprintTemplateService.DistributeToDevicesDetailedAsync(
                        template.FingerprintId, targets);
                    foreach (var pair in result)
                    {
                        if (pair.Value.ok) totalSuccess++;
                        else
                        {
                            totalFail++;
                            if (failDetails.Count < 5)
                            {
                                failDetails.Add(
                                    $"指纹{template.FingerprintId} → {pair.Key}: {pair.Value.error}");
                            }
                        }
                    }
                }

                string detail = failDetails.Count == 0
                    ? ""
                    : "\n\n失败原因：\n" + string.Join("\n", failDetails);
                MessageBox.Show(
                    $"下发完成。\n成功：{totalSuccess}\n失败：{totalFail}{detail}",
                    totalFail == 0 ? "下发完成" : "下发部分完成",
                    MessageBoxButton.OK,
                    totalFail == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                await LoadTemplatesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"下发失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>删除选中模板</summary>
        private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = TemplateDataGrid.SelectedItems.OfType<FingerprintTemplate>().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("请先选中要删除的模板", "提示");
                return;
            }

            var confirm = MessageBox.Show(
                $"确认删除选中的 {selected.Count} 个本地模板？\n（SD 卡上的备份不会被删除）",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            SetBusy(true, "正在删除模板");
            try
            {
                int deleted = 0;
                foreach (var t in selected)
                {
                    if (await Task.Run(() => App.FingerprintTemplateService.DeleteTemplate(t.FingerprintId)))
                        deleted++;
                }
                MessageBox.Show($"已删除 {deleted} 个模板", "删除完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadTemplatesAsync();
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>单条：关联用户</summary>
        private async void BindUserButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.Tag is not int fingerprintId) return;

            List<UserBrief> users;
            try
            {
                users = await Task.Run(App.UserService.GetAllUsersBrief);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载用户列表失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (users.Count == 0)
            {
                MessageBox.Show("没有可用用户", "提示");
                return;
            }

            UserBrief? selected = ShowSelectUserDialog(users);
            if (selected == null) return;

            SetBusy(true, "正在关联用户");
            try
            {
                bool ok = await Task.Run(() =>
                    App.FingerprintTemplateService.BindToUser(fingerprintId, selected.UserId));
                MessageBox.Show(ok ? "已关联用户" : "关联失败", ok ? "完成" : "错误",
                    MessageBoxButton.OK,
                    ok ? MessageBoxImage.Information : MessageBoxImage.Error);
                await LoadTemplatesAsync();
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>单条：下发</summary>
        private async void DistributeOneButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.Tag is not int fingerprintId) return;

            var cabinets = GetOnlineCabinets();
            if (cabinets.Count == 0)
            {
                MessageBox.Show("当前没有在线柜子，无法下发", "无法下发",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var targets = ShowSelectCabinetsDialog(cabinets);
            if (targets == null || targets.Count == 0)
            {
                MessageBox.Show("未选择目标柜子，已取消下发", "提示");
                return;
            }

            SetBusy(true, $"正在下发指纹 {fingerprintId} 到 {targets.Count} 个柜子");
            try
            {
                var result = await App.FingerprintTemplateService.DistributeToDevicesDetailedAsync(
                    fingerprintId, targets);
                int success = result.Count(p => p.Value.ok);
                int fail = result.Count(p => !p.Value.ok);
                var failDetails = result.Where(p => !p.Value.ok)
                    .Take(5)
                    .Select(p => $"{p.Key}: {p.Value.error}")
                    .ToList();
                string detail = failDetails.Count == 0
                    ? ""
                    : "\n\n失败原因：\n" + string.Join("\n", failDetails);
                MessageBox.Show(
                    $"下发完成。\n成功：{success}\n失败：{fail}{detail}",
                    fail == 0 ? "下发完成" : "下发部分完成",
                    MessageBoxButton.OK,
                    fail == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                await LoadTemplatesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"下发失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>单条：删除</summary>
        private async void DeleteOneButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.Tag is not int fingerprintId) return;

            var confirm = MessageBox.Show(
                $"确认删除指纹 ID {fingerprintId} 的本地模板？\n（SD 卡上的备份不会被删除）",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            SetBusy(true, "正在删除模板");
            try
            {
                bool ok = await Task.Run(() =>
                    App.FingerprintTemplateService.DeleteTemplate(fingerprintId));
                MessageBox.Show(ok ? "已删除" : "删除失败", ok ? "完成" : "错误",
                    MessageBoxButton.OK,
                    ok ? MessageBoxImage.Information : MessageBoxImage.Error);
                await LoadTemplatesAsync();
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ===== 辅助方法 =====

        /// <summary>获取在线柜子列表（不包含根节点）</summary>
        private static List<Device> GetOnlineCabinets()
        {
            return App.MeshBridge.GetOnlineDevices()
                .Where(d => !d.IsRoot && d.IsOnline)
                .Select(d => new Device
                {
                    DeviceId = d.DeviceId,
                    DeviceName = string.IsNullOrWhiteSpace(d.DeviceName) ? d.DeviceId : d.DeviceName,
                    IsOnline = true
                })
                .OrderBy(d => d.DeviceId)
                .ToList();
        }

        /// <summary>显示多选柜子弹窗，返回选中的设备 ID 列表；取消返回 null</summary>
        private List<string>? ShowSelectCabinetsDialog(List<Device> cabinets)
        {
            var dlg = new Window
            {
                Title = "选择目标柜子（可多选）",
                Width = 360,
                Height = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = FindResource("BackgroundBrush") as Brush
            };

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock
            {
                Text = $"共 {cabinets.Count} 个在线柜子，勾选要下发的目标：",
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap
            });

            var listBox = new ListBox { SelectionMode = SelectionMode.Multiple, Height = 220 };
            foreach (var c in cabinets)
            {
                var item = new ListBoxItem
                {
                    Content = $"{c.DeviceName} ({c.DeviceId})",
                    Tag = c.DeviceId
                };
                listBox.Items.Add(item);
            }
            panel.Children.Add(listBox);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var okBtn = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0) };
            var cancelBtn = new Button { Content = "取消", Width = 70,
                Style = FindResource("SecondaryButton") as Style };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            panel.Children.Add(btnPanel);

            dlg.Content = panel;

            List<string>? selected = null;
            okBtn.Click += (s, e) =>
            {
                selected = listBox.SelectedItems.OfType<ListBoxItem>()
                    .Select(i => i.Tag as string)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList()!;
                dlg.Close();
            };
            cancelBtn.Click += (s, e) => dlg.Close();
            dlg.ShowDialog();
            return selected;
        }

        /// <summary>显示选择用户弹窗，返回选中的用户；取消返回 null</summary>
        private UserBrief? ShowSelectUserDialog(List<UserBrief> users)
        {
            var dlg = new Window
            {
                Title = "选择关联用户",
                Width = 360,
                Height = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = FindResource("BackgroundBrush") as Brush
            };

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock
            {
                Text = "选择要关联的用户（指纹 ID 已被占用的用户会标注）",
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap,
                Foreground = FindResource("SubTextBrush") as Brush
            });

            var listBox = new ListBox { Height = 260 };
            foreach (var u in users)
            {
                var item = new ListBoxItem
                {
                    Content = u.ToString(),
                    Tag = u
                };
                listBox.Items.Add(item);
            }
            if (listBox.Items.Count > 0) listBox.SelectedIndex = 0;
            panel.Children.Add(listBox);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var okBtn = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0) };
            var cancelBtn = new Button { Content = "取消", Width = 70,
                Style = FindResource("SecondaryButton") as Style };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            panel.Children.Add(btnPanel);

            dlg.Content = panel;

            UserBrief? selected = null;
            okBtn.Click += (s, e) =>
            {
                selected = (listBox.SelectedItem as ListBoxItem)?.Tag as UserBrief;
                dlg.Close();
            };
            cancelBtn.Click += (s, e) => dlg.Close();
            dlg.ShowDialog();
            return selected;
        }

        private void SetBusy(bool busy, string? status = null)
        {
            RefreshButton.IsEnabled = !busy;
            UploadAllButton.IsEnabled = !busy;
            DistributeButton.IsEnabled = !busy;
            DeleteSelectedButton.IsEnabled = !busy;
            if (!string.IsNullOrEmpty(status)) PageStatusText.Text = status;
        }
    }
}
