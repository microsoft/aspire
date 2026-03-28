// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;

namespace Aspire.Hosting.Utils;

internal static class ConcurrencyUtils
{
    public static Action Once(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var invoked = 0;

        return () =>
        {
            if (Interlocked.CompareExchange(ref invoked, 1, 0) != 0)
            {
                return;
            }

            action();
        };
    }

    public static void AddRange<T>(this ConcurrentBag<T> bag, IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(bag);
        ArgumentNullException.ThrowIfNull(items);

        foreach (var item in items)
        {
            bag.Add(item);
        }
    }
}