using System.Runtime.InteropServices;

namespace GBSharp.Emulator;

/// <summary>
/// The <c>gbsharp.h</c> ABI, one method per entry point and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Both library flavours export this identical set, so one set of declarations
/// serves both and <see cref="EmulatorRuntime"/> decides at load time which
/// file the library name resolves to.
/// </para>
/// <para>
/// <c>size_t</c> is <see cref="nuint"/> and the ABI's <c>bool</c> is one byte,
/// which is what C compilers produce for <c>_Bool</c> on every platform GB#
/// ships for. Getting that wrong would read three bytes of adjacent stack, so
/// the marshalling is spelled out rather than left to the default.
/// </para>
/// </remarks>
internal static unsafe partial class NativeMethods
{
    /// <summary>
    /// The logical name. <see cref="EmulatorRuntime"/> maps it to a real file.
    /// </summary>
    internal const string Library = "gbsharp_emulator";

    /// <summary>
    /// Puts the resolver in place before the runtime binds any of the entry
    /// points below, which it does on first call rather than at load.
    /// </summary>
    static NativeMethods() => EmulatorRuntime.Register();

    [LibraryImport(Library, EntryPoint = "gbsharp_abi_version")]
    internal static partial uint AbiVersion();

    [LibraryImport(Library, EntryPoint = "gbsharp_has_debug_support")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool HasDebugSupport();

    [LibraryImport(Library, EntryPoint = "gbsharp_create")]
    internal static partial GameBoyHandle Create();

    [LibraryImport(Library, EntryPoint = "gbsharp_destroy")]
    internal static partial void Destroy(nint emulator);

    [LibraryImport(Library, EntryPoint = "gbsharp_load_rom")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool LoadRom(GameBoyHandle emulator, byte* rom, nuint size);

    [LibraryImport(Library, EntryPoint = "gbsharp_reset")]
    internal static partial void Reset(GameBoyHandle emulator);

    [LibraryImport(Library, EntryPoint = "gbsharp_run_frame")]
    internal static partial uint RunFrame(GameBoyHandle emulator);

    [LibraryImport(Library, EntryPoint = "gbsharp_step")]
    internal static partial uint Step(GameBoyHandle emulator);

    [LibraryImport(Library, EntryPoint = "gbsharp_get_framebuffer")]
    internal static partial uint* GetFramebuffer(GameBoyHandle emulator);

    [LibraryImport(Library, EntryPoint = "gbsharp_get_audio")]
    internal static partial nuint GetAudio(GameBoyHandle emulator, short* destination, nuint frames);

    [LibraryImport(Library, EntryPoint = "gbsharp_set_button")]
    internal static partial void SetButton(
        GameBoyHandle emulator,
        GameBoyButton button,
        [MarshalAs(UnmanagedType.U1)] bool pressed);

    [LibraryImport(Library, EntryPoint = "gbsharp_read_memory")]
    internal static partial byte ReadMemory(GameBoyHandle emulator, ushort address);

    [LibraryImport(Library, EntryPoint = "gbsharp_write_memory")]
    internal static partial void WriteMemory(GameBoyHandle emulator, ushort address, byte value);

    [LibraryImport(Library, EntryPoint = "gbsharp_get_pc")]
    internal static partial ushort GetProgramCounter(GameBoyHandle emulator);

    [LibraryImport(Library, EntryPoint = "gbsharp_get_rom_bank")]
    internal static partial int GetRomBank(GameBoyHandle emulator, ushort address);

    [LibraryImport(Library, EntryPoint = "gbsharp_get_ram_bank")]
    internal static partial int GetRamBank(GameBoyHandle emulator);

    [LibraryImport(Library, EntryPoint = "gbsharp_get_rom_size")]
    internal static partial nuint GetRomSize(GameBoyHandle emulator);

    [LibraryImport(Library, EntryPoint = "gbsharp_get_registers")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool GetRegisters(GameBoyHandle emulator, out CpuRegisters registers);

    [LibraryImport(Library, EntryPoint = "gbsharp_add_breakpoint")]
    internal static partial int AddBreakpoint(GameBoyHandle emulator, ushort bank, ushort address);

    [LibraryImport(Library, EntryPoint = "gbsharp_remove_breakpoint")]
    internal static partial void RemoveBreakpoint(int id);

    [LibraryImport(Library, EntryPoint = "gbsharp_clear_breakpoints")]
    internal static partial void ClearBreakpoints();

    [LibraryImport(Library, EntryPoint = "gbsharp_set_profiling_enabled")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool SetProfilingEnabled([MarshalAs(UnmanagedType.U1)] bool enabled);

    [LibraryImport(Library, EntryPoint = "gbsharp_get_profiling_enabled")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool GetProfilingEnabled();

    [LibraryImport(Library, EntryPoint = "gbsharp_clear_profile")]
    internal static partial void ClearProfile();

    [LibraryImport(Library, EntryPoint = "gbsharp_read_profile")]
    internal static partial nuint ReadProfile(
        GameBoyHandle emulator, uint* counts, uint* cycles, nuint entries);

    [LibraryImport(Library, EntryPoint = "gbsharp_set_rom_usage_enabled")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool SetRomUsageEnabled([MarshalAs(UnmanagedType.U1)] bool enabled);

    [LibraryImport(Library, EntryPoint = "gbsharp_get_rom_usage_enabled")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool GetRomUsageEnabled();

    [LibraryImport(Library, EntryPoint = "gbsharp_clear_rom_usage")]
    internal static partial void ClearRomUsage();

    [LibraryImport(Library, EntryPoint = "gbsharp_read_rom_usage")]
    internal static partial nuint ReadRomUsage(GameBoyHandle emulator, byte* usage, nuint entries);

    [LibraryImport(Library, EntryPoint = "gbsharp_save_ram_size")]
    internal static partial nuint SaveRamSize(GameBoyHandle emulator);

    [LibraryImport(Library, EntryPoint = "gbsharp_read_save_ram")]
    internal static partial void ReadSaveRam(GameBoyHandle emulator, byte* destination);

    [LibraryImport(Library, EntryPoint = "gbsharp_write_save_ram")]
    internal static partial void WriteSaveRam(GameBoyHandle emulator, byte* source);
}
