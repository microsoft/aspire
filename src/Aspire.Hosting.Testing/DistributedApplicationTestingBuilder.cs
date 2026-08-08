// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREUSERSECRETS001
#pragma warning disable ASPIREFILESYSTEM001

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Testing;

/// <summary>
/// Methods for creating distributed application instances for testing purposes.
/// </summary>
public static class DistributedApplicationTestingBuilder
{

    /// <summary>
    /// Creates a new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </summary>
    /// <typeparam name="TEntryPoint">
    /// A type in the entry point assembly of the target Aspire AppHost. Typically, the Program class can be used.
    /// </typeparam>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>
    /// A new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Generic and non-generic")]
    public static Task<IDistributedApplicationTestingBuilder> CreateAsync<TEntryPoint>(CancellationToken cancellationToken = default)
        where TEntryPoint : class
        => CreateAsync(typeof(TEntryPoint), cancellationToken);

    /// <summary>
    /// Creates a new instance of <see cref="IDistributedApplicationTestingBuilder"/> using the specified testing options.
    /// </summary>
    /// <typeparam name="TEntryPoint">
    /// A type in the entry point assembly of the target Aspire AppHost. Typically, the Program class can be used.
    /// </typeparam>
    /// <param name="options">The options that configure behavior selected while the underlying builder is constructed.</param>
    /// <param name="args">The command line arguments to pass to the entry point.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>
    /// A new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="options"/> or <paramref name="args"/> is <see langword="null"/>, or when
    /// <paramref name="args"/> contains a <see langword="null"/> value.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="args"/> contains an empty value.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="DistributedApplicationTestingBuilderOptions.EnableDashboard"/> is enabled in publish mode.
    /// </exception>
    /// <remarks>
    /// The <paramref name="args"/> parameter is required so calls such as <c>CreateAsync&lt;TEntryPoint&gt;(default)</c>
    /// continue to bind to the existing cancellation-token overload.
    /// </remarks>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Generic and non-generic")]
    public static Task<IDistributedApplicationTestingBuilder> CreateAsync<TEntryPoint>(
        DistributedApplicationTestingBuilderOptions options,
        string[] args,
        CancellationToken cancellationToken = default)
        where TEntryPoint : class
    {
        ArgumentNullException.ThrowIfNull(options);

        return CreateAsync(typeof(TEntryPoint), options, args, cancellationToken);
    }

    /// <summary>
    /// Creates a new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </summary>
    /// <param name="entryPoint">A type in the entry point assembly of the target Aspire AppHost. Typically, the Program class can be used.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>
    /// A new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Generic and non-generic")]
    public static Task<IDistributedApplicationTestingBuilder> CreateAsync(Type entryPoint, CancellationToken cancellationToken = default)
        => CreateAsync(entryPoint, [], cancellationToken);

    /// <summary>
    /// Creates a new instance of <see cref="IDistributedApplicationTestingBuilder"/> using the specified testing options.
    /// </summary>
    /// <param name="entryPoint">A type in the entry point assembly of the target Aspire AppHost. Typically, the Program class can be used.</param>
    /// <param name="options">The options that configure behavior selected while the underlying builder is constructed.</param>
    /// <param name="args">The command line arguments to pass to the entry point.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>
    /// A new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="entryPoint"/>, <paramref name="options"/>, or <paramref name="args"/> is
    /// <see langword="null"/>, or when <paramref name="args"/> contains a <see langword="null"/> value.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="args"/> contains an empty value.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="DistributedApplicationTestingBuilderOptions.EnableDashboard"/> is enabled in publish mode.
    /// </exception>
    /// <remarks>
    /// The <paramref name="args"/> parameter is required so calls such as <c>CreateAsync(entryPoint, default)</c>
    /// continue to bind to the existing cancellation-token overload.
    /// </remarks>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Generic and non-generic")]
    public static Task<IDistributedApplicationTestingBuilder> CreateAsync(
        Type entryPoint,
        DistributedApplicationTestingBuilderOptions options,
        string[] args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        return CreateAsyncCore(entryPoint, args, options, (_, __) => { }, cancellationToken);
    }

