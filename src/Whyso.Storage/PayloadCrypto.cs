using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Whyso.Storage;

/// <summary>
/// Per-session payload encryption (AES-256-GCM). Each session has a random 256-bit key stored
/// under <c>.whyso/keys/SESSION.key</c>. Deleting that key crypto-shreds the session: the
/// append-only JSONL and SQLite rows stay, but their payloads can no longer be read.
/// </summary>
public static class PayloadCrypto
{
    /// <summary>Single-key wrapper object that marks an encrypted payload.</summary>
    public const string Marker = "$enc";

    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static string KeysDir(string root) => Path.Combine(WorktreePaths.WorktreeDir(root), "keys");

    public static string KeyPath(string root, string sessionId)
    {
        var safe = new string(sessionId.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        return Path.Combine(KeysDir(root), safe + ".key");
    }

    /// <summary>Returns the session key, creating it on first use.</summary>
    public static byte[] GetOrCreateKey(string root, string sessionId)
    {
        var existing = TryGetKey(root, sessionId);
        if (existing is not null)
        {
            return existing;
        }

        var path = KeyPath(root, sessionId);
        Directory.CreateDirectory(KeysDir(root));
        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        try
        {
            // CreateNew: if another process created the key first, use theirs.
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(Encoding.ASCII.GetBytes(Convert.ToBase64String(key)));
        }
        catch (IOException) when (File.Exists(path))
        {
            return TryGetKey(root, sessionId)
                ?? throw new StorageException($"could not read encryption key for {sessionId}");
        }

        return key;
    }

    public static byte[]? TryGetKey(string root, string sessionId)
    {
        var path = KeyPath(root, sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        var key = Convert.FromBase64String(File.ReadAllText(path).Trim());
        return key.Length == KeyBytes ? key : null;
    }

    /// <summary>Deletes the session key. Returns false if the session had no key.</summary>
    public static bool DeleteKey(string root, string sessionId)
    {
        var path = KeyPath(root, sessionId);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    public static JsonObject Encrypt(byte[] key, string eventId, string sessionId, JsonNode plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext.ToJsonString());
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plain, cipher, tag, Aad(eventId, sessionId));

        return new JsonObject
        {
            [Marker] = new JsonObject
            {
                ["v"] = 1,
                ["n"] = Convert.ToBase64String(nonce),
                ["c"] = Convert.ToBase64String(cipher),
                ["t"] = Convert.ToBase64String(tag),
            },
        };
    }

    public static bool IsEncrypted(JsonNode? payload) =>
        payload is JsonObject obj && obj.Count == 1 && obj.ContainsKey(Marker);

    /// <summary>Decrypts an envelope produced by <see cref="Encrypt"/>. Throws on tampering.</summary>
    public static JsonNode Decrypt(byte[] key, string eventId, string sessionId, JsonNode envelope)
    {
        var inner = envelope[Marker] as JsonObject
            ?? throw new System.Security.Cryptography.CryptographicException("not an encrypted payload");
        var nonce = Convert.FromBase64String(inner["n"]!.GetValue<string>());
        var cipher = Convert.FromBase64String(inner["c"]!.GetValue<string>());
        var tag = Convert.FromBase64String(inner["t"]!.GetValue<string>());
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(nonce, cipher, tag, plain, Aad(eventId, sessionId));
        return JsonNode.Parse(plain) ?? new JsonObject();
    }

    /// <summary>What readers see for a payload that cannot be decrypted.</summary>
    public static JsonObject Placeholder(string reason) => new() { ["unreadable"] = reason };

    // Binding the ciphertext to its event and session stops rows being swapped between events.
    private static byte[] Aad(string eventId, string sessionId) => Encoding.UTF8.GetBytes($"{eventId}\n{sessionId}");
}
