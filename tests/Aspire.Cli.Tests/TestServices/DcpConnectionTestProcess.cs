// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Aspire.Cli.DotNet;
using Aspire.Cli.Utils.EnvironmentChecker;
using Aspire.Shared.Tests;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class DcpConnectionTestProcess(
    IProcessExecution inner,
    Func<IProcessExecution, CancellationToken, Task> afterStart) : IProcessExecution
{
    internal const string SessionDirectoryFileName = "dcp-session-directory";
    internal const string StandardOutputFileName = "dcp-stdout.log";
    internal const string StandardErrorFileName = "dcp-stderr.log";

    public string FileName => inner.FileName;
    public IReadOnlyList<string> Arguments => inner.Arguments;
    public IReadOnlyDictionary<string, string?> EnvironmentVariables => inner.EnvironmentVariables;
    public int ProcessId => inner.ProcessId;
    public DateTimeOffset? StartTime => inner.StartTime;
    public bool HasExited => inner.HasExited;
    public int ExitCode => inner.ExitCode;

    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        if (!await inner.StartAsync(cancellationToken))
        {
            return false;
        }

        // Hold the real probe before it can finish and dispose DCP. The outer test must be able
        // to kill only this parent, bypassing managed cleanup without replacing the launch path.
        await afterStart(inner, cancellationToken);

        return true;
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => inner.WaitForExitAsync(cancellationToken);
    public Task<int> WaitForRootExitAsync(CancellationToken cancellationToken) => inner.WaitForRootExitAsync(cancellationToken);
    public void Kill(bool entireProcessTree) => inner.Kill(entireProcessTree);
    public ValueTask DisposeAsync() => inner.DisposeAsync();

    internal static async Task RunAsync(string dcpDirectory, string workingDirectory, string readinessName, string containerRuntime)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var control = new NamedPipeServerStream(
            $"{readinessName}-control", PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        // The control pipe exchanges one line per phase: "continue", "completed", then "exit".
        using var reader = new StreamReader(control, leaveOpen: true);
        var standardOutputPath = Path.Combine(workingDirectory, StandardOutputFileName);
        var standardErrorPath = Path.Combine(workingDirectory, StandardErrorFileName);
        using var standardOutput = File.OpenHandle(standardOutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var standardError = File.OpenHandle(standardErrorPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var recordingFactory = new TestProcessExecutionFactory
        {
            CreateExecutionWithFileNameCallback = (fileName, arguments, environment, directory, options) =>
            {
                Assert.NotNull(environment);
                var sessionDirectory = environment["DCP_SESSION_FOLDER"];
                File.WriteAllText(Path.Combine(workingDirectory, SessionDirectoryFileName), sessionDirectory);
                var kubeconfigPath = Path.Combine(sessionDirectory, "kubeconfig");

                // Verbose startup metadata supplies the controller PID, not our readiness condition.
                string[] testArguments = [.. arguments, "-v=debug"];
                if (!string.IsNullOrEmpty(containerRuntime))
                {
                    testArguments = [.. testArguments, "--container-runtime", containerRuntime];
                }

                var startInfo = ProcessExecutionFactory.CreateProcessStartInfo(fileName, testArguments, directory.FullName, options);
                foreach (var (key, value) in environment)
                {
                    startInfo.Environment[key] = value;
                }

                // Regular files keep stdio usable after the helper dies. A broken stdout/stderr
                // pipe can itself terminate a Go process and falsely pass the monitor regression:
                // https://pkg.go.dev/os/signal#hdr-SIGPIPE.
                // The normal factory redirects to helper-owned pipes, so retain its launch policy
                // and execution primitive but replace only the test's standard handles.
                startInfo.RedirectStandardOutput = false;
                startInfo.RedirectStandardError = false;
                startInfo.StandardOutputEncoding = null;
                startInfo.StandardErrorEncoding = null;
                startInfo.StandardOutputHandle = standardOutput;
                startInfo.StandardErrorHandle = standardError;
                var execution = new ProcessExecution(startInfo, NullLogger.Instance, options, new HostEnvironment());
                return new DcpConnectionTestProcess(execution, async (process, cancellationToken) =>
                {
                    var apiServer = ProcessTestIdentity.Capture(process.ProcessId);
                    await NotifyAsync(readinessName, "guardian", apiServer, cancellationToken);
                    var controllerIdentity = await ReadControllerAsync(standardOutputPath, standardErrorPath, fileName, cancellationToken);
                    await WaitForApiAsync(kubeconfigPath, cancellationToken);
                    Assert.True(ProcessStartTimeHelper.IsProcessRunning(controllerIdentity.ProcessId, controllerIdentity.StartTime));
                    await Task.WhenAll(
                        NotifyAsync(readinessName, "runtime", apiServer, cancellationToken),
                        NotifyAsync(readinessName, "worker", controllerIdentity, cancellationToken));
                    await control.WaitForConnectionAsync(cancellationToken);
                    Assert.Equal("continue", await reader.ReadLineAsync(cancellationToken));
                });
            }
        };
        var root = new DirectoryInfo(workingDirectory);
        var context = new CliExecutionContext(
            workingDirectory: root,
            hivesDirectory: root.CreateSubdirectory(".aspire-hives"),
            cacheDirectory: root.CreateSubdirectory(".aspire-cache"),
            sdksDirectory: root.CreateSubdirectory(".aspire-sdks"),
            logsDirectory: root.CreateSubdirectory(".aspire-logs"),
            logFilePath: "test.log",
            identityChannel: "local");
        var checker = new DcpConnectionChecker(
            CertificateManager.Create(NullLogger.Instance, new HostEnvironment(), nssDbOverride: null),
            recordingFactory,
            context,
            new HostEnvironment(),
            NullLogger<DcpConnectionChecker>.Instance);

        var result = await checker.TestConnectionAsync(dcpDirectory, useDeveloperCertificate: false, lifetime.Token);
        Assert.True(result.Status == EnvironmentCheckStatus.Pass, $"{result.Message}: {result.Details}");
        using var writer = new StreamWriter(control, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync("completed");

        // Keep the parent alive after a successful probe so the test cannot mistake the
        // parent-exit monitor for the probe's normal session disposal.
        Assert.Equal("exit", await reader.ReadLineAsync(lifetime.Token));
    }

    internal static async Task<string> ReadLogAsync(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task<ProcessTestIdentity> ReadControllerAsync(
        string standardOutputPath,
        string standardErrorPath,
        string executable,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            foreach (var path in new[] { standardOutputPath, standardErrorPath })
            {
                var content = await ReadLogAsync(path, cancellationToken);
                // Only parse complete lines; a live writer may not have flushed the final JSON yet.
                foreach (var line in content.Split('\n').SkipLast(1))
                {
                    if (ReadController(line, executable) is { } controller)
                    {
                        return controller;
                    }
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    private static ProcessTestIdentity? ReadController(string line, string executable)
    {
        // DCP emits tab-delimited logs with trailing JSON metadata, for example:
        //   <time>\tdebug\t<category>\tProcess started successfully\t{"PID":123,"Command":"<dcp>","Args":["run-controllers",...]}
        // JSON string values escape embedded tabs, so the final tab delimits the metadata.
        if (!line.Contains("\tProcess started successfully\t", StringComparison.Ordinal))
        {
            return null;
        }

        using var metadata = JsonDocument.Parse(line[(line.LastIndexOf('\t') + 1)..]);
        var root = metadata.RootElement;
        var command = root.GetProperty("Command").GetString();
        var arguments = root.GetProperty("Args");
        if (string.Equals(command, executable, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
            arguments.GetArrayLength() > 0 &&
            arguments[0].GetString() == "run-controllers")
        {
            return ProcessTestIdentity.Capture(root.GetProperty("PID").GetInt32());
        }

        return null;
    }

    private static async Task WaitForApiAsync(string kubeconfigPath, CancellationToken cancellationToken)
    {
        DcpKubeconfig kubeconfig;
        while (true)
        {
            try
            {
                kubeconfig = await DcpKubeconfig.ReadFileWithRetryAsync(kubeconfigPath, cancellationToken: cancellationToken);
                break;
            }
            catch (IOException ex)
            {
                // The live DCP process can still be creating or writing the kubeconfig on Windows.
                Console.Error.WriteLine($"Waiting for readable DCP kubeconfig: {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }

        using (kubeconfig)
        using (var handler = new SocketsHttpHandler())
        {
            handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck
            };
            handler.SslOptions.CertificateChainPolicy.CustomTrustStore.AddRange(kubeconfig.CertificateAuthorityCertificates.ToArray());
            if (kubeconfig.ClientCertificate is { } certificate)
            {
                handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate };
            }

            using var client = new HttpClient(handler);
            if (!string.IsNullOrEmpty(kubeconfig.Token))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", kubeconfig.Token);
            }

            while (true)
            {
                try
                {
                    using var response = await client.GetAsync(new Uri(kubeconfig.Server, "/version"), cancellationToken);
                    response.EnsureSuccessStatusCode();
                    return;
                }
                catch (HttpRequestException ex)
                {
                    // The generated kubeconfig can appear before the API listener is ready.
                    Console.Error.WriteLine($"Waiting for DCP API readiness: {ex.Message}");
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                }
            }
        }
    }

    private static async Task NotifyAsync(string name, string role, ProcessTestIdentity identity, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(".", $"{name}-{role}", PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteLineAsync(FormattableString.Invariant($"{identity.ProcessId}:{identity.StartTime}"));
    }
}