    /// <summary>
    /// Creates a new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </summary>
    /// <typeparam name="TEntryPoint">
    /// A type in the entry point assembly of the target Aspire AppHost. Typically, the Program class can be used.
    /// </typeparam>
    /// <param name="args">The command line arguments to pass to the entry point.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>
    /// A new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Generic and non-generic")]
    public static Task<IDistributedApplicationTestingBuilder> CreateAsync<TEntryPoint>(string[] args, CancellationToken cancellationToken = default)
        where TEntryPoint : class
        => CreateAsync(typeof(TEntryPoint), args, (_, __) => { }, cancellationToken);

    /// <summary>
    /// Creates a new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </summary>
    /// <param name="entryPoint">A type in the entry point assembly of the target Aspire AppHost. Typically, the Program class can be used.</param>
    /// <param name="args">The command line arguments to pass to the entry point.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>
    /// A new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Generic and non-generic")]
    public static Task<IDistributedApplicationTestingBuilder> CreateAsync(Type entryPoint, string[] args, CancellationToken cancellationToken = default)
        => CreateAsync(entryPoint, args, (_, __) => { }, cancellationToken);

    /// <summary>
    /// Creates a new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </summary>
    /// <typeparam name="TEntryPoint">
    /// A type in the entry point assembly of the target Aspire AppHost. Typically, the Program class can be used.
    /// </typeparam>
    /// <param name="args">The command line arguments to pass to the entry point.</param>
    /// <param name="configureBuilder">The delegate used to configure the builder.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>
    /// A new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Generic and non-generic")]
    public static Task<IDistributedApplicationTestingBuilder> CreateAsync<TEntryPoint>(string[] args, Action<DistributedApplicationOptions, HostApplicationBuilderSettings> configureBuilder, CancellationToken cancellationToken = default)
        => CreateAsync(typeof(TEntryPoint), args, configureBuilder, cancellationToken);

    /// <summary>
    /// Creates a new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </summary>
    /// <param name="entryPoint">A type in the entry point assembly of the target Aspire AppHost. Typically, the Program class can be used.</param>
    /// <param name="args">The command line arguments to pass to the entry point.</param>
    /// <param name="configureBuilder">The delegate used to configure the builder.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>
    /// A new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Generic and non-generic")]
    public static async Task<IDistributedApplicationTestingBuilder> CreateAsync(Type entryPoint, string[] args, Action<DistributedApplicationOptions, HostApplicationBuilderSettings> configureBuilder, CancellationToken cancellationToken = default)
        => await CreateAsyncCore(entryPoint, args, testingOptions: null, configureBuilder, cancellationToken).ConfigureAwait(false);

    private static async Task<IDistributedApplicationTestingBuilder> CreateAsyncCore(
        Type entryPoint,
        string[] args,
        DistributedApplicationTestingBuilderOptions? testingOptions,
        Action<DistributedApplicationOptions, HostApplicationBuilderSettings> configureBuilder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);
        ThrowIfNullOrContainsIsNullOrEmpty(args);
        ArgumentNullException.ThrowIfNull(configureBuilder, nameof(configureBuilder));

