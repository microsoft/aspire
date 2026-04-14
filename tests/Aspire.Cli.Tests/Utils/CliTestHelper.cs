// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Bundles;
using Aspire.Cli.Certificates;
using Aspire.Cli.Commands;
using Aspire.Cli.Commands.Sdk;
using Aspire.Cli.Documentation.ApiDocs;
using Aspire.Cli.DotNet;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Layout;
using Aspire.Cli.Mcp;
using Aspire.Cli.Documentation.Docs;
using Aspire.Cli.NuGet;
using Aspire.Cli.Projects;
using Aspire.Cli.Scaffolding;
using Aspire.Cli.Secrets;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Aspire.Cli.Configuration;
using Aspire.Cli.Utils;
using Aspire.Cli.Utils.EnvironmentChecker;
using Aspire.Cli.Packaging;
using Aspire.Cli.Caching;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.Npm;

namespace Aspire.Cli.Tests.Utils;

internal static class CliTestHelper
{
    public static IServiceCollection CreateServiceCollection(TemporaryWorkspace workspace, ITestOutputHelper outputHelper, Action<CliServiceCollectionTestOptions>? configure = null)
    {
        var options = new CliServiceCollectionTestOptions(outputHelper, workspace.WorkspaceRoot);
        configure?.Invoke(options);

        var services = new ServiceCollection();

        var configBuilder = new ConfigurationBuilder();

        var configurationValues = new Dictionary<string, string?>();

        // Populate feature flag configuration in in-memory collection.
        options.ConfigurationCallback += config => {
            foreach (var featureFlag in options.EnabledFeatures)
            {
                config[$"{KnownFeatures.FeaturePrefix}:{featureFlag}"] = "true";
            }

            foreach (var featureFlag in options.DisabledFeatures)
            {
                config[$"{KnownFeatures.FeaturePrefix}:{featureFlag}"] = "false";
            }
        };

        options.ConfigurationCallback(configurationValues);

        configBuilder.AddInMemoryCollection(configurationValues);

        var globalSettingsFilePath = Path.Combine(options.WorkingDirectory.FullName, ".aspire", "settings.global.json");
        var globalSettingsFile = new FileInfo(globalSettingsFilePath);
        ConfigurationHelper.RegisterSettingsFiles(configBuilder, options.WorkingDirectory, globalSettingsFile);

        var configuration = configBuilder.Build();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace)).AddXunitLogging(outputHelper);

        // Register logging options for test
        var testLogsDirectory = Path.Combine(options.WorkingDirectory.FullName, ".aspire", "logs");
        var testLogFilePath = FileLoggerProvider.GenerateLogFilePath(testLogsDirectory, TimeProvider.System);
        services.AddSingleton(new FileLoggerProvider(testLogFilePath, new TestStartupErrorWriter()));
        services.AddSingleton(new Program.CliLoggingOptions(ConsoleLogLevel: null, DebugMode: false, LogsDirectory: testLogsDirectory, LogFilePath: testLogFilePath));

        services.AddMemoryCache();

        services.AddSingleton(options.ConsoleEnvironmentFactory);
        services.AddSingleton(sp => sp.GetRequiredService<ConsoleEnvironment>().Out);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(options.TelemetryFactory);
        services.AddSingleton(options.ProjectLocatorFactory);
        services.AddSingleton(options.SolutionLocatorFactory);
        services.AddSingleton(options.ExtensionRpcTargetFactory);
        services.AddTransient(options.ExtensionBackchannelFactory);
        services.AddSingleton(options.InteractionServiceFactory);
        services.AddSingleton(options.CertificateToolRunnerFactory);
        services.AddSingleton(options.CertificateServiceFactory);
        services.AddSingleton(options.NewCommandPrompterFactory);
        services.AddSingleton<ITemplateVersionPrompter>(sp => (ITemplateVersionPrompter)sp.GetRequiredService<INewCommandPrompter>());
        services.AddSingleton(options.AddCommandPrompterFactory);
        services.AddSingleton(options.PublishCommandPrompterFactory);
        services.AddTransient(options.DotNetCliExecutionFactoryFactory);
        services.AddTransient(options.DotNetCliRunnerFactory);
        services.AddTransient(options.NuGetPackageCacheFactory);
        services.AddSingleton(options.TemplateProviderFactory);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITemplateFactory, DotNetTemplateFactory>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITemplateFactory, CliTemplateFactory>());
        services.AddSingleton(options.ConfigurationServiceFactory);
        services.AddSingleton(options.FeatureFlagsFactory);
        services.AddSingleton(options.CliUpdateNotifierFactory);
        services.AddSingleton<IDotNetSdkInstaller>(options.DotNetSdkInstallerFactory);
        services.AddSingleton(options.PackagingServiceFactory);
        services.AddSingleton(options.CliExecutionContextFactory);
        services.AddSingleton(options.DiskCacheFactory);
        services.AddSingleton(options.CliHostEnvironmentFactory);
        services.AddSingleton(options.CliDownloaderFactory);
        services.AddSingleton(options.FirstTimeUseNoticeSentinelFactory);
        services.AddSingleton(options.BannerServiceFactory);
        services.AddSingleton<FallbackProjectParser>();
        services.AddSingleton(options.ProjectUpdaterFactory);
        services.AddSingleton<NuGetPackagePrefetcher>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<NuGetPackagePrefetcher>());
        services.AddSingleton(options.AuxiliaryBackchannelMonitorFactory);
        services.AddSingleton(options.AgentEnvironmentDetectorFactory);
        services.AddSingleton(options.GitRepositoryFactory);
        services.AddSingleton(options.NpmRunnerFactory);
        services.AddSingleton(options.NpmProvenanceCheckerFactory);
        services.AddSingleton(options.PlaywrightCliRunnerFactory);
        services.AddSingleton<PlaywrightCliInstaller>();
        services.AddSingleton<IScaffoldingService, ScaffoldingService>();
        services.AddSingleton<IAppHostServerProjectFactory, AppHostServerProjectFactory>();
        services.AddSingleton(options.AppHostServerSessionFactory);
        services.AddSingleton<ILanguageDiscovery, DefaultLanguageDiscovery>();
        services.AddSingleton(options.LanguageServiceFactory);

        // Bundle layout services - return null/no-op implementations to trigger SDK mode fallback
        // This ensures backward compatibility: no layout found = use legacy SDK mode
        services.AddSingleton(options.LayoutDiscoveryFactory);
        services.AddTransient<LayoutProcessRunner>();
        services.AddSingleton(options.BundleServiceFactory);
        services.AddSingleton<BundleNuGetService>();

        // AppHost project handlers - must match Program.cs registration pattern
        services.AddSingleton<DotNetAppHostProject>();
        services.AddSingleton<Func<LanguageInfo, GuestAppHostProject>>(sp =>
        {
            return language => ActivatorUtilities.CreateInstance<GuestAppHostProject>(sp, language);
        });
        services.AddSingleton<IAppHostProjectFactory, AppHostProjectFactory>();

        services.AddSingleton<IEnvironmentCheck, WslEnvironmentCheck>();
        services.AddSingleton<IEnvironmentCheck, DotNetSdkCheck>();
        services.AddSingleton<IEnvironmentCheck, DeprecatedWorkloadCheck>();
        services.AddSingleton<IEnvironmentCheck, DevCertsCheck>();
        services.AddSingleton<IEnvironmentCheck, ContainerRuntimeCheck>();
        services.AddSingleton<IEnvironmentCheck, DeprecatedAgentConfigCheck>();
        services.AddSingleton<IEnvironmentChecker, EnvironmentChecker>();

        // MCP server transport
        services.AddSingleton(options.McpServerTransportFactory);

        // Documentation services - use test doubles
        services.AddSingleton<IDocsCache, DocsCache>();
        services.AddSingleton<IHttpClientFactory, TestHttpClientFactory>();
        services.AddSingleton<IDocsFetcher, TestDocsFetcher>();
        services.AddSingleton(options.DocsIndexServiceFactory);
        services.AddSingleton(options.DocsSearchServiceFactory);
        services.AddSingleton<IApiDocsCache, ApiDocsCache>();
        services.AddSingleton<IApiDocsFetcher, TestApiDocsFetcher>();
        services.AddSingleton(options.ApiDocsIndexServiceFactory);

        services.AddTransient<RootCommand>();
        services.AddTransient<NewCommand>();
        services.AddTransient<InitCommand>();
        services.AddTransient<AppHostLauncher>();
        services.AddTransient<RunCommand>();
        services.AddTransient<StopCommand>();
        services.AddTransient<StartCommand>();
        services.AddTransient<ResourceCommand>();
        services.AddTransient<PsCommand>();
        services.AddTransient<DescribeCommand>();
        services.AddTransient<LogsCommand>();
        services.AddTransient<ExecCommand>();
        services.AddTransient<AddCommand>();
        services.AddTransient<DeployCommand>();
        services.AddTransient<DestroyCommand>();
        services.AddTransient<DoCommand>();
        services.AddTransient<PublishCommand>();
        services.AddTransient<ConfigCommand>();
        services.AddTransient<CacheCommand>();
        services.AddTransient<CertificatesCommand>();
        services.AddTransient<CertificatesCleanCommand>();
        services.AddTransient<CertificatesTrustCommand>();
        services.AddTransient<DoctorCommand>();
        services.AddTransient<DashboardCommand>();
        services.AddTransient<DashboardRunCommand>();
        services.AddTransient<UpdateCommand>();
        services.AddTransient<SetupCommand>();
        services.AddTransient<McpCommand>();
        services.AddTransient<McpStartCommand>();
        services.AddTransient<McpInitCommand>();
        services.AddTransient<McpToolsCommand>();
        services.AddTransient<McpCallCommand>();
        services.AddTransient<AgentCommand>();
        services.AddTransient<AgentMcpCommand>();
        services.AddTransient<AgentInitCommand>();
        services.AddSingleton<ResourceColorMap>();
        services.AddTransient<TelemetryCommand>();
        services.AddTransient<TelemetryLogsCommand>();
        services.AddTransient<TelemetrySpansCommand>();
        services.AddTransient<TelemetryTracesCommand>();
        services.AddTransient<ExportCommand>();
        services.AddTransient<ExtensionInternalCommand>();
        services.AddTransient<WaitCommand>();
        services.AddTransient<RestoreCommand>();
        services.AddTransient<SdkCommand>();
        services.AddTransient<SdkGenerateCommand>();
        services.AddTransient<SdkDumpCommand>();
        services.AddTransient<ApiCommand>();
        services.AddTransient<ApiListCommand>();
        services.AddTransient<ApiSearchCommand>();
        services.AddTransient<ApiGetCommand>();
        services.AddTransient<DocsCommand>();
        services.AddTransient<DocsListCommand>();
        services.AddTransient<DocsSearchCommand>();
        services.AddTransient<DocsGetCommand>();
        services.AddTransient<SecretCommand>();
        services.AddTransient<SecretSetCommand>();
        services.AddTransient<SecretGetCommand>();
        services.AddTransient<SecretListCommand>();
        services.AddTransient<SecretPathCommand>();
        services.AddTransient<SecretDeleteCommand>();
        services.AddTransient<SecretStoreResolver>();
