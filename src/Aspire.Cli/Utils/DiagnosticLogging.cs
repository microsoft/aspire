// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

/// <summary>
/// Helpers for adding diagnostic wait warnings without changing task behavior.
/// </summary>
internal static class DiagnosticLogging
{
    public static async Task<T> WaitWithSlowWarningAsync<T>(Task<T> task, TimeSpan threshold, Action logWarning)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(logWarning);

        using var delayCancellation = new CancellationTokenSource();
        var delayTask = Task.Delay(threshold, delayCancellation.Token);
        if (await Task.WhenAny(task, delayTask).ConfigureAwait(false) == delayTask && !task.IsCompleted)
        {
            logWarning();
        }
        else
        {
            delayCancellation.Cancel();
        }

        return await task.ConfigureAwait(false);
    }

    public static async Task WaitWithSlowWarningAsync(Task task, TimeSpan threshold, Action logWarning)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(logWarning);

        using var delayCancellation = new CancellationTokenSource();
        var delayTask = Task.Delay(threshold, delayCancellation.Token);
        if (await Task.WhenAny(task, delayTask).ConfigureAwait(false) == delayTask && !task.IsCompleted)
        {
            logWarning();
        }
        else
        {
            delayCancellation.Cancel();
        }

        await task.ConfigureAwait(false);
    }
}
