namespace GBSharp.Emulator;

/// <summary>
/// A Game Boy, one method per <c>gbsharp.h</c> entry point.
/// </summary>
/// <remarks>
/// <para>
/// Nothing above the raw ABI belongs here: no pacing, no input mapping, no
/// save file policy. Those are decisions a host makes, and different hosts make
/// them differently, which is the reason the ABI leaves them out. Convenience
/// for tests lives in <c>GameBoyTest</c>.
/// </para>
/// <para>
/// Not thread safe. Two threads may hold two machines, but one machine belongs
/// to one thread at a time.
/// </para>
/// </remarks>
public sealed class GameBoy : IDisposable
{
    /// <summary>Pixels across, as <c>GBSHARP_SCREEN_WIDTH</c>.</summary>
    public const int ScreenWidth = 160;

    /// <summary>Pixels down, as <c>GBSHARP_SCREEN_HEIGHT</c>.</summary>
    public const int ScreenHeight = 144;

    /// <summary>Samples per second per channel, as <c>GBSHARP_AUDIO_FREQUENCY</c>.</summary>
    public const int AudioFrequency = 44100;

    /// <summary>Interleaved channels per audio frame, as <c>GBSHARP_AUDIO_CHANNELS</c>.</summary>
    public const int AudioChannels = 2;

    private readonly GameBoyHandle handle;

    private GameBoy(GameBoyHandle handle) => this.handle = handle;

    /// <summary>
    /// Boots a ROM held in memory.
    /// </summary>
    /// <remarks>
    /// Bytes rather than a path, because the ABI has no path arguments anywhere:
    /// the same call serves a test, a native player reading its own executable,
    /// and a browser that has no filesystem to read.
    /// </remarks>
    /// <exception cref="ArgumentException">The ROM is not a Game Boy ROM.</exception>
    public static unsafe GameBoy Load(ReadOnlySpan<byte> rom)
    {
        if (rom.IsEmpty)
        {
            throw new ArgumentException("The ROM is empty.", nameof(rom));
        }

        EmulatorRuntime.EnsureLoaded();

        GameBoyHandle handle = NativeMethods.Create();
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new InvalidOperationException("The emulator runtime could not create an emulator.");
        }

        bool loaded;
        fixed (byte* bytes = rom)
        {
            loaded = NativeMethods.LoadRom(handle, bytes, (nuint)rom.Length);
        }

        if (!loaded)
        {
            handle.Dispose();
            throw new ArgumentException(
                $"The emulator runtime could not map these {rom.Length} bytes as a cartridge. The " +
                "size or the cartridge type in the header is one it does not implement. Note that " +
                "the logo and header checksums are deliberately not checked, so a ROM rejected here " +
                "is malformed rather than merely unofficial.",
                nameof(rom));
        }

