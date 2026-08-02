using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CabinetLock.Controls
{
    public partial class SearchableMultiSelect : UserControl, INotifyPropertyChanged
    {
        private readonly List<ISearchableMultiSelectItem> _items = new();
        private ICollectionView? _view;
        private string _selectionSummary = "请选择";
        private string _filterSummary = "共 0 项";

        public SearchableMultiSelect()
        {
            InitializeComponent();
        }

        public string SelectionSummary
        {
            get => _selectionSummary;
            private set
            {
                if (_selectionSummary == value) return;
                _selectionSummary = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionSummary)));
            }
        }

        public string FilterSummary
        {
            get => _filterSummary;
            private set
            {
                if (_filterSummary == value) return;
                _filterSummary = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilterSummary)));
            }
        }

        public void SetItems(IEnumerable<ISearchableMultiSelectItem> items)
        {
            foreach (ISearchableMultiSelectItem item in _items)
                item.PropertyChanged -= Item_PropertyChanged;

            _items.Clear();
            _items.AddRange(items);
            foreach (ISearchableMultiSelectItem item in _items)
                item.PropertyChanged += Item_PropertyChanged;

            _view = CollectionViewSource.GetDefaultView(_items);
            _view.Filter = MatchesSearch;
            OptionList.ItemsSource = _view;
            RefreshText();
        }

        private bool MatchesSearch(object item)
        {
            if (item is not ISearchableMultiSelectItem option) return false;
            string keyword = SearchBox.Text.Trim();
            return string.IsNullOrWhiteSpace(keyword)
                || option.PrimaryText.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || option.SecondaryText.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _view?.Refresh();
            RefreshText();
        }

        private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ISearchableMultiSelectItem.IsSelected)) RefreshText();
        }

        private void RefreshText()
        {
            List<ISearchableMultiSelectItem> selected = _items.Where(item => item.IsSelected).ToList();
            SelectionSummary = selected.Count switch
            {
                0 => "请选择",
                1 => selected[0].PrimaryText,
                _ => $"已选择 {selected.Count} 项 · {string.Join("、", selected.Take(2).Select(item => item.PrimaryText))}"
            };
            int visible = _view?.Cast<object>().Count() ?? _items.Count;
            FilterSummary = string.IsNullOrWhiteSpace(SearchBox.Text)
                ? $"共 {_items.Count} 项 · 已选 {selected.Count} 项"
                : $"找到 {visible} 项 · 已选 {selected.Count} 项";
        }

        private void SelectAvailableButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (ISearchableMultiSelectItem item in _items.Where(item => item.IsAvailable))
                item.IsSelected = true;
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (ISearchableMultiSelectItem item in _items) item.IsSelected = false;
        }

        private void DropDownPopup_Opened(object? sender, EventArgs e)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public interface ISearchableMultiSelectItem : INotifyPropertyChanged
    {
        string PrimaryText { get; }
        string SecondaryText { get; }
        string StatusText { get; }
        bool IsAvailable { get; }
        bool IsSelected { get; set; }
    }
}
