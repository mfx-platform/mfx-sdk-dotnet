using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace MfxHub;

/// <summary>
/// The four headers every MFX Hub request carries, and the exact message their signature covers.
/// </summary>
/// <remarks>
/// <para>
/// A request is signed, not bearing a token. <c>X-MFX-Key</c> names the key. <c>X-MFX-Timestamp</c>
/// and <c>X-MFX-Nonce</c> make each request usable once. <c>X-MFX-Signature</c> is the base64
/// Ed25519 signature over <c>timestamp_ms\nnonce\nMETHOD\npath_with_query\nsha256_hex(body)</c>.
/// </para>
/// <para>
/// The timestamp is epoch milliseconds and must be within 30 s of the platform's clock. The nonce
/// must be unique per key for five minutes. It is inside the signature, and it is one line of the
/// signed message, so it is restricted to <c>^[A-Za-z0-9_-]{16,64}$</c>: a nonce with a newline in
/// it could move the line boundaries and sign one request as another. A retry is therefore a new
/// signature, never the same one sent twice.
/// </para>
/// </remarks>
public static partial class Signing
{
    /// <summary>The header naming the key.</summary>
    public const string KeyHeader = "X-MFX-Key";

    /// <summary>The header carrying the request's time, in epoch milliseconds.</summary>
    public const string TimestampHeader = "X-MFX-Timestamp";

    /// <summary>The header carrying the request's single-use nonce.</summary>
    public const string NonceHeader = "X-MFX-Nonce";

    /// <summary>The header carrying the base64 Ed25519 signature.</summary>
    public const string SignatureHeader = "X-MFX-Signature";

    [GeneratedRegex("^[A-Za-z0-9_-]{16,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex NonceShape();

    /// <summary>
    /// The exact bytes an MFX Hub signature covers. One place, so the client and its tests agree.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The nonce is not 16 to 64 characters of <c>A-Z a-z 0-9 _ -</c>. It is refused here rather
    /// than sent, because the platform answers a bad nonce with 401, which says nothing about why.
    /// </exception>
    public static byte[] Message(
        string timestamp, string nonce, string method, string pathWithQuery, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(pathWithQuery);
        if (!NonceShape().IsMatch(nonce))
        {
            throw new ArgumentException(
                $"a nonce is 16 to 64 characters of A-Z a-z 0-9 _ - ; got {nonce.Length} characters. "
                + "It is a line of the signed message, so anything else could move the line boundaries.",
                nameof(nonce));
        }
        var digest = Convert.ToHexStringLower(SHA256.HashData(body));
        return Encoding.UTF8.GetBytes(string.Join(
            '\n', timestamp, nonce, method.ToUpperInvariant(), pathWithQuery, digest));
    }
}

/// <summary>
/// A tenant's Ed25519 private key. It renders as <c>***</c> wherever it could be printed, and it is
/// never serialised.
/// </summary>
/// <remarks>
/// The platform returns the private key once, when the key is issued, and never stores it. This
/// type never writes it anywhere: <see cref="ToString"/> is <c>***</c>, and serialising it with
/// System.Text.Json throws.
/// </remarks>
[JsonConverter(typeof(NeverSerialised))]
public sealed class SigningKey
{
    private const int SeedBytes = 32;

    private readonly Ed25519PrivateKeyParameters _key;

    private SigningKey(Ed25519PrivateKeyParameters key) => _key = key;

    /// <summary>A key from its 32 raw private bytes.</summary>
    /// <exception cref="ArgumentException">The seed is not 32 bytes.</exception>
    public static SigningKey FromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedBytes)
        {
            throw new ArgumentException(
                $"an Ed25519 private key is {SeedBytes} bytes; this is {seed.Length}", nameof(seed));
        }
        return new SigningKey(new Ed25519PrivateKeyParameters(seed.ToArray(), 0));
    }

