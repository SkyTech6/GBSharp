# Audio

The `Audio` class is the sound hardware at register level, and that is deliberate. It writes the APU registers and stops there: a note plays until something stops it, nothing is sequenced, and there is no mixing. A game that wants music wants a tracker driver, and that is a library rather than a framework concern: GB# abstracts boilerplate, not hardware, and a music engine would be a policy layer pretending to be a wrapper.

## The channels

The Game Boy has four sound channels, named by the `Channel` enum:

- **`Channel.Pulse1`** is a square wave with a frequency sweep.
- **`Channel.Pulse2`** is a square wave without a sweep.
- **`Channel.Wave`** plays a 32-sample waveform from wave RAM.
- **`Channel.Noise`** is pseudo-random noise, for percussion and effects.

Square waves also take a `Duty` (how much of the wave's period is spent high): `Eighth`, `Quarter`, `Half` or `ThreeQuarters`. Lower duties sound thinner and reedier; `Half` is the classic square.

## Notes are register values

`Note` names pitches as the value the frequency registers actually take. These are periods, not frequencies: the hardware wants `2048 - 131072/Hz`, and that is what the members hold, so higher values are higher pitches and the spacing is not linear. The enum covers C3 through B6.

The enum is backed by `ushort` so a note used at a call site folds to a literal during lowering. There is no note table in ROM and no lookup at runtime: writing `Note.A4` costs exactly what writing `1750` would. The name is free.

## What exists

```csharp
Audio.Enable();
Audio.PlayTone(Channel.Pulse1, Note.A4, 15, Duty.Half);
```

- **`Enable()`** powers up the APU and enables all channels on both speakers. The sound hardware ignores every other register while it is off, so this has to come first.
- **`Disable()`** powers it down, silencing everything and clearing its registers.
- **`SetMasterVolume(left, right)`** sets each speaker's volume, 0–7.
- **`SetRouting(mask)`** sets which channels reach which speaker: the low nibble routes channels 1–4 to the right speaker, the high nibble to the left, `0xFF` is everything on both.
- **`PlayTone(channel, note, volume, duty)`** starts a note on a square-wave channel. Only `Pulse1` and `Pulse2` do anything, and the note plays until `Stop`. The channel is a runtime value, so this costs a branch in the shim on top of the register writes.
- **`PlayNoise(volume, period)`** starts noise on channel 4. `period` is the raw polynomial-counter byte: lower values are brighter. Useful for hits and explosions.
- **`Stop(channel)`** silences one channel by dropping its envelope to zero.

## What does not exist, and where to go instead

There is no music driver, no sequencer, no sound-effect engine, and no per-frame envelope handling beyond what the hardware does itself. A note started with `PlayTone` sounds until stopped; anything resembling a melody is your frame loop's job, one register write at a time.

That gap is intentional, and it has a supported exit. Framework members reach the hardware through `[Native]`, and your code can use exactly the same mechanism: there is no privileged path. A hUGEDriver-style music driver is C code supplied under `"libraries"` in `gbsharp.json`, a header under `"includes"`, and a static class of `[Native]` declarations to call it from C#. [The native escape hatch](native-escape-hatch.md) walks through the pattern, and the [gbsharp.json reference](../reference/gbsharp-json.md) documents the two keys.

When a driver like that lands, it will be a library you add, not a framework release you wait for, which is the point of the escape hatch existing.
