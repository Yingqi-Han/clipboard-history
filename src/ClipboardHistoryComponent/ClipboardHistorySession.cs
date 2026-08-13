using System.Security.Cryptography;
using System.Text;

namespace YingqiClipboard;

public sealed class ClipboardHistorySession : IAsyncDisposable
{
    private readonly ClipboardHistoryOptions _options;
    private readonly IWindowsClipboardHistoryAdapter _adapter;
    private readonly IClipboardHistoryStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _debounceLock = new();
    private List<ClipboardHistoryEntry> _entries = [];
    private CancellationTokenSource? _lifetimeCts;
    private CancellationTokenSource? _debounceCts;
    private bool _started;

    public ClipboardHistorySession(ClipboardHistoryOptions options)
        : this(options, new WindowsClipboardHistoryAdapter(), new EncryptedClipboardHistoryStore(options.DataDirectory))
    {
    }

    public ClipboardHistorySession(
        ClipboardHistoryOptions options,
        IWindowsClipboardHistoryAdapter adapter,
        IClipboardHistoryStore store)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxItems <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaxItems));
        if (options.MaxAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.MaxAge));
        if (options.MaxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaxBytes));
        _options = options;
        _adapter = adapter;
        _store = store;
    }

    public event EventHandler<ClipboardEntriesChangedEventArgs>? EntriesChanged;
    public event EventHandler? StateChanged;

    public ClipboardSyncState SyncState { get; private set; } = ClipboardSyncState.Faulted;
    public string? LastError { get; private set; }
    public int Count => _entries.Count;
    public long StorageBytes => _store.GetStorageBytes();
    public IReadOnlyList<ClipboardHistoryEntry> Entries => _entries.ToArray();

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        _lifetimeCts = new CancellationTokenSource();
        _adapter.HistoryChanged += OnHistoryChanged;
        await _gate.WaitAsync(_lifetimeCts.Token).ConfigureAwait(false);
        try
        {
            IReadOnlyList<ClipboardHistoryStoreItem> stored = await _store.LoadAsync(_lifetimeCts.Token).ConfigureAwait(false);
            _entries = stored.Select(value => new ClipboardHistoryEntry(
                value.Id,
                value.Kind,
                value.CreatedAt,
                value.UpdatedAt,
                value.ContentBytes,
                value.Kind == ClipboardEntryKind.Text ? value.Text : null,
                null,
                value.ContentHash)).ToList();
            bool pruned = Prune(DateTimeOffset.UtcNow);
            if (pruned) await SaveEntriesAsync(_lifetimeCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        RaiseEntriesChanged();
        await RefreshAsync().ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (!_started) return;
        _started = false;
        _adapter.HistoryChanged -= OnHistoryChanged;
        CancellationTokenSource? lifetime = _lifetimeCts;
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }
        lifetime?.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
        lifetime?.Dispose();
        _lifetimeCts = null;
    }

    public async Task RefreshAsync()
    {
        CancellationToken token = _lifetimeCts?.Token ?? CancellationToken.None;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ClipboardHistoryReadResult result = await _adapter.ReadAsync(token).ConfigureAwait(false);
            SetState(result.State, result.Error);
            if (result.State != ClipboardSyncState.Ready) return;

            bool changed = false;
            foreach (ClipboardImportItem item in result.Items.OrderBy(value => value.Timestamp))
                changed |= Import(item);
            changed |= Prune(DateTimeOffset.UtcNow);
            if (!changed) return;
            await SaveEntriesAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            SetState(ClipboardSyncState.Faulted, "读取或保存剪贴板历史失败。");
            return;
        }
        finally
        {
            _gate.Release();
        }
        RaiseEntriesChanged();
    }

    public async Task<bool> CopyAsync(Guid id)
    {
        ClipboardHistoryEntry? entry = _entries.FirstOrDefault(value => value.Id == id);
        if (entry is null) return false;
        try
        {
            CancellationToken token = _lifetimeCts?.Token ?? CancellationToken.None;
            if (entry.Kind == ClipboardEntryKind.Text && entry.Text is null)
                entry = entry with { Text = await _store.ReadTextAsync(entry.Id, token).ConfigureAwait(false) };
            if (entry.Kind == ClipboardEntryKind.Image && entry.PngBytes is null)
                entry = entry with { PngBytes = await _store.ReadImageAsync(entry.Id, token).ConfigureAwait(false) };
            return await _adapter.WriteAsync(entry, token).ConfigureAwait(false);
        }
        catch
        {
            SetState(ClipboardSyncState.Faulted, "写入系统剪贴板失败，请稍后重试。");
            return false;
        }
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            int removed = _entries.RemoveAll(value => value.Id == id);
            if (removed == 0) return false;
            await SaveEntriesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        RaiseEntriesChanged();
        return true;
    }

    public async Task ClearAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _entries.Clear();
            await SaveEntriesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        RaiseEntriesChanged();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _adapter.Dispose();
        _gate.Dispose();
    }

    private bool Import(ClipboardImportItem item)
    {
        string? text = item.Kind == ClipboardEntryKind.Text ? item.Text : null;
        byte[]? pngBytes = item.Kind == ClipboardEntryKind.Image ? item.PngBytes : null;
        if (item.Kind == ClipboardEntryKind.Text)
        {
            if (string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) > 4 * 1024 * 1024) return false;
            if (SecretDetector.ContainsHighConfidenceSecret(text)) return false;
        }
        else if (pngBytes is null || pngBytes.Length == 0 || pngBytes.Length > 16 * 1024 * 1024)
        {
            return false;
        }

        string hash = ComputeHash(item.Kind, text, pngBytes);
        ClipboardHistoryEntry? duplicate = _entries.FirstOrDefault(value =>
            string.Equals(value.ContentHash ?? ComputeHash(value.Kind, value.Text, value.PngBytes), hash, StringComparison.Ordinal));
        DateTimeOffset timestamp = item.Timestamp == default ? DateTimeOffset.UtcNow : item.Timestamp;
        if (duplicate is not null)
        {
            _entries.Remove(duplicate);
            _entries.Insert(0, duplicate with { UpdatedAt = timestamp });
            return true;
        }

        long bytes = item.Kind == ClipboardEntryKind.Text ? Encoding.UTF8.GetByteCount(text!) : pngBytes!.LongLength;
        _entries.Insert(0, new ClipboardHistoryEntry(Guid.NewGuid(), item.Kind, timestamp, timestamp, bytes, text, pngBytes, hash));
        return true;
    }

    public async Task<byte[]?> LoadImageAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ClipboardHistoryEntry? entry = _entries.FirstOrDefault(value => value.Id == id);
        if (entry is null || entry.Kind != ClipboardEntryKind.Image) return null;
        return entry.PngBytes ?? await _store.ReadImageAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private Task SaveEntriesAsync(CancellationToken cancellationToken) => _store.SaveAsync(
        _entries.Select(value => new ClipboardHistoryStoreItem(
            value.Id,
            value.Kind,
            value.CreatedAt,
            value.UpdatedAt,
            value.ContentBytes,
            value.Text,
            value.PngBytes,
            value.ContentHash)).ToArray(),
        cancellationToken);

    private bool Prune(DateTimeOffset now)
    {
        int initialCount = _entries.Count;
        DateTimeOffset cutoff = now.Subtract(_options.MaxAge);
        _entries.RemoveAll(entry => entry.UpdatedAt < cutoff);
        _entries = _entries.OrderByDescending(entry => entry.UpdatedAt).Take(_options.MaxItems).ToList();
        long total = _entries.Sum(entry => entry.ContentBytes);
        while (_entries.Count > 0 && total > _options.MaxBytes)
        {
            ClipboardHistoryEntry last = _entries[^1];
            total -= last.ContentBytes;
            _entries.RemoveAt(_entries.Count - 1);
        }
        return initialCount != _entries.Count;
    }

    private static string ComputeHash(ClipboardEntryKind kind, string? text, byte[]? image)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData([(byte)kind, 0]);
        if (kind == ClipboardEntryKind.Text) hash.AppendData(Encoding.UTF8.GetBytes(text ?? string.Empty));
        else hash.AppendData(image ?? []);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        if (!_started) return;
        CancellationTokenSource debounce;
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts?.Token ?? CancellationToken.None);
            debounce = _debounceCts;
        }
        _ = RefreshAfterDelayAsync(debounce.Token);
    }

    private async Task RefreshAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetState(ClipboardSyncState state, string? error)
    {
        if (SyncState == state && string.Equals(LastError, error, StringComparison.Ordinal)) return;
        SyncState = state;
        LastError = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseEntriesChanged() =>
        EntriesChanged?.Invoke(this, new ClipboardEntriesChangedEventArgs(Entries));
}
