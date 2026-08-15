# Structs

A struct is a layout, and its methods are functions that take a pointer to one. That is the whole model: no object header, no vtable, no hidden copy, and a C output you can read next to the C# that produced it.

## The model

```csharp
public struct Player
{
    public byte X;
    public byte Y;

    public Player(byte x, byte y)
    {
        X = x;
        Y = y;
    }

    public byte Middle => (byte)(X + 4);

    public void Update()
    {
        if (Input.Left) X--;
        if (Input.Right) X++;
    }
}
```

`--emit-c` shows what each member becomes: one C function per method, each taking `self` as a plain pointer:

```c
void Player__ctor(Player* self, uint8_t x, uint8_t y)
{
    self->X = x;
    self->Y = y;
}

uint8_t Player_get_Middle(Player* self)
{
    return (self->X + 4U);
}

void Player_Update(Player* self)
{
    if (gbs_input_left())
    {
        self->X--;
    }
    if (gbs_input_right())
    {
        self->X++;
    }
}
```

Using the struct reads like ordinary C#, and the C underneath stays one-to-one:

```csharp
Player p = new Player(80, 72);
p.Update();
Sprites.Move(0, p.Middle, p.Y);
```

```c
Player__ctor((&p), 80U, 72U);
Player_Update((&p));
gbs_sprite_move(0U, Player_get_Middle((&p)), p.Y);
```

Nothing is inlined and no temporary appears, so a constructor costs one visible call. A property getter is a call too (`Middle` above), which is worth knowing before putting one inside a loop that runs every frame.

## Why `new` must be assigned to a variable

A GB# constructor writes through a pointer to storage that already exists. That is what `Player__ctor((&p), …)` is: a call against the variable it fills. Constructing straight into an argument or a return value would need a temporary GB# invented (stack you cannot see in the build report), so it is refused by name:

```
Program.cs(14,18): error GBS0059: A 'Player' constructor cannot be used here.
    A GB# constructor writes through a pointer to storage that already exists, so it needs a
    variable to fill. Assign it to one first, like 'Point p = new Point(3, 4);', and pass that.
```

Assign first, then pass the variable ([GBS0059](../reference/diagnostics/language.md#gbs0059-constructor-needs-somewhere-to-construct)).

## The one-constructor rule

A struct carries at most one constructor. Two would mangle to the same C name, and the usual fix, a generated suffix, is a name you cannot find again in a linker map. GB#'s output is meant to be traceable from C# to C to the map file and back, and invented names break that chain. If a type genuinely has two ways to be initialised, write a static factory-style method with a name of your own choosing; the name then survives into the C and the map.

## Passing structs around

Small structs pass by value cheaply. A large one passed by value is copied through the stack, and GB# points it out ([GBS0202](../reference/diagnostics/memory.md#gbs0202-large-struct-passed-by-value)). Pass it by `ref` to copy a 2-byte pointer instead. `ref` parameters are in the subset for exactly this reason: they are how systems operate on structs stored elsewhere without copying them.

## Data-oriented code is the house style

Instance members are there when a type genuinely owns its behaviour, not as the default. The more natural GB# style is data-oriented: plain structs held in explicitly bounded storage, with static systems that operate over them. `Samples/Enemies` is written that way: a `FixedList<Enemy>` with `static Update(ref Enemy)` systems over it:

```csharp
[Capacity(8)]
private static FixedList<Enemy> enemies;

private static void UpdateEnemies()
{
    for (byte i = 0; i < enemies.Count; i++)
    {
        EnemySystem.Update(ref enemies[i], frame);
    }
}
```

There is no object graph and no allocation, and the memory layout is visible in the source: eight enemies at four bytes each, plus the list's count, is 33 bytes of WRAM, and the build reports exactly that against the declaration. The [Many objects](../tutorials/many-objects.md) tutorial walks through building a game in this style, and [The language subset](language-subset.md) covers the fixed collections it rests on.
