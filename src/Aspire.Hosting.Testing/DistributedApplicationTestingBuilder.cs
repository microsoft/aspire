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
    {
        ArgumentNullException.ThrowIfNull(entryPoint);
        ThrowIfNullOrContainsIsNullOrEmpty(args);
        ArgumentNullException.ThrowIfNull(configureBuilder, nameof(configureBuilder));

        var factory = new SuspendingDistributedApplicationFactory(entryPoint, args, configureBuilder);
        return await factory.CreateBuilderAsync(cancellationToken).ConfigureAwait(false);
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
    /// Creates a new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </summary>
    /// <param name="args">The command line arguments to pass to the entry point.</param>
    /// <param name="configureBuilder">The delegate used to configure the builder.</param>
    /// <returns>
    /// A new instance of <see cref="IDistributedApplicationTestingBuilder"/>.
    /// </returns>
    public static IDistributedApplicationTestingBuilder Create(string[] args, Action<DistributedApplicationOptions, HostApplicationBuilderSettings> configureBuilder)
    {
        ThrowIfNullOrContainsIsNullOrEmpty(args);
        ArgumentNullException.ThrowIfNull(configureBuilder);

        return new TestingBuilder(args, configureBuilder);
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
    {
        ThrowIfNullOrContainsIsNullOrEmpty(args);
        ArgumentNullException.ThrowIfNull(configureBuilder);

        return new TestingBuilder(args, configureBuilder, appHostAssembly);
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

    private sealed class SuspendingDistributedApplicationFactory(Type entryPoint, string[] args, Action<DistributedApplicationOptions, HostApplicationBuilderSettings> configureBuilder)
        : DistributedApplicationFactory(entryPoint, args)
    {
        private readonly SemaphoreSlim _continueBuilding = new(0);

        public async Task<IDistributedApplicationTestingBuilder> CreateBuilderAsync(CancellationToken cancellationToken)
        {
            var innerBuilder = await ResolveBuilderAsync(cancellationToken).ConfigureAwait(false);
            return new Builder(this, innerBuilder);
        }

        protected override void OnBuilderCreating(DistributedApplicationOptions applicationOptions, HostApplicationBuilderSettings hostOptions)
        {
            base.OnBuilderCreating(applicationOptions, hostOptions);
            configureBuilder(applicationOptions, hostOptions);
        }

        protected override void OnBuilderCreated(DistributedApplicationBuilder applicationBuilder)
        {
            base.OnBuilderCreated(applicationBuilder);
        }

        protected override void OnBuilding(DistributedApplicationBuilder applicationBuilder)
        {
            base.OnBuilding(applicationBuilder);

            // Wait until the owner signals that building can continue by calling BuildAsync().
            _continueBuilding.Wait();
        }

        public async Task<DistributedApplication> BuildAsync(CancellationToken cancellationToken)
        {
            _continueBuilding.Release();
            return await ResolveApplicationAsync(cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask DisposeAsync()
        {
            _continueBuilding.Release();
            await base.DisposeAsync().ConfigureAwait(false);
        }

        public override void Dispose()
        {
            _continueBuilding.Release();
            base.Dispose();
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
        Action<DistributedApplicationOptions, HostApplicationBuilderSettings> configureBuilder,
        Assembly? appHostAssembly = null)
        : IDistributedApplicationTestingBuilder
    {
        private readonly DistributedApplicationBuilder _innerBuilder = CreateInnerBuilder(args, configureBuilder, appHostAssembly);
        private DistributedApplication? _app;

        private static DistributedApplicationBuilder CreateInnerBuilder(
            string[] args,
            Action<DistributedApplicationOptions, HostApplicationBuilderSettings> configureBuilder,
            Assembly? appHostAssembly = null)
        {
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

                DistributedApplicationFactory.ConfigureBuilder(args, applicationOptions, hostBuilderOptions, appAssembly, configureBuilder);
            });

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
