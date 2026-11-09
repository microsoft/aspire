// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Git;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Git;

public class GitOperationTimeoutTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(120)]
    public async Task CredentialWaitExcludesHumanTimeAndResumesRemainingBudget(int seconds)
    {
        var time = new FakeTimeProvider();
        using var budget = new GitOperationTimeout(TimeSpan.FromSeconds(seconds), time, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(seconds - 1));
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new GitAskPassSession(time, new Uri("https://example.test"), (_, _) =>
        {
            opened.TrySetResult();
            return answer.Task;
        }, budget.Token);
        var info = new ProcessStartInfo("git");
        session.Configure(info, "helper");
        var output = new StringWriter();
        var error = new StringWriter();
        var helper = GitAskPassInvocation.InvokeAsync(time, ["git", "askpass", "Password for 'https://example.test': "],
            key => info.Environment.TryGetValue(key, out var value) ? value : null,
            output, error, TestContext.Current.CancellationToken);
        await opened.Task.DefaultTimeout();
        time.Advance(TimeSpan.FromHours(1));
        Assert.False(budget.Token.IsCancellationRequested);
        Assert.False(helper.IsCompleted);
        answer.SetResult("synthetic-secret");
        Assert.Equal(0, await helper.DefaultTimeout());
        Assert.Equal($"synthetic-secret{Environment.NewLine}", output.ToString());
        Assert.Equal("", error.ToString());
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(budget.Token.IsCancellationRequested);
    }

    [Fact]
    public void PausingDoesNotPauseCallerCancellationOrAnotherOperation()
    {
        var time = new FakeTimeProvider();
        using var caller = new CancellationTokenSource();
        using var outer = new GitOperationTimeout(TimeSpan.FromSeconds(30), time, caller.Token);
        using var inner = new GitOperationTimeout(TimeSpan.FromMinutes(2), time, caller.Token);
        using (GitOperationTimeout.PauseCurrent())
        {
            time.Advance(TimeSpan.FromHours(1));
            Assert.False(outer.Token.IsCancellationRequested);
            Assert.False(inner.Token.IsCancellationRequested);
            using var other = new GitOperationTimeout(TimeSpan.FromSeconds(30), time, TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(30));
            Assert.True(other.Token.IsCancellationRequested);
            caller.Cancel();
            Assert.True(outer.Token.IsCancellationRequested);
            Assert.True(inner.Token.IsCancellationRequested);
        }
    }
}
