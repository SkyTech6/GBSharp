# Many objects

You will run eight enemies from fixed storage with no allocation and no object graph, by reading `Samples/Enemies` in the repo, the sample that exercises the language core: structs, enums, fixed collections, arrays, `ref` parameters, `for`, `switch` and 8-bit arithmetic.

The style matters as much as the API. This sample is written the way GB# expects games to be written: plain structs held in explicitly bounded storage, with systems that operate over them. The memory layout is visible in the source.

## Bounded storage

```csharp
[Capacity(8)]
private static FixedList<Enemy> enemies;
```

`FixedList<T>` is the answer GB# gives when you reach for `List<T>` (GBS0042): storage reserved up front, with a live count. It cannot grow, which is the point: the memory it occupies is decided when it is declared, not while the game is running, because there is no allocator to grow into. The thesis writes this as `FixedList<Enemy, 8>`, but C# has no value type parameters, so the capacity travels as an attribute instead. What matters is preserved: the capacity sits at the declaration, in the source, where you can see exactly what it costs.

The capacity also does work the syntax does not advertise. `enemies.Count` is a runtime field, but a `FixedList` refuses to grow past its capacity, so 8 is a ceiling the compiler can prove, and that is what lets the build put a total cost on the update loop below rather than shrugging at it.

## Plain data

```csharp
public enum EnemyKind : byte
{
    Walker = 0,
    Flyer = 1,
    Turret = 2,
}

public struct Enemy
{
    public byte X;
    public byte Y;
    public byte Sprite;
    public EnemyKind Kind;
}
```

An `Enemy` is four bytes, and you can see all four. No base class, no virtual dispatch, no reference to anything: a struct in GB# is a layout, and eight of them in the list is 32 bytes of work RAM plus the count, which the build report will state exactly. The enum is `: byte` for the same reason every position is: this is an 8-bit machine, and the natural word size should be the default, not an optimisation.

## Systems, not methods

```csharp
public static class EnemySystem
{
    public static void Update(ref Enemy enemy, byte frame)
    {
        switch (enemy.Kind)
        {
            case EnemyKind.Walker:
                enemy.X++;
                break;

            case EnemyKind.Flyer:
                enemy.X++;
                // A shift, not a divide: the cost should be obvious from the source.
                enemy.Y = (byte)(64 + ((frame >> 3) & 7));
                break;

            case EnemyKind.Turret:
                break;
        }

        if (enemy.X > 160)
        {
            enemy.X = 0;
        }
    }
}
```

`Update` is a static function taking `ref Enemy`: in the generated C, a function taking a pointer to a four-byte struct. Passing by `ref` means no copy in and no copy out; the function works on the enemy where it lives, inside the list's storage. Behaviour switches on `Kind`, a byte compare, rather than on a vtable that does not exist.

This is the house style rather than instance-heavy OO, and the reason is visibility. An object graph hides its layout, its lifetime and its indirections; this hides nothing: every byte is in a declaration, every call is a plain function, and the cost model can price all of it. Instance members exist in GB# for when a type genuinely owns its behaviour, not as the default shape of a program.

## Driving it

```csharp
public static void Main()
{
    Display.Enable();
    Display.ShowSprites();

    Setup();

    while (true)
    {
        UpdateEnemies();
        Draw();

        frame++;
        Game.WaitVBlank();
    }
}
```

Setup fills the list once:

```csharp
for (byte i = 0; i < 4; i++)
{
    Enemy enemy = new Enemy();
    enemy.X = spawnTable[i];
    enemy.Y = 64;
    enemy.Kind = i < 2 ? EnemyKind.Walker : EnemyKind.Flyer;
    enemy.Sprite = i;

    enemies.Add(enemy);
}
```

`Add` copies the struct into the list's storage and returns `false` when the list is full, since there is nowhere to grow into, so the caller decides what full means. Note each enemy remembers which hardware sprite is its own; nothing maps objects to sprites for you.

Update and draw are each one loop over the same storage:

```csharp
private static void UpdateEnemies()
{
    for (byte i = 0; i < enemies.Count; i++)
    {
        EnemySystem.Update(ref enemies[i], frame);
    }
}

private static void Draw()
{
    for (byte i = 0; i < enemies.Count; i++)
    {
        Sprites.Move(enemies[i].Sprite, enemies[i].X, enemies[i].Y);
    }
}
```

The list's indexer returns by reference, so `ref enemies[i]` hands `Update` a pointer straight into the array, with no copies anywhere in the frame. `Sprites.Move` sets a sprite's X and Y in one call, one OAM write cheaper than assigning the two properties separately. And because the capacity bounds `Count`, the build can tell you what the whole loop costs: this shape of loop is exactly what the GBS0410 estimate prices, up to 8 iterations at a stated cost each.

Updating and drawing are separate loops on purpose. Update touches game state; Draw touches OAM. Keeping the OAM writes together, next to the `WaitVBlank`, is the habit that scales: when a game grows enough that VRAM timing matters, the code that must land in VBlank is already in one place.

## Run it

```
gbsharp run Samples/Enemies
```

Four enemies drift rightward and wrap at the screen edge; two of them, the flyers, bob vertically on a period set by the frame counter. As with `MoveSprite`, the sample loads no artwork, so watch the motion rather than the pixels; the build report is the other half of the output. Read it: the WRAM line itemises the list's 33 bytes, and the cycle estimates price `UpdateEnemies` and `Draw` against the 70,224-cycle frame.

## Where to go next

- [Structs in GB#](../guides/structs.md): constructors, properties and what they compile to.
- [The language subset](../guides/language-subset.md): why `List<T>` is refused and what stands in for it.
- [Banking a big game](banking-a-big-game.md): when the game outgrows 32 KB.
