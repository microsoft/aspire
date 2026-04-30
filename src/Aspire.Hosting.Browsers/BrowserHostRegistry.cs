// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Diagnostics;
using Aspire.Hosting.Browsers.Resources;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

// Coordinates host sharing for all tracked browser sessions in an AppHost. The registry is the only component that
// decides whether a request reuses an in-process host, adopts a previously launched debug-enabled browser, or starts a
// new owned browser, and it centralizes reference counting for those choices.
internal sealed class BrowserHostRegistry : IAsyncDisposable
{
    private readonly BrowserEndpointDiscovery _endpointDiscovery;
    private readonly Func<BrowserConfiguration, string, BrowserLogsUserDataDirectory> _createUserDataDirectory;
    private readonly Func<BrowserConfiguration, BrowserHostIdentity, BrowserLogsUserDataDirectory, CancellationToken, Task<IBrowserHost>> _createHostAsync;
    private readonly Dictionary<BrowserHostIdentity, BrowserHostEntry> _hosts = new();
    private readonly bool _enableEndpointMetadataAdoption;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _lockLifetimeGate = new();
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly TimeProvider _timeProvider;
    private TaskCompletionSource? _lockUsersDrained;
    private int _activeLockUsers;
    private int _disposed;
    private bool _lockDisposed;

    public BrowserHostRegistry(ILogger<BrowserLogsSessionManager> logger, TimeProvider timeProvider)
        : this(logger, timeProvider, createUserDataDirectory: null, createHostAsync: null)
    {
    }

    internal BrowserHostRegistry(
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider,
        Func<BrowserConfiguration, string, BrowserLogsUserDataDirectory>? createUserDataDirectory,
        Func<BrowserConfiguration, BrowserHostIdentity, BrowserLogsUserDataDirectory, CancellationToken, Task<IBrowserHost>>? createHostAsync,
        bool enableEndpointMetadataAdoption = false)
    {
        _endpointDiscovery = new BrowserEndpointDiscovery(logger);
        _createUserDataDirectory = createUserDataDirectory ?? CreateUserDataDirectory;
        _createHostAsync = createHostAsync ?? CreateHostCoreAsync;
        _enableEndpointMetadataAdoption = enableEndpointMetadataAdoption;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<BrowserHostLease> AcquireAsync(BrowserConfiguration configuration, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var browserExecutable = ChromiumBrowserResolver.TryResolveExecutable(configuration.Browser)
            ?? throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, BrowserMessageStrings.BrowserLogsUnableToLocateBrowser, configuration.Browser));
        var userDataDirectory = _createUserDataDirectory(configuration, browserExecutable);
        var identity = new BrowserHostIdentity(browserExecutable, userDataDirectory.Path);

