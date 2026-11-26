using System.Collections;
using System.Runtime.CompilerServices;

namespace Juice.Observability.Notifications.Migration
{
    /// <summary>A read-only list compared by its items, in order, so the definitions holding one compare by value (the round trip is checked by equality).</summary>
    [CollectionBuilder(typeof(ValueList), nameof(ValueList.Create))]
    public sealed class ValueList<T>(IEnumerable<T> items) : IReadOnlyList<T>, IEquatable<ValueList<T>>
    {
        private readonly T[] _items = [.. items];

        public static readonly ValueList<T> Empty = new([]);

        public T this[int index] => _items[index];

        public int Count => _items.Length;

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

        public bool Equals(ValueList<T>? other) => other is not null && _items.SequenceEqual(other._items);

        public override bool Equals(object? obj) => obj is ValueList<T> other && Equals(other);

        public override int GetHashCode() => _items.Aggregate(new HashCode(), (h, i) => { h.Add(i); return h; }).ToHashCode();

        public override string ToString() => "[" + string.Join(", ", _items) + "]";

        public static implicit operator ValueList<T>(T[] items) => new(items);

        public static implicit operator ValueList<T>(List<T> items) => new(items);
    }

    /// <summary>Builds a <see cref="ValueList{T}"/> from a collection expression.</summary>
    public static class ValueList
    {
        public static ValueList<T> Create<T>(ReadOnlySpan<T> items) => new(items.ToArray());
    }
}
