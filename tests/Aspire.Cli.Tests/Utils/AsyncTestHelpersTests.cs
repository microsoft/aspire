// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Utils;

public class AsyncTestHelpersTests
{
    [Fact]
    public async Task WithDisposeTimeout_CreatesAndDisposesInstance()
    {
        var instance = new TestAsyncDisposable(() => ValueTask.CompletedTask);
        var factoryCalls = 0;

        await using (var lifetime = AsyncTestHelpers.WithDisposeTimeout(() =>
        {
            factoryCalls++;
            return instance;
        }))
        {
            Assert.Same(instance, lifetime.Instance);
            Assert.Equal(1, factoryCalls);
            Assert.Equal(0, instance.DisposeCount);
        }

        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, instance.DisposeCount);
    }

    [Fact]
    public async Task WithDisposeTimeout_WaitsForAsynchronousDisposal()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var instance = new TestAsyncDisposable(async () =>
        {
            started.TrySetResult();
            await release.Task.ConfigureAwait(false);
        });
        var lifetime = AsyncTestHelpers.WithDisposeTimeout(() => instance);
        var disposal = lifetime.DisposeAsync().AsTask();
        try
        {
            await started.Task.DefaultTimeout();
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await disposal.DefaultTimeout();
        }

        Assert.Equal(1, instance.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithDisposeTimeout_PropagatesDisposalException(bool synchronous)
    {
        var failure = new InvalidOperationException("Disposal failed.");
        var instance = new TestAsyncDisposable(() => synchronous
            ? throw failure
            : ValueTask.FromException(failure));
        var lifetime = AsyncTestHelpers.WithDisposeTimeout(() => instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifetime.DisposeAsync().AsTask()).DefaultTimeout();

        Assert.Same(failure, exception);
        Assert.Equal(1, instance.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithDisposeTimeout_TimesOutBlockedDisposal(bool synchronous)
    {
        if (Debugger.IsAttached)
        {
            Assert.Skip("Disposal timeouts are disabled while a debugger is attached.");
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var instance = new TestAsyncDisposable(async () =>
        {
            started.TrySetResult();
            try
            {
                if (synchronous)
                {
                    release.Task.GetAwaiter().GetResult();
                }
                else
                {
                    await release.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                completed.TrySetResult();
            }
        });
        var timeout = TimeSpan.FromMilliseconds(100);
        var caller = GetCallerInfo();
        var lifetime = AsyncTestHelpers.WithDisposeTimeout(() => instance, timeout);
        // Keep this regression test bounded even if the wrapper stops offloading synchronous disposal.
        var disposal = Task.Run(() => lifetime.DisposeAsync().AsTask());
        try
        {
            await started.Task.DefaultTimeout();
            var exception = await Assert.ThrowsAsync<TimeoutException>(() => disposal).DefaultTimeout();

            Assert.Equal($"The operation at {caller.FilePath}:{caller.LineNumber + 1} timed out after reaching the limit of {timeout.TotalMilliseconds}ms.", exception.Message);
            Assert.False(completed.Task.IsCompleted);
            Assert.Equal(1, instance.DisposeCount);
        }
        finally
        {
            release.TrySetResult();
            await completed.Task.DefaultTimeout();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WithDisposeTimeout_InvalidTimeout_DoesNotCreateInstance(int milliseconds)
    {
        var factoryCalls = 0;

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            AsyncTestHelpers.WithDisposeTimeout(() =>
            {
                factoryCalls++;
                return new TestAsyncDisposable(() => ValueTask.CompletedTask);
            }, TimeSpan.FromMilliseconds(milliseconds)));

        Assert.Equal("timeout", exception.ParamName);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public void WithDisposeTimeout_PropagatesFactoryException()
    {
        var failure = new InvalidOperationException("Creation failed.");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AsyncTestHelpers.WithDisposeTimeout<TestAsyncDisposable>(() => throw failure));

        Assert.Same(failure, exception);
    }

    private static (string FilePath, int LineNumber) GetCallerInfo(
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = default) => (filePath, lineNumber);
}
