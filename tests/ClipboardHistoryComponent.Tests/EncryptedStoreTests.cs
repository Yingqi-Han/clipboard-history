using System.IO;
using Xunit;

namespace YingqiClipboard.Tests;

public sealed class EncryptedStoreTests
{
    [Fact]
    public async Task RoundTrip_DoesNotLeavePlaintextOnDisk()
    {
        string directory = NewDirectory();
        FakeProtector protector = new();
        EncryptedClipboardHistoryStore store = new(directory, protector);
        string marker = "private-test-marker-clipboard";
        ClipboardHistoryStoreItem entry = new(Guid.NewGuid(), ClipboardEntryKind.Text, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, marker.Length, marker, null);

        await store.SaveAsync([entry], CancellationToken.None);
        IReadOnlyList<ClipboardHistoryStoreItem> loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Single(loaded);
        Assert.Equal(marker, loaded[0].Text);
        byte[] disk = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).SelectMany(File.ReadAllBytes).ToArray();
        Assert.DoesNotContain(marker, System.Text.Encoding.UTF8.GetString(disk));
    }

    [Fact]
    public async Task TamperedEnvelope_IsQuarantined()
    {
        string directory = NewDirectory();
        EncryptedClipboardHistoryStore store = new(directory, new FakeProtector());
        ClipboardHistoryStoreItem entry = new(Guid.NewGuid(), ClipboardEntryKind.Text, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 4, "safe", null);
        await store.SaveAsync([entry], CancellationToken.None);
        string path = Directory.GetFiles(Path.Combine(directory, "Items"), "*.clip").Single();
        byte[] bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0x5A;
        File.WriteAllBytes(path, bytes);

        IReadOnlyList<ClipboardHistoryStoreItem> loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Empty(loaded);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(Path.Combine(directory, "Quarantine")));
    }

    [Fact]
    public void IndexContract_RoundTrips()
    {
        EncryptedClipboardHistoryStore.StoredEntry item = new(Guid.NewGuid(), ClipboardEntryKind.Text, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 4, "HASH");
        EncryptedClipboardHistoryStore.StoreDocument document = new(1, [item]);
        string json = System.Text.Json.JsonSerializer.Serialize(document, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        EncryptedClipboardHistoryStore.StoreDocument? loaded = System.Text.Json.JsonSerializer.Deserialize<EncryptedClipboardHistoryStore.StoreDocument>(json, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.NotNull(loaded);
        Assert.Single(loaded.Items);
    }

    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), "YingqiClipboardTests", Guid.NewGuid().ToString("N"));

    private sealed class FakeProtector : IKeyProtector
    {
        public byte[] Protect(byte[] value) => value.Select(b => (byte)(b ^ 0xA5)).ToArray();
        public byte[] Unprotect(byte[] value) => Protect(value);
    }
}
