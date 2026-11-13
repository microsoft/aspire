// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Aspire.Hosting.Utils;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public class ProcessCaptureTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("")]
    [InlineData("NUL\0ü日本語\r\n\nno final newline")]
    public async Task Capture_PreservesExactInputAndUtf8TextThroughEof(string input)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var app = DotnetFileAppProcess.WriteApp(workspace.Path, "echo.cs", """
            #:sdk Microsoft.NET.Sdk
            using System.Text;
            using var input = new MemoryStream();
            await Console.OpenStandardInput().CopyToAsync(input);
            var bytes = input.ToArray();
            await Console.OpenStandardOutput().WriteAsync(bytes);
            await Console.OpenStandardError().WriteAsync(bytes);
            """);

        IProcessRunner runner = new DefaultProcessRunner();
        var result = await runner.RunAndCaptureTextAsync(DotnetFileAppProcess.ResolvedExecutablePath,
            DotnetFileAppProcess.CreateArguments(app), workspace.Path, standardInput: Encoding.UTF8.GetBytes(input),
            cancellationToken: TestContext.Current.CancellationToken).DefaultTimeout(TestConstants.LongTimeoutDuration);

        Assert.Equal(new ProcessCaptureResult(0, input, input), result);
    }

    [Fact]
    public async Task Capture_OverlaysInheritedEnvironmentAndPreservesArgumentsAndWorkingDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var inheritedPath = Environment.GetEnvironmentVariable("PATH");
        Assert.NotNull(inheritedPath);
        var app = DotnetFileAppProcess.WriteApp(workspace.Path, "environment.cs", """
            #:sdk Microsoft.NET.Sdk
            Console.Write(Environment.GetEnvironmentVariable("TF_BUILD") + "|" + Environment.GetEnvironmentVariable("PATH") + "|" + Environment.GetEnvironmentVariable("CAPTURE_OVERLAY") + "|" + Environment.CurrentDirectory + "|" + string.Join("|", args));
            """);
        var arguments = new[] { "space value", "quote\"value", "", "日本語" };

        var result = await new DefaultProcessRunner().RunAndCaptureTextAsync(DotnetFileAppProcess.ResolvedExecutablePath,
            DotnetFileAppProcess.CreateArguments(app, arguments), workspace.Path,
            new Dictionary<string, string> { ["CAPTURE_OVERLAY"] = "ü", ["TF_BUILD"] = "overridden" },
            cancellationToken: TestContext.Current.CancellationToken).DefaultTimeout(TestConstants.LongTimeoutDuration);

        Assert.Equal(new ProcessCaptureResult(0, "overridden|" + inheritedPath + "|ü|" + workspace.Path + "|" + string.Join("|", arguments), ""), result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_DrainsBothOutputPipesBeforeChildReadsLargeInput(bool errorFirst)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var app = DotnetFileAppProcess.WriteApp(workspace.Path, "pressure.cs", """
            #:sdk Microsoft.NET.Sdk
            using System.Text;
            var output = Console.OpenStandardOutput();
            var error = Console.OpenStandardError();
            var prefix = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("ü\0\r\n", 50000)));
            var first = args[0] == "True" ? error : output;
            var second = args[0] == "True" ? output : error;
            await Task.WhenAll(first.WriteAsync(prefix).AsTask(), second.WriteAsync(prefix).AsTask());
            using var input = new MemoryStream();
            await Console.OpenStandardInput().CopyToAsync(input);
            await Task.WhenAll(output.WriteAsync(input.ToArray()).AsTask(), error.WriteAsync(input.ToArray()).AsTask());
            """);
        var prefix = string.Concat(Enumerable.Repeat("ü\0\r\n", 50000));
        var input = string.Concat(Enumerable.Repeat("input 日本語\0\n", 50000));
        Assert.True(Encoding.UTF8.GetByteCount(prefix) > 200000);
        Assert.True(Encoding.UTF8.GetByteCount(input) > 200000);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.LongTimeoutDuration);

        var result = await new DefaultProcessRunner().RunAndCaptureTextAsync(DotnetFileAppProcess.ResolvedExecutablePath,
            DotnetFileAppProcess.CreateArguments(app, [errorFirst.ToString()]), workspace.Path,
            standardInput: Encoding.UTF8.GetBytes(input), cancellationToken: cancellation.Token);

        Assert.Equal(new ProcessCaptureResult(0, prefix + input, prefix + input), result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2000000)]
    public async Task Capture_ReturnsNonzeroExitAndLateDiagnosticsWhenChildRejectsInput(int inputSize)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var app = DotnetFileAppProcess.WriteApp(workspace.Path, "reject.cs", """
            #:sdk Microsoft.NET.Sdk
            using System.Text;
            Console.OpenStandardInput().Dispose();
            await Console.OpenStandardOutput().WriteAsync(Encoding.UTF8.GetBytes("output\0no newline"));
            await Console.OpenStandardError().WriteAsync(Encoding.UTF8.GetBytes(new string('E', 220000) + "late ü\0\r\n"));
            Environment.Exit(128);
            """);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.LongTimeoutDuration);

        var result = await new DefaultProcessRunner().RunAndCaptureTextAsync(DotnetFileAppProcess.ResolvedExecutablePath,
            DotnetFileAppProcess.CreateArguments(app), workspace.Path, standardInput: new byte[inputSize], cancellationToken: cancellation.Token);

        Assert.Equal(new ProcessCaptureResult(128, "output\0no newline", new string('E', 220000) + "late ü\0\r\n"), result);
    }

    [Fact]
    public async Task Capture_MissingExecutableThrowsAndPreCancellationDoesNotStartIt()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var missing = Path.Combine(workspace.Path, "missing-executable");
        Assert.False(File.Exists(missing));
        var runner = new DefaultProcessRunner();
        await Assert.ThrowsAsync<Win32Exception>(() => runner.RunAndCaptureTextAsync(missing, [], workspace.Path));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAndCaptureTextAsync(missing, [], workspace.Path, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Capture_CancellationStopsChildTreeAndSettlesBlockedInputBeforeThrowing()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var name = "capture-" + Guid.NewGuid().ToString("N");
        await using var parentPipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var childPipe = new NamedPipeServerStream(name + "-child", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var app = DotnetFileAppProcess.WriteApp(workspace.Path, "cancel.cs", """
            #:sdk Microsoft.NET.Sdk
            using System.Diagnostics;
            using System.IO.Pipes;
            using System.Reflection;
            var child = args.Length > 1;
            if (!child)
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                // File apps may run as an apphost or through dotnet. Only dotnet needs the DLL argument.
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                }
                start.ArgumentList.Add(args[0] + "-child");
                start.ArgumentList.Add("child");
                Process.Start(start)!.Dispose();
            }
            using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync();
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(Environment.ProcessId.ToString());
            await pipe.ReadAsync(new byte[1]);
            """);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.LongTimeoutDuration);
        var capture = new DefaultProcessRunner().RunAndCaptureTextAsync(DotnetFileAppProcess.ResolvedExecutablePath,
            DotnetFileAppProcess.CreateArguments(app, [name]), workspace.Path, standardInput: new byte[2000000], cancellationToken: cancellation.Token);
        try
        {
            await Task.WhenAll(parentPipe.WaitForConnectionAsync(cancellation.Token), childPipe.WaitForConnectionAsync(cancellation.Token));
            using var parentReader = new StreamReader(parentPipe, leaveOpen: true);
            using var childReader = new StreamReader(childPipe, leaveOpen: true);
            using var parent = Process.GetProcessById(int.Parse((await parentReader.ReadLineAsync(cancellation.Token))!));
            using var child = Process.GetProcessById(int.Parse((await childReader.ReadLineAsync(cancellation.Token))!));
            Assert.False(parent.HasExited);
            Assert.False(child.HasExited);
            Assert.False(capture.IsCompleted);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture).DefaultTimeout(TestConstants.LongTimeoutDuration);

            Assert.True(parent.HasExited);
            // A killed Unix descendant can remain a zombie; use pipe closure as a proxy for termination.
            using var eofCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            eofCancellation.CancelAfter(TestConstants.LongTimeoutDuration);
            Assert.Equal("", await childReader.ReadToEndAsync(eofCancellation.Token));
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture).DefaultTimeout(TestConstants.LongTimeoutDuration);
        }
    }
}
