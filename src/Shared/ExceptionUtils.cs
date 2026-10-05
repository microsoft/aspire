// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Utils;

internal static class ExceptionUtils
{
    public static IEnumerable<Exception> EnumerateSelfAndInnerExceptions(Exception exception)
    {
        yield return exception;

        if (exception is AggregateException aggregateException)
        {
            foreach (var innerException in aggregateException.InnerExceptions)
            {
                foreach (var nestedException in EnumerateSelfAndInnerExceptions(innerException))
                {
                    yield return nestedException;
                }
            }

            yield break;
        }

        if (exception.InnerException is { } inner)
        {
            foreach (var innerException in EnumerateSelfAndInnerExceptions(inner))
            {
                yield return innerException;
            }
        }
    }
}