#if DEBUG
        services.AddTransient<RenderCommand>();
#endif
        services.AddTransient(options.AppHostBackchannelFactory);

        return services;
    }
}

internal sealed class CliServiceCollectionTestOptions
{
    private readonly ITestOutputHelper _outputHelper;

    public CliServiceCollectionTestOptions(ITestOutputHelper outputHelper, DirectoryInfo workingDirectory)
    {
        _outputHelper = outputHelper;
        WorkingDirectory = workingDirectory;

        ProjectLocatorFactory = CreateDefaultProjectLocatorFactory;
        SolutionLocatorFactory = CreateDefaultSolutionLocatorFactory;
        ConfigurationServiceFactory = CreateDefaultConfigurationServiceFactory;
        CliExecutionContextFactory = CreateDefaultCliExecutionContextFactory;
    }

    private CliExecutionContext CreateDefaultCliExecutionContextFactory(IServiceProvider provider)
    {
        var hivesDirectory = new DirectoryInfo(Path.Combine(WorkingDirectory.FullName, ".aspire", "hives"));
        var cacheDirectory = new DirectoryInfo(Path.Combine(WorkingDirectory.FullName, ".aspire", "cache"));
        var logsDirectory = new DirectoryInfo(Path.Combine(WorkingDirectory.FullName, ".aspire", "logs"));
        var logFilePath = Path.Combine(logsDirectory.FullName, "test.log");
        return new CliExecutionContext(WorkingDirectory, hivesDirectory, cacheDirectory, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-sdks")), logsDirectory, logFilePath, packagesDirectory: PackagesDirectory);
    }

    public DirectoryInfo WorkingDirectory { get; set; }

    public DirectoryInfo? PackagesDirectory { get; set; }

    public Action<Dictionary<string, string?>> ConfigurationCallback { get; set; } = (Dictionary<string, string?> config) =>
    {
    };

    public string[] EnabledFeatures { get; set; } = Array.Empty<string>();
    public string[] DisabledFeatures { get; set; } = Array.Empty<string>();

    public TestOutputTextWriter? OutputTextWriter { get; set; }
    public StringWriter? ErrorTextWriter { get; set; }
    public bool DisableAnsi { get; set; }

    public Func<IServiceProvider, ConsoleEnvironment> ConsoleEnvironmentFactory => (IServiceProvider serviceProvider) =>
    {
        var outputTextWriter = OutputTextWriter ?? new TestOutputTextWriter(_outputHelper);
        var errorTextWriter = ErrorTextWriter ?? new StringWriter();

        var outConsole = CreateAnsiConsole(outputTextWriter, !DisableAnsi);
        var errorConsole = CreateAnsiConsole(errorTextWriter, !DisableAnsi);

        return new ConsoleEnvironment(outConsole, errorConsole);
    };

    private static IAnsiConsole CreateAnsiConsole(TextWriter textWriter, bool ansi = true)
    {
        var settings = new AnsiConsoleSettings()
        {
            Ansi = ansi ? AnsiSupport.Yes : AnsiSupport.No,
            Interactive = InteractionSupport.Yes,
            ColorSystem = ansi ? ColorSystemSupport.Standard : ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(textWriter),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        };
        var console = AnsiConsole.Create(settings);
        if (!ansi)
        {
            // Use a large width to prevent Spectre.Console from word-wrapping output lines.
            console.Profile.Width = int.MaxValue;
            // Disable link capabilities to prevent OSC 8 hyperlink sequences in output.
            console.Profile.Capabilities.Links = false;
        }
        return console;
    }

    public Func<IServiceProvider, INewCommandPrompter> NewCommandPrompterFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var interactionService = serviceProvider.GetRequiredService<IInteractionService>();
        return new NewCommandPrompter(interactionService);
    };

