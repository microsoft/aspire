// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Testing;

/// <summary>
/// Factory for creating a distributed application for testing.
/// </summary>
/// <param name="entryPoint">A type in the entry point assembly of the target Aspire AppHost. Typically, the Program class can be used.</param>
/// <param name="args">
/// The command-line arguments to pass to the entry point.
/// </param>
public class DistributedApplicationFactory(Type entryPoint, string[] args) : IDisposable, IAsyncDisposable
{
    private readonly Type _entryPoint = entryPoint ?? throw new ArgumentNullException(nameof(entryPoint));
    private readonly string[] _args = ThrowIfNullOrContainsIsNullOrEmpty(args);
    private readonly TaskCompletionSource _startedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<DistributedApplicationBuilder> _builderTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<DistributedApplication> _appTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _disposingCts = new();
    private TimeSpan _shutdownTimeout = TimeSpan.FromSeconds(10);
    private readonly object _lockObj = new();
    private bool _entryPointStarted;
    private IHostApplicationLifetime? _hostApplicationLifetime;

    /// <summary>
    /// Initializes a new instance of the <see cref="DistributedApplicationFactory"/> class.
    /// </summary>
    /// <param name="entryPoint">A type in the entry point assembly of the target Aspire AppHost. Typically, the Program class can be used.</param>
    public DistributedApplicationFactory(Type entryPoint) : this(entryPoint, [])
    {
    }

