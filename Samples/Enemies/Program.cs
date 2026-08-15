using GB;
using static GB.Hardware;

/// <summary>
/// Exercises the Phase 1 language core: structs, enums, fixed collections,
/// arrays, ref parameters, for, switch and 8-bit arithmetic.
/// </summary>
/// <remarks>
/// Written in the data-oriented style of thesis section 12: plain structs held
/// in explicitly bounded storage, with systems that operate over them. There is
/// no object graph and no allocation, and the memory layout is visible in the
/// source.
/// </remarks>
public static class Program
{
    [Capacity(8)]
    private static FixedList<Enemy> enemies;

    private static byte[] spawnTable = new byte[4];

    private static byte frame;

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

    private static void Setup()
    {
        spawnTable[0] = 40;
        spawnTable[1] = 72;
        spawnTable[2] = 104;
        spawnTable[3] = 136;

        for (byte i = 0; i < 4; i++)
        {
            Enemy enemy = new Enemy();
            enemy.X = spawnTable[i];
            enemy.Y = 64;
            enemy.Kind = i < 2 ? EnemyKind.Walker : EnemyKind.Flyer;
            enemy.Sprite = i;

            enemies.Add(enemy);
        }
    }

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
}

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