    public Func<IServiceProvider, ICliUpdateNotifier> CliUpdateNotifierFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var logger = NullLoggerFactory.Instance.CreateLogger<CliUpdateNotifier>();
        var nuGetPackageCache = serviceProvider.GetRequiredService<INuGetPackageCache>();
        var interactionService = serviceProvider.GetRequiredService<IInteractionService>();
        return new CliUpdateNotifier(logger, nuGetPackageCache, interactionService);
    };

    public Func<IServiceProvider, IAddCommandPrompter> AddCommandPrompterFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var interactionService = serviceProvider.GetRequiredService<IInteractionService>();
        return new AddCommandPrompter(interactionService);
    };

    public Func<IServiceProvider, IPublishCommandPrompter> PublishCommandPrompterFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var interactionService = serviceProvider.GetRequiredService<IInteractionService>();
        return new PublishCommandPrompter(interactionService);
    };

    public Func<IServiceProvider, IConfigurationService> ConfigurationServiceFactory { get; set; }

    public IConfigurationService CreateDefaultConfigurationServiceFactory(IServiceProvider serviceProvider)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var executionContext = serviceProvider.GetRequiredService<CliExecutionContext>();
        return new ConfigurationService(configuration, executionContext, GetGlobalSettingsFile(WorkingDirectory), NullLogger<ConfigurationService>.Instance);
    }

    private static FileInfo GetGlobalSettingsFile(DirectoryInfo workingDirectory)
    {
        var globalSettingsFilePath = Path.Combine(workingDirectory.FullName, ".aspire", "settings.global.json");
        return new FileInfo(globalSettingsFilePath);
    }

    public Func<IServiceProvider, IProjectLocator> ProjectLocatorFactory { get; set; }
    public Func<IServiceProvider, ISolutionLocator> SolutionLocatorFactory { get; set; }
    public Func<IServiceProvider, CliExecutionContext> CliExecutionContextFactory { get; set; }
    public Func<IServiceProvider, IFirstTimeUseNoticeSentinel> FirstTimeUseNoticeSentinelFactory { get; set; } = _ => new TestFirstTimeUseNoticeSentinel();
    public Func<IServiceProvider, IBannerService> BannerServiceFactory { get; set; } = _ => new TestBannerService();

    public IProjectLocator CreateDefaultProjectLocatorFactory(IServiceProvider serviceProvider)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<ProjectLocator>>();
        var executionContext = serviceProvider.GetRequiredService<CliExecutionContext>();
        var interactionService = serviceProvider.GetRequiredService<IInteractionService>();
        var configurationService = serviceProvider.GetRequiredService<IConfigurationService>();
        var projectFactory = serviceProvider.GetService<IAppHostProjectFactory>() ?? new TestAppHostProjectFactory();
        var languageDiscovery = serviceProvider.GetService<ILanguageDiscovery>() ?? new TestLanguageDiscovery();
        var sdkInstaller = serviceProvider.GetRequiredService<IDotNetSdkInstaller>();
        var telemetry = serviceProvider.GetRequiredService<AspireCliTelemetry>();
        return new ProjectLocator(logger, executionContext, interactionService, configurationService, projectFactory, languageDiscovery, sdkInstaller, telemetry);
    }

    public ISolutionLocator CreateDefaultSolutionLocatorFactory(IServiceProvider serviceProvider)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<SolutionLocator>>();
        var interactionService = serviceProvider.GetRequiredService<IInteractionService>();
        return new SolutionLocator(logger, interactionService);
    }

    public Func<IServiceProvider, AspireCliTelemetry> TelemetryFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        return TestTelemetryHelper.CreateInitializedTelemetry();
    };

    public Func<IServiceProvider, IProjectUpdater> ProjectUpdaterFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var logger = serviceProvider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = serviceProvider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = serviceProvider.GetRequiredService<IInteractionService>();
        var cache = serviceProvider.GetRequiredService<IMemoryCache>();
        var executionContext = serviceProvider.GetRequiredService<CliExecutionContext>();
        var fallbackParser = serviceProvider.GetRequiredService<FallbackProjectParser>();
        return new ProjectUpdater(logger, runner, interactionService, cache, executionContext, fallbackParser);
    };

    public Func<IServiceProvider, ICliHostEnvironment> CliHostEnvironmentFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        return new CliHostEnvironment(configuration, nonInteractive: true);
    };

    public Func<IServiceProvider, IInteractionService> InteractionServiceFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var consoleEnvironment = serviceProvider.GetRequiredService<ConsoleEnvironment>();
        var executionContext = serviceProvider.GetRequiredService<CliExecutionContext>();
        var hostEnvironment = serviceProvider.GetRequiredService<ICliHostEnvironment>();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        return new ConsoleInteractionService(consoleEnvironment, executionContext, hostEnvironment, loggerFactory);
    };

    public Func<IServiceProvider, ICertificateToolRunner> CertificateToolRunnerFactory { get; set; } = (IServiceProvider _) =>
    {
        // Use TestCertificateToolRunner by default to avoid calling real dotnet dev-certs
        // which can be slow or block on macOS (keychain access prompts)
        return new TestCertificateToolRunner();
    };

    public Func<IServiceProvider, ICertificateService> CertificateServiceFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var certificateToolRunner = serviceProvider.GetRequiredService<ICertificateToolRunner>();
        var interactiveService = serviceProvider.GetRequiredService<IInteractionService>();
        var telemetry = serviceProvider.GetRequiredService<AspireCliTelemetry>();
        var hostEnvironment = serviceProvider.GetRequiredService<ICliHostEnvironment>();
        return new CertificateService(certificateToolRunner, interactiveService, telemetry, hostEnvironment);
    };

    public Func<IServiceProvider, IProcessExecutionFactory> DotNetCliExecutionFactoryFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        return new TestProcessExecutionFactory();
    };

    public Func<IServiceProvider, IDotNetCliRunner> DotNetCliRunnerFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var logger = serviceProvider.GetRequiredService<ILogger<DotNetCliRunner>>();
        var telemetry = serviceProvider.GetRequiredService<AspireCliTelemetry>();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var features = serviceProvider.GetRequiredService<IFeatures>();
        var diskCache = serviceProvider.GetRequiredService<IDiskCache>();
        var executionContext = serviceProvider.GetRequiredService<CliExecutionContext>();
        var executionFactory = serviceProvider.GetRequiredService<IProcessExecutionFactory>();
        var interactionService = serviceProvider.GetRequiredService<IInteractionService>();

        return new DotNetCliRunner(logger, serviceProvider, telemetry, configuration, diskCache, features, interactionService, executionContext, executionFactory);
    };

    public Func<IServiceProvider, IDotNetSdkInstaller> DotNetSdkInstallerFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        return new TestDotNetSdkInstaller();
    };

    public Func<IServiceProvider, INuGetPackageCache> NuGetPackageCacheFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var runner = serviceProvider.GetRequiredService<IDotNetCliRunner>();
        var cache = serviceProvider.GetRequiredService<IMemoryCache>();
        var telemetry = serviceProvider.GetRequiredService<AspireCliTelemetry>();
        var features = serviceProvider.GetRequiredService<IFeatures>();
        return new NuGetPackageCache(runner, cache, telemetry, features);
    };

    public Func<IServiceProvider, IAppHostCliBackchannel> AppHostBackchannelFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var logger = serviceProvider.GetRequiredService<ILogger<AppHostCliBackchannel>>();
        var telemetry = serviceProvider.GetRequiredService<AspireCliTelemetry>();
        return new AppHostCliBackchannel(logger, telemetry);
    };

    public Func<IServiceProvider, IExtensionRpcTarget> ExtensionRpcTargetFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        return new ExtensionRpcTarget(configuration);
    };

    public Func<IServiceProvider, IExtensionBackchannel> ExtensionBackchannelFactory { get; set; } = serviceProvider =>
    {
        var logger = serviceProvider.GetRequiredService<ILogger<ExtensionBackchannel>>();
        var rpcTarget = serviceProvider.GetRequiredService<IExtensionRpcTarget>();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        return new ExtensionBackchannel(logger, rpcTarget, configuration);
    };

    public Func<IServiceProvider, IFeatures> FeatureFlagsFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var logger = serviceProvider.GetRequiredService<ILogger<Features>>();
        return new Features(configuration, logger);
    };

    public Func<IServiceProvider, ITemplateProvider> TemplateProviderFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var interactionService = serviceProvider.GetRequiredService<IInteractionService>();
        var runner = serviceProvider.GetRequiredService<IDotNetCliRunner>();
        var certificateService = serviceProvider.GetRequiredService<ICertificateService>();
        var packagingService = serviceProvider.GetRequiredService<IPackagingService>();
        var prompter = serviceProvider.GetRequiredService<INewCommandPrompter>();
        var executionContext = serviceProvider.GetRequiredService<CliExecutionContext>();
        var features = serviceProvider.GetRequiredService<IFeatures>();
        var configurationService = serviceProvider.GetRequiredService<IConfigurationService>();
        var hostEnvironment = serviceProvider.GetRequiredService<ICliHostEnvironment>();
        var sdkInstaller = serviceProvider.GetRequiredService<IDotNetSdkInstaller>();
        var telemetry = serviceProvider.GetRequiredService<AspireCliTelemetry>();
        var templateVersionPrompter = serviceProvider.GetRequiredService<ITemplateVersionPrompter>();
        var languageDiscovery = serviceProvider.GetRequiredService<ILanguageDiscovery>();
        var scaffoldingService = serviceProvider.GetRequiredService<IScaffoldingService>();
        var cliTemplateLogger = serviceProvider.GetRequiredService<ILogger<CliTemplateFactory>>();
        var templateNuGetConfigService = new TemplateNuGetConfigService(interactionService, executionContext, packagingService, configurationService);
        var dotNetFactory = new DotNetTemplateFactory(interactionService, runner, certificateService, packagingService, prompter, templateVersionPrompter, executionContext, sdkInstaller, features, configurationService, telemetry, hostEnvironment, templateNuGetConfigService);
        var projectFactory = serviceProvider.GetRequiredService<IAppHostProjectFactory>();
        var cliFactory = new CliTemplateFactory(languageDiscovery, projectFactory, scaffoldingService, prompter, executionContext, interactionService, hostEnvironment, templateNuGetConfigService, cliTemplateLogger);
        return new TemplateProvider([dotNetFactory, cliFactory]);
    };

    public Func<IServiceProvider, IPackagingService> PackagingServiceFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var executionContext = serviceProvider.GetRequiredService<CliExecutionContext>();
        var nuGetPackageCache = serviceProvider.GetRequiredService<INuGetPackageCache>();
        var features = serviceProvider.GetRequiredService<IFeatures>();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        return new PackagingService(executionContext, nuGetPackageCache, features, configuration);
    };

    public Func<IServiceProvider, IDiskCache> DiskCacheFactory { get; set; } = (IServiceProvider serviceProvider) => new NullDiskCache();

    public Func<IServiceProvider, ICliDownloader> CliDownloaderFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var executionContext = serviceProvider.GetRequiredService<CliExecutionContext>();
        var tmpDirectory = new DirectoryInfo(Path.Combine(executionContext.WorkingDirectory.FullName, "tmp"));
        return new TestCliDownloader(tmpDirectory);
    };

    public Func<IServiceProvider, IAuxiliaryBackchannelMonitor> AuxiliaryBackchannelMonitorFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        return new TestAuxiliaryBackchannelMonitor();
    };

    public Func<IServiceProvider, IAgentEnvironmentDetector> AgentEnvironmentDetectorFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        return new AgentEnvironmentDetector([]);
    };

    public Func<IServiceProvider, IGitRepository> GitRepositoryFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var executionContext = serviceProvider.GetRequiredService<CliExecutionContext>();
        var logger = serviceProvider.GetRequiredService<ILogger<GitRepository>>();
        return new GitRepository(executionContext, logger);
    };

    public Func<IServiceProvider, INpmRunner> NpmRunnerFactory { get; set; } = _ => new FakeNpmRunner();

    public Func<IServiceProvider, INpmProvenanceChecker> NpmProvenanceCheckerFactory { get; set; } = _ => new FakeNpmProvenanceChecker();

    public Func<IServiceProvider, IPlaywrightCliRunner> PlaywrightCliRunnerFactory { get; set; } = _ => new FakePlaywrightCliRunner();

    public Func<IServiceProvider, ILanguageService> LanguageServiceFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var projects = serviceProvider.GetServices<IAppHostProject>();
        var defaultProject = projects.FirstOrDefault(p => p.LanguageId == KnownLanguageId.CSharp)
            ?? serviceProvider.GetService<DotNetAppHostProject>();
        return new TestLanguageService { DefaultProject = defaultProject };
    };

    public Func<IServiceProvider, IAppHostServerSessionFactory> AppHostServerSessionFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        return new TestAppHostServerSessionFactory();
    };

    // Layout discovery - returns null by default (no bundle layout), causing SDK mode fallback
    public Func<IServiceProvider, ILayoutDiscovery> LayoutDiscoveryFactory { get; set; } = _ => new NullLayoutDiscovery();

    // Bundle service - returns no-op implementation by default (no embedded bundle)
    public Func<IServiceProvider, IBundleService> BundleServiceFactory { get; set; } = _ => new NullBundleService();

    public Func<IServiceProvider, IMcpTransportFactory> McpServerTransportFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        return new StdioMcpTransportFactory(loggerFactory ?? NullLoggerFactory.Instance);
    };

    public Func<IServiceProvider, IDocsIndexService> DocsIndexServiceFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var fetcher = serviceProvider.GetRequiredService<IDocsFetcher>();
        var cache = serviceProvider.GetRequiredService<IDocsCache>();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var logger = serviceProvider.GetRequiredService<ILogger<DocsIndexService>>();
        return new DocsIndexService(fetcher, cache, configuration, logger);
    };

    public Func<IServiceProvider, IDocsSearchService> DocsSearchServiceFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var indexService = serviceProvider.GetRequiredService<IDocsIndexService>();
        var logger = serviceProvider.GetRequiredService<ILogger<DocsSearchService>>();
        return new DocsSearchService(indexService, logger);
    };

    public Func<IServiceProvider, IApiDocsIndexService> ApiDocsIndexServiceFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var fetcher = serviceProvider.GetRequiredService<IApiDocsFetcher>();
        var cache = serviceProvider.GetRequiredService<IApiDocsCache>();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var logger = serviceProvider.GetRequiredService<ILogger<ApiDocsIndexService>>();
        return new ApiDocsIndexService(fetcher, cache, configuration, logger);
    };
}