    /// <summary>
    /// Gets the distributed application associated with this instance.
    /// </summary>
    internal async Task<DistributedApplicationBuilder> ResolveBuilderAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposingCts.IsCancellationRequested, this);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposingCts.Token);
        EnsureEntryPointStarted();
        return await _builderTcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the distributed application associated with this instance.
    /// </summary>
    internal async Task<DistributedApplication> ResolveApplicationAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposingCts.Token);
        EnsureEntryPointStarted();
        return await _appTcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the application.
    /// </summary>
    /// <param name="cancellationToken">A token used to signal cancellation.</param>
    /// <returns>A <see cref="Task"/> representing the completion of the operation.</returns>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposingCts.IsCancellationRequested, this);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposingCts.Token);
        EnsureEntryPointStarted();
        await _startedTcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates an instance of <see cref="HttpClient"/> that is configured to route requests to the specified resource and endpoint.
    /// </summary>
    /// <returns>The <see cref="HttpClient"/>.</returns>
    public HttpClient CreateHttpClient(string resourceName, string? endpointName = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        ObjectDisposedException.ThrowIf(_disposingCts.IsCancellationRequested, this);
        return GetStartedApplication().CreateHttpClient(resourceName, endpointName);
    }

    /// <summary>
    /// Gets the connection string for the specified resource.
    /// </summary>
    /// <param name="resourceName">The resource name.</param>
    /// <returns>The connection string for the specified resource.</returns>
    /// <exception cref="ArgumentException">The resource was not found or does not expose a connection string.</exception>
    public ValueTask<string?> GetConnectionString(string resourceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        ObjectDisposedException.ThrowIf(_disposingCts.IsCancellationRequested, this);
        return GetStartedApplication().GetConnectionStringAsync(resourceName);
    }

    /// <summary>
    /// Gets the endpoint for the specified resource.
    /// </summary>
    /// <param name="resourceName">The resource name.</param>
    /// <param name="endpointName">The optional endpoint name. If none are specified, the single defined endpoint is returned.</param>
    /// <returns>A URI representation of the endpoint.</returns>
    /// <exception cref="ArgumentException">The resource was not found, no matching endpoint was found, or multiple endpoints were found.</exception>
    /// <exception cref="InvalidOperationException">The resource has no endpoints.</exception>
    public Uri GetEndpoint(string resourceName, string? endpointName = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        ObjectDisposedException.ThrowIf(_disposingCts.IsCancellationRequested, this);
        return GetStartedApplication().GetEndpoint(resourceName, endpointName);
    }

    /// <summary>
    /// Called when the application builder is being created.
    /// </summary>
    /// <param name="applicationOptions">The application options.</param>
    /// <param name="hostOptions">The host builder options.</param>
    protected virtual void OnBuilderCreating(DistributedApplicationOptions applicationOptions, HostApplicationBuilderSettings hostOptions)
    {
    }

    /// <summary>
    /// Called when the application builder is created.
    /// </summary>
    /// <param name="applicationBuilder">The application builder.</param>
    protected virtual void OnBuilderCreated(DistributedApplicationBuilder applicationBuilder)
    {
    }

    /// <summary>
    /// Called when the application is being built.
    /// </summary>
    /// <param name="applicationBuilder">The application builder.</param>
    protected virtual void OnBuilding(DistributedApplicationBuilder applicationBuilder)
    {
    }

    /// <summary>
    /// Called when the application has been built.
    /// </summary>
    /// <param name="application">The application.</param>
    protected virtual void OnBuilt(DistributedApplication application)
    {
    }

    private static string[] ThrowIfNullOrContainsIsNullOrEmpty(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        foreach (var arg in args)
        {
            if (string.IsNullOrEmpty(arg))
            {
                var values = string.Join(", ", args);
                if (arg is null)
                {
                    throw new ArgumentNullException(nameof(args), $"Array params contains null item: [{values}]");
                }
                throw new ArgumentException($"Array params contains empty item: [{values}]", nameof(args));
            }
        }
        return args;
    }

    private void OnBuiltCore(DistributedApplication application)
    {
        _shutdownTimeout = application.Services.GetService<IOptions<HostOptions>>()?.Value.ShutdownTimeout ?? _shutdownTimeout;
        _appTcs.TrySetResult(application);
        OnBuilt(application);
    }

    private static void PreConfigureBuilderOptions(
        DistributedApplicationOptions applicationOptions,
        HostApplicationBuilderSettings hostBuilderOptions,
        string[] args,
        Assembly entryPointAssembly)
    {
        hostBuilderOptions.Args = hostBuilderOptions.Args switch
        {
            { } existing => [.. existing, .. args],
            null => args
        };
        applicationOptions.Args = hostBuilderOptions.Args;

        hostBuilderOptions.ApplicationName = entryPointAssembly.GetName().Name ?? string.Empty;
        applicationOptions.AssemblyName = entryPointAssembly.GetName().Name ?? string.Empty;
        applicationOptions.DisableDashboard = true;
        applicationOptions.EnableResourceLogging = true;
        var existingConfig = new ConfigurationManager();
        existingConfig.AddCommandLine(applicationOptions.Args ?? []);
        if (hostBuilderOptions.Configuration is not null)
        {
            existingConfig.AddConfiguration(hostBuilderOptions.Configuration);
        }

        var additionalConfig = new Dictionary<string, string?>();
        SetDefault("DcpPublisher:ContainerRuntimeInitializationTimeout", "00:00:30");
        SetDefault("DcpPublisher:RandomizePorts", "true");
        SetDefault("DcpPublisher:WaitForResourceCleanup", "true");

        // Make sure we have a dashboard URL and OTLP endpoint URL.
        SetDefault(KnownConfigNames.AspNetCoreUrls, "http://localhost:8080");
        SetDefaultFallback(KnownConfigNames.DashboardOtlpGrpcEndpointUrl, KnownConfigNames.Legacy.DashboardOtlpGrpcEndpointUrl, "http://localhost:4317");

        var appHostProjectPath = ResolveProjectPath(entryPointAssembly);
        if (!string.IsNullOrEmpty(appHostProjectPath) && Directory.Exists(appHostProjectPath))
        {
            hostBuilderOptions.ContentRootPath = appHostProjectPath;
        }

        hostBuilderOptions.Configuration ??= new();
        hostBuilderOptions.Configuration.AddInMemoryCollection(additionalConfig);

        void SetDefault(string key, string? value)
        {
            if (existingConfig[key] is null)
            {
                additionalConfig[key] = value;
            }
        }

        void SetDefaultFallback(string primaryKey, string secondaryKey, string? value)
        {
            if (existingConfig[primaryKey] is null && existingConfig[secondaryKey] is null)
            {
                additionalConfig[primaryKey] = value;
            }
        }
    }

    internal static void ConfigureBuilder(
        string[] args,
        DistributedApplicationOptions applicationOptions,
        HostApplicationBuilderSettings hostBuilderOptions,
        Assembly entryPointAssembly,
        Action<DistributedApplicationOptions, HostApplicationBuilderSettings> configureBuilder)
    {
        PreConfigureBuilderOptions(applicationOptions, hostBuilderOptions, args, entryPointAssembly);
        configureBuilder(applicationOptions, hostBuilderOptions);
        PostConfigureBuilderOptions(hostBuilderOptions, entryPointAssembly);
    }

    private void OnBuilderCreatingCore(
        DistributedApplicationOptions applicationOptions,
        HostApplicationBuilderSettings hostBuilderOptions)
    {
        ConfigureBuilder(_args, applicationOptions, hostBuilderOptions, _entryPoint.Assembly, OnBuilderCreating);
    }

    private static void PostConfigureBuilderOptions(
        HostApplicationBuilderSettings hostBuilderOptions,
        Assembly entryPointAssembly)
    {
        var existingConfig = new ConfigurationManager();
        existingConfig.AddCommandLine(hostBuilderOptions.Args ?? []);
        if (hostBuilderOptions.Configuration is not null)
        {
            existingConfig.AddConfiguration(hostBuilderOptions.Configuration);
        }

        var additionalConfig = new Dictionary<string, string?>();
        var appHostProjectPath = ResolveProjectPath(entryPointAssembly);

        // Populate the launch profile name.
        var appHostLaunchSettings = GetLaunchSettings(appHostProjectPath);
        var launchProfileName = existingConfig["DOTNET_LAUNCH_PROFILE"];

        // Load the launch profile and populate configuration with environment variables.
        if (appHostLaunchSettings is not null)
        {
            var launchProfiles = appHostLaunchSettings.Profiles;
            LaunchProfile? launchProfile;
            if (string.IsNullOrEmpty(launchProfileName))
            {
                // If a launch profile was not specified, select the first launch profile.
                var firstLaunchProfile = launchProfiles.FirstOrDefault();
                launchProfile = firstLaunchProfile.Value;
                SetDefault("DOTNET_LAUNCH_PROFILE", firstLaunchProfile.Key);
            }
            else
            {
                if (!launchProfiles.TryGetValue(launchProfileName, out launchProfile))
                {
                    throw new InvalidOperationException($"The configured launch profile, '{launchProfileName}', was not found in the launch settings file.");
                }
            }

            // Populate config from env vars.
            if (launchProfile?.EnvironmentVariables is { Count: > 0 } envVars)
            {
                foreach (var (key, value) in envVars)
                {
                    SetDefault(key, value);

                    // See https://github.com/dotnet/runtime/blob/8edaf7460777e791b6279b395a68a77533db2d20/src/libraries/Microsoft.Extensions.Hosting/src/HostApplicationBuilder.cs#L96
                    if (key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase))
                    {
                        SetDefault(key["DOTNET_".Length..], value);
                    }

                    // See https://github.com/dotnet/aspnetcore/blob/4ce2db7b8d85c07cad2c59242edc19af6a91b0d7/src/DefaultBuilder/src/WebApplicationBuilder.cs#L38
                    if (key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase))
                    {
                        SetDefault(key["ASPNETCORE_".Length..], value);
                    }
                }
            }
        }

        hostBuilderOptions.Configuration ??= new();
        hostBuilderOptions.Configuration.AddInMemoryCollection(additionalConfig);

        void SetDefault(string key, string? value)
        {
            if (existingConfig[key] is null)
            {
                additionalConfig[key] = value;
            }
        }
    }

    private static string? ResolveProjectPath(Assembly? assembly)
    {
        var assemblyMetadata = assembly?.GetCustomAttributes<AssemblyMetadataAttribute>();
        return GetMetadataValue(assemblyMetadata, "AppHostProjectPath");
    }

    private static string? GetMetadataValue(IEnumerable<AssemblyMetadataAttribute>? assemblyMetadata, string key)
    {
        return assemblyMetadata?.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static LaunchSettings? GetLaunchSettings(string? appHostPath)
    {
        if (appHostPath is null || !Directory.Exists(appHostPath))
        {
            return null;
        }

        return LaunchSettingsReader.GetLaunchSettingsFromDirectory(appHostPath, $"project '{appHostPath}'");
    }

    private void OnBuilderCreatedCore(DistributedApplicationBuilder applicationBuilder)
    {
        var services = applicationBuilder.Services;
        services.AddHttpClient();
        OnBuilderCreated(applicationBuilder);
    }

    private void OnBuildingCore(DistributedApplicationBuilder applicationBuilder)
    {
        InterceptHostCreation(applicationBuilder);

        _builderTcs.TrySetResult(applicationBuilder);
        OnBuilding(applicationBuilder);
    }

    private void EnsureEntryPointStarted()
    {
        if (!_entryPointStarted)
        {
            lock (_lockObj)
            {
                if (!_entryPointStarted)
                {
                    if (entryPoint.Assembly.EntryPoint == null)
                    {
                        throw new InvalidOperationException($"Assembly of specified type {entryPoint.Name} does not have an entry point.");
                    }

                    // This helper launches the target assembly's entry point and hooks into the lifecycle
                    // so we can intercept execution at key stages.
                    var factory = DistributedApplicationEntryPointInvoker.ResolveEntryPoint(
                        _entryPoint.Assembly,
                        onConstructing: OnBuilderCreatingCore,
                        onConstructed: OnBuilderCreatedCore,
                        onBuilding: OnBuildingCore,
                        entryPointCompleted: OnEntryPointExit);

                    if (factory is null)
                    {
                        throw new InvalidOperationException(
                            $"Could not intercept application builder instance. Ensure that {_entryPoint} is a type in an executable assembly, that the entrypoint creates an {typeof(DistributedApplicationBuilder)}, and that the resulting {typeof(DistributedApplication)} is being started.");
                    }

                    _ = InvokeEntryPoint(factory);
                    _entryPointStarted = true;
                }
            }
        }
    }

    private async Task InvokeEntryPoint(Func<string[], CancellationToken, Task<DistributedApplication>> factory)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            using var cts = new CancellationTokenSource(GetConfiguredTimeout());
            var app = await factory(_args, cts.Token).ConfigureAwait(false);
            _hostApplicationLifetime = app.Services.GetService<IHostApplicationLifetime>()
                ?? throw new InvalidOperationException($"Application did not register an implementation of {typeof(IHostApplicationLifetime)}.");
            OnBuiltCore(app);
        }
        catch (Exception exception)
        {
            _exitTcs.TrySetException(exception);
            OnException(exception);
        }

        static TimeSpan GetConfiguredTimeout()
        {
            const string TimeoutEnvironmentKey = "DOTNET_HOST_FACTORY_RESOLVER_DEFAULT_TIMEOUT_IN_SECONDS";
            if (Debugger.IsAttached)
            {
                return Timeout.InfiniteTimeSpan;
            }

            if (uint.TryParse(Environment.GetEnvironmentVariable(TimeoutEnvironmentKey), out var timeoutInSeconds))
            {
                return TimeSpan.FromSeconds((int)timeoutInSeconds);
            }

            return TimeSpan.FromMinutes(5);
        }
    }

    private void OnEntryPointExit(Exception? exception)
    {
        if (exception is not null)
        {
            _exitTcs.TrySetException(exception);
            OnException(exception);
        }
        else
        {
            _exitTcs.TrySetResult();
        }
    }

    private void ThrowIfNotInitialized()
    {
        if (!_startedTcs.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("The application has not been initialized.");
        }
    }

    private DistributedApplication GetStartedApplication()
    {
        ThrowIfNotInitialized();
        return _appTcs.Task.GetAwaiter().GetResult();
    }

    private void OnException(Exception exception)
    {
        _appTcs.TrySetException(exception);
        _builderTcs.TrySetException(exception);
        _startedTcs.TrySetException(exception);
    }

    private void OnDisposed()
    {
        _disposingCts.Cancel();
        _builderTcs.TrySetCanceled();
        _startedTcs.TrySetCanceled();
    }

    /// <inheritdoc/>
    public virtual void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public virtual async ValueTask DisposeAsync()
    {
        if (_disposingCts.IsCancellationRequested)
        {
            // Dispose already called.
            return;
        }

        OnDisposed();
        if (_appTcs.Task is not { IsCompletedSuccessfully: true } appTask)
        {
            _appTcs.TrySetCanceled();
            return;
        }

        // If the application has started, or when it starts, stop it.
        using var applicationStartedRegistration = _hostApplicationLifetime?.ApplicationStarted.Register(
            static state => (state as IHostApplicationLifetime)?.StopApplication(),
            _hostApplicationLifetime);

        using var shutdownTimeoutCts = new CancellationTokenSource(_shutdownTimeout);
        try
        {
            await _exitTcs.Task.WaitAsync(shutdownTimeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Ignore errors thrown from the app host thread.
            // These should be caught and handled within the app host.
        }

        // We need to dispose so that the ResourceNotificationService will propagate cancellation.
        if (appTask.GetAwaiter().GetResult() is { } app)
        {
            try
            {
                await app.StopAsync().WaitAsync(shutdownTimeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_disposingCts.IsCancellationRequested)
            {
                // Ignore during disposal.
            }

            try
            {
                await app.DisposeAsync().AsTask().WaitAsync(shutdownTimeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_disposingCts.IsCancellationRequested)
            {
                // Ignore during disposal.
            }
        }
    }

    // Replaces the IHost registration with an InterceptedHost registration which delegates to the original registration.
    private void InterceptHostCreation(DistributedApplicationBuilder applicationBuilder)
    {
        // Find the original IHost registration and remove it.
        var hostDescriptor = applicationBuilder.Services.Single(s => s.ServiceType == typeof(IHost) && s.ServiceKey is null);
        applicationBuilder.Services.Remove(hostDescriptor);

        // Insert the registration, modified to be a keyed service keyed on this factory instance.
        var interceptedDescriptor = hostDescriptor switch
        {
            { ImplementationFactory: { } factory } => ServiceDescriptor.KeyedSingleton<IHost>(this, (sp, _) => (IHost)factory(sp)),
            { ImplementationInstance: { } instance } => ServiceDescriptor.KeyedSingleton<IHost>(this, (IHost)instance),
            { ImplementationType: { } type } => ServiceDescriptor.KeyedSingleton(typeof(IHost), this, type),
            _ => throw new InvalidOperationException($"Registered service descriptor for {typeof(IHost)} does not conform to any known pattern.")
        };
        applicationBuilder.Services.Add(interceptedDescriptor);

        // Add a non-keyed registration which resolved the keyed registration, enabling interception.
        applicationBuilder.Services.AddSingleton<IHost>(sp => new ObservedHost(sp.GetRequiredKeyedService<IHost>(this), this));
    }

    private sealed class ObservedHost(IHost innerHost, DistributedApplicationFactory appFactory) : IHost, IAsyncDisposable
    {
        private bool _disposing;

        public IServiceProvider Services => innerHost.Services;

        public void Dispose()
        {
            if (_disposing)
            {
                return;
            }

            _disposing = true;
            innerHost.Dispose();
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            if (_disposing)
            {
                return;
            }

            _disposing = true;
            if (innerHost is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().AsTask().WaitAsync(appFactory._disposingCts.Token).ConfigureAwait(false);
            }
            else
            {
                innerHost.Dispose();
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, appFactory._disposingCts.Token);
                await innerHost.StartAsync(linkedToken.Token).ConfigureAwait(false);
                appFactory._startedTcs.TrySetResult();
            }
            catch (Exception exception)
            {
                appFactory.OnException(exception);
                throw;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            // The cancellation token is passed as-is here to give the host a chance to stop gracefully.
            // Internally, in the host itself, the value of HostOptions.ShutdownTimeout limits how long the host has to stop gracefully.
            await innerHost.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
