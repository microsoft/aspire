// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Globalization;
using Aspire.Hosting.Resources;
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
    private readonly IFileSystemService _fileSystemService;
    private readonly Dictionary<BrowserHostIdentity, BrowserHostEntry> _hosts = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _lockLifetimeGate = new();
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly TimeProvider _timeProvider;
    private TaskCompletionSource? _lockUsersDrained;
    private int _activeLockUsers;
    private int _disposed;
    private bool _lockDisposed;

    public BrowserHostRegistry(IFileSystemService fileSystemService, ILogger<BrowserLogsSessionManager> logger, TimeProvider timeProvider)
        : this(fileSystemService, logger, timeProvider, createUserDataDirectory: null, createHostAsync: null)
    {
    }

    internal BrowserHostRegistry(
        IFileSystemService fileSystemService,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider,
        Func<BrowserConfiguration, string, BrowserLogsUserDataDirectory>? createUserDataDirectory,
        Func<BrowserConfiguration, BrowserHostIdentity, BrowserLogsUserDataDirectory, CancellationToken, Task<IBrowserHost>>? createHostAsync)
    {
        _endpointDiscovery = new BrowserEndpointDiscovery(logger);
        _createUserDataDirectory = createUserDataDirectory ?? CreateUserDataDirectory;
        _createHostAsync = createHostAsync ?? CreateHostCoreAsync;
        _fileSystemService = fileSystemService;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<BrowserHostLease> AcquireAsync(BrowserConfiguration configuration, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var browserExecutable = ChromiumBrowserResolver.TryResolveExecutable(configuration.Browser)
            ?? throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, MessageStrings.BrowserLogsUnableToLocateBrowser, configuration.Browser));
        var userDataDirectory = _createUserDataDirectory(configuration, browserExecutable);
        var identity = new BrowserHostIdentity(browserExecutable, userDataDirectory.Path);

        // The core AcquireAsync flow has to make one atomic decision per browser identity:
        //
        // 1. If the registry already has a host for this executable + user data root, reuse it and increment the lease
        //    count.
        // 2. Otherwise, create a host exactly once and publish it into the registry with the first lease.
        //
        // Keep the lock held across CreateHostCoreAsync. That method may adopt an existing debug-enabled browser or
        // start a new process, both of which depend on filesystem endpoint metadata for the same user data root. If two
        // callers ran that decision concurrently they could both miss the dictionary entry and race to adopt/start a
        // browser for the same profile.
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
                _logger.LogInformation("Reusing tracked browser host '{BrowserExecutable}' at '{Endpoint}'. Active leases: {ReferenceCount}.", identity.ExecutablePath, entry.Host.DebugEndpoint, entry.ReferenceCount);
                userDataDirectory.Dispose();
                return new BrowserHostLease(entry.Host, releaseAsync: token => ReleaseAsync(identity, token));
            }

            // No host exists for this identity yet. CreateHostCoreAsync owns the second-stage decision:
            // adopt a validated shared browser if one is already running, reject an incompatible locked profile, or
            // start a new owned browser. The returned host is inserted before returning the first lease so future
            // callers can reuse it. This keeps the visible behavior stable when several resources request browser logs
            // together: the first request may open/adopt the browser, and the rest should attach to that result.
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
        if (configuration.UserDataMode == BrowserUserDataMode.Shared)
        {
            // Shared mode has three outcomes, in this order:
            //
            // 1. Adopt a browser that Aspire previously launched for this user data root and profile. The endpoint file
            //    must validate against this browser identity, which protects us from stale metadata left behind by a
            //    different browser or profile. Real browser sessions can leave sidecar files behind if they are closed
            //    externally or crash, so the file is only useful after the process and /json/version endpoint respond.
            // 2. If the profile is locked but no valid Aspire endpoint exists, fail with guidance. That means a normal
            //    browser is using the profile without remote debugging, so we cannot attach and must not start a second
            //    browser against the same locked user data directory. On real Chromium profiles that second launch tends
            //    to hand off to the already-running browser or fail before writing a usable DevTools endpoint.
            // 3. If nothing is running, fall through and start an owned debug-enabled browser.
            if (await _endpointDiscovery.TryReadAndValidateAsync(identity, userDataDirectory.ProfileDirectoryName, cancellationToken).ConfigureAwait(false) is { } metadata)
            {
                var endpoint = new Uri(metadata.Endpoint, UriKind.Absolute);
                _logger.LogInformation("Adopting tracked browser host '{BrowserExecutable}' at '{Endpoint}'.", identity.ExecutablePath, endpoint);
                userDataDirectory.Dispose();
                return new AdoptedBrowserHost(identity, endpoint, configuration.Browser, _logger, _timeProvider);
            }

            if (BrowserEndpointDiscovery.IsNonDebuggableBrowserRunning(identity.UserDataRootPath))
            {
                userDataDirectory.Dispose();
                throw new InvalidOperationException(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        MessageStrings.BrowserLogsNonDebuggableBrowserRunning,
                        identity.UserDataRootPath,
                        BrowserLogsBuilderExtensions.UserDataModeConfigurationKey,
                        BrowserUserDataMode.Isolated));
            }
        }

        _logger.LogInformation("Starting tracked browser host '{BrowserExecutable}'.", identity.ExecutablePath);
        return await OwnedBrowserHost.StartAsync(identity, configuration.Browser, userDataDirectory, _logger, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private BrowserLogsUserDataDirectory CreateUserDataDirectory(BrowserConfiguration configuration, string browserExecutable)
    {
        if (configuration.UserDataMode == BrowserUserDataMode.Isolated)
        {
            // Isolated mode never reuses the user's normal profile. Each host gets a temp user data directory that can
            // be safely deleted when the last lease releases the owned browser.
            return BrowserLogsUserDataDirectory.CreateTemporary(_fileSystemService.TempDirectory.CreateTempSubdirectory("aspire-browser-logs"));
        }

        // Shared mode is a persistent browser context over the browser's real user data root, so user state,
        // extensions, and profiles are available. Chromium puts singleton locks, DevToolsActivePort, and our Aspire
        // endpoint sidecar at that root; named profiles are subdirectories selected by command-line argument. The later
        // endpoint/probe logic decides whether that root is reusable, adoptable, or locked.
        var userDataDirectory = ChromiumBrowserResolver.TryResolveUserDataDirectory(configuration.Browser, browserExecutable)
            ?? throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, MessageStrings.BrowserLogsUnableToResolveUserDataDirectory, configuration.Browser));

        if (!Directory.Exists(userDataDirectory))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, MessageStrings.BrowserLogsUserDataDirectoryNotFoundForBrowser, userDataDirectory, configuration.Browser));
        }

        if (ChromiumBrowserResolver.IsGoogleChromeDefaultUserDataDirectory(configuration.Browser, browserExecutable, userDataDirectory))
        {
            throw new InvalidOperationException(
                string.Format(
                    CultureInfo.CurrentCulture,
                    MessageStrings.BrowserLogsGoogleChromeDefaultUserDataDirectoryNotSupported,
                    userDataDirectory,
                    BrowserLogsBuilderExtensions.UserDataModeConfigurationKey,
                    BrowserUserDataMode.Isolated));
        }

        var profileDirectoryName = configuration.Profile is { } profile
            ? ChromiumBrowserResolver.ResolveProfileDirectory(userDataDirectory, profile)
            : null;
        return BrowserLogsUserDataDirectory.CreatePersistent(userDataDirectory, profileDirectoryName);
    }

    private static void ValidateProfileCompatibility(BrowserHostIdentity identity, string? existingProfileDirectoryName, string? requestedProfileDirectoryName)
    {
        // A request without an explicit profile can attach to any tracked browser for the same user data root. Once a
        // caller asks for a named profile, however, reusing a host launched for a different profile would put the session
        // in the wrong browser context, so fail instead of silently attaching to the wrong profile.
        if (requestedProfileDirectoryName is null ||
            string.Equals(existingProfileDirectoryName, requestedProfileDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            string.Format(
                CultureInfo.CurrentCulture,
                MessageStrings.BrowserLogsTrackedBrowserProfileConflict,
                identity.UserDataRootPath,
                existingProfileDirectoryName ?? MessageStrings.BrowserLogsDefaultProfileName,
                requestedProfileDirectoryName));
    }

    private sealed class BrowserHostEntry(IBrowserHost host, string? profileDirectoryName, int ReferenceCount)
    {
        public IBrowserHost Host { get; } = host;

        public string? ProfileDirectoryName { get; } = profileDirectoryName;

        public int ReferenceCount { get; set; } = ReferenceCount;
    }
}
