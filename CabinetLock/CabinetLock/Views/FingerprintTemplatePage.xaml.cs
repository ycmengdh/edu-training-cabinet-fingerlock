using System.Windows;
using System.Windows.Controls;

namespace CabinetLock
{
    /// <summary>
    /// 指纹库页面
    /// 展示本地缓存的指纹模板列表，支持上传到 SD、测试、删除等操作。
    /// </summary>
    public partial class FingerprintTemplatePage : Page
    {
        private List<FingerprintTemplate> _allTemplates = new();
        private readonly ListPager _pager = new(50);
        private bool _busy;

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
            _pager.BindChrome(Pager, "个模板");
            int unassigned = _allTemplates.Count(t => string.IsNullOrWhiteSpace(t.UserId));
            SummaryText.Text =
                $"{_pager.StatusText(page.Count)}，其中未关联用户 {unassigned} 个";
            UpdateSelectionState();
        }

        private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (TemplateDataGrid.ItemsSource is not IEnumerable<FingerprintTemplate> page) return;
            bool select = SelectAllCheckBox.IsChecked == true;
            foreach (FingerprintTemplate template in page) template.IsSelected = select;
            TemplateDataGrid.Items.Refresh();
            UpdateSelectionState();
        }

        private void TemplateCheckBox_Click(object sender, RoutedEventArgs e) => UpdateSelectionState();

        private void UpdateSelectionState()
        {
            List<FingerprintTemplate> page = TemplateDataGrid.ItemsSource?
                .OfType<FingerprintTemplate>().ToList() ?? new List<FingerprintTemplate>();
            int pageSelected = page.Count(template => template.IsSelected);
            SelectAllCheckBox.IsChecked = page.Count == 0 || pageSelected == 0
                ? false
                : pageSelected == page.Count ? true : null;
            int totalSelected = _allTemplates.Count(template => template.IsSelected);
            DeleteSelectedButton.IsEnabled = !_busy && totalSelected > 0;
            DeleteSelectedButton.ToolTip = totalSelected == 0 ? "请先勾选指纹" : $"删除已勾选的 {totalSelected} 枚指纹";
        }

        /// <summary>刷新按钮</summary>
        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadTemplatesAsync(resetPage: false);
        }

        private void Pager_PageRequested(object sender, Controls.PaginationRequestedEventArgs e)
        {
            _pager.ApplyRequest(e);
            ApplyTemplatePage();
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

        /// <summary>删除选中模板</summary>
        private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = _allTemplates.Where(template => template.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("请先勾选要删除的模板", "提示");
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

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            RefreshButton.IsEnabled = !busy;
            UploadAllButton.IsEnabled = !busy;
            UpdateSelectionState();
            if (!string.IsNullOrEmpty(status)) PageStatusText.Text = status;
        }
    }
}