        var factory = new SuspendingDistributedApplicationFactory(entryPoint, args, testingOptions, configureBuilder);
        try
        {
            return await factory.CreateBuilderAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await factory.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Creates a new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </summary>
    /// <param name="args">The command line arguments to pass to the entry point.</param>
    /// <returns>
    /// A new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </returns>
    public static IDistributedApplicationTestingBuilder Create(params string[] args)
        => Create(args, (_, __) => { });

    /// <summary>
    /// Creates a new instance of <see cref="IDistributedApplicationTestingBuilder"/> using the specified testing options.
    /// </summary>
    /// <param name="options">The options that configure behavior selected while the underlying builder is constructed.</param>
    /// <param name="args">The command line arguments to use when building the distributed application.</param>
    /// <returns>
    /// A new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="options"/> or <paramref name="args"/> is <see langword="null"/>, or when
    /// <paramref name="args"/> contains a <see langword="null"/> value.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="args"/> contains an empty value.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="DistributedApplicationTestingBuilderOptions.EnableDashboard"/> is enabled in publish mode.
    /// </exception>
    /// <remarks>
    /// The <paramref name="args"/> parameter is required so calls such as <c>Create(default)</c> continue to bind to
    /// the existing command-line-arguments overload.
    /// </remarks>
    public static IDistributedApplicationTestingBuilder Create(
        DistributedApplicationTestingBuilderOptions options,
        string[] args)
    {
        ArgumentNullException.ThrowIfNull(options);

        return CreateCore(args, options, (_, __) => { });
    }

    /// <summary>
    /// Creates a new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </summary>
    /// <param name="args">The command line arguments to pass to the entry point.</param>
    /// <param name="configureBuilder">The delegate used to configure the builder.</param>
    /// <returns>
    /// A new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </returns>
    public static IDistributedApplicationTestingBuilder Create(string[] args, Action<DistributedApplicationOptions, HostApplicationBuilderSettings> configureBuilder)
        => CreateCore(args, testingOptions: null, configureBuilder);

    private static IDistributedApplicationTestingBuilder CreateCore(
        string[] args,
        DistributedApplicationTestingBuilderOptions? testingOptions,
        Action<DistributedApplicationOptions, HostApplicationBuilderSettings> configureBuilder,
        Assembly? appHostAssembly = null)
    {
        ThrowIfNullOrContainsIsNullOrEmpty(args);
        ArgumentNullException.ThrowIfNull(configureBuilder);

        return new TestingBuilder(args, testingOptions, configureBuilder, appHostAssembly);
    }

    /// <summary>
    /// Creates a new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </summary>
    /// <param name="args">The command line arguments to pass to the entry point.</param>
    /// <param name="configureBuilder">The delegate used to configure the builder.</param>
    /// <param name="appHostAssembly">The assembly of app host</param>
    /// <returns>
    /// A new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </returns>
    internal static IDistributedApplicationTestingBuilder Create(
        string[] args,
        Action<DistributedApplicationOptions, HostApplicationBuilderSettings> configureBuilder,
        Assembly appHostAssembly)
        => CreateCore(args, testingOptions: null, configureBuilder, appHostAssembly);

    private static bool IsDashboardTestingEnabled(DistributedApplicationTestingBuilderOptions? testingOptions)
    {
        // Only the explicit option turns on the hardened testing defaults below. Setting
        // DistributedApplicationOptions.DisableDashboard = false through the configureBuilder callback is the older,
        // already-shipped spelling of "run a dashboard", and it has to keep the behavior it shipped with: callers use
        // it to exercise dashboard behavior against configuration they chose themselves (fixed URLs, anonymous
        // access, an ambient browser token), and in publish mode it is simply ignored because no dashboard resource
        // is ever added. Treating it as equivalent to the option silently rewrote that configuration and turned
        // publish-mode callers into an InvalidOperationException.
        return testingOptions?.EnableDashboard == true;
    }

    private static void ConfigureDashboardTesting(
        DistributedApplicationOptions applicationOptions,
        HostApplicationBuilderSettings hostBuilderOptions,
        DistributedApplicationTestingBuilderOptions? testingOptions,
        out DashboardTestingState dashboardTestingState)
    {
        if (!IsDashboardTestingEnabled(testingOptions))
        {
            dashboardTestingState = default;
            return;
        }

        var browserToken = TokenGenerator.GenerateToken();

        dashboardTestingState = new DashboardTestingState(
            Enabled: true,
            DefaultWaitBehavior: testingOptions?.DefaultWaitBehavior ?? WaitBehavior.StopOnResourceUnavailable,
            BrowserToken: browserToken);

        applicationOptions.DisableDashboard = false;

        // Command-line configuration has higher precedence than the configuration sources supplied through
        // HostApplicationBuilderSettings. Append this after the AppHost callback so creation-time arguments and
        // configuration cannot select anonymous dashboard authentication before testing defaults are reapplied.
        //
        // The browser token rides along on the same mechanism, and deliberately not on the in-memory collection
        // below, for two reasons. DistributedApplicationBuilder resolves the token during construction and freezes
        // it into AppHost:BrowserToken, so it has to be visible before the builder is created and there has to be
        // exactly one value. And turning off anonymous access only closes the door if a credential actually exists:
        // without this, an ambient ASPIRE_DASHBOARD_FRONTEND_BROWSERTOKEN on a CI agent would share a single known
        // token across every test application running there.
        //
        // DistributedApplicationFactory assigns the same array instance to both properties before the AppHost
        // callback runs, and that callback is free to replace either one. Read whichever instance it left in place,
        // and both when they diverged, so caller arguments are appended to rather than silently dropped.
        string[] existingArgs = ReferenceEquals(hostBuilderOptions.Args, applicationOptions.Args)
            ? [.. hostBuilderOptions.Args ?? []]
            : [.. hostBuilderOptions.Args ?? [], .. applicationOptions.Args ?? []];

        hostBuilderOptions.Args =
        [
            .. existingArgs,
            $"{KnownConfigNames.DashboardUnsecuredAllowAnonymous}=false",
            $"{KnownConfigNames.DashboardFrontendBrowserToken}={browserToken}"
        ];
        applicationOptions.Args = hostBuilderOptions.Args;

        hostBuilderOptions.Configuration ??= new();
        AddDashboardTestingConfiguration(hostBuilderOptions.Configuration);
    }

    private static void ConfigureDashboardTesting(IDistributedApplicationBuilder builder, DashboardTestingState dashboardTestingState)
    {
        if (!dashboardTestingState.Enabled)
        {
            return;
        }

        if (builder.ExecutionContext.IsPublishMode)
        {
            throw new InvalidOperationException(Properties.Resources.DashboardTestingPublishModeExceptionMessage);
        }

        // Apply these after the builder has loaded environment variables and command-line arguments so test
        // automation cannot accidentally opt back into fixed ports, anonymous access, or interactivity.
        // Callers can still override runtime settings through the returned builder; constructor-time service
        // selection, including dashboard authentication, has already completed.
        AddDashboardTestingConfiguration(builder.Configuration);

        // Restore the generated browser token if something cleared it. DistributedApplicationBuilder freezes the
        // token into AppHost:BrowserToken during construction, but DashboardOptions does not read that key until the
        // application starts, so AppHost code running between those two points can blank it. DashboardEventHandlers
        // treats a null or empty token as a request for Unsecured frontend authentication, which would silently
        // downgrade the authenticated default this opt-in promises, so the guard mirrors that same emptiness check
        // and leaves any non-empty token, including a deliberately chosen one, alone. This runs after the AppHost
        // entry point has finished configuring, because the builder is not handed back until it reaches Build(), and
        // before the caller sees the builder, so a test that wants the anonymous dashboard can still choose it
        // through the returned builder.
        if (string.IsNullOrEmpty(builder.Configuration["AppHost:BrowserToken"]))
        {
            builder.Configuration["AppHost:BrowserToken"] = dashboardTestingState.BrowserToken;
        }

        // Enabling the dashboard makes the hosting default wait indefinitely when a dependency becomes unavailable,
        // which would hang a test run instead of failing it. Re-pin the testing builder's fail-fast default unless
        // the caller asked for something else, since waiting is the useful behavior when the whole point is to open
        // the dashboard and look at the stuck resource. A later user registration still overrides this.
        var defaultWaitBehavior = dashboardTestingState.DefaultWaitBehavior;
        builder.Services.Configure<ResourceNotificationServiceOptions>(
            options => options.DefaultWaitBehavior = defaultWaitBehavior);
    }

    private static void AddDashboardTestingConfiguration(IConfigurationBuilder configuration)
    {
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DcpPublisher:RandomizePorts"] = "true",

            // Empty means "not configured", which is how the product asks for a dynamically assigned port:
            // ConfigureDefaultDashboardOptions normalizes blank to null, and DashboardEventHandlers then creates
            // the endpoint with port: null. Writing an explicit "http://127.0.0.1:0" instead would parse to the
            // fixed port 0 and only behave dynamically while DcpPublisher:RandomizePorts stays true, which a test
            // is free to turn off. These endpoints still bind to loopback because EndpointAnnotation.TargetHost
            // defaults to localhost.
            [KnownAspNetCoreConfigNames.Urls] = string.Empty,
            [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = string.Empty,
            [KnownConfigNames.DashboardOtlpHttpEndpointUrl] = string.Empty,

            // The resource service reads its port directly and has first-class handling for port 0.
            [KnownConfigNames.ResourceServiceEndpointUrl] = "http://127.0.0.1:0",

            [KnownConfigNames.AllowUnsecuredTransport] = "true",
            [KnownConfigNames.DashboardUnsecuredAllowAnonymous] = "false",
            [KnownConfigNames.InteractivityEnabled] = "false"
        });
    }

