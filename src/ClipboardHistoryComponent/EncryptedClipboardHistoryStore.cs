using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YingqiClipboard;

public sealed class EncryptedClipboardHistoryStore : IClipboardHistoryStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly byte[] Entropy = "YingqiTools.ClipboardHistory.v1"u8.ToArray();
    private readonly string _dataDirectory;
    private readonly string _keyPath;
    private readonly string _indexPath;
    private readonly string _itemsDirectory;
    private readonly string _quarantineDirectory;
    private readonly IKeyProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public EncryptedClipboardHistoryStore(string dataDirectory)
        : this(dataDirectory, new DpapiKeyProtector())
    {
    }

    internal EncryptedClipboardHistoryStore(string dataDirectory, IKeyProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _keyPath = Path.Combine(_dataDirectory, "master.key");
        _indexPath = Path.Combine(_dataDirectory, "index.dat");
        _itemsDirectory = Path.Combine(_dataDirectory, "Items");
        _quarantineDirectory = Path.Combine(_dataDirectory, "Quarantine");
        _protector = protector;
    }

    public async Task<IReadOnlyList<ClipboardHistoryStoreItem>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                StoreDocument document = await LoadDocumentUnlockedAsync(cancellationToken).ConfigureAwait(false);
                List<ClipboardHistoryStoreItem> entries = [];
                List<StoredEntry> valid = [];
                foreach (StoredEntry item in document.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string path = ItemPath(item.Id);
                    if (!File.Exists(path)) continue;
                    try
                    {
                        string? text = null;
                        if (item.Kind == ClipboardEntryKind.Text)
                        {
                            byte[] plaintext = await ReadEnvelopeUnlockedAsync(path, cancellationToken).ConfigureAwait(false);
                            text = Encoding.UTF8.GetString(plaintext);
                        }
                        entries.Add(new ClipboardHistoryStoreItem(item.Id, item.Kind, item.CreatedAt, item.UpdatedAt, item.ContentBytes, text, null, item.ContentHash));
                        valid.Add(item);
                    }
                    catch
                    {
                        Quarantine(path, $"item-{item.Id:N}");
                    }
                }
                if (valid.Count != document.Items.Count)
                    await SaveIndexUnlockedAsync(valid, cancellationToken).ConfigureAwait(false);
                return entries;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                Quarantine(_indexPath, "index");
                return Array.Empty<ClipboardHistoryStoreItem>();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyList<ClipboardHistoryStoreItem> entries, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureDirectories();
            StoreDocument current;
            try
            {
                current = await LoadDocumentUnlockedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                current = new StoreDocument(CurrentSchemaVersion, []);
            }
            Dictionary<Guid, StoredEntry> currentById = current.Items.ToDictionary(item => item.Id);
            HashSet<Guid> desiredIds = entries.Select(item => item.Id).ToHashSet();

            foreach (ClipboardHistoryStoreItem entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = ItemPath(entry.Id);
                bool hasPayload = entry.Kind == ClipboardEntryKind.Text ? entry.Text is not null : entry.PngBytes is not null;
                if (hasPayload)
                {
                    byte[] payload = entry.Kind == ClipboardEntryKind.Text
                        ? Encoding.UTF8.GetBytes(entry.Text!)
                        : entry.PngBytes!;
                    await WriteEnvelopeAtomicUnlockedAsync(path, payload, cancellationToken).ConfigureAwait(false);
                }
                else if (!currentById.ContainsKey(entry.Id) || !File.Exists(path))
                {
                    throw new InvalidDataException($"Missing encrypted payload for {entry.Id}.");
                }
            }

            StoredEntry[] index = entries.Select(value => new StoredEntry(
                value.Id,
                value.Kind,
                value.CreatedAt,
                value.UpdatedAt,
                value.ContentBytes,
                value.ContentHash ?? ComputeContentHash(value))).ToArray();
            await SaveIndexUnlockedAsync(index, cancellationToken).ConfigureAwait(false);

            foreach (StoredEntry removed in current.Items.Where(item => !desiredIds.Contains(item.Id)))
            {
                string path = ItemPath(removed.Id);
                if (File.Exists(path)) File.Delete(path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> ReadTextAsync(Guid id, CancellationToken cancellationToken)
    {
        byte[]? value = await ReadItemAsync(id, cancellationToken).ConfigureAwait(false);
        return value is null ? null : Encoding.UTF8.GetString(value);
    }

    public Task<byte[]?> ReadImageAsync(Guid id, CancellationToken cancellationToken) => ReadItemAsync(id, cancellationToken);

    public long GetStorageBytes()
    {
        if (!Directory.Exists(_dataDirectory)) return 0;
        return Directory.EnumerateFiles(_dataDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(_quarantineDirectory, StringComparison.OrdinalIgnoreCase))
            .Sum(path => new FileInfo(path).Length);
    }

    private async Task<byte[]?> ReadItemAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = ItemPath(id);
            if (!File.Exists(path)) return null;
            try
            {
                return await ReadEnvelopeUnlockedAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Quarantine(path, $"item-{id:N}");
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StoreDocument> LoadDocumentUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_indexPath)) return new StoreDocument(CurrentSchemaVersion, []);
        byte[] plaintext = await ReadEnvelopeUnlockedAsync(_indexPath, cancellationToken).ConfigureAwait(false);
        StoreDocument? document = JsonSerializer.Deserialize<StoreDocument>(plaintext, _jsonOptions);
        if (document is null || document.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException("Unsupported clipboard history schema.");
        return document;
    }

    private Task SaveIndexUnlockedAsync(IReadOnlyList<StoredEntry> entries, CancellationToken cancellationToken)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new StoreDocument(CurrentSchemaVersion, entries), _jsonOptions);
        return WriteEnvelopeAtomicUnlockedAsync(_indexPath, json, cancellationToken);
    }

    private async Task<byte[]> ReadEnvelopeUnlockedAsync(string path, CancellationToken cancellationToken)
    {
        byte[] envelope = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Decrypt(envelope, GetOrCreateKey());
    }

    private async Task WriteEnvelopeAtomicUnlockedAsync(string path, byte[] plaintext, CancellationToken cancellationToken)
    {
        byte[] envelope = Encrypt(plaintext, GetOrCreateKey());
        string temporaryPath = path + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, envelope, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(temporaryPath, path, true);
    }

    private byte[] GetOrCreateKey()
    {
        EnsureDirectories();
        if (File.Exists(_keyPath)) return _protector.Unprotect(File.ReadAllBytes(_keyPath));
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] protectedKey = _protector.Protect(key);
        string temporaryPath = _keyPath + ".tmp";
        File.WriteAllBytes(temporaryPath, protectedKey);
        try
        {
            File.Move(temporaryPath, _keyPath, false);
            return key;
        }
        catch (IOException) when (File.Exists(_keyPath))
        {
            File.Delete(temporaryPath);
            return _protector.Unprotect(File.ReadAllBytes(_keyPath));
        }
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_itemsDirectory);
    }

    private string ItemPath(Guid id) => Path.Combine(_itemsDirectory, $"{id:N}.clip");

    private static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];
        using AesGcm aes = new(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Entropy);
        byte[] result = new byte[1 + nonce.Length + tag.Length + ciphertext.Length];
        result[0] = CurrentSchemaVersion;
        Buffer.BlockCopy(nonce, 0, result, 1, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, 13, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, result, 29, ciphertext.Length);
        return result;
    }

    private static byte[] Decrypt(byte[] envelope, byte[] key)
    {
        const int headerLength = 29;
        if (envelope.Length < headerLength || envelope[0] != CurrentSchemaVersion)
            throw new InvalidDataException("Invalid clipboard history envelope.");
        byte[] plaintext = new byte[envelope.Length - headerLength];
        using AesGcm aes = new(key, 16);
        aes.Decrypt(envelope.AsSpan(1, 12), envelope.AsSpan(headerLength), envelope.AsSpan(13, 16), plaintext, Entropy);
        return plaintext;
    }

    private void Quarantine(string path, string name)
    {
        try
        {
            if (!File.Exists(path)) return;
            Directory.CreateDirectory(_quarantineDirectory);
            File.Move(path, Path.Combine(_quarantineDirectory, $"{name}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.corrupt"), false);
        }
        catch
        {
            // Corrupt content must never prevent startup.
        }
    }

    internal sealed class StoreDocument
    {
        public StoreDocument() { }
        public StoreDocument(int schemaVersion, IReadOnlyList<StoredEntry> items)
        {
            SchemaVersion = schemaVersion;
            Items = items;
        }

        public int SchemaVersion { get; set; }
        public IReadOnlyList<StoredEntry> Items { get; set; } = [];
    }
    private static string ComputeContentHash(ClipboardHistoryStoreItem item)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData([(byte)item.Kind, 0]);
        hash.AppendData(item.Kind == ClipboardEntryKind.Text ? Encoding.UTF8.GetBytes(item.Text ?? string.Empty) : item.PngBytes ?? []);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal sealed class StoredEntry
    {
        public StoredEntry() { }
        public StoredEntry(Guid id, ClipboardEntryKind kind, DateTimeOffset createdAt, DateTimeOffset updatedAt, long contentBytes, string contentHash)
        {
            Id = id;
            Kind = kind;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
            ContentBytes = contentBytes;
            ContentHash = contentHash;
        }

        public Guid Id { get; set; }
        public ClipboardEntryKind Kind { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public long ContentBytes { get; set; }
        public string ContentHash { get; set; } = string.Empty;
    }

    private sealed class DpapiKeyProtector : IKeyProtector
    {
        public byte[] Protect(byte[] value) => ProtectedData.Protect(value, Entropy, DataProtectionScope.CurrentUser);
        public byte[] Unprotect(byte[] value) => ProtectedData.Unprotect(value, Entropy, DataProtectionScope.CurrentUser);
    }
}
