using System.Globalization;
using System.Security.Cryptography;
using GBSharp.Compiler.Assets;
using GBSharp.Compiler.Diagnostics;
using GBSharp.Compiler.Frontend;
using GBSharp.Compiler.IR;
using Microsoft.CodeAnalysis;

namespace GBSharp.Compiler.Lowering;

/// <summary>
/// One converted asset, and the arguments a loader call expands into.
/// </summary>
/// <param name="Arguments">
/// The C arguments this asset contributes, in the order the runtime shim
/// declares its parameters. This ordering is a contract between here and
/// <c>gbs_runtime.h</c>; changing one without the other produces C that
/// compiles and does the wrong thing.
/// </param>
public sealed record AssetBinding(
    string Name,
    AssetKind Kind,
    IReadOnlyList<IRGlobal> Globals,
    IReadOnlyList<IRExpression> Arguments,
    IRAsset Description)
{
    /// <summary>The ROM bank this asset's data lives in.</summary>
    public IRBank Bank => Description.Bank;
}

/// <summary>
/// Turns <c>[Asset]</c> and <c>[Sprite]</c> fields into ROM data.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <see cref="FixedCollections"/>, the other place where GB#
/// generates something at compile time: both produce IR rather than C text, so
/// the result flows through the same emitter and looks like the rest of the
/// program.
/// </para>
/// <para>
/// The interesting move is that an asset argument expands into several. This is
/// the exact dual of <see cref="IRUnit"/>, which removes an argument so that
/// <c>Sprites[0].X</c> loses its receiver; here one C# argument becomes the
/// four pointers and four counts the loader needs, and every C symbol name
/// stays in the framework's <c>[Native]</c> attribute where it belongs.
/// </para>
/// </remarks>
public sealed class AssetBindings(
    FrameworkSymbols framework,
    IAssetCompiler compiler,
    IReadOnlyList<string> searchPaths,
    AssetTargetProfile profile,
    DiagnosticBag diagnostics)
{
    private readonly Dictionary<ISymbol, AssetBinding> _bindings = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, AssetBinding> _byContent = new(StringComparer.Ordinal);
    private readonly List<IRGlobal> _globals = [];
    private readonly List<IRAsset> _assets = [];

    /// <summary>The ROM globals every converted asset produced.</summary>
    public IReadOnlyList<IRGlobal> Globals => _globals;

    /// <summary>What was converted, for the build report.</summary>
    public IReadOnlyList<IRAsset> Assets => _assets;

    public AssetBinding? For(ISymbol field) =>
        _bindings.TryGetValue(field, out AssetBinding? binding) ? binding : null;

    /// <summary>True if this field is an asset, whether or not it converted.</summary>
    public bool IsAsset(ISymbol field) => framework.GetAssetAttribute(field) is not null;

    /// <summary>
    /// Converts a field's asset, if it declares one.
    /// </summary>
    /// <returns>True if the field was an asset and needs no WRAM global.</returns>
    public bool TryCollect(IFieldSymbol field, Location? location)
    {
        AttributeData? attribute = framework.GetAssetAttribute(field);
        if (attribute is null)
        {
            return false;
        }

        var span = SourceSpan.FromLocation(location);
        string display = field.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

        if (!field.IsStatic)
        {
            diagnostics.Report(GBDiagnostics.InvalidAssetDeclaration, location, display, "not static");
            return true;
        }

        AssetKind? kind = KindOf(
            field.Type,
            framework.IsSpriteAttribute(attribute),
            framework.IsMetaspriteAttribute(attribute),
            framework.IsBinaryAttribute(attribute),
            framework.IsFontAttribute(attribute));

        if (kind is null)
        {
            diagnostics.Report(
                GBDiagnostics.InvalidAssetDeclaration,
                location,
                display,
                $"typed '{field.Type.Name}', which is not an asset type");
            return true;
        }

        if (attribute.ConstructorArguments is not [{ Value: string path }] || path.Length == 0)
        {
            diagnostics.Report(GBDiagnostics.InvalidAssetDeclaration, location, display, "given no path");
            return true;
        }

        string? resolved = ResolvePath(path, location, out IReadOnlyList<string> probed);
        if (resolved is null)
        {
            diagnostics.Report(GBDiagnostics.AssetNotFound, location, path, string.Join(", ", probed));
            return true;
        }

        var options = new AssetOptions(
            profile,
            ReadInt(attribute, "MaxTiles"),
            ReadBool(attribute, "DedupeFlips"),
            ReadBool(attribute, "TallSprites") ?? false,
            ReadInt(attribute, "FrameWidth"),
            ReadInt(attribute, "FrameHeight"),
            ReadString(attribute, "Characters"));

        // Characters is the only required property [Font] has, and an attribute
        // constructor argument cannot be made required by name, so this is
        // checked here rather than left to fail obscurely inside the pipeline.
        if (kind == AssetKind.Font && string.IsNullOrEmpty(options.Characters))
        {
            diagnostics.Report(GBDiagnostics.FontCharactersRequired, location, display);
            return true;
        }

        // Two fields naming the same image with the same settings share one copy
        // in ROM. On a 32 KB cartridge that is a real saving, not a tidy-up.
        string key = ContentKey(resolved, options, kind.Value);

        IRBank bank = BankResolver.Resolve(framework, field);

        if (_byContent.TryGetValue(key, out AssetBinding? shared))
        {
            // Sharing one copy and asking for two banks cannot both be honoured.
            // Silently taking the first field's bank would put the data somewhere
            // the second field's author did not ask for and would not be told.
            if (shared.Bank != bank)
            {
                diagnostics.Report(
                    GBDiagnostics.ConflictingBanks,
                    location,
                    display,
                    shared.Name,
                    bank.ToString(),
                    shared.Bank.ToString());

                return true;
            }

            diagnostics.Report(GBDiagnostics.SharedAsset, location, display, shared.Name);
            _bindings[field] = shared;
            return true;
        }

        // Raw bytes never reach the image pipeline: there is nothing to convert,
        // and handing an arbitrary file to a PNG decoder to be told it is not a
        // PNG would be a worse answer than copying it.
        if (kind == AssetKind.Binary)
        {
            AssetBinding? blob = MaterializeBinary(field, resolved, span, bank, location, display);
            if (blob is not null)
            {
                _bindings[field] = blob;
                _byContent[key] = blob;
            }

            return true;
        }

        AssetArtifact? artifact = compiler.Compile(
            new AssetRequest(kind.Value, resolved, display, options, span),
            diagnostics);

        if (artifact is null)
        {
            return true;
        }

        AssetBinding binding = Materialize(field, kind.Value, resolved, artifact, span, bank);
        _bindings[field] = binding;
        _byContent[key] = binding;
        return true;
    }

    /// <summary>
    /// Copies a file into ROM as one array, and expands to a pointer and a length.
    /// </summary>
    /// <remarks>
    /// The length is a <c>uint16</c>, not a <c>uint8</c>: a data file passes 255
    /// bytes immediately, which is exactly the case the image kinds never hit and
    /// so the reason their counts can stay a byte.
    /// </remarks>
    private AssetBinding? MaterializeBinary(
        IFieldSymbol field,
        string sourcePath,
        SourceSpan span,
        IRBank bank,
        Location? location,
        string display)
    {
        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(sourcePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            diagnostics.Report(GBDiagnostics.AssetNotFound, location, sourcePath, e.Message);
            return null;
        }

        if (bytes.Length > ushort.MaxValue)
        {
            diagnostics.Report(
                GBDiagnostics.BinaryTooLarge,
                location,
                display,
                bytes.Length,
                ushort.MaxValue);
            return null;
        }

        string name = NameMangler.ForGlobal(field);
        var type = new IRArrayType(IRPrimitiveType.U8, bytes.Length);

        var global = new IRGlobal(name + "_data", type, new IRDataBlob(type, bytes, 1), span)
        {
            IsReadOnly = true,
            Bank = bank,
        };

        var description = new IRAsset(
            display,
            Path.GetFileName(sourcePath),
            new AssetStats(0, 0, 0, 0, 0, 0, 0, 0),
            bytes.Length,
            [global.Name])
        {
            Bank = bank,
        };

        _globals.Add(global);
        _assets.Add(description);

        diagnostics.Report(GBDiagnostics.BinaryRomCost, span, display, bytes.Length);

        IReadOnlyList<IRExpression> arguments =
        [
            new IRGlobalRef(global),
            new IRConstant(IRPrimitiveType.U16, (ushort)bytes.Length),
            BankOf(bank, global),
        ];

        return new AssetBinding(name, AssetKind.Binary, [global], arguments, description);
    }

    /// <summary>
    /// Builds the ROM globals and the argument list for one converted asset.
    /// </summary>
    private AssetBinding Materialize(
        IFieldSymbol field,
        AssetKind kind,
        string sourcePath,
        AssetArtifact artifact,
        SourceSpan span,
        IRBank bank)
    {
        string prefix = NameMangler.ForGlobal(field);
        var globals = new List<IRGlobal>();

        IRGlobal? Blob(AssetBlobRole role)
        {
            AssetBlob? blob = artifact[role];
            if (blob is null)
            {
                return null;
            }

            IRType element = blob.ElementWidth == 2 ? IRPrimitiveType.U16 : IRPrimitiveType.U8;
            var type = new IRArrayType(element, blob.ElementCount);

            var global = new IRGlobal(
                prefix + blob.NameSuffix,
                type,
                new IRDataBlob(type, blob.Bytes, blob.ElementWidth),
                span)
            {
                IsReadOnly = true,
                Bank = bank,
            };

            globals.Add(global);
            return global;
        }

        IRGlobal? tiles = Blob(AssetBlobRole.TileData);
        IRGlobal? map = Blob(AssetBlobRole.MapIndices);
        IRGlobal? attributes = Blob(AssetBlobRole.AttributeMap);
        IRGlobal? palettes = Blob(AssetBlobRole.Palettes);
        IRGlobal? frames = Blob(AssetBlobRole.MetaspriteFrames);
        IRGlobal? frameOffsets = Blob(AssetBlobRole.FrameOffsets);
        IRGlobal? glyphTable = Blob(AssetBlobRole.GlyphTable);

        AssetStats stats = artifact.Stats;

        // The bank goes last, so the loader can map the data in before reading
        // it. See AssetSignature for why the rest of the shape lives in a table
        // rather than here.
        IRExpression bankArgument = BankOf(bank, tiles);

        IReadOnlyList<IRExpression> arguments = [.. AssetSignature.For(kind).Select(arg => arg switch
        {
            AssetSignatureArg.Tiles => Pointer(tiles, IRPrimitiveType.U8),
            AssetSignatureArg.Map => Pointer(map, IRPrimitiveType.U8),
            AssetSignatureArg.Attributes => Pointer(attributes, IRPrimitiveType.U8),
            AssetSignatureArg.Palettes => Pointer(palettes, IRPrimitiveType.U16),
            AssetSignatureArg.Frames => Pointer(frames, IRPrimitiveType.U8),
            AssetSignatureArg.FrameOffsets => Pointer(frameOffsets, IRPrimitiveType.U8),
            AssetSignatureArg.GlyphTable => Pointer(glyphTable, IRPrimitiveType.U8),
            AssetSignatureArg.TileCount => Count(stats.UniqueTiles),
            AssetSignatureArg.Width => Count(stats.WidthTiles),
            AssetSignatureArg.Height => Count(stats.HeightTiles),
            AssetSignatureArg.PaletteCount => Count(stats.PaletteCount),
            AssetSignatureArg.FrameCount => Count(stats.FrameCount),
            AssetSignatureArg.Bank => bankArgument,
            _ => throw new ArgumentOutOfRangeException(nameof(arg), arg, "unhandled asset signature argument"),
        })];

        var description = new IRAsset(
            field.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            Path.GetFileName(sourcePath),
            stats,
            artifact.RomBytes,
            [.. globals.Select(g => g.Name)])
        {
            Bank = bank,
        };

        _globals.AddRange(globals);
        _assets.Add(description);

        diagnostics.Report(
            GBDiagnostics.AssetRomCost,
            span,
            description.Name,
            artifact.RomBytes,
            stats.TotalTiles,
            stats.UniqueTiles,
            stats.WidthTiles,
            stats.HeightTiles);

        return new AssetBinding(prefix, kind, globals, arguments, description);
    }

    /// <summary>
    /// The bank the loader must map before reading, as an argument.
    /// </summary>
    /// <remarks>
    /// Resident data is a literal 0, which the loader treats as "no switch
    /// needed", so an unbanked program pays nothing but one constant argument.
    /// Banked data uses GBDK's <c>BANK()</c> macro over the tile blob: the bank
    /// is decided by the linker, not by GB#, so naming the symbol is the only
    /// answer that stays true after bankpack has moved things. That needs no new
    /// IR node: <see cref="IRNativeCall"/> already emits <c>symbol(args)</c>
    /// verbatim, and a macro invocation is exactly that shape.
    /// </remarks>
    private static IRExpression BankOf(IRBank bank, IRGlobal? tiles) =>
        bank.IsResident || tiles is null
            ? new IRConstant(IRPrimitiveType.U8, (byte)0)
            : new IRNativeCall("BANK", [new IRGlobalRef(tiles)], IRPrimitiveType.U8);

    /// <summary>A blob's address, or a null pointer when the target has no such data.</summary>
    private static IRExpression Pointer(IRGlobal? global, IRType element) =>
        global is null
            ? new IRDefaultValue(new IRPointerType(element))
            : new IRGlobalRef(global);

    /// <summary>
    /// A count, as the <c>uint8_t</c> the shim declares.
    /// </summary>
    /// <remarks>
    /// Every value reaching here is already bounded by an earlier diagnostic:
    /// tile counts by GBS0604, map dimensions by GBS0611, palettes by GBS0603.
    /// So a value outside a byte means one of those checks did not run, not that
    /// the developer asked for something too large: silently clamping it would
    /// produce a ROM that loads the wrong number of tiles and no explanation of
    /// why. Asset kinds whose counts genuinely exceed a byte need a wider shim
    /// parameter, not a wider clamp here.
    /// </remarks>
    private IRExpression Count(int value)
    {
        if (value is < 0 or > 255)
        {
            diagnostics.Report(
                GBDiagnostics.InternalError,
                SourceSpan.None,
                $"asset count {value} does not fit the uint8_t the runtime shim declares");
        }

        return new IRConstant(IRPrimitiveType.U8, (byte)Math.Clamp(value, 0, 255));
    }

    /// <summary>
    /// The conversion a field's type selects, or null if it is not an asset type.
    /// </summary>
    /// <remarks>
    /// Compared by symbol rather than by name: a user's own struct called
    /// <c>TileMap</c> is their type, not the framework's, and should not be
    /// silently handed to the image pipeline.
    /// </remarks>
    private AssetKind? KindOf(
        ITypeSymbol type,
        bool isSpriteAttribute,
        bool isMetaspriteAttribute,
        bool isBinaryAttribute,
        bool isFontAttribute)
    {
        if (isBinaryAttribute)
        {
            return framework.IsBinaryAsset(type) ? AssetKind.Binary : null;
        }

        if (isMetaspriteAttribute)
        {
            return framework.IsMetaspriteAsset(type) ? AssetKind.Metasprite : null;
        }

        if (isFontAttribute)
        {
            return framework.IsFontAsset(type) ? AssetKind.Font : null;
        }

        if (isSpriteAttribute)
        {
            return framework.IsSpriteAsset(type) ? AssetKind.SpriteSheet : null;
        }

        if (framework.IsTileMap(type))
        {
            return AssetKind.TileMap;
        }

        if (framework.IsTileSet(type))
        {
            return AssetKind.TileSet;
        }

        return framework.IsSpriteAsset(type) ? AssetKind.SpriteSheet : null;
    }

    /// <summary>
    /// Finds the image: beside the declaring file first, then the project's
    /// search paths.
    /// </summary>
    /// <remarks>
    /// Absolute paths are refused. A ROM that only builds on the machine that
    /// wrote it is not reproducible, and the failure would surface as a missing
    /// file on someone else's checkout rather than as a decision anyone made.
    /// </remarks>
    private string? ResolvePath(string path, Location? location, out IReadOnlyList<string> probed)
    {
        var candidates = new List<string>();

        if (Path.IsPathRooted(path))
        {
            probed = ["(absolute paths are not allowed)"];
            return null;
        }

        if (location?.SourceTree?.FilePath is { Length: > 0 } declaringFile &&
            Path.GetDirectoryName(declaringFile) is { Length: > 0 } declaringDirectory)
        {
            candidates.Add(Path.Combine(declaringDirectory, path));
        }

        foreach (string root in searchPaths)
        {
            candidates.Add(Path.Combine(root, path));
        }

        var seen = new List<string>();

        foreach (string candidate in candidates)
        {
            string full;

            try
            {
                full = Path.GetFullPath(candidate);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (seen.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            seen.Add(full);

            if (File.Exists(full))
            {
                probed = seen;
                return full;
            }
        }

        probed = seen;
        return null;
    }

    /// <summary>
    /// Identifies an asset by what it is, not where it came from.
    /// </summary>
    /// <remarks>
    /// Hashing the bytes rather than the path means two names for the same image
    /// share one copy. It also means nothing depends on a timestamp: git does
    /// not preserve those, so a fresh checkout would disagree with the machine
    /// that wrote the file.
    /// </remarks>
    private static string ContentKey(string path, AssetOptions options, AssetKind kind)
    {
        byte[] hash;

        try
        {
            using FileStream stream = File.OpenRead(path);
            hash = SHA256.HashData(stream);
        }
        catch (IOException)
        {
            // Unreadable here means unreadable in the converter too, which will
            // report it properly. Key on the path so nothing collides meanwhile.
            return path;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Convert.ToHexString(hash)}/{kind}/{options.Profile}/{options.MaxTiles}/{options.DedupeFlips}/" +
            $"{options.TallSprites}/{options.FrameWidth}/{options.FrameHeight}/{options.Characters}/{PipelineVersion}");
    }

    /// <summary>
    /// Bumped whenever conversion could produce different bytes for the same
    /// input, so a cache keyed on this can never serve stale output.
    /// </summary>
    private const int PipelineVersion = 1;

    private static int ReadInt(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value is int value ? value : 0;

    private static bool? ReadBool(AttributeData attribute, string name) =>
        attribute.NamedArguments.Any(a => a.Key == name)
            ? attribute.NamedArguments.First(a => a.Key == name).Value.Value as bool?
            : null;

    private static string? ReadString(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value as string;
}
