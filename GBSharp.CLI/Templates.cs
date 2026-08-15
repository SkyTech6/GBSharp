using GBSharp.Assets.Images;

namespace GBSharp.Cli;

/// <summary>The files a new project starts with.</summary>
/// <param name="Files">Relative path to text content.</param>
/// <param name="Binaries">Relative path to bytes, for synthesised placeholder art.</param>
public sealed record TemplateContent(
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyDictionary<string, byte[]> Binaries);

/// <summary>
/// The project templates <c>gbsharp new</c> writes.
/// </summary>
/// <remarks>
/// <para>
/// Held here rather than as <c>dotnet new</c> templates: that would need a second
/// copy of the content and a template engine the CLI cannot assume is installed.
/// A <c>dotnet new</c> package can wrap these later without duplicating them.
/// </para>
/// <para>
/// No template ships a binary. The one that needs art synthesises it with
/// <see cref="PngEncoder"/>, so the templates stay readable in source and there
/// is no checked-in file whose contents nobody can review.
/// </para>
/// </remarks>
public static class Templates
{
    public static readonly string[] Names = ["empty", "sprite", "background"];

    private static readonly Rgba32 White = new(255, 255, 255, 255);
    private static readonly Rgba32 Light = new(170, 170, 170, 255);
    private static readonly Rgba32 Dark = new(85, 85, 85, 255);
    private static readonly Rgba32 Black = new(0, 0, 0, 255);

