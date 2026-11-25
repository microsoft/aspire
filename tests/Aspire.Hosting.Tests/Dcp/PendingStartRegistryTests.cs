// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp;

namespace Aspire.Hosting.Tests.Dcp;

public class PendingStartRegistryTests
{
    [Fact]
    public void StopOfAStartQueuedBehindALaunchedObjectStopsThatObject()
    {
        // A start reached DCP and ended, so its object runs. A queued start (for example, a Start command that waited for
        // the AppHost's startup of the instance) would replace that object, but hasn't begun to delete it: canceling the
        // queued start alone would leave the object running.
        var registry = new PendingStartRegistry();
        using (var launched = registry.Begin(["app"], CancellationToken.None))
        {
            Assert.True(launched.TryCommit("app"));
        }
        using var queued = registry.Begin(["app"], CancellationToken.None);

        Assert.Equal(PendingStartCancellation.Committed, registry.Cancel("app", running: false, out var groups, out _, out var replacement));

        Assert.Null(replacement);
        Assert.Same(queued, Assert.Single(groups));
        Assert.False(queued.TryEndCanceled("app"));
        Assert.False(registry.IsCanceled("app"));
    }

    [Fact]
    public void StopOfAStartThatBeganToReplaceTheLaunchedObjectCancelsTheStart()
    {
        // A Stop doesn't interrupt the deletion of the object that the start replaces, so nothing runs once it completes.
        var registry = new PendingStartRegistry();
        using (var launched = registry.Begin(["app"], CancellationToken.None))
        {
            Assert.True(launched.TryCommit("app"));
        }
        using var replacing = registry.Begin(["app"], CancellationToken.None);
        using var deletion = registry.BeginDeletion("app");

        Assert.Equal(PendingStartCancellation.Canceled, registry.Cancel("app", running: false, out _, out _, out var replacement));
        Assert.NotNull(replacement);
        deletion.Complete();

        Assert.False(replacement.LeftObjectToStop());
        Assert.True(replacing.TryEndCanceled("app"));
        Assert.True(registry.IsCanceled("app"));
    }

    [Fact]
    public void AnObjectThatACanceledStartFailedToDeleteIsLeftToANewerStartInProgress()
    {
        // The Stop stops the object that the canceled start failed to delete, unless a newer start of the instance is in
        // progress: that start replaces the object. One whose caller gave up before it deleted the object doesn't.
        var registry = new PendingStartRegistry();
        using (var launched = registry.Begin(["app"], CancellationToken.None))
        {
            Assert.True(launched.TryCommit("app"));
        }
        var canceled = registry.Begin(["app"], CancellationToken.None);
        var deletion = registry.BeginDeletion("app");
        Assert.Equal(PendingStartCancellation.Canceled, registry.Cancel("app", running: false, out _, out _, out var replacement));
        Assert.NotNull(replacement);
        using var caller = new CancellationTokenSource();
        var newer = registry.Begin(["app"], caller.Token);
        // The canceled start's deletion fails, and the canceled start ends.
        deletion.Dispose();
        canceled.Dispose();

        Assert.False(replacement.LeftObjectToStop());
        caller.Cancel();
        Assert.False(newer.TryEndCanceled("app"));
        newer.Dispose();
        Assert.True(replacement.LeftObjectToStop());
    }

    [Fact]
    public void AnObjectThatACanceledStartFailedToDeleteIsLeftToANewerStartThatReplacedIt()
    {
        // The newer start deleted the object, reached DCP and ended: the object that the instance has now is its own.
        var registry = new PendingStartRegistry();
        using (var launched = registry.Begin(["app"], CancellationToken.None))
        {
            Assert.True(launched.TryCommit("app"));
        }
        var canceled = registry.Begin(["app"], CancellationToken.None);
        var deletion = registry.BeginDeletion("app");
        Assert.Equal(PendingStartCancellation.Canceled, registry.Cancel("app", running: false, out _, out _, out var replacement));
        Assert.NotNull(replacement);
        deletion.Dispose();
        canceled.Dispose();
        using (var newer = registry.Begin(["app"], CancellationToken.None))
        {
            using (var replaced = registry.BeginDeletion("app"))
            {
                replaced.Complete();
            }
            Assert.True(newer.TryCommit("app"));
        }

        Assert.True(registry.IsLaunched("app"));
        Assert.False(replacement.LeftObjectToStop());
    }

    [Fact]
    public void AStartWhoseCallerGaveUpBeforeItReplacedTheLaunchedObjectLeavesTheStateToThatObject()
    {
        // The object that an earlier start launched still runs, so the instance didn't become not started.
        var registry = new PendingStartRegistry();
        using (var launched = registry.Begin(["app"], CancellationToken.None))
        {
            Assert.True(launched.TryCommit("app"));
        }
        using var caller = new CancellationTokenSource();
        using var queued = registry.Begin(["app"], caller.Token);
        caller.Cancel();

        Assert.False(queued.TryEndCanceled("app"));
        Assert.False(registry.IsCanceled("app"));
    }

    [Fact]
    public void AStartCanceledWhileItReplacedWhatTheStartsBeforeItLaunchedReportsThatTheInstanceDidNotStart()
    {
        // A Start command began while the AppHost's startup of the instance was in progress, and waited for it. The startup
        // reached DCP, then the Start took the instance's lock, and a Stop canceled it while it deleted the startup's
        // object. That commit was before the Start's turn, so it doesn't mean that another start determines the state.
        var registry = new PendingStartRegistry();
        using var queued = registry.Begin(["app"], CancellationToken.None);
        using (var startup = registry.Begin(["app"], CancellationToken.None))
        {
            Assert.True(startup.TryCommit("app"));
        }
        queued.HoldsInstanceLock("app");
        using (var deletion = registry.BeginDeletion("app"))
        {
            Assert.Equal(PendingStartCancellation.Canceled, registry.Cancel("app", running: false, out _, out _, out _));
            deletion.Complete();
        }

        Assert.True(queued.TryEndCanceled("app"));
        Assert.True(registry.IsCanceled("app"));
    }

    [Fact]
    public void StartsWhoseCallersAllGaveUpReportThatTheInstanceDidNotStart()
    {
        // Neither start reached DCP, so whichever ends first mustn't leave the state to the other one.
        var registry = new PendingStartRegistry();
        using var firstCaller = new CancellationTokenSource();
        using var secondCaller = new CancellationTokenSource();
        using var first = registry.Begin(["app"], firstCaller.Token);
        using var second = registry.Begin(["app"], secondCaller.Token);
        firstCaller.Cancel();
        secondCaller.Cancel();

        Assert.True(first.TryEndCanceled("app"));
        Assert.True(second.TryEndCanceled("app"));
        Assert.True(registry.IsCanceled("app"));
    }
}
