// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.Net;
using Aspire.Hosting.Tests.Publishing;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Azure.Tests;

public class AcrLoginServiceTests
{
    [Fact]
    public async Task LoginAsync_RetriesTransientExchangeFailures()
    {
        var handler = new CallbackHttpMessageHandler((attempt, _) =>
        {
            if (attempt < 3)
            {
                throw new HttpRequestException("Name or service not known");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"refresh_token":"refresh-token"}""")
            });
        });
        var runtime = new FakeContainerRuntime();
        var timeProvider = new ImmediateTimeProvider();
        var service = new AcrLoginService(
            new TestHttpClientFactory(handler),
            runtime,
            NullLogger<AcrLoginService>.Instance,
            timeProvider);

        await service.LoginAsync("registry.azurecr.io", "tenant", new StaticTokenCredential());

        Assert.Equal(3, handler.CallCount);
        Assert.Equal(2, timeProvider.DelayCount);
        Assert.True(runtime.WasLoginToRegistryCalled);
        var login = Assert.Single(runtime.LoginToRegistryCalls);
        Assert.Equal("registry.azurecr.io", login.registryServer);
        Assert.Equal("refresh-token", login.password);
    }

    [Fact]
    public async Task LoginAsync_DoesNotRetryNonRetryableExchangeFailures()
    {
        var handler = new CallbackHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("bad request")
        }));
        var runtime = new FakeContainerRuntime();
        var timeProvider = new ImmediateTimeProvider();
        var service = new AcrLoginService(
            new TestHttpClientFactory(handler),
            runtime,
            NullLogger<AcrLoginService>.Instance,
            timeProvider);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.LoginAsync("registry.azurecr.io", "tenant", new StaticTokenCredential()));

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(0, timeProvider.DelayCount);
        Assert.False(runtime.WasLoginToRegistryCalled);
    }

    [Fact]
    public async Task LoginAsync_StopsRetryingAfterMaxAttempts()
    {
        var handler = new CallbackHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("registry not ready")
        }));
        var runtime = new FakeContainerRuntime();
        var timeProvider = new ImmediateTimeProvider();
        var service = new AcrLoginService(
            new TestHttpClientFactory(handler),
            runtime,
            NullLogger<AcrLoginService>.Instance,
            timeProvider);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.LoginAsync("registry.azurecr.io", "tenant", new StaticTokenCredential()));

        Assert.Equal(30, handler.CallCount);
        Assert.Equal(29, timeProvider.DelayCount);
        Assert.False(runtime.WasLoginToRegistryCalled);
    }

    [Fact]
    public async Task LoginAsync_StopsRetryingAfterTimeBudgetExceeded()
    {
        var handler = new CallbackHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("registry not ready")
        }));
        var runtime = new FakeContainerRuntime();
        // ElapsedTimeProvider simulates 2 minutes elapsed after the first GetTimestamp() call,
        // so the s_maxLoginRetryDuration (1 minute) guard in ShouldRetryAcrLoginFailure trips
        // immediately and the loop stops after a single attempt without entering Task.Delay.
        var timeProvider = new ElapsedTimeProvider();
        var service = new AcrLoginService(
            new TestHttpClientFactory(handler),
            runtime,
            NullLogger<AcrLoginService>.Instance,
            timeProvider);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.LoginAsync("registry.azurecr.io", "tenant", new StaticTokenCredential()));

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(0, timeProvider.DelayCount);
        Assert.False(runtime.WasLoginToRegistryCalled);
    }

    private sealed class CallbackHttpMessageHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return callback(CallCount, cancellationToken);
        }
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StaticTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return new AccessToken("aad-token", DateTimeOffset.MaxValue);
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(GetToken(requestContext, cancellationToken));
        }
    }

    private sealed class ImmediateTimeProvider : TimeProvider
    {
        public int DelayCount { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DelayCount++;
            var timer = new ImmediateTimer();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (!timer.IsDisposed)
                {
                    callback(state);
                }
            });
            return timer;
        }
    }

    /// <summary>
    /// A <see cref="TimeProvider"/> that reports 2 minutes of elapsed time after the first
    /// <see cref="GetTimestamp"/> call so the time-budget guard in
    /// <c>ShouldRetryAcrLoginFailure</c> fires immediately after one failed attempt.
    /// </summary>
    private sealed class ElapsedTimeProvider : TimeProvider
    {
        public int DelayCount { get; private set; }
        private int _getTimestampCallCount;

        public override long GetTimestamp()
        {
            var count = Interlocked.Increment(ref _getTimestampCallCount);
            // First call captures the retryStartTimestamp (0).
            // All subsequent calls return 2 minutes of ticks so GetElapsedTime() exceeds
            // s_maxLoginRetryDuration (1 minute) and the retry guard returns false.
            return count == 1 ? 0L : TimestampFrequency * 120;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DelayCount++;
            var timer = new ImmediateTimer();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (!timer.IsDisposed)
                {
                    callback(state);
                }
            });
            return timer;
        }
    }

    private sealed class ImmediateTimer : ITimer
    {
        public bool IsDisposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
            IsDisposed = true;
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
