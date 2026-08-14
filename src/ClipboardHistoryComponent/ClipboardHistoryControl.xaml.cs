using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using Wpf.Ui.Controls;

namespace YingqiClipboard;

public partial class ClipboardHistoryControl : UserControl
{
    private readonly ClipboardHistorySession _session;
    private readonly ClipboardHistoryDisplayMode _displayMode;
    private string _filter = "All";
    private string _search = string.Empty;
    private bool _loaded;
    private readonly Dictionary<Guid, ImageSource> _thumbnails = [];

    public ClipboardHistoryControl(ClipboardHistorySession session, ClipboardHistoryDisplayMode displayMode = ClipboardHistoryDisplayMode.FullPage)
    {
        InitializeComponent();
        _session = session;
        _displayMode = displayMode;
        if (displayMode == ClipboardHistoryDisplayMode.CompactWindow)
        {
            HeaderSection.Visibility = Visibility.Collapsed;
            ManagementBar.Visibility = Visibility.Collapsed;
            ManagementRow.Height = new GridLength(0);
            CommandCard.Margin = new Thickness(0, 0, 0, 10);
            CommandCard.Padding = new Thickness(12);
            SearchBox.Margin = new Thickness(0, 0, 10, 0);
            RootLayout.Margin = new Thickness(0, 0, 0, 12);
            Resources["CompactDeleteVisibility"] = Visibility.Collapsed;
            Resources["EntryCardPadding"] = new Thickness(10);
            Resources["EntryItemMargin"] = new Thickness(0, 0, 0, 6);
            Resources["EntryActionMargin"] = new Thickness(8, 0, 0, 0);
            Resources["EntryMetadataMargin"] = new Thickness(0, 6, 0, 0);
            Resources["EntryTextFontSize"] = 12d;
            Resources["EntryMetadataFontSize"] = 10.5d;
            Resources["EntryTextMaxHeight"] = 58d;
            Resources["EntryImageMaxHeight"] = 112d;
            Resources["EntryButtonHeight"] = 30d;
        }
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event EventHandler? OpenCompactWindowRequested;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            _loaded = true;
            _session.EntriesChanged += Session_EntriesChanged;
            _session.StateChanged += Session_StateChanged;
        }
        await _session.RefreshAsync();
        RefreshView();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _session.EntriesChanged -= Session_EntriesChanged;
        _session.StateChanged -= Session_StateChanged;
        _loaded = false;
        // The shared session itself keeps syncing while another page is open.
    }

    private void Session_EntriesChanged(object? sender, ClipboardEntriesChangedEventArgs e) =>
        Dispatcher.BeginInvoke(RefreshView);

    private void Session_StateChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(RefreshState);

    private void RefreshView()
    {
        IEnumerable<ClipboardHistoryEntry> query = _session.Entries;
        if (_filter == "Text") query = query.Where(value => value.Kind == ClipboardEntryKind.Text);
        if (_filter == "Image") query = query.Where(value => value.Kind == ClipboardEntryKind.Image);
        if (!string.IsNullOrWhiteSpace(_search))
            query = query.Where(value => value.Kind == ClipboardEntryKind.Text && (value.Text?.Contains(_search, StringComparison.CurrentCultureIgnoreCase) ?? false));
        ClipboardEntryViewModel[] items = query.Select(value => new ClipboardEntryViewModel(
            value,
            _thumbnails.GetValueOrDefault(value.Id))).ToArray();
        HistoryList.ItemsSource = items;
        EmptyState.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.Visibility = items.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UsageText.Text = $"{_session.Count}/200 条  ·  {FormatBytes(_session.StorageBytes)}/500 MB";
        RefreshState();
        _ = LoadVisibleThumbnailsAsync(items.Where(item => item.IsImage && item.Thumbnail is null).ToArray());
    }

    private void RefreshState()
    {
        (StatusBar.Title, StatusBar.Message, StatusBar.Severity) = _session.SyncState switch
        {
            ClipboardSyncState.Ready => ("同步正常", $"已保存 {_session.Count} 条；双击即可复制，窗口不会消失。", InfoBarSeverity.Success),
            ClipboardSyncState.HistoryDisabled => ("Win+V 未开启", "本地历史仍可使用。开启 Windows 剪贴板历史后才能继续导入。", InfoBarSeverity.Warning),
            ClipboardSyncState.AccessDenied => ("无法访问 Win+V", "系统策略拒绝读取剪贴板历史。", InfoBarSeverity.Error),
            _ => ("同步暂不可用", _session.LastError ?? "请稍后点击“立即同步”重试。", InfoBarSeverity.Warning)
        };
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text;
        RefreshView();
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string filter }) _filter = filter;
        RefreshView();
    }

    private void OpenCompact_Click(object sender, RoutedEventArgs e) => OpenCompactWindowRequested?.Invoke(this, EventArgs.Empty);
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _session.RefreshAsync();
    private void OpenSettings_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:clipboard") { UseShellExecute = true });

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBoxResult result = System.Windows.MessageBox.Show(
            "只清空 Yingqi Tools 的本地历史，不会清空 Win+V。确定继续吗？",
            "清空本地剪贴板历史",
            System.Windows.MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result == System.Windows.MessageBoxResult.Yes) await _session.ClearAsync();
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ClipboardEntryViewModel item }) await CopyAsync(item);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ClipboardEntryViewModel item }) await _session.DeleteAsync(item.Id);
    }

    private async void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItem is ClipboardEntryViewModel item) await CopyAsync(item);
    }

    private void HistoryList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = ScrollByWheelDelta(e.Delta);
    }

    public bool ScrollByWheelDelta(int delta)
    {
        if (FindDescendant<ScrollViewer>(HistoryList) is not { ScrollableHeight: > 0 } scrollViewer) return false;
        double previousOffset = scrollViewer.VerticalOffset;
        int notches = Math.Max(1, Math.Abs(delta) / 120);
        int linesPerNotch = _displayMode == ClipboardHistoryDisplayMode.CompactWindow ? 2 : 1;
        for (int line = 0; line < notches * linesPerNotch; line++)
        {
            if (delta < 0) scrollViewer.LineDown();
            else scrollViewer.LineUp();
        }
        scrollViewer.UpdateLayout();
        return !scrollViewer.VerticalOffset.Equals(previousOffset);
    }

    private async Task CopyAsync(ClipboardEntryViewModel item)
    {
        bool copied = await _session.CopyAsync(item.Id);
        StatusBar.Title = copied ? "已复制" : "复制失败";
        StatusBar.Message = copied ? "内容已放入剪贴板，可以在目标位置粘贴。" : "剪贴板可能正被其他程序占用，请重试。";
        StatusBar.Severity = copied ? InfoBarSeverity.Success : InfoBarSeverity.Error;
    }

    private async Task LoadVisibleThumbnailsAsync(IReadOnlyList<ClipboardEntryViewModel> items)
    {
        bool changed = false;
        foreach (ClipboardEntryViewModel item in items.Take(30))
        {
            if (_thumbnails.ContainsKey(item.Id)) continue;
            byte[]? png = await _session.LoadImageAsync(item.Id);
            if (png is null)
            {
                await _session.DeleteAsync(item.Id);
                continue;
            }
            using MemoryStream stream = new(png);
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 420;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            _thumbnails[item.Id] = image;
            changed = true;
        }
        if (changed) _ = Dispatcher.BeginInvoke(RefreshView);
    }

    private static string FormatBytes(long value) => value < 1024 * 1024
        ? $"{value / 1024d:0.#} KB"
        : $"{value / 1024d / 1024d:0.#} MB";

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
