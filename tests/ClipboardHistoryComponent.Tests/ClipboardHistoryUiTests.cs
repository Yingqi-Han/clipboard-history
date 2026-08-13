using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Xunit;

namespace YingqiClipboard.Tests;

public sealed class ClipboardHistoryUiTests
{
    [Fact]
    public void FullPage_RendersReadOnlyEntryPropertiesWithoutDispatcherException()
    {
        RunOnSta((session, adapter) =>
        {
            ClipboardHistoryControl control = new(session);
            Window window = CreateHost(control, 900, 700);

            window.Show();
            PumpDispatcher();
            window.UpdateLayout();

            Assert.True(window.IsVisible);
            Assert.NotNull(control.FindName("HistoryList"));
            window.Close();
        });
    }

    [Fact]
    public void CompactWindow_KeepsStandardButtonsAndStaysOpenAfterDoubleClickCopy()
    {
        RunOnSta((session, adapter) =>
        {
            ClipboardCompactWindow window = new(session, topmost: true);
            window.Show();
            PumpDispatcher();
            window.UpdateLayout();

            TitleBar titleBar = Assert.IsType<TitleBar>(window.FindName("CompactTitleBar"));
            Assert.True(titleBar.ShowMinimize);
            Assert.True(titleBar.ShowMaximize);
            Assert.True(titleBar.ShowClose);
            Assert.True(window.Topmost);

            ContentPresenter controlHost = Assert.IsType<ContentPresenter>(window.FindName("ControlHost"));
            ClipboardHistoryControl control = Assert.IsType<ClipboardHistoryControl>(controlHost.Content);
            ListBox history = Assert.IsType<ListBox>(control.FindName("HistoryList"));
            typeof(ClipboardHistoryControl)
                .GetMethod("RefreshView", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(control, null);
            PumpDispatcher();
            Assert.Single(history.Items);
            history.SelectedIndex = 0;
            MouseButtonEventArgs doubleClick = new(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Control.MouseDoubleClickEvent,
                Source = history
            };
            typeof(ClipboardHistoryControl)
                .GetMethod("HistoryList_MouseDoubleClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(control, [history, doubleClick]);
            PumpDispatcher();

            Assert.Equal(1, adapter.WriteCount);
            Assert.True(window.IsVisible);
            window.Close();
        });
    }

    private static Window CreateHost(UIElement content, double width, double height) => new()
    {
        Content = content,
        Width = width,
        Height = height,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -10000,
        Top = -10000
    };

    private static void RunOnSta(Action<ClipboardHistorySession, UiFakeAdapter> action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.UnhandledException += (_, args) =>
            {
                failure = args.Exception;
                args.Handled = true;
            };
            UiFakeAdapter adapter = new();
            UiMemoryStore store = new();
            using ClipboardHistorySession session = new(
                new ClipboardHistoryOptions { DataDirectory = "unused" }, adapter, store);
            try
            {
                session.StartAsync().GetAwaiter().GetResult();
                action(session, adapter);
                PumpDispatcher();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                foreach (Window window in Application.Current?.Windows.Cast<Window>().ToArray() ?? [])
                    window.Close();
                dispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF UI test timed out.");
        Assert.Null(failure);
    }

    private static void PumpDispatcher()
    {
        DispatcherFrame frame = new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class UiFakeAdapter : IWindowsClipboardHistoryAdapter
    {
        public event EventHandler? HistoryChanged { add { } remove { } }
        public int WriteCount { get; private set; }
        public Task<ClipboardHistoryReadResult> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(
            new ClipboardHistoryReadResult(ClipboardSyncState.Ready,
            [new ClipboardImportItem(ClipboardEntryKind.Text, DateTimeOffset.UtcNow, Text: "visual test entry")]));
        public Task<bool> WriteAsync(ClipboardHistoryEntry entry, CancellationToken cancellationToken)
        {
            WriteCount++;
            return Task.FromResult(true);
        }
        public void Dispose() { }
    }

    private sealed class UiMemoryStore : IClipboardHistoryStore
    {
        private IReadOnlyList<ClipboardHistoryStoreItem> _entries = [];
        public Task<IReadOnlyList<ClipboardHistoryStoreItem>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_entries);
        public Task SaveAsync(IReadOnlyList<ClipboardHistoryStoreItem> entries, CancellationToken cancellationToken)
        {
            _entries = entries.ToArray();
            return Task.CompletedTask;
        }
        public Task<string?> ReadTextAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(_entries.FirstOrDefault(value => value.Id == id)?.Text);
        public Task<byte[]?> ReadImageAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
        public long GetStorageBytes() => _entries.Sum(value => value.ContentBytes);
    }
}
