// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

/// <summary>
/// Extension methods for <see cref="IEnumerable{T}"/>.
/// </summary>
internal static class EnumerableExtensions
{
    /// <summary>
    /// Creates a dictionary from a sequence, keeping the first element for each key when duplicates exist.
    /// </summary>
    public static Dictionary<TKey, TValue> ToDistinctDictionary<TSource, TKey, TValue>(
        this IEnumerable<TSource> source,
        Func<TSource, TKey> keySelector,
        Func<TSource, TValue> valueSelector) where TKey : notnull
    {
        var dictionary = new Dictionary<TKey, TValue>();
        foreach (var item in source)
        {
            dictionary.TryAdd(keySelector(item), valueSelector(item));
        }
        return dictionary;
    }
}
