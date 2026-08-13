namespace YingqiClipboard;

public interface IWindowsClipboardHistoryAdapter : IDisposable
{
    event EventHandler? HistoryChanged;
    Task<ClipboardHistoryReadResult> ReadAsync(CancellationToken cancellationToken);
    Task<bool> WriteAsync(ClipboardHistoryEntry entry, CancellationToken cancellationToken);
}

public sealed record ClipboardHistoryReadResult(
    ClipboardSyncState State,
    IReadOnlyList<ClipboardImportItem> Items,
    string? Error = null)
{
    public static ClipboardHistoryReadResult Empty(ClipboardSyncState state, string? error = null) =>
        new(state, Array.Empty<ClipboardImportItem>(), error);
}

public interface IClipboardHistoryStore
{
    Task<IReadOnlyList<ClipboardHistoryStoreItem>> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(IReadOnlyList<ClipboardHistoryStoreItem> entries, CancellationToken cancellationToken);
    Task<string?> ReadTextAsync(Guid id, CancellationToken cancellationToken);
    Task<byte[]?> ReadImageAsync(Guid id, CancellationToken cancellationToken);
    long GetStorageBytes();
}

internal interface IKeyProtector
{
    byte[] Protect(byte[] value);
    byte[] Unprotect(byte[] value);
}
