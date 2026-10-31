// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.IO.Pipelines;
using Aspire.Hosting.Cli;
using Aspire.Hosting.Tests.Utils;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.Tests;

[Trait("Partition", "4")]
public class TestOutputCaptureTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureWaitsForCompletionAndPreservesBothStreamsIncludingEmpty(bool empty)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var logs = new ResourceLoggerService();
        var expectedOutput = empty ? "" : string.Concat(Enumerable.Repeat("{\"Action\":\"output\"}\n", 12001));
        var expectedError = empty ? "" : "plain stderr: no timestamps\n";
        var calls = 0;
        logs.SetConsoleLogsService(new TestConsoleLogsService
        {
            RawOutput = (name, error, _) =>
            {
                Assert.Equal("tests", name);
                Interlocked.Increment(ref calls);
                return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(error ? expectedError : expectedOutput)));
            }
        });
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new ResourceTestingContext(new TestRunContext(workspace.Path), "tests", workspace.Path,
            _ => Assert.Fail(), completion.Task, TestContext.Current.CancellationToken)
        {
            CaptureOutputCallback = (ctx, token) => TestOutputCapture.CaptureAsync(
                new ExecutableResource("tests", "go", workspace.Path), logs, ctx, token)
        };
        var capture = context.CaptureOutputAsync();
        Assert.Same(capture, context.CaptureOutputAsync());
        Assert.Equal(0, calls);
        Assert.False(capture.IsCompleted);
        completion.SetResult(false);
        await capture.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        Assert.Equal(expectedOutput, await File.ReadAllTextAsync(Path.Combine(workspace.Path, "stdout.log")));
        Assert.Equal(expectedError, await File.ReadAllTextAsync(Path.Combine(workspace.Path, "stderr.log")));
        Assert.True(File.Exists(Path.Combine(workspace.Path, "output.complete")));
        if (!OperatingSystem.IsWindows())
        {
            foreach (var file in Directory.GetFiles(workspace.Path))
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            }
        }
        context.Close();
        await Assert.ThrowsAsync<InvalidOperationException>(context.CaptureOutputAsync);
    }

    [Fact]
    public async Task StreamFailureCancelsItsSiblingAndNeverMarksCaptureComplete()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var logs = new ResourceLoggerService();
        var siblingCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        logs.SetConsoleLogsService(new TestConsoleLogsService
        {
            RawOutput = async (_, error, token) =>
            {
                if (error)
                {
                    throw new IOException("intentional stream failure");
                }
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    throw new InvalidOperationException("Cancellation was expected.");
                }
                finally
                {
                    siblingCancelled.TrySetResult();
                }
            }
        });
        var context = CreateContext(workspace, logs);
        await Assert.ThrowsAsync<IOException>(() => context.CaptureOutputAsync().WaitAsync(
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await siblingCancelled.Task;
        Assert.False(File.Exists(Path.Combine(workspace.Path, "output.complete")));
    }

    [Fact]
    public async Task ExistingArtifactIsNotOverwritten()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var logs = new ResourceLoggerService();
        File.WriteAllText(Path.Combine(workspace.Path, "stdout.log"), "existing");
        await Assert.ThrowsAsync<IOException>(() => CreateContext(workspace, logs).CaptureOutputAsync());
        Assert.Equal("existing", File.ReadAllText(Path.Combine(workspace.Path, "stdout.log")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureRequiresBothProducerEofsAndPropagatesStreamCancellation(bool cancel)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var logs = new ResourceLoggerService();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stdout = new Pipe();
        var stderr = new Pipe();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        logs.SetConsoleLogsService(new TestConsoleLogsService
        {
            RawOutput = (_, error, _) =>
            {
                if (Interlocked.Increment(ref calls) == 2)
                {
                    started.TrySetResult();
                }
                return Task.FromResult((error ? stderr : stdout).Reader.AsStream());
            }
        });
        var context = new ResourceTestingContext(new TestRunContext(workspace.Path), "tests", workspace.Path,
            _ => Assert.Fail(), Task.FromResult(false), cancellation.Token)
        {
            CaptureOutputCallback = (ctx, token) => TestOutputCapture.CaptureAsync(
                new ExecutableResource("tests", "go", workspace.Path), logs, ctx, token)
        };
        var capture = context.CaptureOutputAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await stdout.Writer.WriteAsync(Encoding.UTF8.GetBytes("complete stdout\n"), cancellation.Token);
        await stdout.Writer.CompleteAsync();
        Assert.False(capture.IsCompleted);
        Assert.False(File.Exists(Path.Combine(workspace.Path, "output.complete")));
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.WaitAsync(
                TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        }
        else
        {
            await stderr.Writer.WriteAsync(Encoding.UTF8.GetBytes("late stderr\n"), cancellation.Token);
        }
        await stderr.Writer.CompleteAsync();
        if (!cancel)
        {
            await capture.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal("complete stdout\n", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "stdout.log")));
            Assert.Equal("late stderr\n", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "stderr.log")));
        }
        Assert.Equal(!cancel, File.Exists(Path.Combine(workspace.Path, "output.complete")));
    }

    [Fact]
    public async Task CancellationDuringCompletionDoesNotCreateArtifacts()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var context = new ResourceTestingContext(new TestRunContext(workspace.Path), "tests", workspace.Path,
            _ => Assert.Fail(), new TaskCompletionSource<bool>().Task, cancellation.Token);
        var capture = context.CaptureOutputAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.Empty(Directory.GetFiles(workspace.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedResourceOrReplicasFail(bool replicas)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var logs = new ResourceLoggerService();
        IResource resource = replicas ? new ExecutableResource("tests", "go", workspace.Path) : new ContainerResource("tests");
        if (replicas)
        {
            resource.Annotations.Add(new ReplicaAnnotation(2));
        }
        var context = new ResourceTestingContext(new TestRunContext(workspace.Path), "tests", workspace.Path,
            _ => Assert.Fail(), Task.FromResult(true), TestContext.Current.CancellationToken)
        {
            CaptureOutputCallback = (ctx, token) => TestOutputCapture.CaptureAsync(resource, logs, ctx, token)
        };
        await Assert.ThrowsAsync<NotSupportedException>(context.CaptureOutputAsync);
    }

    [Fact]
    public async Task CaptureRejectsASymlinkedOutputDirectoryWithoutWritingItsTarget()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links requires additional Windows privileges.");
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var logs = new ResourceLoggerService();
        var target = Directory.CreateDirectory(Path.Combine(workspace.Path, "target"));
        var link = Path.Combine(workspace.Path, "link");
        Directory.CreateSymbolicLink(link, target.FullName);
        var context = new ResourceTestingContext(new TestRunContext(workspace.Path), "tests", link,
            _ => Assert.Fail(), Task.FromResult(true), TestContext.Current.CancellationToken)
        {
            CaptureOutputCallback = (ctx, token) => TestOutputCapture.CaptureAsync(
                new ExecutableResource("tests", "go", workspace.Path), logs, ctx, token)
        };
        await Assert.ThrowsAsync<InvalidOperationException>(context.CaptureOutputAsync);
        Assert.Empty(Directory.GetFiles(target.FullName));
    }

    private static ResourceTestingContext CreateContext(TemporaryWorkspace workspace, ResourceLoggerService logs) =>
        new(new TestRunContext(workspace.Path), "tests", workspace.Path, _ => Assert.Fail(),
            Task.FromResult(true), TestContext.Current.CancellationToken)
        {
            CaptureOutputCallback = (ctx, token) => TestOutputCapture.CaptureAsync(
                new ExecutableResource("tests", "go", workspace.Path), logs, ctx, token)
        };
}
