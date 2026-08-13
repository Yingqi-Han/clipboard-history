using System.Runtime.InteropServices.WindowsRuntime;
using System.IO;
using System.Windows.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace YingqiClipboard;

public sealed class WindowsClipboardHistoryAdapter : IWindowsClipboardHistoryAdapter
{
    private bool _disposed;

    public event EventHandler? HistoryChanged;

    public WindowsClipboardHistoryAdapter()
    {
        WinClipboard.HistoryChanged += OnHistoryChanged;
    }

    public async Task<ClipboardHistoryReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            ClipboardHistoryItemsResult result = await WinClipboard.GetHistoryItemsAsync().AsTask(cancellationToken).ConfigureAwait(false);
            ClipboardSyncState state = result.Status switch
            {
                ClipboardHistoryItemsResultStatus.Success => ClipboardSyncState.Ready,
                ClipboardHistoryItemsResultStatus.AccessDenied => ClipboardSyncState.AccessDenied,
                ClipboardHistoryItemsResultStatus.ClipboardHistoryDisabled => ClipboardSyncState.HistoryDisabled,
                _ => ClipboardSyncState.Faulted
            };
            if (state != ClipboardSyncState.Ready) return ClipboardHistoryReadResult.Empty(state);

            List<ClipboardImportItem> items = [];
            foreach (ClipboardHistoryItem historyItem in result.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DataPackageView content = historyItem.Content;
                try
                {
                    if (content.Contains(StandardDataFormats.Bitmap))
                    {
                        byte[]? png = await ReadBitmapAsPngAsync(content, cancellationToken).ConfigureAwait(false);
                        if (png is not null)
                            items.Add(new ClipboardImportItem(ClipboardEntryKind.Image, historyItem.Timestamp, PngBytes: png, WindowsHistoryId: historyItem.Id));
                    }
                    else if (content.Contains(StandardDataFormats.Text))
                    {
                        string text = await content.GetTextAsync().AsTask(cancellationToken).ConfigureAwait(false);
                        items.Add(new ClipboardImportItem(ClipboardEntryKind.Text, historyItem.Timestamp, Text: text, WindowsHistoryId: historyItem.Id));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // One unsupported or protected history item must not block the remaining list.
                }
            }
            return new ClipboardHistoryReadResult(ClipboardSyncState.Ready, items);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ClipboardHistoryReadResult.Empty(ClipboardSyncState.Faulted, "无法读取 Windows 剪贴板历史。");
        }
    }

    public async Task<bool> WriteAsync(ClipboardHistoryEntry entry, CancellationToken cancellationToken)
    {
        DataPackage package = new();
        if (entry.Kind == ClipboardEntryKind.Text)
        {
            package.SetText(entry.Text ?? string.Empty);
        }
        else
        {
            if (entry.PngBytes is null) return false;
            InMemoryRandomAccessStream stream = new();
            await stream.WriteAsync(entry.PngBytes.AsBuffer()).AsTask(cancellationToken).ConfigureAwait(false);
            stream.Seek(0);
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        }

        ClipboardContentOptions options = new()
        {
            IsAllowedInHistory = false,
            IsRoamable = false
        };
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WinClipboard.SetContentWithOptions(package, options))
            {
                WinClipboard.Flush();
                return true;
            }
            await Task.Delay(attempt == 0 ? 50 : 150, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WinClipboard.HistoryChanged -= OnHistoryChanged;
    }

    private void OnHistoryChanged(object? sender, ClipboardHistoryChangedEventArgs e) =>
        HistoryChanged?.Invoke(this, EventArgs.Empty);

    private static async Task<byte[]?> ReadBitmapAsPngAsync(DataPackageView content, CancellationToken cancellationToken)
    {
        RandomAccessStreamReference reference = await content.GetBitmapAsync().AsTask(cancellationToken).ConfigureAwait(false);
        using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false);
        if (stream.Size == 0 || stream.Size > 16L * 1024 * 1024) return null;
        using Stream source = stream.AsStreamForRead();
        BitmapDecoder decoder = BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapFrame frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 64L * 1024 * 1024) return null;
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(frame);
        using MemoryStream output = new();
        encoder.Save(output);
        return output.Length <= 16L * 1024 * 1024 ? output.ToArray() : null;
    }
}
