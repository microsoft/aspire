// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests.Utils;

[Trait("Partition", "4")]
public class PeriodicRestartAsyncEnumerableTests
{
    [Fact]
    public async Task CancellingMainTokenCancelsEnumerable()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(1));
        var start = DateTime.UtcNow;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var innerFactory = (int? lastValue, CancellationToken cancellationToken) => Task.FromResult(CountingAsyncEnumerable(0, TimeSpan.FromMilliseconds(50), cancellationToken));

            await foreach (var _ in PeriodicRestartAsyncEnumerable.CreateAsync(innerFactory, restartInterval: TimeSpan.FromSeconds(2), cancellationToken: cts.Token).ConfigureAwait(false))
            {
                if (DateTime.UtcNow - start > TimeSpan.FromSeconds(2))
                {
                    Assert.Fail("expected cancellation after 1 second");
                }
            }
        });
    }

    private static int s_totalEnumerablesRun;
    private static int s_activeRunningEnumerables;

    [Fact]
    public async Task EnumerableIsRecreatedPeriodically()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(1));
        var start = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var innerFactory = (int? lastValue, CancellationToken cancellationToken) => Task.FromResult(RefCountingAsyncEnumerable(0, TimeSpan.FromMilliseconds(10), cancellationToken));

            await foreach (var _ in PeriodicRestartAsyncEnumerable.CreateAsync(innerFactory, restartInterval: TimeSpan.FromMilliseconds(100), cancellationToken: cts.Token).ConfigureAwait(false))
            {
                if (DateTime.UtcNow - start > TimeSpan.FromSeconds(2))
                {
                    Assert.Fail("expected cancellation after 1 second");
                }
            }
        });

        Assert.True(s_totalEnumerablesRun > 1, "expected additional iteration runs");
        Assert.True(s_activeRunningEnumerables == 0, "expected all enumerables to be ended after cancellation");
    }

    static async IAsyncEnumerable<int> CountingAsyncEnumerable(int start, TimeSpan delay, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var value = start;
        while (!cancellationToken.IsCancellationRequested)
        {
            yield return value++;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    static async IAsyncEnumerable<int> RefCountingAsyncEnumerable(int start, TimeSpan delay, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref s_totalEnumerablesRun);
        Interlocked.Increment(ref s_activeRunningEnumerables);

        try
        {
            await foreach (var innerValue in CountingAsyncEnumerable(start, delay, cancellationToken).ConfigureAwait(false))
            {
                yield return innerValue;
            }
        }
        finally
        {
            Interlocked.Decrement(ref s_activeRunningEnumerables);
        }
    }
}
