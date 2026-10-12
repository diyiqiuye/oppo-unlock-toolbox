namespace OppoUnlockToolbox.Core.Foundation;

/// <summary>
/// .NET Framework 4.8 缺的几个 BCL 成员，用同名扩展方法补齐：
/// TakeLast / string.Contains(带 StringComparison) / Dictionary.GetValueOrDefault /
/// KeyValuePair 的解构。调用处因此完全不用改。
/// </summary>
public static class Net48Compat
{
    public static IEnumerable<T> TakeLast<T>(this IEnumerable<T> source, int count)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));
        if (count <= 0)
            return Array.Empty<T>();
        var buffer = new Queue<T>(count);
        foreach (var item in source)
        {
            if (buffer.Count == count)
                buffer.Dequeue();
            buffer.Enqueue(item);
        }
        return buffer;
    }

    public static bool Contains(this string? text, string value, StringComparison comparison) =>
        text is not null && text.IndexOf(value, comparison) >= 0;

    public static TValue? GetValueOrDefault<TKey, TValue>(
        this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key) =>
        dictionary.TryGetValue(key, out var value) ? value : default;

    public static TValue GetValueOrDefault<TKey, TValue>(
        this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key, TValue defaultValue) =>
        dictionary.TryGetValue(key, out var value) ? value : defaultValue;

    public static void Deconstruct<TKey, TValue>(
        this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
    {
        key = pair.Key;
        value = pair.Value;
    }
}
