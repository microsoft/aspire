// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Aspire.TestUtilities;

namespace Microsoft.AspNetCore.InternalTesting;

internal static class TestConstants
{
    // IMPORTANT: If a test fails because these time out, consider adding a new field with a larger value.
    // These values are as big as they need to be to test things complete in the expected time.
    // Possible exception: OK to increase the CI multipliers to accommodate slow networks/hardware.

    // Shorter duration when running tests on a dev machine.
    // Less time waiting for hang unit tests to fail in aspnetcore solution.
    public static readonly int DefaultTimeoutDuration = 5 * 1000 * (PlatformDetection.IsRunningOnCI ? 6 : 1); // 5 sec, 30 sec in CI
    public static readonly int LongTimeoutDuration = 60 * 1000 * (PlatformDetection.IsRunningOnCI ? 3 : 1); // 60 sec, 180 sec in CI
    public static readonly int ExtraLongTimeoutDuration = 60 * 1000 * 3 * (PlatformDetection.IsRunningOnCI ? 2 : 1); // 180 sec, 360 sec in CI -- useful when a docker image might need pulling
    public static readonly int DefaultOrchestratorTestTimeout = 15 * 1000 * (PlatformDetection.IsRunningOnCI ? 2 : 1); // 15 sec, 30 sec in CI
    public static readonly int DefaultOrchestratorTestLongTimeout = 45 * 1000 * (PlatformDetection.IsRunningOnCI ? 4 : 1); // 45 sec, 180 sec in CI

    public static TimeSpan DefaultTimeoutTimeSpan { get; } = TimeSpan.FromMilliseconds(DefaultTimeoutDuration);
    public static TimeSpan LongTimeoutTimeSpan { get; } = TimeSpan.FromMilliseconds(LongTimeoutDuration);
    public static TimeSpan ExtraLongTimeoutTimeSpan { get; } = TimeSpan.FromMilliseconds(ExtraLongTimeoutDuration);
}

internal static class AsyncTestHelpers
{
    private static readonly string s_assemblyName = typeof(TimeoutException).Assembly.GetName().Name!;

    public static CancellationTokenSource CreateDefaultTimeoutTokenSource(int milliseconds = -1)
    {
        if (milliseconds == -1)
        {
            milliseconds = TestConstants.DefaultTimeoutDuration;
        }

        var cts = new CancellationTokenSource();
        if (!Debugger.IsAttached)
        {
            cts.CancelAfter(TimeSpan.FromMilliseconds(milliseconds));
        }
        return cts;
    }

    public static async IAsyncEnumerable<T> DefaultTimeout<T>(this IAsyncEnumerable<T> asyncEnumerable, int milliseconds = -1, [CallerFilePath] string? filePath = null, [CallerLineNumber] int lineNumber = default)
    {
        if (milliseconds == -1)
        {
            milliseconds = TestConstants.DefaultTimeoutDuration;
        }

        // Wrap the enumerable with an enumerable that times out after exceeding time limit on each iteration.
        var enumerator = asyncEnumerable.GetAsyncEnumerator();
        try
        {
            while (await enumerator.MoveNextAsync().DefaultTimeout(milliseconds, filePath, lineNumber))
            {
                yield return enumerator.Current;
            }
        }
        finally
        {
            try
            {
                await enumerator.DisposeAsync();
            }
            catch (NotSupportedException)
            {
                // When a timeout fires while the inner async iterator's MoveNextAsync() is still
                // in flight, DisposeAsync() is called on an enumerator with an active MoveNextAsync().
                // Async iterator state machines reject this with NotSupportedException. This is safe
                // to suppress because the enumerator is being abandoned after a timeout anyway.
            }
        }
    }

    public static Task DefaultTimeout(this Task task, int milliseconds = -1, [CallerFilePath] string? filePath = null, [CallerLineNumber] int lineNumber = default)
    {
        if (milliseconds == -1)
        {
            milliseconds = TestConstants.DefaultTimeoutDuration;
        }

        return task.TimeoutAfter(TimeSpan.FromMilliseconds(milliseconds), filePath, lineNumber);
    }

    public static Task DefaultTimeout(this Task task, TimeSpan timeout, [CallerFilePath] string? filePath = null, [CallerLineNumber] int lineNumber = default)
    {
        return task.TimeoutAfter(timeout, filePath, lineNumber);
    }

    public static Task DefaultTimeout(this ValueTask task, int milliseconds = -1, [CallerFilePath] string? filePath = null, [CallerLineNumber] int lineNumber = default)
    {
        if (milliseconds == -1)
        {
            milliseconds = TestConstants.DefaultTimeoutDuration;
        }

        return task.AsTask().TimeoutAfter(TimeSpan.FromMilliseconds(milliseconds), filePath, lineNumber);
    }

