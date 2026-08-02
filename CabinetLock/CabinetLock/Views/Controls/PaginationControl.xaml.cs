using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CabinetLock.Controls
{
    public partial class PaginationControl : UserControl
    {
        private static readonly int[] StandardPageSizes = [20, 50, 100];
        private bool _updating;

        public PaginationControl()
        {
            InitializeComponent();
        }

        public event EventHandler<PaginationRequestedEventArgs>? PageRequested;

        public int PageIndex { get; private set; }
        public int PageSize { get; private set; } = 50;
        public int TotalCount { get; private set; }
        public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));

        public void Configure(int totalCount, int pageIndex, int pageSize, string unit = "条")
        {
            _updating = true;
            TotalCount = Math.Max(0, totalCount);
            PageSize = Math.Max(1, pageSize);
            PageIndex = Math.Clamp(pageIndex, 0, TotalPages - 1);

            var sizes = StandardPageSizes.Append(PageSize).Distinct().OrderBy(value => value).ToList();
            PageSizeBox.ItemsSource = sizes;
            PageSizeBox.SelectedItem = PageSize;
            TotalText.Text = $"共 {TotalCount} {unit}";
            PageInfoText.Text = $"第 {PageIndex + 1} / {TotalPages} 页";

            bool hasPrevious = PageIndex > 0;
            bool hasNext = PageIndex + 1 < TotalPages;
            FirstButton.IsEnabled = hasPrevious;
            PreviousButton.IsEnabled = hasPrevious;
            NextButton.IsEnabled = hasNext;
            LastButton.IsEnabled = hasNext;
            BuildPageNumbers();
            _updating = false;
        }

        private void BuildPageNumbers()
        {
            PageNumberPanel.Children.Clear();
            int firstPage = Math.Max(0, Math.Min(PageIndex - 2, TotalPages - 5));
            int lastPage = Math.Min(TotalPages - 1, firstPage + 4);
            for (int index = firstPage; index <= lastPage; index++)
            {
                int requestedIndex = index;
                var button = new Button
                {
                    Content = (index + 1).ToString(),
                    Width = 34,
                    MinWidth = 34,
                    Height = 28,
                    MinHeight = 28,
                    Padding = new Thickness(0),
                    Margin = new Thickness(index == firstPage ? 0 : 4, 0, 0, 0),
                    Style = FindResource(index == PageIndex ? typeof(Button) : "CompactSecondaryButton") as Style,
                    IsHitTestVisible = index != PageIndex,
                    Focusable = index != PageIndex
                };
                button.Click += (_, _) => RequestPage(requestedIndex, PageSize);
                PageNumberPanel.Children.Add(button);
            }
        }

        private void RequestPage(int pageIndex, int pageSize)
        {
            int normalizedSize = Math.Max(1, pageSize);
            int totalPages = Math.Max(1, (int)Math.Ceiling(TotalCount / (double)normalizedSize));
            int normalizedIndex = Math.Clamp(pageIndex, 0, totalPages - 1);
            PageRequested?.Invoke(this, new PaginationRequestedEventArgs(normalizedIndex, normalizedSize));
        }

        private void PageSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updating || PageSizeBox.SelectedItem is not int pageSize || pageSize == PageSize) return;
            RequestPage(0, pageSize);
        }

        private void FirstButton_Click(object sender, RoutedEventArgs e) => RequestPage(0, PageSize);
        private void PreviousButton_Click(object sender, RoutedEventArgs e) => RequestPage(PageIndex - 1, PageSize);
        private void NextButton_Click(object sender, RoutedEventArgs e) => RequestPage(PageIndex + 1, PageSize);
        private void LastButton_Click(object sender, RoutedEventArgs e) => RequestPage(TotalPages - 1, PageSize);

        private void JumpButton_Click(object sender, RoutedEventArgs e) => JumpToPage();

        private void JumpPageBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            JumpToPage();
            e.Handled = true;
        }

        private void JumpToPage()
        {
            if (!int.TryParse(JumpPageBox.Text.Trim(), out int pageNumber) || pageNumber < 1 || pageNumber > TotalPages)
            {
                AppToast.Info($"请输入 1 到 {TotalPages} 之间的页码");
                JumpPageBox.SelectAll();
                return;
            }

            RequestPage(pageNumber - 1, PageSize);
            JumpPageBox.Clear();
        }
    }

    public sealed class PaginationRequestedEventArgs(int pageIndex, int pageSize) : EventArgs
    {
        public int PageIndex { get; } = pageIndex;
        public int PageSize { get; } = pageSize;
    }
}
