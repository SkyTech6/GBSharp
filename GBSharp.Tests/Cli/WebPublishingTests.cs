using GBSharp.Cli.Publishing;

namespace GBSharp.Tests.Cli;

/// <summary>
/// What a published web page is made of.
/// </summary>
/// <remarks>
/// The runtime and the player assets are stood in for here, because what these
/// pin is the assembly: which files come out, and whether the single file
/// really is single. Whether the wasm inside it emulates correctly is checked
/// where that can be answered, which is the emulator repository's own CI
/// running blargg's ROMs through it under node.
/// </remarks>
public sealed class WebPublishingTests
{
    private sealed record Layout(string Runtime, string Assets, string Output);

    private static Layout CreateLayout()
    {
        string root = Path.Combine(Path.GetTempPath(), "gbsharp-web", Guid.NewGuid().ToString("N"));

        string runtime = Path.Combine(root, "runtime");
        string assets = Path.Combine(root, "assets");
        string output = Path.Combine(root, "out");

        Directory.CreateDirectory(runtime);
        Directory.CreateDirectory(assets);

        // Stand-ins with the shape that matters: modules that import each other
        // by relative path, and a wasm file that is not text.
        File.WriteAllText(Path.Combine(runtime, "gbsharp-runtime.js"), "export const abi = 1;\n");
        File.WriteAllText(
            Path.Combine(runtime, "gbsharp.js"),
            "import { abi } from './gbsharp-runtime.js';\nexport default async () => ({ abi });\n");
        File.WriteAllBytes(Path.Combine(runtime, "gbsharp.wasm"), [0x00, 0x61, 0x73, 0x6D, 1, 0, 0, 0]);

        File.WriteAllText(
            Path.Combine(assets, "player.js"),
            "import { abi } from './gbsharp-runtime.js';\nexport class WebPlayer {}\n");
        File.WriteAllText(
            Path.Combine(assets, "index.html"),
            "<!DOCTYPE html><title>{{TITLE}}</title><body><h1>{{TITLE}}</h1>\n" +
            "<script type=\"module\">\nconst config = {{CONFIG}};\nimport './player.js';\n</script>\n");

        return new Layout(runtime, assets, output);
    }

    private static byte[] Rom => [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

    [Fact]
    public void TheFolderLayoutCarriesEveryFileThePageAsksFor()
    {
        Layout layout = CreateLayout();

        string page = WebPublisher.Write(
            layout.Runtime, layout.Assets, layout.Output, Rom, "My Game", "{}", singleFile: false);

        Assert.Equal("index.html", Path.GetFileName(page));

        foreach (string expected in new[]
                 { "index.html", "player.js", "gbsharp.js", "gbsharp-runtime.js", "gbsharp.wasm", "game.gb" })
        {
            Assert.True(
                File.Exists(Path.Combine(layout.Output, expected)),
                $"{expected} is missing, so the page would 404 on it");
        }

        Assert.Equal(Rom, File.ReadAllBytes(Path.Combine(layout.Output, "game.gb")));
    }

    [Fact]
    public void TheSingleFileIsActuallySingle()
    {
        Layout layout = CreateLayout();

        string page = WebPublisher.Write(
            layout.Runtime, layout.Assets, layout.Output, Rom, "My Game", "{}", singleFile: true);

        // Nothing beside it, because the point of this mode is being one file
        // somebody can send to somebody else.
        Assert.Equal([page], Directory.GetFiles(layout.Output));
        Assert.EndsWith(".html", page);

        string html = File.ReadAllText(page);

        // The wasm and the ROM are in the page rather than beside it.
        Assert.Contains("id=\"wasm\"", html);
        Assert.Contains("id=\"rom\"", html);
        Assert.Contains(Convert.ToBase64String(Rom), html);

        // And the modules resolve to blobs, so nothing is fetched by path.
        Assert.Contains("importmap", html);
        Assert.Contains("createObjectURL", html);
    }

    [Fact]
    public void InlinedModulesCannotCloseTheScriptTagTheyAreInside()
    {
        Layout layout = CreateLayout();

        // A module containing this text would end the script element early and
        // spill the rest of itself into the page as markup.
        File.WriteAllText(
            Path.Combine(layout.Assets, "player.js"),
            "export const trouble = '</script><h1>escaped</h1>';\n");

        string page = WebPublisher.Write(
            layout.Runtime, layout.Assets, layout.Output, Rom, "My Game", "{}", singleFile: true);

        string html = File.ReadAllText(page);

        Assert.DoesNotContain("<h1>escaped</h1>", html);
        Assert.Contains("\\u003c/script", html);
    }

    [Fact]
    public void ATitleCannotInjectMarkup()
    {
        Layout layout = CreateLayout();

        string page = WebPublisher.Write(
            layout.Runtime,
            layout.Assets,
            layout.Output,
            Rom,
            "<script>alert(1)</script>",
            "{}",
            singleFile: false);

        string html = File.ReadAllText(page);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void TheSettingsReachThePage()
    {
        Layout layout = CreateLayout();

        WebPublisher.Write(
            layout.Runtime,
            layout.Assets,
            layout.Output,
            Rom,
            "My Game",
            "{\"title\":\"My Game\",\"scale\":4}",
            singleFile: false);

        string html = File.ReadAllText(Path.Combine(layout.Output, "index.html"));

        Assert.Contains("\"scale\":4", html);
        Assert.DoesNotContain("{{CONFIG}}", html);
        Assert.DoesNotContain("{{TITLE}}", html);
    }

    [Fact]
    public void ATitleThatIsNotAFileNameStillProducesAFile()
    {
        Layout layout = CreateLayout();

        string page = WebPublisher.Write(
            layout.Runtime, layout.Assets, layout.Output, Rom, "Where/Are: My *Saves?", "{}",
            singleFile: true);

        Assert.True(File.Exists(page));
        Assert.DoesNotContain(Path.GetFileName(page), new[] { "/", ":", "*" });
    }
}