    public static Task DefaultTimeout(this ValueTask task, TimeSpan timeout, [CallerFilePath] string? filePath = null, [CallerLineNumber] int lineNumber = default)
    {
        return task.AsTask().TimeoutAfter(timeout, filePath, lineNumber);
    }

    public static Task<T> DefaultTimeout<T>(this Task<T> task, int milliseconds = -1, [CallerFilePath] string? filePath = null, [CallerLineNumber] int lineNumber = default)
    {
        if (milliseconds == -1)
        {
            milliseconds = TestConstants.DefaultTimeoutDuration;
        }

        return task.TimeoutAfter(TimeSpan.FromMilliseconds(milliseconds), filePath, lineNumber);
    }

    public static Task<T> DefaultTimeout<T>(this Task<T> task, TimeSpan timeout, [CallerFilePath] string? filePath = null, [CallerLineNumber] int lineNumber = default)
    {
        return task.TimeoutAfter(timeout, filePath, lineNumber);
    }

    public static Task<T> DefaultTimeout<T>(this ValueTask<T> task, int milliseconds = -1, [CallerFilePath] string? filePath = null, [CallerLineNumber] int lineNumber = default)
    {
        if (milliseconds == -1)
        {
            milliseconds = TestConstants.DefaultTimeoutDuration;
        }

        return task.AsTask().TimeoutAfter(TimeSpan.FromMilliseconds(milliseconds), filePath, lineNumber);
    }

    public static Task<T> DefaultTimeout<T>(this ValueTask<T> task, TimeSpan timeout, [CallerFilePath] string? filePath = null, [CallerLineNumber] int lineNumber = default)
    {
        return task.AsTask().TimeoutAfter(timeout, filePath, lineNumber);
    }

    public static async Task<T> TimeoutAfter<T>(this Task<T> task, TimeSpan timeout,
        [CallerFilePath] string? filePath = null,
        [CallerLineNumber] int lineNumber = default)
    {
        // Don't create a timer if the task is already completed
        // or the debugger is attached
        if (task.IsCompleted || Debugger.IsAttached)
        {
            return await task.ConfigureAwait(false);
        }
#if NET6_0_OR_GREATER
        try
        {
            return await task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException ex) when (ex.Source == s_assemblyName)
        {
            throw new TimeoutException(CreateMessage(timeout, filePath!, lineNumber));
        }
#else
        var cts = new CancellationTokenSource();
        if (task == await Task.WhenAny(task, Task.Delay(timeout, cts.Token)).ConfigureAwait(false))
        {
            cts.Cancel();
            return await task.ConfigureAwait(false);
        }
        else
        {
            throw new TimeoutException(CreateMessage(timeout, filePath, lineNumber));
        }
#endif
    }

    public static async Task TimeoutAfter(this Task task, TimeSpan timeout,
        [CallerFilePath] string? filePath = null,
        [CallerLineNumber] int lineNumber = default)
    {
        // Don't create a timer if the task is already completed
        // or the debugger is attached
        if (task.IsCompleted || Debugger.IsAttached)
        {
            await task.ConfigureAwait(false);
            return;
        }
#if NET6_0_OR_GREATER
        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException ex) when (ex.Source == s_assemblyName)
        {
            throw new TimeoutException(CreateMessage(timeout, filePath!, lineNumber));
        }
#else
        var cts = new CancellationTokenSource();
        if (task == await Task.WhenAny(task, Task.Delay(timeout, cts.Token)).ConfigureAwait(false))
        {
            cts.Cancel();
            await task.ConfigureAwait(false);
        }
        else
        {
            throw new TimeoutException(CreateMessage(timeout, filePath, lineNumber));
        }
#endif
    }

    private static string CreateMessage(TimeSpan timeout, string filePath, int lineNumber)
        => string.IsNullOrEmpty(filePath)
        ? $"The operation timed out after reaching the limit of {timeout.TotalMilliseconds}ms."
        : $"The operation at {filePath}:{lineNumber} timed out after reaching the limit of {timeout.TotalMilliseconds}ms.";

    public static Task AssertIsTrueRetryAsync(Func<bool> assert, string message, ILogger? logger = null, int retries = 10)
    {
        return AssertIsTrueRetryAsync(() => Task.FromResult(assert()), message, logger, retries);
    }

    public static async Task AssertIsTrueRetryAsync(Func<Task<bool>> assert, string message, ILogger? logger = null, int retries = 10)
    {
        logger?.LogInformation("Start: {Message}", message);

        for (var i = 0; i < retries; i++)
        {
            if (i > 0)
            {
                await Task.Delay((i + 1) * (i + 1) * 10 * 5);
            }

            if (await assert())
            {
                logger?.LogInformation("End: {Message}", message);
                return;
            }
        }

        throw new InvalidOperationException($"Assert failed after {retries} retries: {message}");
    }
}
