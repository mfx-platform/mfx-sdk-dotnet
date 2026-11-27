using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace MfxHub.Sdk.Tests;

/// <summary>
/// Every MFX Hub request is signed with Ed25519 over the API's canonical message, and the key that
/// signs it never reaches a log or a file.
/// </summary>
public sealed class SigningTests
{
    /// <summary>The API description's test key: the 32 bytes 00 to 1f. Public on purpose.</summary>
    internal static readonly byte[] TestSeed = Enumerable.Range(0, 32).Select(b => (byte)b).ToArray();

    internal const string TestPem = """
        -----BEGIN PRIVATE KEY-----
        MC4CAQAwBQYDK2VwBCIEIAABAgMEBQYHCAkKCwwNDg8QERITFBUWFxgZGhscHR4f
        -----END PRIVATE KEY-----
        """;

    internal static bool Verifies(byte[] publicKey, byte[] message, string signature)
    {
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
        verifier.BlockUpdate(message, 0, message.Length);
        return verifier.VerifySignature(Convert.FromBase64String(signature));
    }

    /// <summary>
    /// Given the API's published signing test vector / When its message is signed with the test key
    /// / Then the signature is the published one, byte for byte.
    /// </summary>
    [Fact]
    public void The_published_test_vector_is_reproduced_byte_for_byte()
    {
        var message = Signing.Message(
            "1790000000000",
            "3f0c9a4e5b7d4c1e9a2b6d8f0a1c2e3f",
            "post",
            "/v1/terminals?dry_run=false",
            Encoding.UTF8.GetBytes("""{"farm_id":"00000000-0000-0000-0000-000000000001"}"""));

        Assert.Equal(
            "OLOeJhRskf6kvodIyvrrpI/Hwlg3MXn//TTc64zpcl0qACk3BOhNfMV8yrVthZUdUXlOCntJAIrfgHTNK6faDg==",
            SigningKey.FromSeed(TestSeed).Sign(message));
        Assert.StartsWith("1790000000000\n3f0c9a4e5b7d4c1e9a2b6d8f0a1c2e3f\nPOST\n", Encoding.UTF8.GetString(message));
    }

    /// <summary>
    /// Given a tenant's key / When it is printed, interpolated or serialised / Then it is *** or a
    /// refusal, never the key; and it loads from PEM or the base64 of its 32 bytes, the same key
    /// either way, while any other text or length is refused.
    /// </summary>
    [Fact]
    public void A_key_is_never_printed_or_serialised_and_loads_from_pem_or_base64()
    {
        var key = SigningKey.Parse(TestPem);

        Assert.Equal("***", key.ToString());
        Assert.Equal("key ***", $"key {key}");
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(key));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<SigningKey>("{}"));
        Assert.DoesNotContain("AAECAwQ", new RequestSigner("mfxk_1", key).ToString(), StringComparison.Ordinal);

        var fromBase64 = SigningKey.Parse(Convert.ToBase64String(TestSeed));
        Assert.Equal(SigningKey.FromSeed(TestSeed).PublicKey(), key.PublicKey());
        Assert.Equal(key.PublicKey(), fromBase64.PublicKey());

        Assert.Throws<ArgumentException>(() => SigningKey.Parse("not a key at all"));
        Assert.Throws<ArgumentException>(() => SigningKey.Parse(Convert.ToBase64String(new byte[31])));
        Assert.Throws<ArgumentException>(() => SigningKey.FromSeed(new byte[33]));
        Assert.Throws<ArgumentException>(() => SigningKey.Parse(
            "-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEAA6EHv/POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg=\n-----END PUBLIC KEY-----"));
    }

    /// <summary>
    /// Given a nonce that is short, long, or carries a newline or another character outside
    /// A-Z a-z 0-9 _ - / When a message is built with it / Then it is refused before anything is
    /// signed, because it is a line of the signed message.
    /// </summary>
    [Theory]
    [InlineData("short")]
    [InlineData("0123456789abcdef\nGET")]
    [InlineData("0123456789abcdef 0")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    public void A_nonce_that_could_move_a_line_of_the_message_is_refused(string nonce)
    {
        var refused = Assert.Throws<ArgumentException>(
            () => Signing.Message("1790000000000", nonce, "GET", "/v1/terminals", []));
        Assert.Equal("nonce", refused.ParamName);
    }

    /// <summary>The default nonce fits the shape the platform accepts, and differs every time.</summary>
    [Fact]
    public void The_default_nonce_fits_the_platform_shape_and_is_fresh_each_request()
    {
        var signer = new RequestSigner("mfxk_1", SigningKey.FromSeed(TestSeed));

        var first = signer.HeadersFor("GET", "/v1/terminals", []);
        var second = signer.HeadersFor("GET", "/v1/terminals", []);

        Assert.Matches("^[A-Za-z0-9_-]{16,64}$", first[Signing.NonceHeader]);
        Assert.NotEqual(first[Signing.NonceHeader], second[Signing.NonceHeader]);
    }
}
