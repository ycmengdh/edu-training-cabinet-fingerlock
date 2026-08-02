using System.Windows;
using System.Windows.Controls;

namespace CabinetLock
{
    /// <summary>
    /// 内存列表客户端分页（全量已在本机/已过滤后再切页）。
    /// 适合用户/模板/班级学生等；服务端大表（开锁日志）仍用各自 offset/limit。
    /// </summary>
    public sealed class ListPager
    {
        public ListPager(int pageSize = 50)
        {
            PageSize = pageSize > 0 ? pageSize : 50;
        }

        public int PageSize { get; private set; }
        public int PageIndex { get; private set; }
        public int TotalCount { get; private set; }
        public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));

        public void Reset() => PageIndex = 0;

        public void ApplyRequest(Controls.PaginationRequestedEventArgs request)
        {
            PageSize = Math.Max(1, request.PageSize);
            PageIndex = Math.Max(0, request.PageIndex);
        }

        public bool CanPrev => PageIndex > 0;
        public bool CanNext => PageIndex + 1 < TotalPages;

        public bool Prev()
        {
            if (!CanPrev) return false;
            PageIndex--;
            return true;
        }

        public bool Next()
        {
            if (!CanNext) return false;
            PageIndex++;
            return true;
        }

        public IReadOnlyList<T> Slice<T>(IReadOnlyList<T> source)
        {
            source ??= Array.Empty<T>();
            TotalCount = source.Count;
            if (PageIndex >= TotalPages) PageIndex = TotalPages - 1;
            if (PageIndex < 0) PageIndex = 0;
            return source.Skip(PageIndex * PageSize).Take(PageSize).ToList();
        }

        public string PageInfoText => $"第 {PageIndex + 1} / {TotalPages} 页";

        public string StatusText(int pageCount, string unit = "条") =>
            TotalCount == 0
                ? $"共 0 {unit}"
                : $"共 {TotalCount} {unit} · 本页 {pageCount} {unit} · 每页 {PageSize}";

        public void BindChrome(Controls.PaginationControl? pager, string unit = "条")
        {
            pager?.Configure(TotalCount, PageIndex, PageSize, unit);
        }

        public void BindChrome(Button? prev, Button? next, TextBlock? pageInfo, TextBlock? status = null, int pageCount = 0)
        {
            if (prev != null) prev.IsEnabled = CanPrev;
            if (next != null) next.IsEnabled = CanNext;
            if (pageInfo != null) pageInfo.Text = PageInfoText;
            if (status != null) status.Text = StatusText(pageCount);
        }
    }
}