    public static TemplateContent? Create(string name, string projectName, string target, string? cliProjectDirectory)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gbsharp.json"] = ProjectFile(projectName, target),
            [projectName + ".csproj"] = CsProj(),
            [".gitignore"] = "build/\n",
            [".vscode/tasks.json"] = TasksJson(cliProjectDirectory),
            [".vscode/launch.json"] = LaunchJson(cliProjectDirectory),
        };

        var binaries = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        switch (name.ToLowerInvariant())
        {
            case "empty":
                files["Program.cs"] = Empty;
                break;

            case "sprite":
                files["Program.cs"] = Sprite;
                break;

            case "background":
                files["Program.cs"] = Background;
                binaries["Assets/tiles.png"] = Placeholder();
                break;

            default:
                return null;
        }

        return new TemplateContent(files, binaries);
    }

    private static string ProjectFile(string name, string target) =>
        $$"""
        {
          "name": "{{name}}",
          "target": "{{target}}"
        }
        """;

    /// <summary>
    /// The version a scaffolded project references, taken from this assembly
    /// so the design-time packages always match the tool that wrote the file.
    /// </summary>
    /// <remarks>
    /// Every GB# package versions together (VersionPrefix in
    /// Directory.Build.props), so the CLI's own version is the right one to
    /// stamp: a hardcoded string here would drift the first time the version
    /// bumped, and the mismatch would surface as a restore failure in the
    /// editor of whoever scaffolded next.
    /// </remarks>
    private static string PackageVersion =>
        typeof(Templates).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.1.0";

    /// <summary>
    /// The design-time project, so an editor can bind and analyse the code.
    /// </summary>
    /// <remarks>
    /// Carries no GB# configuration on purpose: everything about the game lives
    /// in gbsharp.json, and duplicating any of it here would create a second
    /// place to disagree. Building this project is an error; 'gbsharp build'
    /// makes the ROM.
    /// </remarks>
    private static string CsProj() =>
        $"""
        <Project Sdk="GBSharp.Sdk/{PackageVersion}">

          <!--
            This project is here so your editor understands your code: completion,
            navigation, and GB# diagnostics as you type.

            It does not build the ROM. Run 'gbsharp build' for that.
          -->

          <ItemGroup>
            <PackageReference Include="GBSharp.Framework" Version="{PackageVersion}" />
            <PackageReference Include="GBSharp.Analyzers" Version="{PackageVersion}" PrivateAssets="all" />
          </ItemGroup>

        </Project>
        """;

    /// <summary>
    /// The CLI project path baked into a new project's VS Code files, as a
    /// forward-slash path so it drops into JSON without escaping.
    /// </summary>
    private static string CliPath(string cliProjectDirectory) =>
        cliProjectDirectory.Replace('\\', '/');

    /// <summary>
    /// Tasks that shell out to the same 'gbsharp' pipeline documented for
    /// manual use, so the Command Palette and Ctrl+Shift+B reach it too.
    /// </summary>
    /// <remarks>
    /// A null <paramref name="cliProjectDirectory"/> means this CLI is not
    /// running from a GB# checkout: it is the installed 'gbsharp' tool, so
    /// the tasks call the bare command the way the developer just did. Inside
    /// a checkout they call back into that exact checkout via 'dotnet run',
    /// so someone working on GB# itself scaffolds projects that exercise
    /// their working copy.
    /// </remarks>
    private static string TasksJson(string? cliProjectDirectory)
    {
        string comment = cliProjectDirectory is null
            ? "these shell out to the installed 'gbsharp' tool."
            : "these shell out to this machine's GB# checkout via 'dotnet run'.";

        string command = cliProjectDirectory is null ? "gbsharp" : "dotnet";

        string Arguments(string verb) => cliProjectDirectory is null
            ? $"[\"{verb}\", \"${{workspaceFolder}}\"]"
            : $"[\"run\", \"--project\", \"{CliPath(cliProjectDirectory)}\", \"--\", \"{verb}\", \"${{workspaceFolder}}\"]";

        return $$"""
        {
            // GB# builds the ROM outside MSBuild (see the .csproj for why), so
            // {{comment}}
            "version": "2.0.0",
            "options": {
                "cwd": "${workspaceFolder}"
            },
            "tasks": [
                {
                    "label": "gbsharp: build",
                    "type": "shell",
                    "command": "{{command}}",
                    "args": {{Arguments("build")}},
                    "group": { "kind": "build", "isDefault": true },
                    "problemMatcher": "$msCompile"
                },
                {
                    "label": "gbsharp: run",
                    "type": "shell",
                    "command": "{{command}}",
                    "args": {{Arguments("run")}},
                    "problemMatcher": "$msCompile"
                },
                {
                    "label": "gbsharp: analyze",
                    "type": "shell",
                    "command": "{{command}}",
                    "args": {{Arguments("analyze")}},
                    "problemMatcher": "$msCompile"
                },
                {
                    "label": "gbsharp: clean",
                    "type": "shell",
                    "command": "{{command}}",
                    "args": {{Arguments("clean")}},
                    "problemMatcher": []
                }
            ]
        }
        """;
    }

    /// <summary>
    /// Makes F5 build and launch the configured emulator (thesis section 4:
    /// "IDE integration could make F5 perform the entire pipeline").
    /// </summary>
    /// <remarks>
    /// Not a real debug session: the .csproj is editor-only (GBS0509 refuses
    /// to compile it) and there is no GBZ80 debugger wired into VS Code, so
    /// there is nothing for a debug adapter to attach to. 'node-terminal' is a
    /// VS Code-builtin launch type that just runs a command in a terminal:
    /// no extension required, and no pretense of stepping through GB# code.
    /// Source-level debugging happens in the emulator itself, from the .sym
    /// written beside the ROM.
    /// </remarks>
    private static string LaunchJson(string? cliProjectDirectory)
    {
        // Same branch as TasksJson: bare 'gbsharp' from the installed tool,
        // 'dotnet run' back into the checkout this CLI ran from.
        string Command(string verb) => cliProjectDirectory is null
            ? $"gbsharp {verb} \\\"${{workspaceFolder}}\\\""
            : $"dotnet run --project \\\"{CliPath(cliProjectDirectory)}\\\" -- {verb} \\\"${{workspaceFolder}}\\\"";

        return $$"""
        {
            "version": "0.2.0",
            "configurations": [
                {
                    "name": "GB#: Build & Run (emulator)",
                    "type": "node-terminal",
                    "request": "launch",
                    "command": "{{Command("run")}}",
                    "cwd": "${workspaceFolder}"
                },
                {
                    "name": "GB#: Build only",
                    "type": "node-terminal",
                    "request": "launch",
                    "command": "{{Command("build")}}",
                    "cwd": "${workspaceFolder}"
                },
                {
                    "name": "GB#: Analyze (lint)",
                    "type": "node-terminal",
                    "request": "launch",
                    "command": "{{Command("analyze")}}",
                    "cwd": "${workspaceFolder}"
                }
            ]
        }
        """;
    }

    private const string Empty =
        """
        using GB;

        public static class Program
        {
            public static void Main()
            {
                Display.Enable();

                while (true)
                {
                    // Everything happens between here and the next frame.

                    Game.WaitVBlank();
                }
            }
        }
        """;

    private const string Sprite =
        """
        using GB;
        using static GB.Hardware;

        public static class Program
        {
            // Two tiles of 2bpp data: 16 bytes each, in the cartridge because it
            // is 'static readonly'. The build report shows what it cost.
            private static readonly byte[] Shape =
            {
                0x3C, 0x3C, 0x42, 0x7E, 0x81, 0xFF, 0xA5, 0xFF,
                0x81, 0xFF, 0xBD, 0xFF, 0x42, 0x7E, 0x3C, 0x3C,
            };

            public static void Main()
            {
                Tiles.LoadSprite(0, 1, Shape);

                Display.Enable();
                Display.ShowSprites();

                byte x = 80;
                byte y = 72;

                Sprites[0].Tile = 0;

                while (true)
                {
                    if (Input.Right) x++;
                    if (Input.Left) x--;
                    if (Input.Down) y++;
                    if (Input.Up) y--;

                    Sprites.Move(0, x, y);

                    Game.WaitVBlank();
                }
            }
        }
        """;

    private const string Background =
        """
        using GB;

        public static class Program
        {
            // The image is decoded, checked against the hardware's limits, reduced
            // to 2bpp tiles, deduplicated and turned into a map while the project
            // builds. This field is a name for that data; there is nothing to run
            // first and nothing to keep in sync.
            [Asset("tiles.png")]
            private static TileMap Art;

            public static void Main()
            {
                Background.Load(Art);

                Display.Enable();
                Display.ShowBackground();

                byte scroll = 0;

                while (true)
                {
                    scroll++;
                    Background.Scroll(1, 0);

                    Game.WaitVBlank();
                }
            }
        }
        """;

    /// <summary>
    /// Placeholder art: four shades in a pattern that repeats, so it converts
    /// cleanly on both machines and deduplicates to a handful of tiles.
    /// </summary>
    private static byte[] Placeholder() =>
        PngEncoder.Rgb(160, 144, (x, y) => (((x / 8) + (y / 8)) % 4) switch
        {
            0 => White,
            1 => Light,
            2 => Dark,
            _ => Black,
        });
}
