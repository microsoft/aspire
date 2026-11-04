// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.RemoteHost.Ats;
using Aspire.Hosting.RemoteHost.Diagnostics;
using Aspire.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Aspire.Hosting.RemoteHost.Language;

/// <summary>
/// Owns integration-host startup, recovery, diagnostics, and shutdown for the AppHost server.
/// </summary>
internal sealed class IntegrationHostLauncher : IHostedService, IAsyncDisposable
{
    internal const string RegistrationIdVariable = "ASPIRE_INTEGRATION_HOST_REGISTRATION_ID";
    private readonly IntegrationHostProcessLauncher _processLauncher;
    private readonly ExternalCapabilityRegistry _externalCapabilityRegistry;
    private readonly IntegrationHostConfiguration _configuration;
    private readonly RemoteHostProfilingTelemetry _profilingTelemetry;
    private readonly TimeProvider _timeProvider;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<IntegrationHostLauncher> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _supervisors = [];
    private readonly object _lifetimeGate = new();
    private readonly TaskCompletionSource<bool> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> _startupFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _startTask;
    private Task? _stopTask;
    private Exception? _failure;

    public IntegrationHostLauncher(
        IntegrationHostProcessLauncher processLauncher,
        ExternalCapabilityRegistry externalCapabilityRegistry,
        IntegrationHostConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILogger<IntegrationHostLauncher> logger,
        RemoteHostProfilingTelemetry profilingTelemetry,
        TimeProvider timeProvider)
    {
        _processLauncher = processLauncher;
        _externalCapabilityRegistry = externalCapabilityRegistry;
        _configuration = configuration;
        _lifetime = lifetime;
        _logger = logger;
        _profilingTelemetry = profilingTelemetry;
        _timeProvider = timeProvider;
    }

    public Task ReadyAsync(CancellationToken cancellationToken = default)
        => _readyTcs.Task.WaitAsync(cancellationToken);

