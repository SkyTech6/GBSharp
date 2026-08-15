using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using GBSharp.Cli;
using GBSharp.Cli.Publishing;

namespace GBSharp.Tests.Cli;

/// <summary>
/// The format a published game is written in.
/// </summary>
/// <remarks>
/// The reader is <c>player/payload.c</c>, in C, in another repository. Nothing
/// but these tests and that file's own comments keep the two halves agreeing, so
/// the layout is pinned here field by field rather than round-tripped through
/// the writer alone, which would pass just as happily if both sides moved.
/// </remarks>
public sealed class PublishingTests
{
    private static string WriteStub(string contents)
    {
        string directory = Path.Combine(Path.GetTempPath(), "gbsharp-publish", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, "stub.bin");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void ThePayloadIsAppendedAfterTheStubWithoutDisturbingIt()
    {
        const string stubContents = "this stands in for the player executable";
        string stub = WriteStub(stubContents);
        string output = Path.Combine(Path.GetDirectoryName(stub)!, "game.bin");

        byte[] rom = [.. Enumerable.Range(0, 512).Select(i => (byte)i)];

        GamePayload.Write(stub, output, rom, "{\"title\":\"Game\"}");

        byte[] published = File.ReadAllBytes(output);

        // The stub has to survive byte for byte, because the operating system
        // is going to load it as an executable and every offset in its headers
        // is relative to where it starts.
        Assert.Equal(stubContents, Encoding.UTF8.GetString(published[..stubContents.Length]));
        Assert.Equal(rom, published[stubContents.Length..(stubContents.Length + rom.Length)]);
    }

    [Fact]
    public void TheTrailerSaysWhereEverythingIs()
    {
        string stub = WriteStub("stub");
        string output = Path.Combine(Path.GetDirectoryName(stub)!, "game.bin");

        byte[] rom = [1, 2, 3, 4, 5, 6, 7, 8];
        const string config = "{\"scale\":2}";

        GamePayload.Write(stub, output, rom, config);

        byte[] published = File.ReadAllBytes(output);
        ReadOnlySpan<byte> trailer = published.AsSpan(published.Length - GamePayload.TrailerSize);

        // Read exactly as payload.c reads it: little endian, at these offsets.
        Assert.Equal(GamePayload.Magic, BinaryPrimitives.ReadUInt32LittleEndian(trailer[0..]));
        Assert.Equal(GamePayload.TrailerVersion, BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]));
        Assert.Equal(4ul, BinaryPrimitives.ReadUInt64LittleEndian(trailer[8..]));
        Assert.Equal((ulong)rom.Length, BinaryPrimitives.ReadUInt64LittleEndian(trailer[16..]));
        Assert.Equal(4ul + (ulong)rom.Length, BinaryPrimitives.ReadUInt64LittleEndian(trailer[24..]));
        Assert.Equal((ulong)config.Length, BinaryPrimitives.ReadUInt64LittleEndian(trailer[32..]));
        Assert.Equal(GamePayload.Magic, BinaryPrimitives.ReadUInt32LittleEndian(trailer[44..]));

        // The magic at both ends is what lets an unpublished player tell "no
        // payload" from "a payload I cannot read", so the trailing one matters
        // as much as the leading one.
        Assert.Equal(48, GamePayload.TrailerSize);
    }

    [Fact]
    public void TheChecksumCoversTheRomAndTheSettings()
    {
        string stub = WriteStub("stub");
        string output = Path.Combine(Path.GetDirectoryName(stub)!, "game.bin");

        byte[] rom = [9, 8, 7, 6];
        const string config = "{}";

        GamePayload.Write(stub, output, rom, config);

        byte[] published = File.ReadAllBytes(output);
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(
            published.AsSpan(published.Length - GamePayload.TrailerSize + 40));

        uint expected = unchecked(
            GamePayload.Adler32(rom) + GamePayload.Adler32(Encoding.UTF8.GetBytes(config)));

        Assert.Equal(expected, stored);
    }

    /// <summary>
    /// Adler-32 against the values from RFC 1950, so a rewrite of the loop
    /// cannot quietly produce a checksum only this codebase agrees with.
    /// </summary>
    [Theory]
    [InlineData("", 0x00000001u)]
    [InlineData("a", 0x00620062u)]
    [InlineData("abc", 0x024d0127u)]
    [InlineData("Wikipedia", 0x11E60398u)]
    public void Adler32MatchesTheStandard(string input, uint expected) =>
        Assert.Equal(expected, GamePayload.Adler32(Encoding.UTF8.GetBytes(input)));

    [Fact]
    public void AGameWithNoPlayerSettingsStillGetsItsOwnName()
    {
        string json = PlayerSettings.Serialize(null, "Metasprite");

        using JsonDocument document = JsonDocument.Parse(json);

        // A window called "GB# Player" tells the person playing nothing about
        // what they are playing, so the project name is the fallback title.
        Assert.Equal("Metasprite", document.RootElement.GetProperty("title").GetString());

        // And nothing else is claimed, so the Player's own defaults apply and
        // can move without changing games that expressed no opinion.
        Assert.Single(document.RootElement.EnumerateObject());
    }

    [Fact]
    public void SettingsTheGameStatedAreCarriedThrough()
    {
        var settings = new PlayerSettings
        {
            Title = "My Game",
            Scale = 4,
            Fullscreen = true,
            IntegerScaling = false,
            Volume = 70,
        };

        using JsonDocument document = JsonDocument.Parse(PlayerSettings.Serialize(settings, "Ignored"));

        Assert.Equal("My Game", document.RootElement.GetProperty("title").GetString());
        Assert.Equal(4, document.RootElement.GetProperty("scale").GetInt32());
        Assert.True(document.RootElement.GetProperty("fullscreen").GetBoolean());
        Assert.False(document.RootElement.GetProperty("integerScaling").GetBoolean());
        Assert.Equal(70, document.RootElement.GetProperty("volume").GetInt32());

        // Not mentioned, so not written.
        Assert.False(document.RootElement.TryGetProperty("resizable", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void AWindowSizeNobodyCouldWantIsRejected(int scale)
    {
        var project = new GameProject { Player = new PlayerSettings { Scale = scale } };

        Assert.False(project.Validate(out IReadOnlyList<GBSharp.Compiler.Diagnostics.GBDiagnostic> diagnostics));
        Assert.Contains(diagnostics, d => d.Message.Contains("player.scale"));
    }

    [Fact]
    public void AVolumeOutsideTheDialIsRejected()
    {
        var project = new GameProject { Player = new PlayerSettings { Volume = 150 } };

        Assert.False(project.Validate(out IReadOnlyList<GBSharp.Compiler.Diagnostics.GBDiagnostic> diagnostics));
        Assert.Contains(diagnostics, d => d.Message.Contains("player.volume"));
    }

    [Fact]
    public void EveryPublishTargetHasAPlayerFileName()
    {
        foreach (string rid in PlayerStub.SupportedRids)
        {
            string name = PlayerStub.PlayerFileName(rid);

            Assert.Equal(rid.StartsWith("win-", StringComparison.Ordinal), name.EndsWith(".exe"));
        }

        // The machine running the tests has to be one of them, or publishing
        // for the current platform could not work.
        Assert.Contains(PlayerStub.HostRid, PlayerStub.SupportedRids);
    }
}
