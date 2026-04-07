// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Ats;

/// <summary>
/// ATS intrinsic collection capabilities for Dict and List operations.
/// </summary>
/// <remarks>
/// <para>
/// These capabilities provide first-class support for mutable collections in polyglot app hosts.
/// Guest languages wrap these in idiomatic collection classes (e.g., AtsDict, AtsList in TypeScript).
/// </para>
/// <para>
/// <strong>Design:</strong>
/// <list type="bullet">
///   <item><description>Mutable collections (Dictionary, List) return handles to the collection</description></item>
///   <item><description>Immutable collections (IReadOnlyDictionary, IReadOnlyList, arrays) return serialized copies</description></item>
///   <item><description>All operations are async (round-trip to host)</description></item>
/// </list>
/// </para>
/// </remarks>
internal static class CollectionExports
{
    #region Dictionary Operations

    /// <summary>
    /// Gets a value from a dictionary by key.
    /// </summary>
    /// <param name="dict">The dictionary handle.</param>
    /// <param name="key">The key to look up.</param>
    /// <returns>The value, or null if not found.</returns>
    [AspireExport("Dict.get", Description = "Gets a value from a dictionary")]
    public static object? DictGet(this IDictionary<string, object> dict, string key)
        => dict.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Sets a value in a dictionary.
    /// </summary>
    /// <param name="dict">The dictionary handle.</param>
    /// <param name="key">The key to set.</param>
    /// <param name="value">The value to set.</param>
    [AspireExport("Dict.set", Description = "Sets a value in a dictionary")]
    public static void DictSet(this IDictionary<string, object> dict, string key, object value)
        => dict[key] = value;

    /// <summary>
    /// Removes a key from a dictionary.
    /// </summary>
    /// <param name="dict">The dictionary handle.</param>
    /// <param name="key">The key to remove.</param>
    /// <returns>True if the key was removed, false if not found.</returns>
    [AspireExport("Dict.remove", Description = "Removes a key from a dictionary")]
    public static bool DictRemove(this IDictionary<string, object> dict, string key)
        => dict.Remove(key);

    /// <summary>
    /// Gets all keys from a dictionary.
    /// </summary>
    /// <param name="dict">The dictionary handle.</param>
    /// <returns>An array of all keys.</returns>
    [AspireExport("Dict.keys", Description = "Gets all keys from a dictionary")]
    public static string[] DictKeys(this IDictionary<string, object> dict)
        => [.. dict.Keys];

    /// <summary>
    /// Checks if a dictionary contains a key.
    /// </summary>
    /// <param name="dict">The dictionary handle.</param>
    /// <param name="key">The key to check.</param>
    /// <returns>True if the key exists.</returns>
    [AspireExport("Dict.has", Description = "Checks if a dictionary contains a key")]
    public static bool DictHas(this IDictionary<string, object> dict, string key)
        => dict.ContainsKey(key);

    /// <summary>
    /// Gets the number of entries in a dictionary.
    /// </summary>
    /// <param name="dict">The dictionary handle.</param>
    /// <returns>The number of key-value pairs.</returns>
    [AspireExport("Dict.count", Description = "Gets the number of entries in a dictionary")]
    public static int DictCount(this IDictionary<string, object> dict)
        => dict.Count;

    /// <summary>
    /// Clears all entries from a dictionary.
    /// </summary>
    /// <param name="dict">The dictionary handle.</param>
    [AspireExport("Dict.clear", Description = "Clears all entries from a dictionary")]
    public static void DictClear(this IDictionary<string, object> dict)
        => dict.Clear();

    /// <summary>
    /// Gets all values from a dictionary.
    /// </summary>
    /// <param name="dict">The dictionary handle.</param>
    /// <returns>An array of all values.</returns>
    [AspireExport("Dict.values", Description = "Gets all values from a dictionary")]
    public static object[] DictValues(this IDictionary<string, object> dict)
        => [.. dict.Values];

