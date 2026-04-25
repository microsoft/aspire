// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

// Coordinates host sharing for all tracked browser sessions in an AppHost. The registry is the only component that
// decides whether a request reuses an in-process host, adopts a previously launched debug-enabled browser, or starts a
// new owned browser, and it centralizes reference counting for those choices.
internal sealed class BrowserHostRegistry : IAsyncDisposable
{
    private readonly BrowserEndpointDiscovery _endpointDiscovery;
    private readonly IFileSystemService _fileSystemService;
    private readonly Dictionary<BrowserHostIdentity, BrowserHostEntry> _hosts = new();
    // Keep the semaphore available for late no-op releases from outstanding leases during registry disposal.
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    public BrowserHostRegistry(IFileSystemService fileSystemService, ILogger<BrowserLogsSessionManager> logger, TimeProvider timeProvider)
    {
        _endpointDiscovery = new BrowserEndpointDiscovery(logger);
        _fileSystemService = fileSystemService;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<BrowserHostLease> AcquireAsync(BrowserLogsSettings settings, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var browserExecutable = BrowserLogsRunningSession.TryResolveBrowserExecutable(settings.Browser)
            ?? throw new InvalidOperationException($"Unable to locate browser '{settings.Browser}'. Specify an installed Chromium-based browser or an explicit executable path.");
        var userDataDirectory = CreateUserDataDirectory(settings, browserExecutable);
        var identity = new BrowserHostIdentity(browserExecutable, userDataDirectory.Path);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (_hosts.TryGetValue(identity, out var entry))
            {
                ValidateProfileCompatibility(identity, entry.ProfileDirectoryName, userDataDirectory.ProfileDirectoryName);
                entry.ReferenceCount++;
                _logger.LogInformation("Reusing tracked browser host '{BrowserExecutable}' at '{Endpoint}'. Active leases: {ReferenceCount}.", identity.ExecutablePath, entry.Host.DebugEndpoint, entry.ReferenceCount);
                userDataDirectory.Dispose();
                return new BrowserHostLease(entry.Host, releaseAsync: token => ReleaseAsync(identity, token));
            }

            var host = await CreateHostAsync(settings, identity, userDataDirectory, cancellationToken).ConfigureAwait(false);
            _hosts[identity] = new BrowserHostEntry(host, userDataDirectory.ProfileDirectoryName, ReferenceCount: 1);
            return new BrowserHostLease(host, releaseAsync: token => ReleaseAsync(identity, token));
        }
        catch
        {
            if (!_hosts.ContainsKey(identity))
            {
                userDataDirectory.Dispose();
            }

            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<IBrowserHost> hosts;
        await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            hosts = [.. _hosts.Values.Select(static entry => entry.Host)];
            _hosts.Clear();
        }
        finally
        {
            _lock.Release();
        }

        foreach (var host in hosts)
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask ReleaseAsync(BrowserHostIdentity identity, CancellationToken cancellationToken)
    {
        IBrowserHost? hostToDispose = null;

        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
            _lock.Release();
        }

        if (hostToDispose is not null)
        {
            await hostToDispose.DisposeAsync().ConfigureAwait(false);
        }

    }

    private async Task<IBrowserHost> CreateHostAsync(
        BrowserLogsSettings settings,
        BrowserHostIdentity identity,
        BrowserLogsUserDataDirectory userDataDirectory,
        CancellationToken cancellationToken)
    {
        if (settings.UserDataMode == BrowserUserDataMode.Shared)
        {
            if (await _endpointDiscovery.TryReadAndValidateAsync(identity, userDataDirectory.ProfileDirectoryName, cancellationToken).ConfigureAwait(false) is { } metadata)
            {
                var endpoint = new Uri(metadata.Endpoint, UriKind.Absolute);
                _logger.LogInformation("Adopting tracked browser host '{BrowserExecutable}' at '{Endpoint}'.", identity.ExecutablePath, endpoint);
                userDataDirectory.Dispose();
                return new AdoptedBrowserHost(identity, endpoint, settings.Browser, _logger, _timeProvider);
            }

            if (BrowserEndpointDiscovery.IsNonDebuggableBrowserRunning(identity.UserDataRootPath))
            {
                userDataDirectory.Dispose();
                throw new InvalidOperationException(
                    $"Browser user data directory '{identity.UserDataRootPath}' is already in use by a non-debuggable browser. " +
                    $"Close that browser, use '{BrowserLogsBuilderExtensions.UserDataModeConfigurationKey}'='{BrowserUserDataMode.Isolated}', or start the browser from Aspire first.");
            }
        }

        _logger.LogInformation("Starting tracked browser host '{BrowserExecutable}'.", identity.ExecutablePath);
        return await OwnedBrowserHost.StartAsync(identity, settings.Browser, userDataDirectory, _logger, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private BrowserLogsUserDataDirectory CreateUserDataDirectory(BrowserLogsSettings settings, string browserExecutable)
    {
        if (settings.UserDataMode == BrowserUserDataMode.Isolated)
        {
            return BrowserLogsUserDataDirectory.CreateTemporary(_fileSystemService.TempDirectory.CreateTempSubdirectory("aspire-browser-logs"));
        }

        var userDataDirectory = BrowserLogsRunningSession.TryResolveBrowserUserDataDirectory(settings.Browser, browserExecutable)
            ?? throw new InvalidOperationException($"Unable to resolve the user data directory for browser '{settings.Browser}'. Specify a known browser such as 'msedge' or 'chrome' when using shared user data mode, or use the isolated user data mode.");

        if (!Directory.Exists(userDataDirectory))
        {
            throw new InvalidOperationException($"Browser user data directory '{userDataDirectory}' was not found for browser '{settings.Browser}'.");
        }

        if (BrowserLogsRunningSession.IsGoogleChromeDefaultUserDataDirectory(settings.Browser, browserExecutable, userDataDirectory))
        {
            throw new InvalidOperationException(
                $"Google Chrome blocks remote debugging against its default user data directory '{userDataDirectory}'. " +
                $"Use '{BrowserLogsBuilderExtensions.UserDataModeConfigurationKey}'='{BrowserUserDataMode.Isolated}' or select Microsoft Edge for shared browser state.");
        }

        var profileDirectoryName = settings.Profile is { } profile
            ? BrowserLogsRunningSession.ResolveBrowserProfileDirectory(userDataDirectory, profile)
            : null;
        return BrowserLogsUserDataDirectory.CreatePersistent(userDataDirectory, profileDirectoryName);
    }

    private static void ValidateProfileCompatibility(BrowserHostIdentity identity, string? existingProfileDirectoryName, string? requestedProfileDirectoryName)
    {
        if (requestedProfileDirectoryName is null ||
            string.Equals(existingProfileDirectoryName, requestedProfileDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"A tracked browser is already running for user data directory '{identity.UserDataRootPath}' with profile '{existingProfileDirectoryName ?? "(default)"}'. " +
            $"The requested profile is '{requestedProfileDirectoryName}'. Close the existing tracked browser session or use isolated user data mode.");
    }

    private sealed class BrowserHostEntry(IBrowserHost host, string? profileDirectoryName, int ReferenceCount)
    {
        public IBrowserHost Host { get; } = host;

        public string? ProfileDirectoryName { get; } = profileDirectoryName;

        public int ReferenceCount { get; set; } = ReferenceCount;
    }
}
