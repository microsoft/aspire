// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Native.Client;

/// <summary>Runs an integration's generated provider contract using framework-owned activation coordinates.</summary>
/// <param name="configuration">Injected provider activation settings.</param>
/// <param name="clock">Provider authentication deadlines.</param>
/// <param name="logger">Classified provider diagnostics.</param>
public sealed class NativeProviderHost(IConfiguration configuration, TimeProvider clock, ILogger logger)
{
    /// <summary>Registers a generated contract and remains alive until the native peer disconnects.</summary>
    /// <param name="definition">The integration's offline-generated ATS contract.</param>
    /// <param name="cancellationToken">Stops provider activation and lifetime waits.</param>
    /// <returns>The provider lifetime.</returns>
    public async Task RunAsync(NativeProviderDefinition definition, CancellationToken cancellationToken)
    {
        EndPoint endpoint = configuration["ASPIRE_NATIVE_RPC_SOCKET_PATH"] is { } socketPath
            ? new UnixDomainSocketEndPoint(socketPath)
            : IPEndPoint.Parse(configuration["ASPIRE_NATIVE_RPC_ENDPOINT"]
                ?? throw new InvalidOperationException("Provider activation requires an AppHost endpoint."));
        var token = configuration["ASPIRE_NATIVE_RPC_TOKEN"]
            ?? throw new InvalidOperationException("Provider activation requires authentication.");
        var authority = configuration["ASPIRE_INTEGRATION_HOST_REGISTRATION_ID"]
            ?? throw new InvalidOperationException("Provider activation requires expected registration authority.");
        var connected = await NativeClient.ConnectAsync(endpoint, token, TimeSpan.FromSeconds(15), clock, cancellationToken).ConfigureAwait(false);
        await using var clientLifetime = connected.ConfigureAwait(false);
        var client = connected;
        await client.RegisterIntegrationHostAsync(authority, definition, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Native integration provider registered.");
        await client.WaitForDisconnectAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Configures independent provider authority cleanup and owned-process shutdown deadlines.</summary>
public sealed class NativeProviderActivationOptions
{
    /// <summary>Gets the independent deadline for abandoning failed activation authority.</summary>
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Gets the deadline for process termination and redirected output release.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(15);

    internal void Validate()
    {
        foreach (var timeout in new[] { CleanupTimeout, ShutdownTimeout })
        {
            if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout), "Provider cleanup deadlines must be positive bounded durations.");
            }
        }
    }
}

/// <summary>Activates an explicitly selected provider process; application code never performs registration or handle plumbing.</summary>
public sealed class NativeProviderActivation : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _output;
    private readonly CancellationTokenSource _outputCancellation = new();
    private readonly TimeProvider _clock;
    private readonly NativeProviderActivationOptions _options;

    /// <summary>Owns a started provider and its redirected output.</summary>
    internal NativeProviderActivation(Process process, NativeProviderActivationOptions options, TimeProvider clock)
    {
        _process = process;
        _clock = clock;
        _options = options;
        // Drain both streams concurrently. Only classifications are logged by the
        // framework; provider console output may include application secrets.
        _output = Task.WhenAll(
            process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, _outputCancellation.Token),
            process.StandardError.BaseStream.CopyToAsync(Stream.Null, _outputCancellation.Token));
    }

    /// <summary>Launches one explicitly selected provider and awaits server-verified registration.</summary>
    public static async Task<NativeProviderActivation> StartAsync(NativeClient client, string packageName,
        string executable, IReadOnlyList<string> arguments, NativeProviderActivationOptions options,
        TimeProvider clock, ILogger logger, CancellationToken cancellationToken)
    {
        options.Validate();
        if (client.Endpoint is not IPEndPoint and not UnixDomainSocketEndPoint)
        {
            throw new NotSupportedException("Provider activation requires a local AppHost endpoint.");
        }
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // Kernel-enforced parent ownership survives abrupt host termination.
        // .NET does not support this option on macOS; explicit disposal still applies.
        // https://learn.microsoft.com/dotnet/api/system.diagnostics.processstartinfo.killonparentexit
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        {
            start.KillOnParentExit = true;
        }
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment.Remove("ASPIRE_NATIVE_RPC_ENDPOINT");
        start.Environment.Remove("ASPIRE_NATIVE_RPC_SOCKET_PATH");
        if (client.Endpoint is UnixDomainSocketEndPoint socket)
        {
            start.Environment["ASPIRE_NATIVE_RPC_SOCKET_PATH"] = socket.ToString();
        }
        else
        {
            start.Environment["ASPIRE_NATIVE_RPC_ENDPOINT"] = client.Endpoint.ToString();
        }
        start.Environment["ASPIRE_NATIVE_RPC_TOKEN"] = client.AuthenticationToken;
        var authority = await client.ExpectIntegrationHostAsync(packageName, cancellationToken).ConfigureAwait(false);
        NativeProviderActivation? activation = null;
        try
        {
            start.Environment["ASPIRE_INTEGRATION_HOST_REGISTRATION_ID"] = authority;
            activation = new NativeProviderActivation(Process.Start(start) ?? throw new InvalidOperationException("The provider process did not start."), options, clock);
            await client.WaitIntegrationHostRegistrationAsync(authority, cancellationToken).ConfigureAwait(false);
            if (activation._process.HasExited)
            {
                throw new InvalidOperationException("The provider disconnected during activation.");
            }
            logger.LogInformation("Native integration provider activation completed.");

            return activation;
        }
        catch (Exception activationError)
        {
            List<Exception> failures = [activationError];
            try
            {
                // The caller token may already be cancelled. An independent deadline
                // lets cleanup run, but a stalled peer cannot prevent process disposal.
                using var cleanup = new CancellationTokenSource(options.CleanupTimeout, clock);
                await client.AbandonIntegrationHostRegistrationAsync(authority, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                failures.Add(cleanupError);
            }
            if (activation is not null)
            {
                try
                {
                    await activation.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    failures.Add(cleanupError);
                }
            }
            if (failures.Count > 1)
            {
                throw new AggregateException("Provider activation and cleanup failed.", failures);
            }
            throw;
        }
    }

    /// <summary>Stops only the provider process owned by this activation and drains its output.</summary>
    public async ValueTask DisposeAsync()
    {
        using var processLifetime = _process;
        using var outputLifetime = _outputCancellation;
        var completion = _output;
        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (_process.HasExited)
                {
                    // Exiting between the check and Kill is normal shutdown.
                }
            }
            // A descendant can retain the pipes after the root exits. Both exit
            // and EOF must fit within the same shutdown budget.
            completion = Task.WhenAll(_process.WaitForExitStatusAsync(), _output);
            await completion.WaitAsync(_options.ShutdownTimeout, _clock).ConfigureAwait(false);
        }
        finally
        {
            // Cancellation and reader disposal release pending reads even when
            // process-tree termination cannot reach a pipe-owning descendant.
            _outputCancellation.Cancel();
            _process.StandardOutput.Dispose();
            _process.StandardError.Dispose();
            // A timed-out drain can fault after its caller has already observed
            // the shutdown failure. Observe that late fault without awaiting EOF.
            _ = completion.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