    /// <summary>
    /// Converts the dictionary to a plain object (creates a copy).
    /// </summary>
    /// <param name="dict">The dictionary handle.</param>
    /// <returns>A copy of the dictionary as an object.</returns>
    [AspireExport("Dict.toObject", Description = "Converts a dictionary to a plain object")]
    public static Dictionary<string, object> DictToObject(this IDictionary<string, object> dict)
        => new(dict);

    #endregion

    #region List Operations

    /// <summary>
    /// Gets an item from a list by index.
    /// </summary>
    /// <param name="list">The list handle.</param>
    /// <param name="index">The zero-based index.</param>
    /// <returns>The item at the specified index.</returns>
    [AspireExport("List.get", Description = "Gets an item from a list by index")]
    public static object? ListGet(this IList<object> list, int index)
        => index >= 0 && index < list.Count ? list[index] : null;

    /// <summary>
    /// Sets an item in a list at a specific index.
    /// </summary>
    /// <param name="list">The list handle.</param>
    /// <param name="index">The zero-based index.</param>
    /// <param name="value">The value to set.</param>
    [AspireExport("List.set", Description = "Sets an item in a list at a specific index")]
    public static void ListSet(this IList<object> list, int index, object value)
    {
        if (index >= 0 && index < list.Count)
        {
            list[index] = value;
        }
    }

    /// <summary>
    /// Adds an item to the end of a list.
    /// </summary>
    /// <param name="list">The list handle.</param>
    /// <param name="item">The item to add.</param>
    [AspireExport("List.add", Description = "Adds an item to the end of a list")]
    public static void ListAdd(this IList<object> list, object item)
        => list.Add(item);

    /// <summary>
    /// Removes an item at a specific index from a list.
    /// </summary>
    /// <param name="list">The list handle.</param>
    /// <param name="index">The zero-based index of the item to remove.</param>
    /// <returns>True if the item was removed.</returns>
    [AspireExport("List.removeAt", Description = "Removes an item at a specific index from a list")]
    public static bool ListRemoveAt(this IList<object> list, int index)
    {
        if (index >= 0 && index < list.Count)
        {
            list.RemoveAt(index);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Gets the number of items in a list.
    /// </summary>
    /// <param name="list">The list handle.</param>
    /// <returns>The number of items.</returns>
    [AspireExport("List.length", Description = "Gets the number of items in a list")]
    public static int ListLength(this IList<object> list)
        => list.Count;

    /// <summary>
    /// Clears all items from a list.
    /// </summary>
    /// <param name="list">The list handle.</param>
    [AspireExport("List.clear", Description = "Clears all items from a list")]
    public static void ListClear(this IList<object> list)
        => list.Clear();

    /// <summary>
    /// Inserts an item at a specific index in a list.
    /// </summary>
    /// <param name="list">The list handle.</param>
    /// <param name="index">The zero-based index at which to insert.</param>
    /// <param name="item">The item to insert.</param>
    [AspireExport("List.insert", Description = "Inserts an item at a specific index in a list")]
    public static void ListInsert(this IList<object> list, int index, object item)
    {
        if (index >= 0 && index <= list.Count)
        {
            list.Insert(index, item);
        }
    }

    /// <summary>
    /// Gets the index of an item in a list.
    /// </summary>
    /// <param name="list">The list handle.</param>
    /// <param name="item">The item to find.</param>
    /// <returns>The zero-based index, or -1 if not found.</returns>
    [AspireExport("List.indexOf", Description = "Gets the index of an item in a list")]
    public static int ListIndexOf(this IList<object> list, object item)
        => list.IndexOf(item);

    /// <summary>
    /// Converts the list to an array (creates a copy).
    /// </summary>
    /// <param name="list">The list handle.</param>
    /// <returns>An array containing all items.</returns>
    [AspireExport("List.toArray", Description = "Converts a list to an array")]
    public static object[] ListToArray(this IList<object> list)
        => [.. list];

    #endregion
}