        return new GameBoy(handle);
    }

    /// <summary>
    /// Power cycles, deterministically: the state <see cref="Load"/> produced,
    /// with held buttons still held.
    /// </summary>
    public void Reset() => NativeMethods.Reset(handle);

    /// <summary>
    /// Advances one frame, leaving a complete frame in <see cref="Framebuffer"/>.
    /// </summary>
    /// <remarks>
    /// Consults no clock, so a test runs as fast as the machine allows and a
    /// player is responsible for its own pacing.
    /// </remarks>
    /// <returns>
    /// Why the run stopped. A caller with no breakpoints set can ignore it; a
    /// caller with breakpoints cannot, because a run that stopped early left a
    /// partial frame in <see cref="Framebuffer"/>.
    /// </returns>
    public EmulatorEvent RunFrame() => (EmulatorEvent)NativeMethods.RunFrame(handle);

    /// <summary>
    /// Advances <paramref name="count"/> frames, stopping early at a breakpoint.
    /// </summary>
    /// <returns>The last frame's event, so a caller can tell why it stopped.</returns>
    public EmulatorEvent RunFrames(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        EmulatorEvent last = EmulatorEvent.None;
        for (int i = 0; i < count; i++)
        {
            last = (EmulatorEvent)NativeMethods.RunFrame(handle);

            // Running on would step over the instruction the caller stopped at,
            // which is the one thing a breakpoint exists to prevent.
            if ((last & EmulatorEvent.Breakpoint) != 0)
            {
                break;
            }
        }

        return last;
    }

    /// <summary>
    /// Executes one instruction.
    /// </summary>
    /// <remarks>
    /// Works in both flavours: the core's stepping is not instrumentation, and
    /// only breakpoints need the hooks. The framebuffer holds a partial frame
    /// after a step, so read it only after a run reporting
    /// <see cref="EmulatorEvent.NewFrame"/>.
    /// </remarks>
    public EmulatorEvent Step() => (EmulatorEvent)NativeMethods.Step(handle);

    /// <summary>
    /// The screen, row major from the top left, each pixel 0xAABBGGRR.
    /// </summary>
    /// <remarks>
    /// A view over the emulator's own memory rather than a copy, so reading it
    /// costs nothing and holding it is a mistake: the next
    /// <see cref="RunFrame"/> overwrites the contents and <see cref="Reset"/>
    /// moves it. Fetched fresh on every access for exactly that reason.
    /// </remarks>
    public unsafe ReadOnlySpan<uint> Framebuffer
    {
        get
        {
            uint* pixels = NativeMethods.GetFramebuffer(handle);
            return pixels == null
                ? ReadOnlySpan<uint>.Empty
                : new ReadOnlySpan<uint>(pixels, ScreenWidth * ScreenHeight);
        }
    }

    /// <summary>
    /// Drains the audio the last <see cref="RunFrame"/> produced, returning how
    /// many frames were written. One frame is one sample per channel.
    /// </summary>
    /// <remarks>
    /// Pulled rather than pushed. Whatever is not taken before the next
    /// <see cref="RunFrame"/> is discarded, so a host that falls behind hears a
    /// gap instead of stalling emulation to protect its buffer.
    /// </remarks>
    public unsafe int ReadAudio(Span<short> destination)
    {
        int frames = destination.Length / AudioChannels;
        if (frames == 0)
        {
            return 0;
        }

        fixed (short* samples = destination)
        {
            return (int)NativeMethods.GetAudio(handle, samples, (nuint)frames);
        }
    }

    /// <summary>
    /// Holds or releases a button. Takes effect at the next <see cref="RunFrame"/>.
    /// </summary>
    public void SetButton(GameBoyButton button, bool pressed) =>
        NativeMethods.SetButton(handle, button, pressed);

    /// <summary>
    /// Reads through the memory map as a debugger would: the current bank at
    /// 0x4000, no bus timing, no effect on the emulated clock.
    /// </summary>
    public byte ReadMemory(ushort address) => NativeMethods.ReadMemory(handle, address);

    /// <summary>
    /// Writes through the memory map. A write to a bank register switches banks,
    /// exactly as a write from emulated code would.
    /// </summary>
    public void WriteMemory(ushort address, byte value) =>
        NativeMethods.WriteMemory(handle, address, value);

    /// <summary>
    /// The address the CPU is about to execute.
    /// </summary>
    /// <remarks>
    /// Paired with <see cref="RomBankAt"/> this is a location a linker map can
    /// name, which is the whole of what GB# needs to say which of the
    /// developer's methods is running.
    /// </remarks>
    public ushort ProgramCounter => NativeMethods.GetProgramCounter(handle);

    /// <summary>
    /// The ROM bank mapped under <paramref name="address"/>, or <c>-1</c> when
    /// the address is not in the cartridge.
    /// </summary>
    /// <remarks>
    /// Bank 0 is a real answer, so "there is no bank here" cannot be zero. Code
    /// genuinely does run outside the cartridge (a routine copied into HRAM to
    /// switch banks under itself, most often), so a caller has to handle it.
    /// </remarks>
    public int RomBankAt(ushort address) => NativeMethods.GetRomBank(handle, address);

    /// <summary>
    /// The cartridge RAM bank mapped at 0xA000, or <c>-1</c> when the cartridge
    /// has no RAM.
    /// </summary>
    /// <remarks>
    /// With <see cref="RomBankAt"/> this is all of the MBC state a caller can
    /// act on. Everything else a mapper holds is latched input to these two.
    /// </remarks>
    public int RamBank => NativeMethods.GetRamBank(handle);

    /// <summary>Bytes of cartridge ROM, after load-time padding.</summary>
    public int RomSize => (int)NativeMethods.GetRomSize(handle);

    /// <summary>
    /// Every register at once, or <see langword="null"/> without instrumentation.
    /// </summary>
    /// <remarks>
    /// Needs the debug flavour, unlike <see cref="ProgramCounter"/>: the core
    /// keeps a whole-register dump behind the same hooks as the rest of the
    /// debugger, while the PC alone is always readable.
    /// </remarks>
    public CpuRegisters? Registers =>
        NativeMethods.GetRegisters(handle, out CpuRegisters registers) ? registers : null;

    /// <summary>
    /// Breaks before the instruction at <paramref name="address"/> in ROM bank
    /// <paramref name="bank"/> runs, returning an id, or <c>-1</c> when there
    /// is no room or no instrumentation.
    /// </summary>
    /// <remarks>
    /// The bank is given rather than inferred, so a breakpoint can be placed
    /// from a linker map before the code has ever run and therefore before its
    /// bank has ever been mapped. For an address outside the cartridge the bank
    /// is ignored.
    /// </remarks>
    public int AddBreakpoint(int bank, ushort address) =>
        NativeMethods.AddBreakpoint(handle, (ushort)Math.Max(bank, 0), address);

    /// <summary>Removes one breakpoint. An unknown id is ignored.</summary>
    public static void RemoveBreakpoint(int id) => NativeMethods.RemoveBreakpoint(id);

    /// <summary>Removes every breakpoint in the process.</summary>
    /// <remarks>
    /// Static because the core keeps breakpoints in the library rather than in
    /// an emulator, so two <see cref="GameBoy"/> instances share one set. A
    /// test that sets one should clear it, for the same reason.
    /// </remarks>
    public static void ClearBreakpoints() => NativeMethods.ClearBreakpoints();

    /// <summary>
    /// Turns the per-address profiler on or off, returning the state actually
    /// reached, always <see langword="false"/> without instrumentation.
    /// </summary>
    /// <remarks>
    /// The return value is the point: a caller that assumed profiling was on
    /// would read zeroes and conclude its game executes no code, rather than
    /// that it loaded the fast flavour.
    /// </remarks>
    public static bool SetProfilingEnabled(bool enabled) =>
        NativeMethods.SetProfilingEnabled(enabled);

    /// <summary>Whether the profiler is gathering.</summary>
    public static bool ProfilingEnabled => NativeMethods.GetProfilingEnabled();

    /// <summary>Zeroes every counter, to profile one scene rather than a session.</summary>
    public static void ClearProfile() => NativeMethods.ClearProfile();

    /// <summary>
    /// Copies profile data, indexed by ROM address: the offset into the
    /// cartridge, <c>bank * 0x4000 + (address &amp; 0x3FFF)</c>.
    /// </summary>
    /// <param name="counts">How often the instruction at each address ran. May be empty.</param>
    /// <param name="cycles">Ticks spent at each address. May be empty.</param>
    /// <returns>Entries written, which is zero without instrumentation.</returns>
    /// <remarks>
    /// A ROM address is the coordinate a linker map uses, so a caller can total
    /// these per symbol without knowing anything about the emulator. Counts and
    /// cycles rank code differently, and the difference is the point: a rarely
    /// called routine can still dominate a frame.
    /// </remarks>
    public unsafe int ReadProfile(Span<uint> counts, Span<uint> cycles)
    {
        int entries = counts.IsEmpty ? cycles.Length : cycles.IsEmpty
            ? counts.Length
            : Math.Min(counts.Length, cycles.Length);

        if (entries == 0)
        {
            return 0;
        }

        fixed (uint* countsPointer = counts)
        fixed (uint* cyclesPointer = cycles)
        {
            return (int)NativeMethods.ReadProfile(
                handle,
                counts.IsEmpty ? null : countsPointer,
                cycles.IsEmpty ? null : cyclesPointer,
                (nuint)entries);
        }
    }

    /// <summary>
    /// Turns byte-usage tracking on or off, returning the state actually
    /// reached, always <see langword="false"/> without instrumentation.
    /// </summary>
    /// <remarks>
    /// On by default, unlike the profiler: the core marks usage in a hook it is
    /// already running, and "what did this session never touch" cannot be asked
    /// retroactively if nobody was recording.
    /// </remarks>
    public static bool SetRomUsageEnabled(bool enabled) =>
        NativeMethods.SetRomUsageEnabled(enabled);

    /// <summary>Whether byte-usage tracking is recording.</summary>
    public static bool RomUsageEnabled => NativeMethods.GetRomUsageEnabled();

    /// <summary>Forgets what has been reached so far. Needs tracking to be on.</summary>
    public static void ClearRomUsage() => NativeMethods.ClearRomUsage();

    /// <summary>
    /// Copies what each ROM byte turned out to be, indexed by ROM address.
    /// </summary>
    /// <returns>Entries written; zero without instrumentation or with tracking off.</returns>
    public unsafe int ReadRomUsage(Span<RomUsage> usage)
    {
        if (usage.IsEmpty)
        {
            return 0;
        }

        fixed (RomUsage* pointer = usage)
        {
            return (int)NativeMethods.ReadRomUsage(handle, (byte*)pointer, (nuint)usage.Length);
        }
    }

    /// <summary>
    /// Bytes of battery backed cartridge RAM, or zero when there is nothing
    /// worth persisting.
    /// </summary>
    public int SaveRamSize => (int)NativeMethods.SaveRamSize(handle);

    /// <summary>Copies the save RAM out, for a host to persist however it likes.</summary>
    public unsafe void ReadSaveRam(Span<byte> destination)
    {
        int size = SaveRamSize;
        if (size == 0)
        {
            return;
        }

        if (destination.Length < size)
        {
            throw new ArgumentException(
                $"The save RAM is {size} bytes and the destination holds {destination.Length}.",
                nameof(destination));
        }

        fixed (byte* bytes = destination)
        {
            NativeMethods.ReadSaveRam(handle, bytes);
        }
    }

    /// <summary>
    /// Loads previously saved RAM back in. Call after <see cref="Load"/>, which
    /// starts the cartridge empty.
    /// </summary>
    public unsafe void WriteSaveRam(ReadOnlySpan<byte> source)
    {
        int size = SaveRamSize;
        if (size == 0)
        {
            return;
        }

        if (source.Length < size)
        {
            throw new ArgumentException(
                $"The save RAM is {size} bytes and the source holds {source.Length}.",
                nameof(source));
        }

        fixed (byte* bytes = source)
        {
            NativeMethods.WriteSaveRam(handle, bytes);
        }
    }

    public void Dispose() => handle.Dispose();
}