    private static void ThrowIfNullOrContainsIsNullOrEmpty(string[] args)
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
    }

    /// <summary>
    /// The dashboard testing configuration resolved during builder construction. Carried as a single value so the
    /// pre-construction and post-construction halves of the configuration cannot drift apart.
    /// </summary>
    private readonly record struct DashboardTestingState(bool Enabled, WaitBehavior DefaultWaitBehavior, string? BrowserToken);

    private sealed class SuspendingDistributedApplicationFactory(
        Type entryPoint,
        string[] args,
        DistributedApplicationTestingBuilderOptions? testingOptions,
        Action<DistributedApplicationOptions, HostApplicationBuilderSettings> configureBuilder)
        : DistributedApplicationFactory(entryPoint, args)
    {
        private readonly SemaphoreSlim _continueBuilding = new(0);

        // Guards the pair of (continuation state, outstanding build count). They have to move together: a caller
        // may only start waiting for the application while no other caller has abandoned it, and an abandoning
        // caller may only claim it once no other caller is still waiting. Interlocked operations on the two fields
        // cannot express that, and nothing is awaited while the lock is held.
        private readonly object _buildStateLock = new();

        // Tracks whether the suspended AppHost entry point has been released, and why. Disposal must win over a
        // concurrent BuildAsync so a rejected builder cannot continue into Build() after the factory is gone.
        private const int ContinuationSuspended = 0;
        private const int ContinuationReleased = 1;
        private const int ContinuationAbandoned = 2;
        private const int ContinuationDisposed = 3;

        private int _buildingContinuationState;

        // Number of BuildAsync calls that have released the AppHost and are still waiting for the application.
        private int _outstandingBuilds;

        // Resolved while the builder is being constructed, because dashboard services and dashboard authentication
        // are selected during construction and the post-construction half of the configuration has to agree with it.
        private DashboardTestingState _dashboardTestingState;

        public async Task<IDistributedApplicationTestingBuilder> CreateBuilderAsync(CancellationToken cancellationToken)
        {
            var innerBuilder = await ResolveBuilderAsync(cancellationToken).ConfigureAwait(false);
            ConfigureDashboardTesting(innerBuilder, _dashboardTestingState);
            return new Builder(this, innerBuilder);
        }

        protected override void OnBuilderCreating(DistributedApplicationOptions applicationOptions, HostApplicationBuilderSettings hostOptions)
        {
            base.OnBuilderCreating(applicationOptions, hostOptions);
            configureBuilder(applicationOptions, hostOptions);
            ConfigureDashboardTesting(applicationOptions, hostOptions, testingOptions, out _dashboardTestingState);
        }

        protected override void OnBuilding(DistributedApplicationBuilder applicationBuilder)
        {
            base.OnBuilding(applicationBuilder);

            // Wait until the owner signals that building can continue by calling BuildAsync().
            _continueBuilding.Wait();
            if (Volatile.Read(ref _buildingContinuationState) == ContinuationDisposed)
            {
                throw CreateDisposedException();
            }
        }

        public async Task<DistributedApplication> BuildAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool releaseAppHost;
            lock (_buildStateLock)
            {
                switch (_buildingContinuationState)
                {
                    case ContinuationDisposed:
                        throw CreateDisposedException();
                    case ContinuationAbandoned:
                        throw CreateAbandonedException();
                }

                releaseAppHost = _buildingContinuationState == ContinuationSuspended;
                Volatile.Write(ref _buildingContinuationState, ContinuationReleased);
                _outstandingBuilds++;
            }

            if (releaseAppHost)
            {
                _continueBuilding.Release();
            }

            var canceled = false;
            try
            {
                return await ResolveApplicationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                throw;
            }
            catch (OperationCanceledException) when (Volatile.Read(ref _buildingContinuationState) == ContinuationDisposed)
            {
                throw CreateDisposedException();
            }
            finally
            {
                if (TryAbandonApplication(canceled))
                {
                    // Once the AppHost has been released, it can still finish building after the caller stops waiting.
                    // Cancellation must return to the caller promptly, so reclaim that application on a background
                    // continuation rather than blocking here until an AppHost that may never finish completes.
                    ReclaimApplicationInBackground();
                }
            }
        }

        /// <summary>
        /// Records that a <see cref="BuildAsync"/> call stopped waiting for the application, and reports whether it
        /// abandoned it, meaning no caller is left to take ownership of an application that still arrives.
        /// </summary>
        private bool TryAbandonApplication(bool canceled)
        {
            lock (_buildStateLock)
            {
                _outstandingBuilds--;

                // A caller that is still waiting takes ownership of the application and disposes it through the
                // builder, so only the last caller to cancel may reclaim it. Disposal already owns the teardown.
                if (!canceled || _outstandingBuilds > 0 || _buildingContinuationState != ContinuationReleased)
                {
                    return false;
                }

                // The AppHost entry point produces exactly one application, and it is now spoken for by the
                // background reclaim, so later builds have to be rejected rather than handed a disposed instance.
                Volatile.Write(ref _buildingContinuationState, ContinuationAbandoned);
                return true;
            }
        }

        /// <summary>
        /// Disposes the factory once a released AppHost finishes building, so an application the caller abandoned
        /// after cancellation cannot keep running.
        /// </summary>
        /// <remarks>
        /// This is best effort. If the caller disposes the builder first, this wait is aborted by that disposal and
        /// <see cref="DistributedApplicationFactory.OnBuiltCoreAsync"/> reclaims the late-arriving application instead.
        /// </remarks>
        private void ReclaimApplicationInBackground()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    _ = await ResolveApplicationAsync(CancellationToken.None).ConfigureAwait(false);
                    await DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // The AppHost can fail, or this wait can be aborted by the caller's own disposal, after the
                    // caller stopped waiting. No caller remains to observe that, and the factory reclaims any
                    // application that arrives after disposal, so swallow it rather than raising an unobserved
                    // task exception on the finalizer thread.
                }
            });
        }

        private static ObjectDisposedException CreateDisposedException()
        {
            // Report the public interface rather than this internal factory type so callers see the object they own.
            return new ObjectDisposedException(
                nameof(IDistributedApplicationTestingBuilder),
                "The testing builder was disposed before the application was built.");
        }

        private static InvalidOperationException CreateAbandonedException()
        {
            return new InvalidOperationException(
                "The application was abandoned because the BuildAsync call that released the AppHost was canceled, " +
                "and it is being disposed. An AppHost entry point builds a single application, so building again " +
                "requires a new testing builder.");
        }

        public override async ValueTask DisposeAsync()
        {
            PrepareForDisposal();
            await base.DisposeAsync().ConfigureAwait(false);
        }

        public override void Dispose()
        {
            PrepareForDisposal();
            base.Dispose();
        }

        private void PrepareForDisposal()
        {
            bool releaseAppHost;
            lock (_buildStateLock)
            {
                releaseAppHost = _buildingContinuationState == ContinuationSuspended;
                Volatile.Write(ref _buildingContinuationState, ContinuationDisposed);
            }

            if (releaseAppHost)
            {
                // Abort on the AppHost entry-point thread instead of allowing a rejected or canceled
                // builder to continue into Build() after the factory has already been disposed.
                _continueBuilding.Release();
            }
        }

        private sealed class Builder(SuspendingDistributedApplicationFactory factory, DistributedApplicationBuilder innerBuilder) : IDistributedApplicationTestingBuilder
        {
            public ConfigurationManager Configuration => innerBuilder.Configuration;

            public string AppHostDirectory => innerBuilder.AppHostDirectory;

            public Assembly? AppHostAssembly => innerBuilder.AppHostAssembly;

            public IHostEnvironment Environment => innerBuilder.Environment;

            public IServiceCollection Services => innerBuilder.Services;

            public DistributedApplicationExecutionContext ExecutionContext => innerBuilder.ExecutionContext;

            public IResourceCollection Resources => innerBuilder.Resources;

            public IDistributedApplicationEventing Eventing => innerBuilder.Eventing;

            public IDistributedApplicationPipeline Pipeline => innerBuilder.Pipeline;

            public IUserSecretsManager UserSecretsManager => innerBuilder.UserSecretsManager;

            public IResourceBuilder<T> AddResource<T>(T resource) where T : IResource => innerBuilder.AddResource(resource);

            public DistributedApplication Build() => BuildAsync(CancellationToken.None).Result;

            public async Task<DistributedApplication> BuildAsync(CancellationToken cancellationToken)
            {
                var innerApp = await factory.BuildAsync(cancellationToken).ConfigureAwait(false);
                return new DelegatedDistributedApplication(new DelegatedHost(factory, innerApp));
            }

            public IResourceBuilder<T> CreateResourceBuilder<T>(T resource) where T : IResource => innerBuilder.CreateResourceBuilder(resource);

            public void Dispose()
            {
                factory.Dispose();
            }

            public async ValueTask DisposeAsync()
            {
                await factory.DisposeAsync().ConfigureAwait(false);
            }
        }

        private sealed class DelegatedDistributedApplication(DelegatedHost host) : DistributedApplication(host)
        {
            private readonly DelegatedHost _host = host;

            public override async Task RunAsync(CancellationToken cancellationToken)
            {
                // Avoid calling the base here, since it will execute the pre-start hooks
                // before calling the corresponding host method, which also executes the same pre-start hooks.
                await _host.RunAsync(cancellationToken).ConfigureAwait(false);
            }

            public override async Task StartAsync(CancellationToken cancellationToken)
            {
                // Avoid calling the base here, since it will execute the pre-start hooks
                // before calling the corresponding host method, which also executes the same pre-start hooks.
                await _host.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            public override async Task StopAsync(CancellationToken cancellationToken)
            {
                await _host.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private sealed class DelegatedHost(SuspendingDistributedApplicationFactory appFactory, DistributedApplication innerApp) : IHost, IAsyncDisposable
        {
            public IServiceProvider Services => innerApp.Services;

            public void Dispose()
            {
                appFactory.Dispose();
            }

            public async ValueTask DisposeAsync()
            {
                await appFactory.DisposeAsync().ConfigureAwait(false);
            }

            public async Task StartAsync(CancellationToken cancellationToken)
            {
                await appFactory.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            public async Task StopAsync(CancellationToken cancellationToken)
            {
                await appFactory.DisposeAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class TestingBuilder(
        string[] args,
        DistributedApplicationTestingBuilderOptions? testingOptions,
        Action<DistributedApplicationOptions, HostApplicationBuilderSettings> configureBuilder,
        Assembly? appHostAssembly = null)
        : IDistributedApplicationTestingBuilder
    {
        private readonly DistributedApplicationBuilder _innerBuilder = CreateInnerBuilder(args, testingOptions, configureBuilder, appHostAssembly);
        private DistributedApplication? _app;

        private static DistributedApplicationBuilder CreateInnerBuilder(
            string[] args,
            DistributedApplicationTestingBuilderOptions? testingOptions,
            Action<DistributedApplicationOptions, HostApplicationBuilderSettings> configureBuilder,
            Assembly? appHostAssembly = null)
        {
            var dashboardTestingState = default(DashboardTestingState);
            var builder = TestingBuilderFactory.CreateBuilder(args, onConstructing: (applicationOptions, hostBuilderOptions) =>
            {
                Assembly appAssembly;
                if (appHostAssembly is not null && GetDcpCliPath(appHostAssembly) is { Length: > 0 })
                {
                    appAssembly = appHostAssembly;
                }
                else
                {
                    appAssembly = FindApplicationAssembly();
                }

                DistributedApplicationFactory.ConfigureBuilder(args, applicationOptions, hostBuilderOptions, appAssembly, (options, settings) =>
                {
                    configureBuilder(options, settings);
                    ConfigureDashboardTesting(options, settings, testingOptions, out dashboardTestingState);
                });
            });

            ConfigureDashboardTesting(builder, dashboardTestingState);

            if (!builder.Configuration.GetValue(KnownConfigNames.TestingDisableHttpClient, false))
            {
                builder.Services.AddHttpClient();
                builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());
            }

            return builder;

            static Assembly FindApplicationAssembly()
            {
                // Walk the stack trace to find the first assembly that has the 'dcpclipath' metadata attribute.
                // This will be selected as the application host assembly. DCP is necessary to launch the application.
                var stackTrace = new StackTrace();
                foreach (var stackFrame in stackTrace.GetFrames())
                {
                    var asm = stackFrame.GetMethod()?.DeclaringType?.Assembly;
                    if (asm is not null && GetDcpCliPath(asm) is { Length: > 0 })
                    {
                        return asm;
                    }
                }

                throw new InvalidOperationException("No application host assembly was found. Ensure that you have a project that references the 'Aspire.Hosting.AppHost' package and imports the 'Aspire.AppHost.Sdk' SDK.");
            }

            static string? GetDcpCliPath(Assembly? assembly)
            {
                var assemblyMetadata = assembly?.GetCustomAttributes<AssemblyMetadataAttribute>();
                return assemblyMetadata?.FirstOrDefault(m => string.Equals(m.Key, "dcpclipath", StringComparison.OrdinalIgnoreCase))?.Value;
            }
        }

        public ConfigurationManager Configuration => _innerBuilder.Configuration;

        public string AppHostDirectory => _innerBuilder.AppHostDirectory;

        public Assembly? AppHostAssembly => _innerBuilder.AppHostAssembly;

        public IHostEnvironment Environment => _innerBuilder.Environment;

        public IServiceCollection Services => _innerBuilder.Services;

        public DistributedApplicationExecutionContext ExecutionContext => _innerBuilder.ExecutionContext;

        public IResourceCollection Resources => _innerBuilder.Resources;

        public IDistributedApplicationEventing Eventing => _innerBuilder.Eventing;

        public IDistributedApplicationPipeline Pipeline => _innerBuilder.Pipeline;

        public IUserSecretsManager UserSecretsManager => _innerBuilder.UserSecretsManager;

        public IResourceBuilder<T> AddResource<T>(T resource) where T : IResource => _innerBuilder.AddResource(resource);

        [MemberNotNull(nameof(_app))]
        public DistributedApplication Build()
        {
            return _app = _innerBuilder.Build();
        }

        public Task<DistributedApplication> BuildAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Build());
        }

        public IResourceBuilder<T> CreateResourceBuilder<T>(T resource) where T : IResource => _innerBuilder.CreateResourceBuilder(resource);

        public void Dispose()
        {
            if (_app is null)
            {
                try
                {
                    Build();
                }
                catch
                {
                    // Suppress.
                }
            }

            if (_app is { } app)
            {
                app.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is null)
            {
                try
                {
                    Build();
                }
                catch
                {
                    // Suppress.
                }
            }

            if (_app is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// A builder for creating instances of <see cref="DistributedApplication"/> for testing purposes.
/// </summary>
public interface IDistributedApplicationTestingBuilder : IDistributedApplicationBuilder, IAsyncDisposable, IDisposable
{
    /// <inheritdoc cref="IDistributedApplicationBuilder.Configuration" />
    new ConfigurationManager Configuration => ((IDistributedApplicationBuilder)this).Configuration;

    /// <inheritdoc cref="IDistributedApplicationBuilder.AppHostDirectory" />
    new string AppHostDirectory => ((IDistributedApplicationBuilder)this).AppHostDirectory;

    /// <inheritdoc cref="IDistributedApplicationBuilder.AppHostAssembly" />
    new Assembly? AppHostAssembly => ((IDistributedApplicationBuilder)this).AppHostAssembly;

    /// <inheritdoc cref="IDistributedApplicationBuilder.Environment" />
    new IHostEnvironment Environment => ((IDistributedApplicationBuilder)this).Environment;

    /// <inheritdoc cref="IDistributedApplicationBuilder.Services" />
    new IServiceCollection Services => ((IDistributedApplicationBuilder)this).Services;

    /// <inheritdoc cref="IDistributedApplicationBuilder.ExecutionContext" />
    new DistributedApplicationExecutionContext ExecutionContext => ((IDistributedApplicationBuilder)this).ExecutionContext;

    /// <inheritdoc cref="IDistributedApplicationBuilder.Eventing" />
    new IDistributedApplicationEventing Eventing => ((IDistributedApplicationBuilder)this).Eventing;

    /// <inheritdoc cref="IDistributedApplicationBuilder.Pipeline" />
    new IDistributedApplicationPipeline Pipeline => ((IDistributedApplicationBuilder)this).Pipeline;

    /// <inheritdoc cref="IDistributedApplicationBuilder.Resources" />
    new IResourceCollection Resources => ((IDistributedApplicationBuilder)this).Resources;

    /// <inheritdoc cref="IDistributedApplicationBuilder.FileSystemService" />
    new IFileSystemService FileSystemService => ((IDistributedApplicationBuilder)this).FileSystemService;

    /// <inheritdoc cref="IDistributedApplicationBuilder.UserSecretsManager" />
    new IUserSecretsManager UserSecretsManager => ((IDistributedApplicationBuilder)this).UserSecretsManager;

    /// <inheritdoc cref="IDistributedApplicationBuilder.AddResource{T}(T)" />
    new IResourceBuilder<T> AddResource<T>(T resource) where T : IResource => ((IDistributedApplicationBuilder)this).AddResource(resource);

    /// <inheritdoc cref="IDistributedApplicationBuilder.CreateResourceBuilder{T}(T)" />
    new IResourceBuilder<T> CreateResourceBuilder<T>(T resource) where T : IResource => ((IDistributedApplicationBuilder)this).CreateResourceBuilder(resource);

    /// <summary>
    /// Builds and returns a new <see cref="DistributedApplication"/> instance. This can only be called once.
    /// </summary>
    /// <returns>A new <see cref="DistributedApplication"/> instance.</returns>
    Task<DistributedApplication> BuildAsync(CancellationToken cancellationToken = default);
}