        // The core AcquireAsync flow has to make one atomic decision per browser identity:
        //
        // 1. If the registry already has a host for this executable + user data root, reuse it and increment the lease
        //    count.
        // 2. Otherwise, create a host exactly once and publish it into the registry with the first lease.
        //
        // Keep the lock held across CreateHostCoreAsync. That method starts a new process by default, and can adopt a
        // WebSocket endpoint when an explicit attach mode enables endpoint metadata. If two callers ran that decision
        // concurrently they could both miss the dictionary entry and race to adopt/start a browser for the same profile.
        var lockAcquired = false;
        var hostPublished = false;
        try
        {
            lockAcquired = await TryWaitForLockAsync(cancellationToken).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(!lockAcquired, this);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (_hosts.TryGetValue(identity, out var entry))
            {
                // The identity is rooted at the browser executable and user data directory, not at a specific profile.
                // In Playwright terms, the user data directory is the persistent-context boundary: multiple pages can
                // share one browser process/context, while requests for a different named profile are rejected.
                // In the playground this shows up as one browser window/process with additional tracked page targets
                // as more resources start browser-log sessions, rather than one browser process per session.
                ValidateProfileCompatibility(identity, entry.ProfileDirectoryName, userDataDirectory.ProfileDirectoryName);
                entry.ReferenceCount++;
                _logger.LogInformation("Reusing tracked browser host '{BrowserExecutable}' at '{Endpoint}'. Active leases: {ReferenceCount}.", identity.ExecutablePath, FormatDebugEndpoint(entry.Host.DebugEndpoint), entry.ReferenceCount);
                userDataDirectory.Dispose();
                return new BrowserHostLease(entry.Host, releaseAsync: token => ReleaseAsync(identity, token));
            }

            // No host exists for this identity yet. CreateHostCoreAsync owns the second-stage decision: start a new
            // pipe-owned browser by default, or adopt a validated WebSocket endpoint if an explicit attach mode enabled
            // that path. The returned host is inserted before returning the first lease so future callers can reuse it.
            // This keeps the visible behavior stable when several resources request browser logs together: the first
            // request opens/adopts the browser, and the rest attach to that result.
            var host = await _createHostAsync(configuration, identity, userDataDirectory, cancellationToken).ConfigureAwait(false);
            _hosts[identity] = new BrowserHostEntry(host, userDataDirectory.ProfileDirectoryName, ReferenceCount: 1);
            hostPublished = true;
            return new BrowserHostLease(host, releaseAsync: token => ReleaseAsync(identity, token));
        }
        catch
        {
            if (!hostPublished)
            {
                userDataDirectory.Dispose();
            }

            throw;
        }
        finally
        {
            if (lockAcquired)
            {
                ReleaseLock();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<IBrowserHost> hosts;
        var lockAcquired = await TryWaitForLockAsync(CancellationToken.None).ConfigureAwait(false);
        // DisposeAsync is the only path that flips _lockDisposed, and the Interlocked guard above allows exactly one
        // disposer through. Therefore the first disposer must be able to acquire the lock here; if a future refactor
        // changes that lifetime ordering, throwing is safer than continuing with a partially-disposed registry.
        ObjectDisposedException.ThrowIf(!lockAcquired, this);
        try
        {
            hosts = [.. _hosts.Values.Select(static entry => entry.Host)];
            _hosts.Clear();
        }
        finally
        {
            ReleaseLock();
        }

        try
        {
            foreach (var host in hosts)
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await DisposeLockAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask ReleaseAsync(BrowserHostIdentity identity, CancellationToken cancellationToken)
    {
        IBrowserHost? hostToDispose = null;

        if (Volatile.Read(ref _disposed) != 0)
        {
            // DisposeAsync clears the registry and disposes every host. Late lease releases can safely no-op because
            // the host they refer to is already part of the registry-wide disposal path.
            return;
        }

        var lockAcquired = await TryWaitForLockAsync(cancellationToken).ConfigureAwait(false);
        if (!lockAcquired)
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (_hosts.TryGetValue(identity, out var entry))
            {
                Debug.Assert(entry.ReferenceCount > 0, "BrowserHostRegistry reference count underflow.");
                if (entry.ReferenceCount <= 0)
                {
                    _logger.LogError("Tracked browser host '{BrowserExecutable}' for user data directory '{UserDataDirectory}' had an invalid reference count '{ReferenceCount}' during release.", identity.ExecutablePath, identity.UserDataRootPath, entry.ReferenceCount);
                    return;
                }

                entry.ReferenceCount--;
                if (entry.ReferenceCount == 0)
                {
                    _hosts.Remove(identity);
                    hostToDispose = entry.Host;
                }
            }
        }
        finally
        {
            ReleaseLock();
        }

        if (hostToDispose is not null)
        {
            await hostToDispose.DisposeAsync().ConfigureAwait(false);
        }

    }

    private async Task<bool> TryWaitForLockAsync(CancellationToken cancellationToken)
    {
        if (!TryAddLockUser())
        {
            return false;
        }

        try
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            RemoveLockUser();
            throw;
        }
    }

    private bool TryAddLockUser()
    {
        lock (_lockLifetimeGate)
        {
            if (_lockDisposed)
            {
                return false;
            }

            _activeLockUsers++;
            return true;
        }
    }

    private void ReleaseLock()
    {
        try
        {
            _lock.Release();
        }
        finally
        {
            RemoveLockUser();
        }
    }

    private void RemoveLockUser()
    {
        TaskCompletionSource? lockUsersDrained = null;

        lock (_lockLifetimeGate)
        {
            _activeLockUsers--;
            if (_lockDisposed && _activeLockUsers == 0)
            {
                lockUsersDrained = _lockUsersDrained;
            }
        }

        lockUsersDrained?.TrySetResult();
    }

    private async Task DisposeLockAsync()
    {
        Task? lockUsersDrained = null;

        lock (_lockLifetimeGate)
        {
            _lockDisposed = true;
            if (_activeLockUsers > 0)
            {
                _lockUsersDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                lockUsersDrained = _lockUsersDrained.Task;
            }
        }

        if (lockUsersDrained is not null)
        {
            await lockUsersDrained.ConfigureAwait(false);
        }

        _lock.Dispose();
    }

    private async Task<IBrowserHost> CreateHostCoreAsync(
        BrowserConfiguration configuration,
        BrowserHostIdentity identity,
        BrowserLogsUserDataDirectory userDataDirectory,
        CancellationToken cancellationToken)
    {
        // Default owned launches use a process-private CDP pipe. WebSocket remains the attach/adoption transport for
        // explicit connect-to-existing-browser modes, but the normal path must not adopt stale endpoint metadata from
        // earlier WebSocket experiments because pipe-backed browsers cannot be reattached across AppHost processes.
        if (_enableEndpointMetadataAdoption &&
            await _endpointDiscovery.TryReadAndValidateAsync(identity, userDataDirectory.ProfileDirectoryName, cancellationToken).ConfigureAwait(false) is { } metadata)
        {
            var endpoint = new Uri(metadata.Endpoint, UriKind.Absolute);
            _logger.LogInformation("Adopting tracked browser host '{BrowserExecutable}' at '{Endpoint}'.", identity.ExecutablePath, endpoint);
            userDataDirectory.Dispose();
            return new AdoptedBrowserHost(identity, endpoint, configuration.Browser, _logger, _timeProvider);
        }

        _logger.LogInformation("Starting tracked browser host '{BrowserExecutable}' with a private CDP pipe.", identity.ExecutablePath);
        return await OwnedBrowserHost.StartAsync(identity, configuration.Browser, userDataDirectory, _logger, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private BrowserLogsUserDataDirectory CreateUserDataDirectory(BrowserConfiguration configuration, string browserExecutable)
    {
        // Both modes use a persistent Aspire-managed user data directory. The mode picks the path scope:
        //   Shared   -> machine-wide, shared across every Aspire AppHost
        //   Isolated -> per-AppHost (keyed on AppHost:PathSha256)
        //
        // The directory is created on demand and not deleted by AppHost shutdown. The browser process itself is
        // pipe-backed and defaults to Session lifetime, so each new AppHost run starts its own debuggable browser
        // process unless an advanced lifetime option intentionally leaves the old browser running.
        var path = BrowserUserDataPathResolver.Resolve(configuration);

        // Profile resolution requires Local State to exist (Chromium writes it on first launch). Skip resolution
        // when the directory is fresh and treat the supplied profile as the literal --profile-directory value;
        // Chromium creates the sub-directory on first use.
        var profileDirectoryName = configuration.Profile is { } profile
            ? ResolveProfileDirectoryName(path, profile)
            : null;
        return BrowserLogsUserDataDirectory.CreatePersistent(path, profileDirectoryName);
    }

    private static string ResolveProfileDirectoryName(string userDataDirectory, string profile)
    {
        var localStatePath = Path.Combine(userDataDirectory, "Local State");
        // Chromium writes a "Local State" JSON file at the user data root containing profile metadata (info_cache).
        // ChromiumBrowserResolver uses it to map display names like "Personal" or shortcut names back to their on-disk
        // profile directory ("Profile 1", "Profile 2", ...).
        if (File.Exists(localStatePath))
        {
            return ChromiumBrowserResolver.ResolveProfileDirectory(userDataDirectory, profile);
        }

        // Fresh user data directory: no Local State to map display names through. Use the supplied profile string
        // as the literal directory name. Chromium creates it on launch.
        return profile;
    }

    private static void ValidateProfileCompatibility(BrowserHostIdentity identity, string? existingProfileDirectoryName, string? requestedProfileDirectoryName)
    {
        // A request without an explicit profile can attach to any tracked browser for the same user data root. Once a
        // caller asks for a named profile, however, reusing a host launched for a different profile would put the session
        // in the wrong browser context, so fail instead of silently attaching to the wrong profile.
        // Profile directory names are case-insensitive on Windows and macOS (default APFS) but case-sensitive on Linux.
        // We compare with OrdinalIgnoreCase intentionally so a request for "default" attaches to a host that was
        // launched with "Default": Chromium itself accepts either casing on Windows/macOS, and on Linux the user is
        // expected to specify the literal directory name. We err on the side of attaching rather than rejecting.
        if (requestedProfileDirectoryName is null ||
            string.Equals(existingProfileDirectoryName, requestedProfileDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            string.Format(
                CultureInfo.CurrentCulture,
                BrowserMessageStrings.BrowserLogsTrackedBrowserProfileConflict,
                identity.UserDataRootPath,
                existingProfileDirectoryName ?? BrowserMessageStrings.BrowserLogsDefaultProfileName,
                requestedProfileDirectoryName));
    }

    private static string FormatDebugEndpoint(Uri? debugEndpoint) =>
        debugEndpoint?.ToString() ?? "private CDP pipe";

    private sealed class BrowserHostEntry(IBrowserHost host, string? profileDirectoryName, int ReferenceCount)
    {
        public IBrowserHost Host { get; } = host;

        public string? ProfileDirectoryName { get; } = profileDirectoryName;

        public int ReferenceCount { get; set; } = ReferenceCount;
    }
}
