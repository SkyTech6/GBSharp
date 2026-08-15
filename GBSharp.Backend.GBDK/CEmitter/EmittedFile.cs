namespace GBSharp.Backend.GBDK;

public enum EmittedFileKind
{
    /// <summary>A shared declaration header, included by every translation unit.</summary>
    Header,

    /// <summary>A <c>.c</c> file to compile and link.</summary>
    TranslationUnit,
}

/// <summary>
/// One generated file.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Name"/> is a bare file name, never a path. Every toolchain
/// invocation runs with its working directory set to the build directory and
/// passes bare names, which is what stops a project path containing a space
/// from ever reaching lcc's command line.
/// </para>
/// <para>
/// <see cref="RomBank"/> is unused today and always null. It exists because the
/// alternative to designing for it now is redesigning the emitter later: GBDK
/// selects a ROM bank per translation unit, so banking is a property of a file
/// rather than of a function, and this is the record that will carry it.
/// </para>
/// </remarks>
public sealed record EmittedFile(string Name, string Text, EmittedFileKind Kind)
{
    /// <summary>The ROM bank this file's contents belong to; null means resident.</summary>
    public int? RomBank { get; init; }
}
