using Maus.Core.Engine.Broker;

namespace Maus.Core.Tests.Engine;

/// <summary>Signature des messages du canal privé et refus des demandes rejouées (anti-rejeu, 29/10/2026).</summary>
public sealed class EngineKeyTests
{
    private static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Fact]
    public void A_request_signed_with_the_key_is_accepted_and_any_change_is_refused()
    {
        var key = EngineKey.Create();
        var signed = key.SignRequest(Utf8("{\"op\":\"ping\",\"language\":\"fr\",\"sequence\":1}"));

        Assert.True(key.VerifyRequest(signed));
        Assert.Equal(EngineKey.MacSize, signed.Mac.Length);
        Assert.False(key.VerifyRequest(signed with { Body = Utf8("{\"op\":\"audit\",\"language\":\"fr\",\"sequence\":1}") }));
        var flipped = (byte[])signed.Mac.Clone();
        flipped[0] ^= 1;
        Assert.False(key.VerifyRequest(signed with { Mac = flipped }));
        Assert.False(key.VerifyRequest(signed with { Mac = [] }));
        Assert.False(key.VerifyRequest(signed with { Mac = signed.Mac[..31] }));
        Assert.False(EngineKey.Create().VerifyRequest(signed));
    }

    [Fact]
    public void A_response_is_valid_only_for_its_own_request_and_never_as_a_request()
    {
        var key = EngineKey.Create();
        var response = key.SignResponse(7, Utf8("{\"kind\":\"result\"}"));

        Assert.True(key.VerifyResponse(7, response));
        Assert.False(key.VerifyResponse(8, response));
        Assert.False(key.VerifyRequest(response));
        Assert.False(key.VerifyResponse(7, key.SignRequest(response.Body)));
    }

    [Fact]
    public void The_key_travels_as_exactly_32_bytes_and_nothing_else_is_accepted()
    {
        var key = EngineKey.Create();
        var secret = key.Introduction().Key!;

        Assert.Equal(EngineKey.Size, secret.Length);
        Assert.Equal(key.Fingerprint(), EngineKey.Parse(secret)!.Fingerprint());
        Assert.NotEqual(key.Fingerprint(), EngineKey.Create().Fingerprint());
        Assert.Null(EngineKey.Parse(null));
        Assert.Null(EngineKey.Parse([]));
        Assert.Null(EngineKey.Parse(secret[..31]));
        Assert.Null(EngineKey.Parse([.. secret, 0]));
        Assert.Throws<ArgumentException>(() => new EngineKey(new byte[16]));
    }

    [Fact]
    public void Only_the_fingerprint_goes_on_the_command_line_and_it_matches_only_its_own_key()
    {
        var key = EngineKey.Create();
        var fingerprint = key.Fingerprint();

        Assert.True(EngineKey.IsFingerprint(fingerprint));
        Assert.NotEqual(Convert.ToHexStringLower(key.Introduction().Key!), fingerprint);
        Assert.True(key.HasFingerprint(fingerprint));
        Assert.False(EngineKey.Create().HasFingerprint(fingerprint));
        Assert.False(EngineKey.IsFingerprint(fingerprint.ToUpperInvariant()));
        Assert.False(EngineKey.IsFingerprint("MAUS"));

        // La présentation porte la clé elle-même, et rien d'autre.
        var introduction = key.Introduction();
        Assert.Empty(introduction.Body);
        Assert.Empty(introduction.Mac);
        Assert.True(EngineKey.Parse(introduction.Key)!.HasFingerprint(fingerprint));

        // Une présentation n'est jamais prise pour un message signé.
        Assert.False(key.VerifyRequest(introduction));
        Assert.False(key.VerifyResponse(0, introduction));
    }

    [Fact]
    public void Each_sequence_number_is_accepted_once_even_out_of_order()
    {
        var window = new ReplayWindow(size: 8);

        Assert.True(window.TryAccept(1));
        Assert.True(window.TryAccept(3));
        Assert.True(window.TryAccept(2));
        Assert.False(window.TryAccept(2));
        Assert.False(window.TryAccept(3));
        Assert.False(window.TryAccept(0));
        Assert.False(window.TryAccept(-5));
    }

    [Fact]
    public void A_number_older_than_the_window_is_refused_even_if_never_seen()
    {
        var window = new ReplayWindow(size: 8);

        Assert.True(window.TryAccept(20));
        Assert.True(window.TryAccept(13));
        Assert.False(window.TryAccept(12));
        Assert.False(window.TryAccept(1));
        Assert.True(window.TryAccept(21));
        Assert.False(window.TryAccept(20));
    }
}
