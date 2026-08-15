namespace GBSharp.Backend.GBDK.Toolchain;

/// <summary>
/// The per-user directory <c>gbsharp doctor --fix</c> installs toolchains into.
/// </summary>
/// <remarks>
/// <para>
/// A GB# checkout carries its toolchains in <c>tools/</c>, fetched by the
/// PowerShell scripts there. A packaged <c>gbsharp</c> tool has no checkout
/// around it and no pwsh to lean on, so it fetches the same pinned archives
/// here instead, and every locator probes this directory after the
/// repo-relative candidates.
/// </para>
/// <para>
/// Rooted at <see cref="Environment.SpecialFolder.LocalApplicationData"/>
/// rather than at <c>EmulatorLocator.ConfigDirectory()</c>'s
/// ApplicationData root, deliberately: ApplicationData is the roaming
/// profile on Windows, and a settings file belongs there, but hundreds of
/// megabytes of machine-specific native binaries do not roam. Local data is
/// the cache-shaped location on every platform (<c>%LOCALAPPDATA%</c>,
/// <c>~/.local/share</c>, <c>~/Library/Application Support</c>).
/// </para>
/// </remarks>
public static class ToolchainCache
{
    /// <summary>The cache root, <c>&lt;local app data&gt;/gbsharp</c>.</summary>
    public static string Root =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "gbsharp");

    /// <summary>Where a fetched GBDK-2020 install lives.</summary>
    public static string GbdkDirectory => Path.Combine(Root, "gbdk");

    /// <summary>Where a fetched emulator runtime lives.</summary>
    /// <remarks>
    /// The same layout <c>tools/emulator</c> has in a checkout: <c>bin/</c>
    /// with the native libraries and the Player, <c>stubs/</c> for other
    /// platforms, so everything that reads one can read the other.
    /// </remarks>
    public static string EmulatorDirectory => Path.Combine(Root, "emulator");
}
