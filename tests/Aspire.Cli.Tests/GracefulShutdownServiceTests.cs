// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests;

public class GracefulShutdownServiceTests
{
    [Fact]
    public void Token_BeforeExpire_NotCancelled()
    {
        using var service = new GracefulShutdownService();

        Assert.False(service.Token.IsCancellationRequested);
    }

    [Fact]
    public void Expire_FiresToken()
    {
        using var service = new GracefulShutdownService();

        service.Expire();

        Assert.True(service.Token.IsCancellationRequested);
    }

    [Fact]
    public void Expire_Idempotent()
    {
        using var service = new GracefulShutdownService();

        service.Expire();
        service.Expire();
        service.Expire();

        Assert.True(service.Token.IsCancellationRequested);
    }

    [Fact]
    public void Expire_AfterDispose_DoesNotThrow()
    {
        var service = new GracefulShutdownService();
        service.Dispose();

        // Expire racing with dispose must not propagate to callers (signal handler /
        // watcher continuation contexts have nowhere meaningful to surface this).
        service.Expire();
    }

    [Fact]
    public void Token_RemainsAccessibleAfterDispose()
    {
        var service = new GracefulShutdownService();
        var token = service.Token;
        service.Dispose();

        // Token was captured up front; reading state after dispose must not throw.
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public void IsEnabled_DefaultsToFalse()
    {
        using var service = new GracefulShutdownService();

        Assert.False(service.IsEnabled);
    }

    [Fact]
    public void IsEnabled_TrueAfterPositiveConfigure()
    {
        using var service = new GracefulShutdownService();

        service.Configure(TimeSpan.FromSeconds(5));

        Assert.True(service.IsEnabled);
    }

    [Fact]
    public void IsEnabled_FalseAfterZeroConfigure()
    {
        using var service = new GracefulShutdownService();

        service.Configure(TimeSpan.Zero);

        Assert.False(service.IsEnabled);
    }

    [Fact]
    public void Configure_NegativeBudget_Throws()
    {
        using var service = new GracefulShutdownService();

        Assert.Throws<ArgumentOutOfRangeException>(() => service.Configure(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void BeginGracefulWindow_ZeroBudget_ExpiresImmediately()
    {
        using var service = new GracefulShutdownService();

        // No Configure call → zero budget → window is "over" the moment it begins.
        service.BeginGracefulWindow();

        Assert.True(service.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task BeginGracefulWindow_PositiveBudget_FiresTokenAfterBudget()
    {
        // BeginGracefulWindow arms CancelAfter, which is a no-op while a debugger is attached
        // (developers need unlimited stepping time). Skip the timing assertion in that case.
        if (Debugger.IsAttached)
        {
            return;
        }

        using var service = new GracefulShutdownService();
        service.Configure(TimeSpan.FromMilliseconds(50));

        service.BeginGracefulWindow();
        Assert.False(service.Token.IsCancellationRequested);

        await service.Token.WaitUntilCancelledAsync().DefaultTimeout();

        Assert.True(service.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task BeginGracefulWindow_SecondCall_DoesNotResetTimer()
    {
        if (Debugger.IsAttached)
        {
            return;
        }

        using var service = new GracefulShutdownService();
        service.Configure(TimeSpan.FromMilliseconds(50));

        service.BeginGracefulWindow();
        // A second call must be idempotent and must not re-arm (which would extend the window).
        service.BeginGracefulWindow();

        await service.Token.WaitUntilCancelledAsync().DefaultTimeout();

        Assert.True(service.Token.IsCancellationRequested);
    }
}
