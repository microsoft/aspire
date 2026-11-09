// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Aspire.Cli.Git;

internal static class GitAskPassInvocation
{
    public static bool Matches(string[] args) => args is ["git", "askpass", ..];

    public static async Task<int> InvokeAsync(TimeProvider timeProvider, string[] args, Func<string, string?> environment,
        TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            if (args is not ["git", "askpass", var prompt] ||
                string.IsNullOrEmpty(prompt) || prompt.Length > GitAskPassProtocol.MaxPromptBytes)
            {
                throw new AuthenticationException("Invalid Git credential callback invocation.");
            }
            var path = environment(GitAskPassProtocol.SocketVariable)
                ?? throw new AuthenticationException("Missing Git credential callback endpoint.");
            GitAskPassDirectory.ValidateEndpoint(path);
            using var setupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), timeProvider);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, setupTimeout.Token);
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token);
            setupTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            // Connecting is bounded; waiting for a human is not. EOF from
            // the parent or caller cancellation still terminates the helper.
            await GitAskPassProtocol.WriteAsync(stream, prompt, GitAskPassProtocol.MaxPromptBytes, timeout.Token);
            var response = await GitAskPassProtocol.ReadAsync(stream, GitAskPassProtocol.MaxResponseBytes, timeout.Token);
            GitAskPassProtocol.ValidateResponse(response);
            await output.WriteLineAsync(response.AsMemory(), timeout.Token);
            await output.FlushAsync(timeout.Token);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or
            ArgumentException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException)
        {
            // Raw prompts, remote URLs, response values and exceptions are private.
            // This early entry point intentionally has no logger, DI or telemetry.
            await error.WriteLineAsync("Git credential callback failed or was cancelled.".AsMemory(), CancellationToken.None);
            return 1;
        }
    }

    internal static string CreateHelperCommand(string processPath, string assemblyPath)
    {
        if (!Path.IsPathFullyQualified(processPath))
        {
            throw new InvalidOperationException("Git credential callbacks require an absolute CLI executable.");
        }
        var command = Quote(processPath);
        if (Path.GetFileNameWithoutExtension(processPath) == "dotnet")
        {
            if (!Path.IsPathFullyQualified(assemblyPath) || Path.GetFileName(assemblyPath) != "aspire.dll")
            {
                throw new InvalidOperationException("Git credential callbacks require the Aspire assembly.");
            }
            command += " " + Quote(assemblyPath);
        }
        else if (Path.GetFileNameWithoutExtension(processPath) != "aspire")
        {
            throw new InvalidOperationException("Git credential callbacks require the Aspire executable.");
        }
        // The operation-owned executable script launches this fixed command, then
        // forwards Git's prompt through "$@". Only trusted launcher paths are quoted.
        // https://git-scm.com/docs/gitcredentials#_requesting_credentials
        return command + " git askpass";
    }

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty assembly location is expected for NativeAOT; only a dotnet-hosted development CLI needs its physical assembly.")]
    internal static string CreateCurrentHelperCommand()
        => CreateHelperCommand(Environment.ProcessPath ?? throw new InvalidOperationException("The Aspire executable path is unavailable."),
            typeof(Program).Assembly.Location);

    private static string Quote(string value)
    {
        if (value.Any(char.IsControl))
        {
            throw new InvalidOperationException("Invalid Git credential callback launcher.");
        }
        // The launcher is interpreted by Git's shell, including on Windows.
        var path = OperatingSystem.IsWindows() ? value.Replace('\\', '/') : value;
        return "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
