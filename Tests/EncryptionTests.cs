using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Data;
using UserIdentityDesktop.Service;
using Xunit;

namespace UserIdentityDesktop.Tests;

public sealed class EncryptionTests
{
    private sealed record Vector(ChatEncryption.Recovery Recovery, ChatEncryption.PublicIdentity PublicIdentity, string Wire, EncryptedFile File, string FileCipher);
    private static Vector Read() => JsonSerializer.Deserialize<Vector>(System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "chat-vector.json")), ApiPayload.Json)!;
    [Fact]
    public void DecryptsAngularJoseVector_AndRejectsSpoofedSenderAndTampering()
    {
        var vector = Read();
        var payload = ChatEncryption.Open(vector.Wire, vector.Recovery, vector.PublicIdentity, vector.Recovery.UserId);
        Assert.Equal("Native chat · مرحباً", payload.Text);
        Assert.Throws<CryptographicException>(() => ChatEncryption.Open(vector.Wire, vector.Recovery, vector.PublicIdentity, Guid.NewGuid()));
        using var doc = JsonDocument.Parse(vector.Wire[4..]);
        var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(doc.RootElement.GetRawText())!;
        var ciphertext = root["ciphertext"].GetString()!;
        root["ciphertext"] = JsonSerializer.SerializeToElement((ciphertext[0] == 'A' ? "B" : "A") + ciphertext[1..]);
        Assert.ThrowsAny<CryptographicException>(() => ChatEncryption.Open("FU1." + JsonSerializer.Serialize(root), vector.Recovery, vector.PublicIdentity, vector.Recovery.UserId));
    }
    [Fact]
    public void NativeEnvelope_RoundTrips_WithPinnedAlgorithmsAndAudience()
    {
        var v = Read();
        var payload = new ChatEncryption.Payload(1, "test", v.Recovery.UserId, v.Recovery.UserId, null, new[] { v.Recovery.UserId }, "Hello", Array.Empty<EncryptedFile>());
        var wire = ChatEncryption.Protect(payload, v.Recovery, new Dictionary<Guid, ChatEncryption.PublicIdentity> { [v.Recovery.UserId] = v.PublicIdentity });
        Assert.Equal("Hello", ChatEncryption.Open(wire, v.Recovery, v.PublicIdentity, v.Recovery.UserId).Text);
    }
    [Fact]
    public void GroupEnvelopeRetainsAuthenticatedConversationAndZeroDirectRecipient()
    {
        var v = Read();
        var payload = new ChatEncryption.Payload(1, "group-test",v.Recovery.UserId,Guid.Empty,"507f1f77bcf86cd799439011",new[] { v.Recovery.UserId },"Group message",Array.Empty<EncryptedFile>());
        var wire = ChatEncryption.Protect(payload,v.Recovery,new Dictionary<Guid,ChatEncryption.PublicIdentity> { [v.Recovery.UserId] = v.PublicIdentity });
        var opened = ChatEncryption.Open(wire,v.Recovery,v.PublicIdentity,v.Recovery.UserId);
        Assert.Equal(Guid.Empty,opened.Receiver); Assert.Equal(payload.ConversationId,opened.ConversationId); Assert.Equal("Group message",opened.Text);
        Assert.Equal(new[] { v.Recovery.UserId },opened.Audience);
    }
    [Fact]
    public async Task DecryptsWebCryptoAttachment_AndDoesNotAcceptModifiedTags()
    {
        var v = Read(); var encrypted = Convert.FromBase64String(v.FileCipher);
        using var source = new MemoryStream(encrypted); using var plain = new MemoryStream();
        await ChatEncryption.TransformFileAsync(source, plain, v.File, false);
        Assert.Equal("hello", Encoding.UTF8.GetString(plain.ToArray()));
        encrypted[^1] ^= 1;
        using var corrupted = new MemoryStream(encrypted); using var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<CryptographicException>(() => ChatEncryption.TransformFileAsync(corrupted, output, v.File, false));
        Assert.Empty(output.ToArray());
    }
    [Theory]
    [InlineData(0)] [InlineData(1048593)]
    public async Task Attachment_StreamsEmptyAndMultipleFrames(int size)
    {
        var v = Read(); var metadata = v.File with { Size = size };
        var bytes = RandomNumberGenerator.GetBytes(size);
        using var source = new MemoryStream(bytes); using var encrypted = new MemoryStream();
        await ChatEncryption.TransformFileAsync(source, encrypted, metadata, true);
        encrypted.Position = 0; using var result = new MemoryStream();
        await ChatEncryption.TransformFileAsync(encrypted, result, metadata, false);
        Assert.Equal(bytes, result.ToArray());
    }
}
