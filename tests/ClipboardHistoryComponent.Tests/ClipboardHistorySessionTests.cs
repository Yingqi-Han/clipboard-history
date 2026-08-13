using System.Text;
using Xunit;

namespace YingqiClipboard.Tests;

public sealed class ClipboardHistorySessionTests
{
    [Fact]
    public async Task Start_ImportsTextAndImage_WithImagePriorityProvidedByAdapter()
    {
        FakeAdapter adapter = new([
            new(ClipboardEntryKind.Text, DateTimeOffset.UtcNow.AddSeconds(-1), Text: "hello"),
            new(ClipboardEntryKind.Image, DateTimeOffset.UtcNow, PngBytes: [1, 2, 3])
        ]);
        MemoryStore store = new();
        await using ClipboardHistorySession session = NewSession(adapter, store);

        await session.StartAsync();

        Assert.Equal(2, session.Count);
        Assert.Equal(ClipboardEntryKind.Image, session.Entries[0].Kind);
        Assert.Equal("hello", session.Entries[1].Text);
    }

    [Fact]
    public async Task DuplicateContent_RefreshesAndMovesToTop()
    {
        DateTimeOffset first = DateTimeOffset.UtcNow.AddMinutes(-3);
        FakeAdapter adapter = new([
            new(ClipboardEntryKind.Text, first, Text: "same"),
            new(ClipboardEntryKind.Text, first.AddMinutes(1), Text: "other")
        ]);
        await using ClipboardHistorySession session = NewSession(adapter, new MemoryStore());
        await session.StartAsync();
        adapter.Items = [new(ClipboardEntryKind.Text, DateTimeOffset.UtcNow, Text: "same")];

        await session.RefreshAsync();

        Assert.Equal(2, session.Count);
        Assert.Equal("same", session.Entries[0].Text);
        Assert.True(session.Entries[0].UpdatedAt > first);
    }

    [Fact]
    public async Task SensitiveText_IsNeverStored()
    {
        string token = "ghp_abcdefghijklmnopqrstuvwxyzABCDE1234567890";
        FakeAdapter adapter = new([new(ClipboardEntryKind.Text, DateTimeOffset.UtcNow, Text: token)]);
        MemoryStore store = new();
        await using ClipboardHistorySession session = NewSession(adapter, store);

        await session.StartAsync();

        Assert.Empty(session.Entries);
        Assert.DoesNotContain(store.Entries, value => value.Text == token);
    }

    [Fact]
    public async Task Retention_AppliesAgeCountAndQuota()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<ClipboardImportItem> items = [];
        items.Add(new(ClipboardEntryKind.Text, now.AddDays(-31), Text: "expired"));
        for (int i = 0; i < 8; i++) items.Add(new(ClipboardEntryKind.Text, now.AddMinutes(i), Text: new string((char)('a' + i), 10)));
        FakeAdapter adapter = new(items);
        ClipboardHistoryOptions options = new()
        {
            DataDirectory = "unused",
            MaxItems = 5,
            MaxAge = TimeSpan.FromDays(30),
            MaxBytes = 25
        };
        await using ClipboardHistorySession session = new(options, adapter, new MemoryStore());

        await session.StartAsync();

        Assert.Equal(2, session.Count);
        Assert.DoesNotContain(session.Entries, value => value.Text == "expired");
    }

    [Theory]
    [InlineData(ClipboardSyncState.HistoryDisabled)]
    [InlineData(ClipboardSyncState.AccessDenied)]
    public async Task ErrorState_PreservesExistingLocalHistory(ClipboardSyncState state)
    {
        ClipboardHistoryStoreItem local = new(Guid.NewGuid(), ClipboardEntryKind.Text, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 5, "local", null);
        MemoryStore store = new() { Entries = [local] };
        FakeAdapter adapter = new([]) { State = state };
        await using ClipboardHistorySession session = NewSession(adapter, store);

        await session.StartAsync();

        Assert.Equal(state, session.SyncState);
        Assert.Single(session.Entries);
    }

    [Fact]
    public async Task Copy_DelegatesToAdapterAndDoesNotAddAnotherEntry()
    {
        FakeAdapter adapter = new([new(ClipboardEntryKind.Text, DateTimeOffset.UtcNow, Text: "copy me")]);
        await using ClipboardHistorySession session = NewSession(adapter, new MemoryStore());
        await session.StartAsync();

        bool result = await session.CopyAsync(session.Entries[0].Id);

        Assert.True(result);
        Assert.Equal("copy me", adapter.LastWritten?.Text);
        Assert.Single(session.Entries);
    }

    [Fact]
    public async Task HistoryChanged_RefreshesAfterDebounce()
    {
        FakeAdapter adapter = new([]);
        await using ClipboardHistorySession session = NewSession(adapter, new MemoryStore());
        await session.StartAsync();
        adapter.Items = [new(ClipboardEntryKind.Text, DateTimeOffset.UtcNow, Text: "arrived")];

        adapter.RaiseHistoryChanged();
        await WaitUntilAsync(() => session.Count == 1, TimeSpan.FromSeconds(2));

        Assert.Equal("arrived", session.Entries[0].Text);
    }

    private static ClipboardHistorySession NewSession(FakeAdapter adapter, MemoryStore store) => new(
        new ClipboardHistoryOptions { DataDirectory = "unused" }, adapter, store);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(condition());
    }

    private sealed class FakeAdapter(IReadOnlyList<ClipboardImportItem> items) : IWindowsClipboardHistoryAdapter
    {
        public event EventHandler? HistoryChanged;
        public IReadOnlyList<ClipboardImportItem> Items { get; set; } = items;
        public ClipboardSyncState State { get; set; } = ClipboardSyncState.Ready;
        public ClipboardHistoryEntry? LastWritten { get; private set; }
        public Task<ClipboardHistoryReadResult> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ClipboardHistoryReadResult(State, Items));
        public Task<bool> WriteAsync(ClipboardHistoryEntry entry, CancellationToken cancellationToken)
        {
            LastWritten = entry;
            return Task.FromResult(true);
        }
        public void RaiseHistoryChanged() => HistoryChanged?.Invoke(this, EventArgs.Empty);
        public void Dispose() { }
    }

    private sealed class MemoryStore : IClipboardHistoryStore
    {
        public IReadOnlyList<ClipboardHistoryStoreItem> Entries { get; set; } = [];
        public Task<IReadOnlyList<ClipboardHistoryStoreItem>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Entries);
        public Task SaveAsync(IReadOnlyList<ClipboardHistoryStoreItem> entries, CancellationToken cancellationToken)
        {
            Entries = entries.ToArray();
            return Task.CompletedTask;
        }
        public Task<string?> ReadTextAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Entries.FirstOrDefault(value => value.Id == id)?.Text);
        public Task<byte[]?> ReadImageAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Entries.FirstOrDefault(value => value.Id == id)?.PngBytes);
        public long GetStorageBytes() => Entries.Sum(entry => entry.ContentBytes);
    }
}
