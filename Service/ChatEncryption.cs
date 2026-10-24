using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Data;

namespace UserIdentityDesktop.Service;

/// <summary>Native FUCHAT1 JOSE interoperability. Private keys and device grants stay in memory.</summary>
public sealed class ChatEncryption(IAuthService auth, IConfigurationService config)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? deviceToken;
    private Guid deviceOwner;
    private sealed record CachedIdentity(Recovery Value, long Epoch);
    private CachedIdentity? sessionIdentity;
    private long identityEpoch;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (Guid Owner, PublicIdentity Key, DateTime Checked)> publicKeys = new();
    public sealed record Recovery(Guid UserId, string PublicKey, string EncryptionPrivateKey, string SigningPrivateKey);
    public sealed record PublicIdentity(JsonElement EncryptionKey, JsonElement SigningKey);
    public sealed record PublicResponse(Guid UserId, string PublicKey);
    public sealed record DeviceGrant(string DeviceToken);
    public sealed record Payload(int Version, string Nonce, Guid Sender, Guid Receiver, string? ConversationId,
        Guid[] Audience, string? Text, EncryptedFile[] Files);
    public sealed record PreparedFile(string Path, EncryptedFile Metadata);
    public void Lock() { Interlocked.Increment(ref identityEpoch); deviceToken = null; deviceOwner = Guid.Empty; sessionIdentity = null; publicKeys.Clear(); }

    private async Task<T> Request<T>(string path, object? body = null)
    {
        var origin = new Uri(config.ServerUrl);
        if (origin.Scheme != "https" && !origin.IsLoopback) throw new InvalidOperationException("Encrypted chat requires an HTTPS server connection.");
        using var client = auth.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, config.ServerUrl.TrimEnd('/') + "/api/Chat/Encryption/" + path);
        if (deviceOwner == auth.CurrentUser?.Id && deviceToken != null) request.Headers.Add("X-Chat-Device", deviceToken);
        if (body != null) request.Content = JsonContent.Create(body, options: ApiPayload.Json);
        using var response = await client.SendAsync(request);
        return await ApiPayload.ReadAsync<T>(response);
    }

    public async Task UnlockAsync(string pin)
    {
        await gate.WaitAsync();
        try
        {
            var owner = auth.CurrentUser?.Id ?? throw new InvalidOperationException("Sign in first.");
            var grant = await Request<DeviceGrant>("Unlock", new { pin });
            if (auth.CurrentUser?.Id != owner) throw new InvalidOperationException("The session changed.");
            deviceOwner = owner; deviceToken = grant.DeviceToken;
        }
        finally { gate.Release(); }
    }

    public Task<ChatSecurityStatus> StatusAsync() => Request<ChatSecurityStatus>("Status");
    public async Task ChangePinAsync(string action, string pin, string confirmation, string password, string otp)
    {
        await gate.WaitAsync();
        try
        {
            if (action == "Recovery") { await Request<bool>("Pin/Recovery", new { }); return; }
            if (action == "Disable") { await Request<bool>("Pin/Disable", new { pin, password }); Lock(); return; }
            var grant = action switch
            {
                "Set" => await Request<DeviceGrant>("Pin", new { pin, confirmPin = confirmation }),
                "Reset" => await Request<DeviceGrant>("Pin/Reset", new { otp, newPin = pin }),
                _ => throw new InvalidOperationException("Unsupported chat security action.")
            };
            Lock(); deviceOwner = auth.CurrentUser?.Id ?? throw new InvalidOperationException("Sign in first."); deviceToken = grant.DeviceToken;
        }
        finally { gate.Release(); }
    }

    private async Task<Recovery> Identity(Guid owner)
    {
        if (sessionIdentity is { } cached && cached.Value.UserId == owner && cached.Epoch == Volatile.Read(ref identityEpoch) && auth.CurrentUser?.Id == owner) return cached.Value;
        var epoch = Volatile.Read(ref identityEpoch);
        var identity = await Request<Recovery>("Identity");
        if (epoch != Volatile.Read(ref identityEpoch)) throw new InvalidOperationException("Chat was locked during this operation.");
        if (identity.UserId != owner || auth.CurrentUser?.Id != owner) throw new CryptographicException("The chat identity does not match this session.");
        sessionIdentity = new CachedIdentity(identity, epoch); return identity;
    }

    private async Task<PublicIdentity> Public(Guid id, Guid owner)
    {
        if (publicKeys.TryGetValue(id, out var cached) && cached.Owner == owner && DateTime.UtcNow - cached.Checked < TimeSpan.FromMinutes(5)) return cached.Key;
        var response = await Request<PublicResponse>("Public/" + id);
        if (response.UserId != id) throw new CryptographicException("Unexpected chat identity.");
        var identity = JsonSerializer.Deserialize<PublicIdentity>(response.PublicKey, ApiPayload.Json)!;
        // Public key pins survive restarts. Never persist a private key or unlock credential.
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            identity.EncryptionKey.GetProperty("n").GetString() + ":" + identity.EncryptionKey.GetProperty("e").GetString() + ":" +
            identity.SigningKey.GetProperty("x").GetString() + ":" + identity.SigningKey.GetProperty("y").GetString())));
        var server = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config.ServerUrl.TrimEnd('/'))));
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UserIdentityDesktop", "identity-pins", server, owner.ToString("D"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id.ToString("D") + ".pin");
        if (File.Exists(path))
        {
            if (await File.ReadAllTextAsync(path) != fingerprint) throw new CryptographicException("A chat identity changed. Verify the account identity before continuing.");
        }
        else await File.WriteAllTextAsync(path, fingerprint);
        if (publicKeys.Count >= 128) publicKeys.Clear();
        publicKeys[id] = (owner, identity, DateTime.UtcNow);
        return identity;
    }

    public async Task<string> EncryptAsync(Guid receiver, string? text, EncryptedFile[]? files = null, string? conversationId = null, Guid[]? participants = null)
    {
        await gate.WaitAsync();
        try
        {
            var owner = auth.CurrentUser?.Id ?? throw new InvalidOperationException("Sign in first.");
            var identity = await Identity(owner);
            var audience = (conversationId == null ? new[] { owner, receiver } : participants ?? throw new InvalidOperationException("Conversation membership is unavailable.")).Distinct().OrderBy(x => x.ToString("D"), StringComparer.Ordinal).ToArray();
            var keys = new Dictionary<Guid, PublicIdentity>();
            foreach (var id in audience) keys[id] = await Public(id, owner);
            if (auth.CurrentUser?.Id != owner) throw new InvalidOperationException("The session changed.");
            return await Task.Run(() => Protect(new Payload(1, Guid.NewGuid().ToString("D"), owner, receiver, conversationId, audience, text, files ?? Array.Empty<EncryptedFile>()), identity, keys));
        }
        finally { gate.Release(); }
    }

    public static string Protect(Payload payload, Recovery identity, IReadOnlyDictionary<Guid, PublicIdentity> audience)
    {
        using var signing = ECDsa.Create(); signing.ImportPkcs8PrivateKey(Convert.FromBase64String(identity.SigningPrivateKey), out _);
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = payload.Sender.ToString("D"), typ = "FUCHAT1" }));
        var body = Encode(JsonSerializer.SerializeToUtf8Bytes(payload, ApiPayload.Json));
        var input = header + "." + body;
        var signed = input + "." + Encode(signing.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var protectedHeader = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RSA-OAEP-256", enc = "A256GCM", typ = "FUCHAT1" }));
        var cek = RandomNumberGenerator.GetBytes(32); var iv = RandomNumberGenerator.GetBytes(12);
        try
        {
            var plain = Encoding.UTF8.GetBytes(signed); var ciphertext = new byte[plain.Length]; var tag = new byte[16];
            using var aes = new AesGcm(cek, 16); aes.Encrypt(iv, plain, ciphertext, tag, Encoding.ASCII.GetBytes(protectedHeader));
            var recipients = audience.Select(pair =>
            {
                using var rsa = ImportRsa(pair.Value.EncryptionKey);
                return new { header = new { kid = pair.Key.ToString("D") }, encrypted_key = Encode(rsa.Encrypt(cek, RSAEncryptionPadding.OaepSHA256)) };
            }).ToArray();
            return "FU1." + JsonSerializer.Serialize(new { @protected = protectedHeader, recipients, iv = Encode(iv), ciphertext = Encode(ciphertext), tag = Encode(tag) });
        }
        finally { CryptographicOperations.ZeroMemory(cek); }
    }

    public static Payload Open(string wire, Recovery identity, PublicIdentity author, Guid expectedSender)
    {
        if (!wire.StartsWith("FU1.", StringComparison.Ordinal)) throw new CryptographicException("Unknown encrypted message format.");
        using var envelope = JsonDocument.Parse(wire[4..]); var root = envelope.RootElement;
        var protectedHeader = root.GetProperty("protected").GetString()!;
        using var outerHeader = JsonDocument.Parse(Decode(protectedHeader));
        var h = outerHeader.RootElement;
        if (h.GetProperty("alg").GetString() != "RSA-OAEP-256" || h.GetProperty("enc").GetString() != "A256GCM" || h.GetProperty("typ").GetString() != "FUCHAT1") throw new CryptographicException("Unexpected encryption algorithms.");
        var recipient = root.GetProperty("recipients").EnumerateArray().Single(x => x.GetProperty("header").GetProperty("kid").GetString() == identity.UserId.ToString("D"));
        using var rsa = RSA.Create(); rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(identity.EncryptionPrivateKey), out _);
        var cek = rsa.Decrypt(Decode(recipient.GetProperty("encrypted_key").GetString()!), RSAEncryptionPadding.OaepSHA256);
        try
        {
            var encrypted = Decode(root.GetProperty("ciphertext").GetString()!); var plain = new byte[encrypted.Length];
            using var aes = new AesGcm(cek, 16);
            aes.Decrypt(Decode(root.GetProperty("iv").GetString()!), encrypted, Decode(root.GetProperty("tag").GetString()!), plain, Encoding.ASCII.GetBytes(protectedHeader));
            var signed = Encoding.UTF8.GetString(plain).Split('.');
            if (signed.Length != 3) throw new CryptographicException("Invalid signed message.");
            using var signatureHeader = JsonDocument.Parse(Decode(signed[0])); var sh = signatureHeader.RootElement;
            if (sh.GetProperty("alg").GetString() != "ES256" || sh.GetProperty("typ").GetString() != "FUCHAT1" || sh.GetProperty("kid").GetGuid() != expectedSender) throw new CryptographicException("Invalid message author.");
            using var verify = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = Decode(author.SigningKey.GetProperty("x").GetString()!), Y = Decode(author.SigningKey.GetProperty("y").GetString()!) } });
            if (!verify.VerifyData(Encoding.ASCII.GetBytes(signed[0] + "." + signed[1]), Decode(signed[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new CryptographicException("Invalid message signature.");
            var payload = JsonSerializer.Deserialize<Payload>(Decode(signed[1]), ApiPayload.Json)!;
            if (payload.Version != 1 || payload.Sender != expectedSender || !payload.Audience.Contains(identity.UserId) || payload.Files == null) throw new CryptographicException("Invalid message audience.");
            return payload;
        }
        finally { CryptographicOperations.ZeroMemory(cek); }
    }

    public async Task<RecipientDto> DecryptPreviewAsync(RecipientDto recipient)
    {
        if (recipient.LastMessage?.StartsWith("FU1.", StringComparison.Ordinal) != true) return recipient;
        await gate.WaitAsync();
        try
        {
            var owner = auth.CurrentUser?.Id ?? throw new InvalidOperationException("Sign in first.");
            var epoch = identityEpoch;
            var identity = await Identity(owner);
            var sender = ReadAuthenticatedAuthor(recipient.LastMessage, identity);
            var author = await Public(sender, owner);
            var payload = Open(recipient.LastMessage, identity, author, sender);
            if (auth.CurrentUser?.Id != owner || epoch != identityEpoch) throw new CryptographicException("Session changed.");
            ValidatePreviewDestination(payload, recipient, owner);
            recipient.LastMessage = payload.Text;
            recipient.AttachmentsCount = payload.Files.Length;
            recipient.LastMessageHasAttachments = payload.Files.Length > 0;
        }
        catch { recipient.LastMessage = "Unlock chat to view this message"; recipient.AttachmentsCount = 0; recipient.LastMessageHasAttachments = false; }
        finally { gate.Release(); }
        return recipient;
    }

    public static void ValidatePreviewDestination(Payload payload, RecipientDto recipient, Guid owner)
    {
        if (payload.ConversationId != recipient.ConversationId || !payload.Audience.Contains(owner) ||
            (recipient.ConversationId == null && !((payload.Sender == owner && payload.Receiver == recipient.ReceiverId) ||
                (payload.Sender == recipient.ReceiverId && payload.Receiver == owner))))
            throw new CryptographicException("Preview destination changed.");
        foreach (var file in payload.Files) ValidateFile(file);
    }

    // The author hint is read only after AEAD authentication. Open then verifies its signature and audience.
    private static Guid ReadAuthenticatedAuthor(string wire, Recovery identity)
    {
        using var envelope = JsonDocument.Parse(wire[4..]); var root = envelope.RootElement;
        var header = root.GetProperty("protected").GetString()!;
        var entry = root.GetProperty("recipients").EnumerateArray().Single(x => x.GetProperty("header").GetProperty("kid").GetString() == identity.UserId.ToString("D"));
        using var rsa = RSA.Create(); rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(identity.EncryptionPrivateKey), out _);
        var key = rsa.Decrypt(Decode(entry.GetProperty("encrypted_key").GetString()!), RSAEncryptionPadding.OaepSHA256);
        try
        {
            var cipher = Decode(root.GetProperty("ciphertext").GetString()!); var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Decode(root.GetProperty("iv").GetString()!), cipher, Decode(root.GetProperty("tag").GetString()!), plain, Encoding.ASCII.GetBytes(header));
            try { var signed = Encoding.UTF8.GetString(plain).Split('.'); using var signature = JsonDocument.Parse(Decode(signed[0])); return signature.RootElement.GetProperty("kid").GetGuid(); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public async Task<ChatMessage> DecryptAsync(ChatMessage message)
    {
        if (message.Text?.StartsWith("FU1.", StringComparison.Ordinal) != true) return message;
        await gate.WaitAsync();
        try
        {
            var owner = auth.CurrentUser?.Id ?? throw new InvalidOperationException("Sign in first.");
            var identity = await Identity(owner);
            // Read the signed author only after authenticating the outer encryption in Open.
            var author = await Public(message.Sender, owner);
            var payload = await Task.Run(() => Open(message.Text, identity, author, message.Sender));
            if (payload.Receiver != message.Receiver || payload.ConversationId != message.ConversationId || payload.Files.Length != (message.Attachments?.Count ?? 0)) throw new CryptographicException("Message destination changed.");
            message.IsLocked = false; message.Text = payload.Text;
            for (var i = 0; i < payload.Files.Length; i++)
            {
                var attachment = message.Attachments![i]; var metadata = payload.Files[i];
                ValidateFile(metadata);
                attachment.FileName = Path.GetFileName(metadata.Name.Replace('\\', '/')); attachment.MimeType = metadata.MimeType;
                attachment.FileSize = metadata.Size; attachment.FileExtension = Path.GetExtension(attachment.FileName);
                attachment.Encryption = metadata;
            }
            return message;
        }
        catch { message.IsLocked = true; message.Text = "Unable to unlock or verify this encrypted message. Unlock chat with your PIN if required."; message.Attachments = new(); return message; }
        finally { gate.Release(); }
    }

    public static void ValidateFile(EncryptedFile file)
    {
        if (file.Size < 0 || file.ChunkSize != 1048576 || Convert.FromBase64String(file.Key).Length != 32 || Convert.FromBase64String(file.NoncePrefix).Length != 8 || !Guid.TryParse(file.Id, out _)) throw new CryptographicException("Invalid attachment metadata.");
    }
    public static async Task<PreparedFile> PrepareFileAsync(string path)
    {
        var file = new FileInfo(path);
        var metadata = new EncryptedFile(Guid.NewGuid().ToString("D"), file.Name, ChatService.GetMimeType(file.Extension.ToLowerInvariant()), file.Length, 1048576, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), Convert.ToBase64String(RandomNumberGenerator.GetBytes(8)));
        if (metadata.MimeType.StartsWith("audio/", StringComparison.Ordinal) && file.Length <= 32 * 1024 * 1024)
        {
            try
            {
                var duration = await Media.AudioNotePlayback.DurationAsync(path, CancellationToken.None);
                metadata = metadata with { Waveform = await Media.AudioNotePlayback.WaveformAsync(path, duration, CancellationToken.None) };
            }
            catch (Exception) { /* Optional visual metadata must not prevent an attachment upload. */ }
        }
        var temporary = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".fua");
        try
        {
            await using var source = File.OpenRead(path); await using var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            await TransformFileAsync(source, destination, metadata, encrypt: true);
            return new PreparedFile(temporary, metadata);
        }
        catch { File.Delete(temporary); throw; }
    }
    public static async Task TransformFileAsync(Stream source, Stream destination, EncryptedFile file, bool encrypt, CancellationToken cancellationToken = default)
    {
        ValidateFile(file);
        var key = Convert.FromBase64String(file.Key);
        try
        {
            using var aes = new AesGcm(key, 16);
            var nonce = new byte[12]; Convert.FromBase64String(file.NoncePrefix).CopyTo(nonce, 0);
            var frames = Math.Max(1, file.Size / file.ChunkSize + (file.Size % file.ChunkSize == 0 ? 0 : 1));
            if (frames > uint.MaxValue) throw new CryptographicException("Attachment frame counter exhausted.");
            var plain = new byte[file.ChunkSize]; var cipher = new byte[file.ChunkSize]; var tag = new byte[16];
            for (long index = 0; index < frames; index++)
            {
                var count = (int)Math.Min(file.ChunkSize, file.Size - index * file.ChunkSize);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), (uint)index);
                var aad = JsonSerializer.SerializeToUtf8Bytes(new object[] { "FUATT", 1, file.Id, index, file.Size, file.ChunkSize });
                if (encrypt)
                {
                    await source.ReadExactlyAsync(plain.AsMemory(0, count), cancellationToken);
                    aes.Encrypt(nonce, plain.AsSpan(0, count), cipher.AsSpan(0, count), tag, aad);
                    await destination.WriteAsync(cipher.AsMemory(0, count), cancellationToken); await destination.WriteAsync(tag, cancellationToken);
                }
                else
                {
                    await source.ReadExactlyAsync(cipher.AsMemory(0, count), cancellationToken); await source.ReadExactlyAsync(tag, cancellationToken);
                    aes.Decrypt(nonce, cipher.AsSpan(0, count), tag, plain.AsSpan(0, count), aad);
                    await destination.WriteAsync(plain.AsMemory(0, count), cancellationToken);
                }
            }
            if (source.ReadByte() != -1) throw new CryptographicException("Attachment length changed.");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    private static RSA ImportRsa(JsonElement key)
    {
        var rsa = RSA.Create(); rsa.ImportParameters(new RSAParameters { Modulus = Decode(key.GetProperty("n").GetString()!), Exponent = Decode(key.GetProperty("e").GetString()!) }); return rsa;
    }
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
}