/// <summary>
/// A layout discovery that always returns null (no bundle layout).
/// Used in tests to ensure SDK mode is used.
/// </summary>
internal sealed class NullLayoutDiscovery : ILayoutDiscovery
{
    public LayoutConfiguration? DiscoverLayout(string? projectDirectory = null) => null;

    public string? GetComponentPath(LayoutComponent component, string? projectDirectory = null) => null;

    public bool IsBundleModeAvailable(string? projectDirectory = null) => false;
}

/// <summary>
/// A no-op bundle service that never extracts anything.
/// Used in tests to ensure SDK mode fallback.
/// </summary>
internal sealed class NullBundleService : IBundleService
{
    public bool IsBundle => false;

    public Task EnsureExtractedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<BundleExtractResult> ExtractAsync(string destinationPath, bool force = false, CancellationToken cancellationToken = default)
        => Task.FromResult(BundleExtractResult.NoPayload);

    public Task<Layout.LayoutConfiguration?> EnsureExtractedAndGetLayoutAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<Layout.LayoutConfiguration?>(null);
}

/// <summary>
/// A configurable bundle service for testing bundle-dependent behavior.
/// </summary>
internal sealed class TestBundleService(bool isBundle) : IBundleService
{
    public bool IsBundle => isBundle;

    public Layout.LayoutConfiguration? Layout { get; set; }

