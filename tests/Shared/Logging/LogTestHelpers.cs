// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.Extensions.Logging.Testing;

/// <summary>
/// Helper methods for testing log output.
/// </summary>
public static class LogTestHelpers
{
    /// <summary>
    /// Gets a value from structured log state by key.
    /// </summary>
    /// <param name="context">The write context from the test sink.</param>
    /// <param name="key">The key to look up (e.g., "DashboardUrl" or "{OriginalFormat}").</param>
    /// <returns>The value associated with the key, or null if not found.</returns>
    public static object? GetValue(WriteContext context, string key)
    {
        return GetValue(context.State, key);
    }

    /// <summary>
    /// Gets a value from structured log state by key.
    /// </summary>
    /// <param name="state">The log state object, typically from <see cref="WriteContext.State"/>.</param>
    /// <param name="key">The key to look up (e.g., "DashboardUrl" or "{OriginalFormat}").</param>
    /// <returns>The value associated with the key, or null if not found.</returns>
    public static object? GetValue(object? state, string key)
    {
        var list = state as IReadOnlyList<KeyValuePair<string, object?>>;
        return list?.SingleOrDefault(kvp => kvp.Key == key).Value;
    }
}
