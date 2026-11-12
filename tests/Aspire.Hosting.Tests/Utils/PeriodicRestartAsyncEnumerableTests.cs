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

    [Fact]
    public async Task FactoryIsRecreatedWhenRestartTokenExpires()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var secondFactoryCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCallCount = 0;

        async Task<IAsyncEnumerable<int>> InnerFactory(int? lastValue, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref factoryCallCount) == 2)
            {
                secondFactoryCall.SetResult();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CountingAsyncEnumerable(0, TimeSpan.Zero, cancellationToken);
        }

        await using var enumerator = PeriodicRestartAsyncEnumerable
            .CreateAsync<int>(InnerFactory, restartInterval: TimeSpan.FromMilliseconds(100), cancellationToken: cts.Token)
            .GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync().AsTask();

        await secondFactoryCall.Task.WaitAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNextTask);
    }

    [Fact]
    public async Task ClassFactoryIsRecreatedWhenRestartTokenExpires()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var secondFactoryCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCallCount = 0;

        async Task<IAsyncEnumerable<string>> InnerFactory(string? lastValue, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref factoryCallCount) == 2)
            {
                secondFactoryCall.SetResult();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return EmptyStringAsyncEnumerable();
        }

        await using var enumerator = PeriodicRestartAsyncEnumerable
            .CreateAsync<string>(InnerFactory, restartInterval: TimeSpan.FromMilliseconds(100), cancellationToken: cts.Token)
            .GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync().AsTask();

        await secondFactoryCall.Task.WaitAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNextTask);
    }

    [Fact]
    public async Task FactoryCancellationFromIndependentTokenAfterRestartExpirationIsPropagated()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var factoryCts = new CancellationTokenSource();
        factoryCts.Cancel();
        var factoryCallCount = 0;

        var enumerable = PeriodicRestartAsyncEnumerable.CreateAsync<int>(
            async (_, restartToken) =>
            {
                Assert.Equal(1, Interlocked.Increment(ref factoryCallCount));

                var restartTokenCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = restartToken.Register(restartTokenCancelled.SetResult);
                await restartTokenCancelled.Task.WaitAsync(cts.Token);

                factoryCts.Token.ThrowIfCancellationRequested();
                return CountingAsyncEnumerable(0, TimeSpan.Zero, restartToken);
            },
            restartInterval: TimeSpan.FromMilliseconds(100),
            cancellationToken: cts.Token);
        await using var enumerator = enumerable.GetAsyncEnumerator();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await enumerator.MoveNextAsync().AsTask().WaitAsync(cts.Token));

        Assert.Equal(factoryCts.Token, exception.CancellationToken);
        Assert.Equal(1, factoryCallCount);
    }

    [Fact]
    public async Task ClassFactoryCancellationFromIndependentTokenAfterRestartExpirationIsPropagated()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var factoryCts = new CancellationTokenSource();
        factoryCts.Cancel();
        var factoryCallCount = 0;

        var enumerable = PeriodicRestartAsyncEnumerable.CreateAsync<string>(
            async (_, restartToken) =>
            {
                Assert.Equal(1, Interlocked.Increment(ref factoryCallCount));

                var restartTokenCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = restartToken.Register(restartTokenCancelled.SetResult);
                await restartTokenCancelled.Task.WaitAsync(cts.Token);

                factoryCts.Token.ThrowIfCancellationRequested();
                return EmptyStringAsyncEnumerable();
            },
            restartInterval: TimeSpan.FromMilliseconds(100),
            cancellationToken: cts.Token);
        await using var enumerator = enumerable.GetAsyncEnumerator();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await enumerator.MoveNextAsync().AsTask().WaitAsync(cts.Token));

        Assert.Equal(factoryCts.Token, exception.CancellationToken);
        Assert.Equal(1, factoryCallCount);
    }

    [Fact]
    public async Task EnumeratorCancellationFromIndependentTokenAfterRestartExpirationIsPropagated()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var enumeratorCts = new CancellationTokenSource();
        enumeratorCts.Cancel();
        var factoryCallCount = 0;
        var disposeCount = 0;

        Task<IAsyncEnumerable<int>> InnerFactory(int? lastValue, CancellationToken restartToken)
        {
            Assert.Equal(1, Interlocked.Increment(ref factoryCallCount));
            return Task.FromResult(ThrowAfterCancellationAsync(0, restartToken, enumeratorCts.Token, () => Interlocked.Increment(ref disposeCount)));
        }

        var enumerable = PeriodicRestartAsyncEnumerable.CreateAsync<int>(
            InnerFactory,
            restartInterval: TimeSpan.FromMilliseconds(100),
            cancellationToken: cts.Token);
        await using var enumerator = enumerable.GetAsyncEnumerator();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await enumerator.MoveNextAsync().AsTask().WaitAsync(cts.Token));

        Assert.Equal(enumeratorCts.Token, exception.CancellationToken);
        Assert.Equal(1, factoryCallCount);
        Assert.Equal(1, disposeCount);
    }

    [Fact]
    public async Task ClassEnumeratorCancellationFromIndependentTokenAfterRestartExpirationIsPropagated()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var enumeratorCts = new CancellationTokenSource();
        enumeratorCts.Cancel();
        var factoryCallCount = 0;
        var disposeCount = 0;

        Task<IAsyncEnumerable<string>> InnerFactory(string? lastValue, CancellationToken restartToken)
        {
            Assert.Equal(1, Interlocked.Increment(ref factoryCallCount));
            return Task.FromResult(ThrowAfterCancellationAsync(string.Empty, restartToken, enumeratorCts.Token, () => Interlocked.Increment(ref disposeCount)));
        }

        var enumerable = PeriodicRestartAsyncEnumerable.CreateAsync<string>(
            InnerFactory,
            restartInterval: TimeSpan.FromMilliseconds(100),
            cancellationToken: cts.Token);
        await using var enumerator = enumerable.GetAsyncEnumerator();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await enumerator.MoveNextAsync().AsTask().WaitAsync(cts.Token));

        Assert.Equal(enumeratorCts.Token, exception.CancellationToken);
        Assert.Equal(1, factoryCallCount);
        Assert.Equal(1, disposeCount);
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

    static async IAsyncEnumerable<string> EmptyStringAsyncEnumerable()
    {
        await Task.Yield();
        yield break;
    }

    static async IAsyncEnumerable<T> ThrowAfterCancellationAsync<T>(
        T value,
        [EnumeratorCancellation] CancellationToken restartToken,
        CancellationToken exceptionToken,
        Action onDispose)
    {
        var restartTokenCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = restartToken.Register(restartTokenCancelled.SetResult);

        try
        {
            await restartTokenCancelled.Task;
            exceptionToken.ThrowIfCancellationRequested();
            yield return value;
        }
        finally
        {
            onDispose();
        }
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
