namespace YingqiClipboard;

public enum ClipboardHistoryDisplayMode
{
    FullPage,
    CompactWindow
}

public enum ClipboardEntryKind
{
    Text,
    Image
}

public enum ClipboardSyncState
{
    Ready,
    HistoryDisabled,
    AccessDenied,
    Faulted
}

public sealed record ClipboardHistoryOptions
{
    public required string DataDirectory { get; init; }
    public int MaxItems { get; init; } = 200;
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(30);
    public long MaxBytes { get; init; } = 500L * 1024 * 1024;
}

public sealed record ClipboardImportItem(
    ClipboardEntryKind Kind,
    DateTimeOffset Timestamp,
    string? Text = null,
    byte[]? PngBytes = null,
    string? WindowsHistoryId = null);

public sealed record ClipboardHistoryEntry(
    Guid Id,
    ClipboardEntryKind Kind,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long ContentBytes,
    string? Text,
    byte[]? PngBytes,
    string? ContentHash = null)
{
    public string Preview => Kind == ClipboardEntryKind.Text
        ? BuildTextPreview(Text)
        : "图片";

    public string DisplayTime => UpdatedAt.LocalDateTime.ToString("MM-dd HH:mm");
    public string DisplaySize => ContentBytes < 1024
        ? $"{ContentBytes} B"
        : ContentBytes < 1024 * 1024
            ? $"{ContentBytes / 1024d:0.#} KB"
            : $"{ContentBytes / 1024d / 1024d:0.#} MB";

    private static string BuildTextPreview(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "空文本";
        string collapsed = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 220 ? collapsed : collapsed[..220] + "…";
    }
}

public sealed record ClipboardHistoryStoreItem(
    Guid Id,
    ClipboardEntryKind Kind,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long ContentBytes,
    string? Text,
    byte[]? PngBytes,
    string? ContentHash = null);

public sealed class ClipboardEntriesChangedEventArgs : EventArgs
{
    public ClipboardEntriesChangedEventArgs(IReadOnlyList<ClipboardHistoryEntry> entries) => Entries = entries;
    public IReadOnlyList<ClipboardHistoryEntry> Entries { get; }
}