    /// <summary>A key from its PEM (PKCS #8) form, or from the base64 of its 32 raw bytes.</summary>
    /// <exception cref="ArgumentException">The text is neither, or is a key of another kind.</exception>
    public static SigningKey Parse(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var text = key.Trim();
        if (text.StartsWith("-----BEGIN", StringComparison.Ordinal))
        {
            return FromPem(text);
        }
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            throw new ArgumentException(
                "a signing key is a PEM private key or the base64 of its 32 raw bytes", nameof(key));
        }
        return FromSeed(raw);
    }

    private static SigningKey FromPem(string pem)
    {
        var fields = PemEncoding.Find(pem);
        var label = pem[fields.Label];
        if (label != "PRIVATE KEY")
        {
            throw new ArgumentException($"an MFX key is a PKCS #8 PRIVATE KEY, not {label}", nameof(pem));
        }
        var der = Convert.FromBase64String(pem[fields.Base64Data]);
        return PrivateKeyFactory.CreateKey(der) is Ed25519PrivateKeyParameters ed25519
            ? new SigningKey(ed25519)
            : throw new ArgumentException("an MFX key is Ed25519", nameof(pem));
    }

    /// <summary>The base64 Ed25519 signature of <paramref name="message"/>.</summary>
    public string Sign(ReadOnlySpan<byte> message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, _key);
        signer.BlockUpdate(message);
        return Convert.ToBase64String(signer.GenerateSignature());
    }

    /// <summary>The 32-byte public key, which the platform holds to verify signatures.</summary>
    public byte[] PublicKey() => _key.GeneratePublicKey().GetEncoded();

    /// <summary>Always <c>***</c>: a key cannot reach a log by being printed.</summary>
    public override string ToString() => "***";

    private sealed class NeverSerialised : JsonConverter<SigningKey>
    {
        public override SigningKey Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException(
                "a SigningKey is never deserialised; load it from your own secret store with SigningKey.Parse");

        public override void Write(Utf8JsonWriter writer, SigningKey value, JsonSerializerOptions options) =>
            throw new NotSupportedException(
                "a SigningKey is never serialised; load it from your own secret store");
    }
}

/// <summary>
/// Signs each request with a fresh timestamp and nonce. The client signs every attempt through here,
/// and so will the stream's WebSocket upgrade, so both put the same bytes under the same signature.
/// </summary>
public sealed class RequestSigner
{
    private readonly SigningKey _key;
    private readonly TimeProvider _clock;
    private readonly Func<string> _nonce;

    /// <summary>A signer for <paramref name="keyId"/>.</summary>
    /// <param name="keyId">The key's id, sent as <c>X-MFX-Key</c>.</param>
    /// <param name="key">The private key.</param>
    /// <param name="clock">The clock the timestamp is read from; the system clock by default.</param>
    /// <param name="nonce">Makes each nonce; 16 random bytes in base64url by default.</param>
    public RequestSigner(string keyId, SigningKey key, TimeProvider? clock = null, Func<string>? nonce = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentNullException.ThrowIfNull(key);
        KeyId = keyId;
        _key = key;
        _clock = clock ?? TimeProvider.System;
        _nonce = nonce ?? NewNonce;
    }

    /// <summary>The key's id.</summary>
    public string KeyId { get; }

    /// <summary>The four headers for one request, with a fresh timestamp and nonce.</summary>
    public IReadOnlyDictionary<string, string> HeadersFor(
        string method, string pathWithQuery, ReadOnlySpan<byte> body)
    {
        var timestamp = _clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var nonce = _nonce();
        return new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
        {
            [Signing.KeyHeader] = KeyId,
            [Signing.TimestampHeader] = timestamp,
            [Signing.NonceHeader] = nonce,
            [Signing.SignatureHeader] = _key.Sign(Signing.Message(timestamp, nonce, method, pathWithQuery, body)),
        };
    }

    /// <inheritdoc/>
    public override string ToString() => $"RequestSigner(KeyId={KeyId}, key=***)";

    private static string NewNonce() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
}
