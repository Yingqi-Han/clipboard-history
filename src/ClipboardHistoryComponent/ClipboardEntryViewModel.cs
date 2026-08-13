using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;

namespace YingqiClipboard;

public sealed class ClipboardEntryViewModel
{
    public ClipboardEntryViewModel(ClipboardHistoryEntry entry, ImageSource? thumbnail = null)
    {
        Entry = entry;
        Thumbnail = thumbnail;
        if (Thumbnail is null && entry.Kind == ClipboardEntryKind.Image && entry.PngBytes is { Length: > 0 })
        {
            using MemoryStream stream = new(entry.PngBytes);
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 420;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            Thumbnail = image;
        }
    }

    public ClipboardHistoryEntry Entry { get; }
    public Guid Id => Entry.Id;
    public ClipboardEntryKind Kind => Entry.Kind;
    public string Preview => Entry.Preview;
    public string DisplayTime => Entry.DisplayTime;
    public string DisplaySize => Entry.DisplaySize;
    public ImageSource? Thumbnail { get; }
    public bool IsText => Kind == ClipboardEntryKind.Text;
    public bool IsImage => Kind == ClipboardEntryKind.Image;
}