    internal void ThrowIfFailed()
    {
        if (Volatile.Read(ref _failure) is { } failure)
        {
            throw new InvalidOperationException("Integration host recovery failed; the AppHost session was stopped.", failure);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifetimeGate)
        {
            return _startTask ??= StartCoreAsync(cancellationToken);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        using var activity = _profilingTelemetry.StartIntegrationHostStartup();
        using var initializationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            initializationCancellation.Token.ThrowIfCancellationRequested();
            var descriptors = _configuration.Descriptors;
            if (_configuration.Bootstrap)
            {
                _logger.LogDebug("Skipping integration hosts while bootstrapping the managed SDK.");
                _readyTcs.TrySetResult(true);
                return;
            }

            foreach (var descriptor in descriptors)
            {
                _supervisors.Add(SuperviseHostAsync(descriptor, _shutdown.Token));
            }

            var initialization = InitializeHostsAsync(
                descriptors.Count, _configuration.RegistrationTimeout, _configuration.DiscoveryTimeout, initializationCancellation.Token);
            if (await Task.WhenAny(initialization, _startupFailure.Task).ConfigureAwait(false) == _startupFailure.Task)
            {
                initializationCancellation.Cancel();
                await initialization.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                throw await _startupFailure.Task.ConfigureAwait(false);
            }

            await initialization.ConfigureAwait(false);
            if (_startupFailure.Task.IsCompleted)
            {
                throw await _startupFailure.Task.ConfigureAwait(false);
            }

            _readyTcs.TrySetResult(true);
        }
        catch (OperationCanceledException ex) when (initializationCancellation.IsCancellationRequested)
        {
            activity.SetError(ex);
            _readyTcs.TrySetCanceled(initializationCancellation.Token);
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            _logger.LogError(ex, "Failed to initialize integration hosts at server startup.");
            _readyTcs.TrySetException(ex);
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal async Task InitializeHostsAsync(int expectedCount, TimeSpan registrationTimeout, TimeSpan discoveryTimeout, CancellationToken cancellationToken)
    {
        var registered = await _externalCapabilityRegistry.WaitForHostsAsync(
            expectedCount, registrationTimeout, cancellationToken).ConfigureAwait(false);
        if (registered != expectedCount)
        {
            throw new TimeoutException(
                $"Only {registered} of {expectedCount} integration hosts registered within the per-host timeout ({registrationTimeout}). " +
                "SDK generation was stopped because required integrations are unavailable. Check the integration host startup logs.");
        }

        await _externalCapabilityRegistry.InitializeAllHostsAsync(expectedCount, discoveryTimeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task SuperviseHostAsync(IntegrationHostDescriptor descriptor, CancellationToken cancellationToken)
    {
        JsonRpc? previousConnection = null;
        var restartAttempts = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var registrationId = Guid.NewGuid().ToString("N");
                var registration = _externalCapabilityRegistry.ExpectHostRegistration(registrationId, descriptor.PackageName);
                using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                JsonRpc? connection = null;
                ProcessScope? process = null;
                Exception? failure = null;
                long? healthySince = null;
                var wasStable = false;
                var hasCallbacks = false;
                try
                {
                    process = await _processLauncher.LaunchAsync(descriptor, registrationId, attemptCancellation.Token).ConfigureAwait(false);
                    using (var activity = _profilingTelemetry.StartIntegrationHostPhase(
                        "registration", descriptor.PackageName, descriptor.Language, _configuration.RegistrationTimeout))
                    {
                        activity.SetIntegrationHostProcessId(process.ProcessId);
                        var started = _timeProvider.GetTimestamp();
                        _logger.LogInformation(
                            "Integration host '{Name}' registration started: attempt {RegistrationId}, supervisor PID {Pid}, timeout {Timeout}.",
                            descriptor.PackageName, registrationId, process.ProcessId, _configuration.RegistrationTimeout);
                        try
                        {
                            var registered = registration.WaitAsync(_configuration.RegistrationTimeout, _timeProvider, attemptCancellation.Token);
                            if (await Task.WhenAny(registered, process.Exit).ConfigureAwait(false) == process.Exit)
                            {
                                throw CreateExitException(descriptor, process, await process.Exit.ConfigureAwait(false));
                            }
                            connection = await registered.ConfigureAwait(false);
                            _logger.LogInformation(
                                "Integration host '{Name}' registered: attempt {RegistrationId}, supervisor PID {Pid}, connection {Host}, elapsed {Elapsed}.",
                                descriptor.PackageName, registrationId, process.ProcessId, connection.GetHashCode(), _timeProvider.GetElapsedTime(started));
                        }
                        catch (Exception ex)
                        {
                            activity.SetError(ex);
                            if (ex is TimeoutException)
                            {
                                throw new TimeoutException(
                                    $"Integration host '{descriptor.PackageName}' registration timed out after {_configuration.RegistrationTimeout} " +
                                    $"(attempt {registrationId}, supervisor PID {process.ProcessId}). Check its stdout/stderr.", ex);
                            }
                            throw;
                        }
                    }

                    if (previousConnection is null)
                    {
                        // Remember the connection as soon as it registers. Readiness and
                        // disconnect callbacks can race; recovery still needs its published
                        // registrations even if this supervisor resumes after the guest.
                        previousConnection = connection;
                        var ready = _readyTcs.Task.WaitAsync(cancellationToken);
                        if (await Task.WhenAny(ready, process.Exit, connection.Completion).ConfigureAwait(false) != ready &&
                            !_readyTcs.Task.IsCompletedSuccessfully)
                        {
                            throw new InvalidOperationException(
                                $"Integration host '{descriptor.PackageName}' disconnected during startup. Check its stdout/stderr.");
                        }
                        await ready.ConfigureAwait(false);
                    }
                    else
                    {
                        using var activity = _profilingTelemetry.StartIntegrationHostPhase(
                            "rediscovery", descriptor.PackageName, descriptor.Language, _configuration.DiscoveryTimeout);
                        activity.SetIntegrationHostProcessId(process.ProcessId);
                        var discoveryStarted = _timeProvider.GetTimestamp();
                        _logger.LogInformation(
                            "Integration host '{Name}' rediscovery started: connection {Host}, supervisor PID {Pid}, timeout {Timeout}.",
                            descriptor.PackageName, connection.GetHashCode(), process.ProcessId, _configuration.DiscoveryTimeout);
                        try
                        {
                            await _externalCapabilityRegistry.ReplaceHostAsync(
                                previousConnection, connection, _configuration.DiscoveryTimeout, cancellationToken).ConfigureAwait(false);
                        }
                        catch (TimeoutException ex)
                        {
                            var error = new TimeoutException(
                                $"Integration host '{descriptor.PackageName}' capability rediscovery timed out after {_configuration.DiscoveryTimeout} " +
                                $"(supervisor PID {process.ProcessId}, connection {connection.GetHashCode()}).", ex);
                            activity.SetError(error);
                            throw error;
                        }
                        catch (Exception ex)
                        {
                            activity.SetError(ex);
                            throw;
                        }
                        finally
                        {
                            _logger.LogInformation(
                                "Integration host '{Name}' rediscovery finished: connection {Host}, elapsed {Elapsed}, timeout {Timeout}.",
                                descriptor.PackageName, connection.GetHashCode(), _timeProvider.GetElapsedTime(discoveryStarted), _configuration.DiscoveryTimeout);
                        }
                        _logger.LogInformation(
                            "Integration host '{Name}' recovered after restart attempt {Attempt}; capabilities rediscovered.",
                            descriptor.PackageName, restartAttempts);
                    }
                    previousConnection = connection;
                    healthySince = _timeProvider.GetTimestamp();

                    await Task.WhenAny(process.Exit, connection.Completion).WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (process.Exit.IsCompleted)
                    {
                        throw CreateExitException(descriptor, process, await process.Exit.ConfigureAwait(false));
                    }

                    throw new InvalidOperationException(
                        $"Integration host '{descriptor.PackageName}' disconnected from the AppHost server.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    if (!_readyTcs.Task.IsCompletedSuccessfully)
                    {
                        _startupFailure.TrySetResult(ex);
                    }
                    wasStable = healthySince is { } timestamp && _timeProvider.GetElapsedTime(timestamp) >= _configuration.StableHostPeriod;
                }
                finally
                {
                    attemptCancellation.Cancel();
                    _externalCapabilityRegistry.ForgetHostRegistration(registrationId);
                    if (connection is null && registration.IsCompletedSuccessfully)
                    {
                        connection = registration.Result;
                    }
                    if (connection is not null)
                    {
                        hasCallbacks = _externalCapabilityRegistry.MarkHostUnavailable(connection);
                    }
                    if (process is not null)
                    {
                        await process.DisposeAsync(failure).ConfigureAwait(false);
                    }
                }

                if (!_readyTcs.Task.IsCompletedSuccessfully)
                {
                    _startupFailure.TrySetResult(failure!);
                    return;
                }
                cancellationToken.ThrowIfCancellationRequested();

                if (hasCallbacks)
                {
                    var callbackFailure = new InvalidOperationException(
                        $"Integration host '{descriptor.PackageName}' owns callbacks that cannot be restored by restarting the host. " +
                        "Restart the AppHost session to rebuild the resource model.", failure);
                    _logger.LogError(callbackFailure,
                        "Integration host '{Name}' owns callbacks that cannot be restored by restarting the host. " +
                        "Stopping the AppHost session. Restart it to rebuild the resource model.",
                        descriptor.PackageName);
                    Interlocked.CompareExchange(ref _failure, callbackFailure, null);
                    _lifetime.StopApplication();
                    return;
                }

                // Count fast crash loops against the same budget. A single successful
                // registration is not evidence that the replacement is healthy.
                if (wasStable)
                {
                    restartAttempts = 0;
                }
                if (restartAttempts == _configuration.MaxRestartAttempts)
                {
                    _logger.LogError(failure,
                        "Integration host '{Name}' failed after {Attempts} restart attempts. Stopping the AppHost session.",
                        descriptor.PackageName, restartAttempts);
                    Interlocked.CompareExchange(ref _failure, failure, null);
                    _lifetime.StopApplication();
                    return;
                }

                restartAttempts++;
                var delay = _configuration.GetRestartDelay(restartAttempts);
                _logger.LogWarning(failure,
                    "Integration host '{Name}' is unavailable. Restart attempt {Attempt}/{Maximum} in {Delay}.",
                    descriptor.PackageName, restartAttempts, _configuration.MaxRestartAttempts, delay);
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Do not start a replacement if cleanup could not establish that the old
            // process scope stopped. That would leave multiple owners mutating the model.
            _logger.LogError(ex, "Integration host supervision failed for '{Name}'.", descriptor.PackageName);
            if (!_readyTcs.Task.IsCompletedSuccessfully)
            {
                _startupFailure.TrySetResult(ex);
            }
            else
            {
                Interlocked.CompareExchange(ref _failure, ex, null);
                _lifetime.StopApplication();
            }
        }
    }

    private static InvalidOperationException CreateExitException(
        IntegrationHostDescriptor descriptor, ProcessScope process, int exitCode)
        => new(
            $"Integration host supervisor for '{descriptor.PackageName}' (PID {process.ProcessId}, entry '{descriptor.HostEntryPoint}') " +
            $"exited with code {exitCode}. Check IntegrationHost[{descriptor.PackageName}] diagnostics for the runtime's original exit code and stderr.");

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lifetimeGate)
        {
            return (_stopTask ??= StopCoreAsync()).WaitAsync(cancellationToken);
        }
    }

    private async Task StopCoreAsync()
    {
        _shutdown.Cancel();
        _readyTcs.TrySetCanceled(_shutdown.Token);
        _externalCapabilityRegistry.Stop();
        await Task.WhenAll(_supervisors).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
