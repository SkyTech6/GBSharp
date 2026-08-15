using System;

namespace GB;

/// <summary>
/// The capacity of a fixed-capacity collection, fixed at compile time.
/// </summary>
/// <remarks>
/// <para>
/// Thesis section 11 writes this as <c>FixedList&lt;Enemy, 8&gt;</c>. C# has no
/// value type parameters, so that is not valid C#, and GB# will not invent a
/// dialect that only looks like C#. The capacity therefore travels as an
/// attribute on the declaration.
/// </para>
/// <para>
/// What matters is preserved: the capacity is written at the declaration, in the
/// source, where the developer reading the field can see exactly how much
/// memory it costs.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class CapacityAttribute : Attribute
{
    public CapacityAttribute(int capacity)
    {
        Capacity = capacity;
    }

    public int Capacity { get; }
}

/// <summary>
/// A fixed-length array of <typeparamref name="T"/>.
/// </summary>
/// <remarks>
/// Specialises at compile time: each distinct element type and capacity becomes
/// its own emitted C struct, so there is no runtime generic machinery and no
/// indirection (thesis section 11).
/// </remarks>
/// <example>
/// <code>
/// [Capacity(16)]
/// private static FixedArray&lt;Enemy&gt; enemies;
/// </code>
/// </example>
public struct FixedArray<T>
    where T : struct
{
    /// <summary>The capacity, known at compile time and folded into a constant.</summary>
    public readonly byte Length => throw FrameworkOnly.Declaration();

    public ref T this[byte index] => throw FrameworkOnly.Declaration();
}

/// <summary>
/// A fixed-capacity list: storage reserved up front, with a live count.
/// </summary>
/// <remarks>
/// This is the answer GBS0042 points <c>List&lt;T&gt;</c> at. It cannot grow,
/// which is the point: the memory it occupies is decided when it is declared,
/// not while the game is running.
/// </remarks>
/// <example>
/// <code>
/// [Capacity(8)]
/// private static FixedList&lt;Enemy&gt; enemies;
/// </code>
/// </example>
public struct FixedList<T>
    where T : struct
{
    /// <summary>How many items the list currently holds.</summary>
    public readonly byte Count => throw FrameworkOnly.Declaration();

    /// <summary>The capacity, known at compile time and folded into a constant.</summary>
    public readonly byte Capacity => throw FrameworkOnly.Declaration();

    public ref T this[byte index] => throw FrameworkOnly.Declaration();

    /// <summary>
    /// Appends an item. Returns false when the list is full: there is nowhere
    /// to grow into, so the caller decides what that means.
    /// </summary>
    public bool Add(T item) => throw FrameworkOnly.Declaration();

    /// <summary>
    /// Removes the item at <paramref name="index"/> by moving the last item
    /// into its place. Cheap, and does not preserve order.
    /// </summary>
    public void RemoveAt(byte index) => throw FrameworkOnly.Declaration();

    /// <summary>Sets the count to zero. The storage is untouched.</summary>
    public void Clear() => throw FrameworkOnly.Declaration();
}
