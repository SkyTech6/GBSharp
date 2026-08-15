# Move a sprite

You will build the smallest complete GB# program (display on, one hardware sprite, joypad input, a frame loop) by reading `Samples/MoveSprite` in the repo, which is the thesis MVP verbatim.

The samples are in the [GB# repository](https://github.com/SkyTech6/GBSharp), so clone it once to follow along and to run them; the installed `gbsharp` tool builds them from the clone with no further setup. Every tutorial here works the same way.

This is the program the whole compiler architecture was aimed at. Every line of it exercises something real: turning the LCD on, reading the joypad, writing to OAM through a typed indexer, and pacing the loop against the hardware's own frame rate.

## The whole program

```csharp
using GB;
using static GB.Hardware;

public static class Program
{
    public static void Main()
    {
        Display.Enable();

        byte x = 80;

        while (true)
        {
            if (Input.Right)
                x++;

            Sprites[0].X = x;

            Game.WaitVBlank();
        }
    }
}
```

That is the entire sample. Walk it top to bottom.

## The two usings

```csharp
using GB;
using static GB.Hardware;
```

`GB` is the framework namespace: `Display`, `Input`, `Game` and everything else live there. The second line is what makes `Sprites[0]` legal: C# has no static indexers, so `Sprites` has to be a value rather than a type for indexing to bind, and `Hardware.Sprites` is that value. It costs nothing, since the handle types erase entirely during lowering, leaving only the sprite index in the generated C.

## Turning the screen on

```csharp
Display.Enable();
```

The Game Boy's LCD controller starts wherever the boot ROM left it. `Display.Enable()` sets the bit that turns the LCD on; it compiles to a single register write. There is no window to create and no surface to acquire, because the hardware has exactly one screen and it is always the same 160x144 pixels.

## Position as a byte

```csharp
byte x = 80;
```

The screen is 160 pixels wide, so a `byte` holds any position on it with room to spare. GB# keeps this arithmetic 8-bit all the way down: the SM83 is an 8-bit CPU, and every promotion to 16 bits is work it has to do one half at a time. Choosing `byte` here is not a style preference; it is the difference between one instruction and several.

## The frame loop

```csharp
while (true)
{
    if (Input.Right)
        x++;

    Sprites[0].X = x;

    Game.WaitVBlank();
}
```

`while (true)` is the canonical GB# game loop. A Game Boy game never returns from `Main`; it runs until the power switch says otherwise.

`Input.Right` reads the joypad register directly at the point of use and answers as a `bool`. Each property read is one register read, so a loop that tests many buttons can sample them all at once with `Input.Read()` instead; this loop tests one, so the property is the right tool.

`Sprites[0].X = x` is the line the thesis set as the target. It looks like an indexer into a collection followed by a property assignment, and in most C# that chain would allocate a bounds check, a temporary, and two calls. Here the whole chain erases to a single OAM store:

```c
gbs_sprite_set_x(0U, x);
```

Sprite 0 is the first of the 40 hardware sprites in OAM (object attribute memory), the small table the video hardware walks every scanline to decide what to draw. Writing a sprite's X is writing one byte of that table. Note the hardware's convention: screen X plus 8, so a sprite at X = 0 is fully off the left edge, and the starting value of 80 puts it a little left of centre.

`Game.WaitVBlank()` blocks until the next vertical blank, the gap between one frame being drawn and the next starting, which is the only time OAM and VRAM are safe to touch. It is also what paces the loop: the hardware refreshes at 59.7 Hz, so one pass through this loop is one frame, and holding Right moves the sprite exactly one pixel per frame. Without the wait, this would be a spin loop running the CPU flat out and racing the video hardware for memory it is not allowed to win.

## Run it

```
gbsharp run Samples/MoveSprite
```

The GB# Player opens straight into the ROM. This sample is deliberately minimal: it loads no tile artwork and never sets the sprite's Y, because its job is to prove the input-to-OAM chain, not to draw a character. Hold Right and sprite 0's X register climbs one pixel per frame; the repo's own integration tests run this exact loop and read the movement back out of OAM. For a sprite you can watch walk around, the metasprite tutorial is the next step.

Build it with `--emit-c` to see the generated C, and read the build report at the end of every build: this program's frame loop costs a rounding error against the 70,224 cycles a frame gives you.

## Where to go next

- [Backgrounds and tilemaps](backgrounds-and-tilemaps.md): put artwork behind the sprite.
- [Metasprites](metasprites.md): a character made of several sprites, animated.
- [The language subset](../guides/language-subset.md): what GB# accepts and why.
