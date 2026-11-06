// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using Aspire.Cli.DotNet;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Watch;

internal static class DotnetWatchServer
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        using var cancellation = new ConsoleCancellationManager(TimeSpan.Zero);
        try
        {
            var completionPath = Environment.GetEnvironmentVariable(DotnetWatchConfiguration.ScopeCompletionPath);
            if (string.IsNullOrEmpty(completionPath) || !Path.IsPathFullyQualified(completionPath))
            {
                throw new InvalidDataException("The watch server requires its private scope completion path.");
            }
            int exitCode;
            try
            {
                exitCode = await RunAsync(
                    arguments,
                    new ProcessExecutionFactory(new HostEnvironment(), NullLogger<ProcessExecutionFactory>.Instance),
                    new DirectoryInfo(Environment.CurrentDirectory),
                    Console.Out,
                    Console.Error,
                    cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.Token.IsCancellationRequested)
            {
                exitCode = 130;
            }
            // The owned execution has disposed and verified its process group/job. DCP
            // observing this owner exit alone is insufficient if it was forcibly killed.
            await File.WriteAllTextAsync(completionPath, "", CancellationToken.None).ConfigureAwait(false);
            return exitCode;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or Win32Exception or TimeoutException)
        {
            Console.Error.WriteLine($"Experimental .NET watch server failed: {exception.Message}");
            return 1;
        }
    }

    internal static async Task<int> RunAsync(
        string[] arguments,
        IProcessExecutionFactory processFactory,
        DirectoryInfo workingDirectory,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        if (arguments is not [var dotnet, "exec", _, "server", ..] || !Path.IsPathFullyQualified(dotnet))
        {
            throw new InvalidDataException("The watch server requires its selected dotnet muxer and server invocation.");
        }

        await using var execution = processFactory.CreateExecution(
            dotnet, arguments[1..], env: null, workingDirectory, new ProcessInvocationOptions
            {
                // A crashed SDK server otherwise leaves its dotnet-run children alive. Reuse
                // the guardian's OS process group/job, including owner-death cleanup, rather
                // than trying to infer application PIDs from the watch protocol.
                Lifetime = ChildProcessLifetime.OwnedTree,
                SuppressLogging = true,
                EnvironmentVariableFilter = name => name.Equals(
                    DotnetWatchConfiguration.ScopeCompletionPath, StringComparison.OrdinalIgnoreCase),
                StandardOutputCallback = standardOutput.WriteLine,
                StandardErrorCallback = standardError.WriteLine
            });
        if (!await execution.StartAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The experimental .NET watch server could not start.");
        }

        return await execution.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }
}