    public Task EnsureExtractedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<BundleExtractResult> ExtractAsync(string destinationPath, bool force = false, CancellationToken cancellationToken = default)
        => Task.FromResult(isBundle ? BundleExtractResult.AlreadyUpToDate : BundleExtractResult.NoPayload);

    public Task<Layout.LayoutConfiguration?> EnsureExtractedAndGetLayoutAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Layout);
}

internal sealed class TestOutputTextWriter : TextWriter
{
    private readonly ITestOutputHelper _outputHelper;
    private readonly StringBuilder _buffer = new();
    public List<string> Logs { get; } = new List<string>();

    public TestOutputTextWriter(ITestOutputHelper outputHelper) : this(outputHelper, null)
    {
    }

    public TestOutputTextWriter(ITestOutputHelper outputHelper, IFormatProvider? formatProvider) : base(formatProvider)
    {
        _outputHelper = outputHelper;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void WriteLine(string? message)
    {
        _buffer.Append(message);
        FlushLine();
    }

    public override void Write(string? message)
    {
        if (message is null)
        {
            return;
        }

        // Spectre.Console writes content and newlines via Write() calls.
        // Split on newline boundaries so each logical line ends up as one Logs entry.
        var remaining = message.AsSpan();
        while (remaining.Length > 0)
        {
            var nlIndex = remaining.IndexOf('\n');
            if (nlIndex < 0)
            {
                _buffer.Append(remaining);
                break;
            }

            // Append everything before the newline (excluding any \r before \n)
            var lineEnd = nlIndex > 0 && remaining[nlIndex - 1] == '\r' ? nlIndex - 1 : nlIndex;
            _buffer.Append(remaining[..lineEnd]);
            FlushLine();
            remaining = remaining[(nlIndex + 1)..];
        }
    }

    public override void Flush()
    {
        if (_buffer.Length > 0)
        {
            FlushLine();
        }
        base.Flush();
    }

    private void FlushLine()
    {
        var line = _buffer.ToString();
        _buffer.Clear();
        _outputHelper.WriteLine(line);
        Logs.Add(line);
    }

}
