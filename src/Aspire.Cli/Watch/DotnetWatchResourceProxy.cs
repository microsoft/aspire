// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Aspire.Shared;

namespace Aspire.Cli.Watch;

internal static class DotnetWatchResourceProxy
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        using var cancellation = new ConsoleCancellationManager(TimeSpan.Zero);
        try
        {
            return await RunAsync(
                arguments,
                Environment.GetEnvironmentVariable,
                Console.Out,
                Console.Error,
                cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.Token.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or JsonException or DecoderFallbackException)
        {
            // Request payloads and service environment values must never enter CLI logs.
            Console.Error.WriteLine($"Experimental .NET watch proxy failed: {exception.Message}");
            return 1;
        }
    }

    internal static async Task<int> RunAsync(
        string[] arguments,
        Func<string, string?> getEnvironment,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        var pipeName = GetRequired(DotnetWatchConfiguration.ResourcePipe);
        var entryPoint = GetRequired(DotnetWatchConfiguration.EntryPoint);
        var names = JsonSerializer.Deserialize(
            GetRequired(DotnetWatchConfiguration.EnvironmentNames),
            DotnetWatchJsonContext.Default.StringArray)
            ?? throw new InvalidDataException("Missing service environment inventory.");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name) || name.Contains('='))
            {
                throw new InvalidDataException("Invalid service environment inventory.");
            }

            environment.Add(name, GetRequired(name));
        }

        var request = DotnetWatchProtocol.CreateLaunchRequest(
            entryPoint,
            arguments,
            environment,
            getEnvironment(DotnetWatchConfiguration.ExcludeProfile) == "true",
            getEnvironment(DotnetWatchConfiguration.LaunchProfile));

        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectionTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        var type = new byte[1];
        try
        {
            await pipe.ConnectAsync(connectionTimeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(new byte[] { 1 }, connectionTimeout.Token).ConfigureAwait(false);
            await DotnetWatchProtocol.WriteStringAsync(pipe, request, connectionTimeout.Token).ConfigureAwait(false);
            await pipe.ReadExactlyAsync(type, connectionTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Timed out initializing the experimental .NET watch resource protocol.");
        }

        if (type[0] != 1)
        {
            throw new InvalidDataException("The .NET watch server rejected the launch protocol.");
        }

        while (true)
        {
            var count = await pipe.ReadAsync(type, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                // EOF describes the transport, not successful application completion.
                throw new EndOfStreamException("The .NET watch server disconnected.");
            }

            var writer = type[0] switch
            {
                1 => standardOutput,
                2 => standardError,
                _ => throw new InvalidDataException("The .NET watch server sent an unknown output record.")
            };
            var message = await DotnetWatchProtocol.ReadStringAsync(pipe, cancellationToken).ConfigureAwait(false);
            await writer.WriteLineAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        string GetRequired(string name) =>
            getEnvironment(name) ?? throw new InvalidDataException($"Missing watch proxy environment variable '{name}'.");
    }
}
