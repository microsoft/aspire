// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Agents.CopilotCli;
using Aspire.Cli.Agents.OpenCode;
using Aspire.Cli.Agents.VsCode;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Bundles;
using Aspire.Cli.Caching;
using Aspire.Cli.Certificates;
using Aspire.Cli.Commands;
using Aspire.Cli.Secrets;
using Aspire.Cli.Documentation.ApiDocs;
using Microsoft.AspNetCore.Certificates.Generation;
using Aspire.Cli.Commands.Sdk;
using Aspire.Cli.Configuration;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Layout;
using Aspire.Cli.Mcp;
using Aspire.Cli.Documentation.Docs;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Scaffolding;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Templating;
using Aspire.Cli.Utils;
using Aspire.Cli.Utils.EnvironmentChecker;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli;

public class Program
{
    private static string GetUsersAspirePath()
    {
        return CliPathHelper.GetAspireHomeDirectory();
    }

    /// <summary>
    /// Contains all logging-related options parsed from command-line arguments.
    /// </summary>
    /// <param name="ConsoleLogLevel">The console log level if specified via --log-level or --debug.</param>
    /// <param name="DebugMode">Whether --debug or -d was specified.</param>
    /// <param name="LogsDirectory">The directory where log files are stored.</param>
    /// <param name="LogFilePath">The full path to the current session's log file.</param>
    internal record CliLoggingOptions(LogLevel? ConsoleLogLevel, bool DebugMode, string LogsDirectory, string LogFilePath);

    /// <summary>
    /// Holds the objects created during early CLI startup, before DI is available.
    /// Disposes the logger factory, file logger provider, and error writer.
    /// </summary>
    internal sealed record CliStartupContext(
        CliLoggingOptions LoggingOptions,
        IStartupErrorWriter ErrorWriter,
        ILoggerFactory LoggerFactory,
        FileLoggerProvider FileLoggerProvider,
        ILogger Logger) : IDisposable
    {
        public void Dispose()
        {
            FileLoggerProvider.Dispose();
            LoggerFactory.Dispose();
            ErrorWriter.Dispose();
        }
    }

    /// <summary>
    /// Parses logging options from command-line arguments.
    /// Returns all logging configuration including log level, debug mode, and file paths.
    /// </summary>
    internal static CliLoggingOptions ParseLoggingOptions(string[]? args)
    {
        LogLevel? logLevel = null;
        var debugMode = false;

        if (args is not null && args.Length > 0)
        {
            // Check for --debug or -d (backward compatibility)
            debugMode = args.Any(a => a == "--debug" || a == "-d");

            // Check for --log-level or -l
            for (var i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--log-level" || args[i] == "-l") && i + 1 < args.Length)
                {
                    if (Enum.TryParse<LogLevel>(args[i + 1], ignoreCase: true, out var parsedLevel))
                    {
                        logLevel = parsedLevel;
                    }
                    break;
                }
            }

            // --debug implies Debug log level if --log-level not specified
            if (debugMode && logLevel is null)
            {
                logLevel = LogLevel.Debug;
            }
        }

        var logsDirectory = Path.Combine(GetUsersAspirePath(), "logs");
        var logFilePath = ParseLogFileOption(args) ?? FileLoggerProvider.GenerateLogFilePath(logsDirectory, TimeProvider.System);

        return new CliLoggingOptions(logLevel, debugMode, logsDirectory, logFilePath);
    }

    /// <summary>
    /// Parses --log-file from raw args before the host is built.
    /// Used by --detach to tell the child CLI where to write its log.
    /// </summary>
    internal static string? ParseLogFileOption(string[]? args)
    {
        if (args is null)
        {
            return null;
        }

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                break;
            }

            if (args[i] == "--log-file" && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static string GetGlobalSettingsPath(ILogger logger)
    {
        var usersAspirePath = GetUsersAspirePath();
        var newPath = Path.Combine(usersAspirePath, Configuration.AspireConfigFile.FileName);

        // TODO: Remove globalsettings.json migration once confident most users have migrated.
        // The old file is intentionally kept so older CLI versions continue to work during
        // the transition period. Tracked by https://github.com/microsoft/aspire/issues/15239
        var legacyPath = Path.Combine(usersAspirePath, "globalsettings.json");
        if (!File.Exists(newPath) && File.Exists(legacyPath))
        {
            try
            {
                var legacyJson = File.ReadAllText(legacyPath);
                var legacyConfig = JsonSerializer.Deserialize(legacyJson, JsonSourceGenerationContext.Default.AspireJsonConfiguration);
                var config = AspireConfigFile.FromLegacy(legacyConfig, profiles: null);
                // Drop the legacy global identity-channel field — the CLI's channel is now
                // baked into the binary (AspireCliChannel assembly metadata) and never read
                // from global config. The per-project channel migration in AspireConfigFile.FromLegacy
                // remains for project-local aspire.config.json files.
                config.Channel = null;
                config.Save(usersAspirePath);
            }
            catch (Exception ex)
            {
                // If migration fails, newPath will be created on first write.
                logger.LogError(ex, "Failed to migrate legacy globalsettings.json to {NewPath}.", newPath);
            }
        }

        return newPath;
    }

    internal static void WarnIfGlobalSettingsContainAppHostPath(FileInfo globalSettingsFile, IStartupErrorWriter errorWriter)
    {
        if (ConfigurationHelper.TryLoadSettingsFile(globalSettingsFile.FullName, out var globalSettings) &&
            AppHostPathConfigurationPolicy.TryFindAppHostPathKey(globalSettings, out var key))
        {
            var warning = string.Format(
                CultureInfo.CurrentCulture,
                ErrorStrings.GlobalAppHostPathIgnored,
                globalSettingsFile.FullName,
                key);

            errorWriter.WriteMarkup(warning.EscapeMarkup(), KnownEmojis.Warning);
        }
    }

    /// <summary>
    /// Creates and configures an <see cref="ILoggerFactory"/> for the CLI application.
    /// Sets up OpenTelemetry logging, file logging, console logging, log-level filters,
    /// and MCP console logging based on command-line arguments.
    /// </summary>
    /// <returns>A tuple containing the configured <see cref="ILoggerFactory"/> and the <see cref="FileLoggerProvider"/> used for file logging.</returns>
    internal static (ILoggerFactory LoggerFactory, FileLoggerProvider FileLoggerProvider) CreateLoggerFactory(string[] args, CliLoggingOptions loggingOptions, IStartupErrorWriter errorWriter)
    {
        var consoleLogLevel = loggingOptions.ConsoleLogLevel;

        var isMcpStartCommand = args?.Length >= 2 &&
            ((args[0] == "mcp" && args[1] == "start") || (args[0] == "agent" && args[1] == "mcp"));

        var extensionEndpoint = Environment.GetEnvironmentVariable(KnownConfigNames.ExtensionEndpoint);

        // Create file logger provider from pre-computed path info
        var fileLoggerProvider = new FileLoggerProvider(loggingOptions.LogFilePath, errorWriter);

        var factory = LoggerFactory.Create(builder =>
        {
            // Always configure OpenTelemetry.
            builder.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
            });

            // Always capture complete CLI session details to disk for diagnostics
            builder.AddProvider(fileLoggerProvider);

            // Configure log-level filters based on --log-level or --debug
            if (consoleLogLevel is not null)
            {
                builder.AddFilter("Aspire.Cli", consoleLogLevel.Value);
                builder.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
            }
            else
            {
                // Default writing debug level logs to file.
                builder.AddFilter<FileLoggerProvider>(null, LogLevel.Debug);

                // These categories are very verbose at Debug; suppress their Debug output by
                // raising the minimum to Information unless an explicit log level is requested.
                builder.AddFilter<FileLoggerProvider>("Microsoft.Extensions.Http.DefaultHttpClientFactory", LogLevel.Information);
                builder.AddFilter<FileLoggerProvider>("Aspire.Cli.Certificates.NativeCertificateToolRunner", LogLevel.Information);
            }

            // Configure console logging based on --verbosity or --debug
            if (consoleLogLevel is not null && !isMcpStartCommand && extensionEndpoint is null)
            {
                // Use custom Spectre Console logger for clean debug output to stderr
                builder.AddProvider(new SpectreConsoleLoggerProvider(Console.Error));
            }

            // For MCP start command, configure console logger to route all logs to stderr
            // This keeps stdout clean for MCP protocol JSON-RPC messages
            if (isMcpStartCommand)
            {
                builder.AddConsole(consoleLogOptions =>
                {
                    // Configure all logs to go to stderr
                    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
                });
            }
        });

        return (factory, fileLoggerProvider);
    }

    internal static async Task<IHost> BuildApplicationAsync(string[] args, CliStartupContext startupContext, Dictionary<string, string?>? configurationValues = null)
    {
        // Check for --non-interactive flag early
        var nonInteractive = args?.Any(a => a == CommonOptionNames.NonInteractive) ?? false;

        // Check if running MCP start command - all logs should go to stderr to keep stdout clean for MCP protocol
        // Support both old 'mcp start' and new 'agent mcp' commands
        var isMcpStartCommand = args?.Length >= 2 &&
            ((args[0] == "mcp" && args[1] == "start") || (args[0] == "agent" && args[1] == "mcp"));

        var settings = new HostApplicationBuilderSettings
        {
            Configuration = new ConfigurationManager()
        };
        settings.Configuration.AddEnvironmentVariables();

        if (configurationValues is not null)
        {
            settings.Configuration.AddInMemoryCollection(configurationValues);
        }

        var builder = Host.CreateEmptyApplicationBuilder(settings);

        // Set up settings with appropriate paths.
        var globalSettingsFilePath = GetGlobalSettingsPath(startupContext.Logger);
        var globalSettingsFile = new FileInfo(globalSettingsFilePath);
        var workingDirectory = new DirectoryInfo(Environment.CurrentDirectory);
        ConfigurationHelper.RegisterSettingsFiles(builder.Configuration, workingDirectory, globalSettingsFile);

        TrySetLocaleOverride(LocaleHelpers.GetLocaleOverride(builder.Configuration), startupContext.Logger, startupContext.ErrorWriter);
        WarnIfGlobalSettingsContainAppHostPath(globalSettingsFile, startupContext.ErrorWriter);

#if !DEBUG
        // In release builds, limit shutdown wait time for telemetry flush to 200ms
        // to ensure the CLI exits quickly even if waiting on shutdown tasks.
        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromMilliseconds(200);
        });
#endif

        // Register the provided logger factory, replacing host defaults
        builder.Services.AddSingleton<ILoggerFactory>(startupContext.LoggerFactory);
        builder.Services.TryAddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // Register file logger provider for components that write directly to the log file
        builder.Services.AddSingleton(startupContext.FileLoggerProvider);

        // Register logging options so components can read the user's chosen log level
        builder.Services.AddSingleton(startupContext.LoggingOptions);

        // Configure OpenTelemetry tracing. TelemetryManager reads configuration and creates
        // separate TracerProvider instances:
        // - Azure Monitor provider with filtering (only exports activities with EXTERNAL_TELEMETRY=true)
        // - Diagnostic provider for OTLP/console exporters (exports all activities, DEBUG only)
        builder.Services.AddSingleton(sp => new TelemetryManager(sp.GetRequiredService<IConfiguration>(), args));

        // Shared services.
        builder.Services.AddSingleton<IIdentityChannelReader>(_ => new IdentityChannelReader(typeof(Program).Assembly));
        builder.Services.AddSingleton(sp =>
        {
            var channelReader = sp.GetRequiredService<IIdentityChannelReader>();
            var channel = channelReader.ReadChannel();
            int? prNumber = null;
            if (channel == "pr")
            {
                var infoVersion = typeof(Program).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                prNumber = IdentityChannelReader.ParsePrNumber(infoVersion);
            }
            return BuildCliExecutionContext(startupContext.LoggingOptions.DebugMode, startupContext.LoggingOptions.LogsDirectory, startupContext.LoggingOptions.LogFilePath, channel, prNumber);
        });
        builder.Services.AddSingleton(s => new ConsoleEnvironment(
            BuildAnsiConsole(s, Console.Out),
            BuildAnsiConsole(s, Console.Error)));
        builder.Services.AddSingleton(s => s.GetRequiredService<ConsoleEnvironment>().Out);
        builder.Services.AddSingleton<ICliHostEnvironment>(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            return new CliHostEnvironment(configuration, nonInteractive);
        });
        builder.Services.AddSingleton(TimeProvider.System);
        AddInteractionServices(builder);
        builder.Services.AddSingleton<IProjectLocator, ProjectLocator>();
        builder.Services.AddSingleton<ISolutionLocator, SolutionLocator>();
        builder.Services.AddSingleton<ILanguageService, LanguageService>();
        builder.Services.AddSingleton<IScaffoldingService, ScaffoldingService>();
        builder.Services.AddSingleton<FallbackProjectParser>();
        builder.Services.AddSingleton<IProjectUpdater, ProjectUpdater>();
        builder.Services.AddSingleton<INewCommandPrompter, NewCommandPrompter>();
        builder.Services.AddSingleton<ITemplateVersionPrompter>(sp => (ITemplateVersionPrompter)sp.GetRequiredService<INewCommandPrompter>());
        builder.Services.AddSingleton<IAddCommandPrompter, AddCommandPrompter>();
        builder.Services.AddSingleton<IPublishCommandPrompter, PublishCommandPrompter>();
        builder.Services.AddSingleton<ICertificateService, CertificateService>();
        builder.Services.AddSingleton(BuildConfigurationService);
        builder.Services.AddSingleton<IFeatures, Features>();
        builder.Services.AddTelemetryServices();
        builder.Services.AddTransient<IProcessExecutionFactory, ProcessExecutionFactory>();
        builder.Services.AddTransient<LayoutProcessRunner>();

        // Register certificate tool runner - uses native CertificateManager directly (no subprocess needed)
        builder.Services.AddSingleton(sp => CertificateManager.Create(sp.GetRequiredService<ILogger<NativeCertificateToolRunner>>()));
        builder.Services.AddSingleton<ICertificateToolRunner, NativeCertificateToolRunner>();

        builder.Services.AddTransient<IDotNetCliRunner, DotNetCliRunner>();
        builder.Services.AddSingleton<IDiskCache, DiskCache>();
        builder.Services.AddSingleton<IDotNetSdkInstaller, DotNetSdkInstaller>();
        builder.Services.AddTransient<IAppHostCliBackchannel, AppHostCliBackchannel>();

        // Register both NuGetPackageCache implementations - factory chooses based on embedded bundle
        builder.Services.AddSingleton<NuGetPackageCache>();
        builder.Services.AddSingleton<BundleNuGetPackageCache>();
        builder.Services.AddSingleton<INuGetPackageCache>(sp =>
        {
            if (sp.GetRequiredService<IBundleService>().IsBundle)
            {
                return sp.GetRequiredService<BundleNuGetPackageCache>();
            }

            // Fall back to SDK-based cache
            return sp.GetRequiredService<NuGetPackageCache>();
        });

        builder.Services.AddSingleton<NuGetPackagePrefetcher>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<NuGetPackagePrefetcher>());
        builder.Services.AddSingleton<AuxiliaryBackchannelMonitor>();
        builder.Services.AddSingleton<IAuxiliaryBackchannelMonitor>(sp => sp.GetRequiredService<AuxiliaryBackchannelMonitor>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AuxiliaryBackchannelMonitor>());
        builder.Services.AddSingleton<ICliUpdateNotifier, CliUpdateNotifier>();
        builder.Services.AddSingleton<IPackagingService, PackagingService>();
        builder.Services.AddSingleton<IBundlePayloadProvider, EmbeddedBundlePayloadProvider>();
        builder.Services.AddSingleton<IBundleService, BundleService>();
        builder.Services.AddSingleton<IAppHostServerProjectFactory, AppHostServerProjectFactory>();
        builder.Services.AddSingleton<ICliDownloader, CliDownloader>();
        builder.Services.AddSingleton<IFirstTimeUseNoticeSentinel>(_ => new FirstTimeUseNoticeSentinel(GetUsersAspirePath()));
        builder.Services.AddSingleton<IBannerService, BannerService>();
        builder.Services.AddSingleton<ResourceColorMap>();
        builder.Services.AddMemoryCache();

        // aspire.dev documentation services.
        builder.Services.AddSingleton<IDocsCache, DocsCache>();
        builder.Services.AddHttpClient<IDocsFetcher, DocsFetcher>();
        builder.Services.AddSingleton<IDocsIndexService, DocsIndexService>();
        builder.Services.AddSingleton<IDocsSearchService, DocsSearchService>();
        builder.Services.AddSingleton<IApiDocsCache, ApiDocsCache>();
        builder.Services.AddHttpClient<IApiDocsFetcher, ApiDocsFetcher>();
        builder.Services.AddSingleton<IApiDocsIndexService, ApiDocsIndexService>();

        // Bundle layout services (for polyglot apphost without .NET SDK).
        // Registered before NuGetPackageCache so the factory can choose implementation.
        builder.Services.AddSingleton<ILayoutDiscovery, LayoutDiscovery>();
        builder.Services.AddSingleton<BundleNuGetService>();

        // Git repository operations.
        builder.Services.AddSingleton<IGitRepository, GitRepository>();

        // OpenCode CLI operations.
        builder.Services.AddSingleton<IOpenCodeCliRunner, OpenCodeCliRunner>();

        // Claude Code CLI operations.
        builder.Services.AddSingleton<IClaudeCodeCliRunner, ClaudeCodeCliRunner>();

        // VS Code CLI operations.
        builder.Services.AddSingleton<IVsCodeCliRunner, VsCodeCliRunner>();
        builder.Services.AddSingleton<ICopilotCliRunner, CopilotCliRunner>();

        // Npm and Playwright CLI operations.
        builder.Services.AddSingleton<Aspire.Cli.Npm.INpmRunner, Aspire.Cli.Npm.NpmRunner>();
        builder.Services.AddHttpClient<Aspire.Cli.Npm.INpmProvenanceChecker, Aspire.Cli.Npm.SigstoreNpmProvenanceChecker>();
        builder.Services.AddSingleton<Aspire.Cli.Agents.Playwright.IPlaywrightCliRunner, Aspire.Cli.Agents.Playwright.PlaywrightCliRunner>();
        builder.Services.AddSingleton<Aspire.Cli.Agents.Playwright.PlaywrightCliInstaller>();

        // Agent environment detection.
        builder.Services.AddSingleton<IAgentEnvironmentDetector, AgentEnvironmentDetector>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentEnvironmentScanner, VsCodeAgentEnvironmentScanner>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentEnvironmentScanner, CopilotCliAgentEnvironmentScanner>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentEnvironmentScanner, OpenCodeAgentEnvironmentScanner>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentEnvironmentScanner, ClaudeCodeAgentEnvironmentScanner>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentEnvironmentScanner, DeprecatedMcpCommandScanner>());

        // Template factories.
        builder.Services.AddSingleton<TemplateNuGetConfigService>();
        builder.Services.AddSingleton<ITemplateProvider, TemplateProvider>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ITemplateFactory, DotNetTemplateFactory>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ITemplateFactory, CliTemplateFactory>());

        // Language discovery for polyglot support.
        builder.Services.AddSingleton<ILanguageDiscovery, DefaultLanguageDiscovery>();

        // AppHost server session factory for RPC communication.
        builder.Services.AddSingleton<IAppHostServerSessionFactory, AppHostServerSessionFactory>();

        // AppHost project handlers.
        builder.Services.AddSingleton<DotNetAppHostProject>();
        builder.Services.AddSingleton<Func<LanguageInfo, GuestAppHostProject>>(sp =>
        {
            return language => ActivatorUtilities.CreateInstance<GuestAppHostProject>(sp, language);
        });
        builder.Services.AddSingleton<IAppHostProjectFactory, AppHostProjectFactory>();

        // Environment checking services.
        builder.Services.AddSingleton<IEnvironmentCheck, WslEnvironmentCheck>();
        builder.Services.AddSingleton<IEnvironmentCheck, DotNetSdkCheck>();
        builder.Services.AddSingleton<IEnvironmentCheck, TypeScriptAppHostToolingCheck>();
        builder.Services.AddSingleton<IEnvironmentCheck, DeprecatedWorkloadCheck>();
        builder.Services.AddSingleton<IEnvironmentCheck, DevCertsCheck>();
        builder.Services.AddSingleton<IEnvironmentCheck, ContainerRuntimeCheck>();
        builder.Services.AddSingleton<IEnvironmentCheck, DeprecatedAgentConfigCheck>();
        builder.Services.AddSingleton<IEnvironmentChecker, EnvironmentChecker>();

        // MCP server transport factory - creates transport only when needed to avoid
        // capturing stdin/stdout before the MCP server command is actually executed.
        builder.Services.AddSingleton<IMcpTransportFactory, StdioMcpTransportFactory>();

        // Commands.
        builder.Services.AddTransient<AppHostLauncher>();
        builder.Services.AddTransient<NewCommand>();
        builder.Services.AddTransient<InitCommand>();
        builder.Services.AddTransient<RunCommand>();
        builder.Services.AddTransient<StopCommand>();
        builder.Services.AddTransient<StartCommand>();
        builder.Services.AddTransient<WaitCommand>();
        builder.Services.AddTransient<ResourceCommand>();
        builder.Services.AddTransient<PsCommand>();
        builder.Services.AddTransient<DescribeCommand>();
        builder.Services.AddTransient<LogsCommand>();
        builder.Services.AddTransient<AddCommand>();
        builder.Services.AddTransient<PublishCommand>();
        builder.Services.AddTransient<ConfigCommand>();
        builder.Services.AddTransient<CacheCommand>();
        builder.Services.AddTransient<CertificatesCommand>();
        builder.Services.AddTransient<CertificatesCleanCommand>();
        builder.Services.AddTransient<CertificatesTrustCommand>();
        builder.Services.AddTransient<DoctorCommand>();
        builder.Services.AddTransient<DashboardCommand>();
        builder.Services.AddTransient<DashboardRunCommand>();
        builder.Services.AddTransient<UpdateCommand>();
        builder.Services.AddTransient<DeployCommand>();
        builder.Services.AddTransient<DestroyCommand>();
        builder.Services.AddTransient<DoCommand>();
        builder.Services.AddTransient<ExecCommand>();
        builder.Services.AddTransient<McpCommand>();
        builder.Services.AddTransient<McpStartCommand>();
        builder.Services.AddTransient<McpInitCommand>();
        builder.Services.AddTransient<McpToolsCommand>();
        builder.Services.AddTransient<McpCallCommand>();
        builder.Services.AddTransient<AgentCommand>();
        builder.Services.AddTransient<AgentMcpCommand>();
        builder.Services.AddTransient<AgentInitCommand>();
        builder.Services.AddTransient<TelemetryCommand>();
        builder.Services.AddTransient<TelemetryLogsCommand>();
        builder.Services.AddTransient<TelemetrySpansCommand>();
        builder.Services.AddTransient<TelemetryTracesCommand>();
        builder.Services.AddTransient<ExportCommand>();
        builder.Services.AddTransient<ApiCommand>();
        builder.Services.AddTransient<ApiListCommand>();
        builder.Services.AddTransient<ApiSearchCommand>();
        builder.Services.AddTransient<ApiGetCommand>();
        builder.Services.AddTransient<DocsCommand>();
        builder.Services.AddTransient<DocsListCommand>();
        builder.Services.AddTransient<DocsSearchCommand>();
        builder.Services.AddTransient<DocsGetCommand>();
        builder.Services.AddTransient<SecretCommand>();
        builder.Services.AddTransient<SecretSetCommand>();
        builder.Services.AddTransient<SecretGetCommand>();
        builder.Services.AddTransient<SecretListCommand>();
        builder.Services.AddTransient<SecretPathCommand>();
        builder.Services.AddTransient<SecretDeleteCommand>();
        builder.Services.AddTransient<SecretStoreResolver>();
        builder.Services.AddTransient<SdkCommand>();
        builder.Services.AddTransient<SdkGenerateCommand>();
        builder.Services.AddTransient<SdkDumpCommand>();
        builder.Services.AddTransient<RestoreCommand>();
        builder.Services.AddTransient<SetupCommand>();
#if DEBUG
        builder.Services.AddTransient<RenderCommand>();
#endif
        builder.Services.AddTransient<RootCommand>();
        builder.Services.AddTransient<ExtensionInternalCommand>();

        var app = builder.Build();
        return app;
    }

    private static DirectoryInfo GetHivesDirectory()
    {
        var homeDirectory = GetUsersAspirePath();
        var hivesDirectory = Path.Combine(homeDirectory, "hives");
        return new DirectoryInfo(hivesDirectory);
    }

    private static DirectoryInfo GetSdksDirectory()
    {
        var homeDirectory = GetUsersAspirePath();
        var sdksPath = Path.Combine(homeDirectory, "sdks");
        return new DirectoryInfo(sdksPath);
    }

    private static CliExecutionContext BuildCliExecutionContext(bool debugMode, string logsDirectory, string logFilePath, string channel, int? prNumber)
    {
        var workingDirectory = new DirectoryInfo(Environment.CurrentDirectory);
        var hivesDirectory = GetHivesDirectory();
        var cacheDirectory = GetCacheDirectory();
        var sdksDirectory = GetSdksDirectory();
        var packagesDirectory = GetPackagesDirectory();
        return new CliExecutionContext(workingDirectory, hivesDirectory, cacheDirectory, sdksDirectory, new DirectoryInfo(logsDirectory), logFilePath, debugMode, packagesDirectory: packagesDirectory, channel: channel, prNumber: prNumber);
    }

    private static DirectoryInfo GetCacheDirectory()
    {
        var homeDirectory = GetUsersAspirePath();
        var cacheDirectoryPath = Path.Combine(homeDirectory, "cache");
        return new DirectoryInfo(cacheDirectoryPath);
    }

    private static DirectoryInfo GetPackagesDirectory()
    {
        var homeDirectory = GetUsersAspirePath();
        var packagesDirectoryPath = Path.Combine(homeDirectory, "packages");
        return new DirectoryInfo(packagesDirectoryPath);
    }

    private static void TrySetLocaleOverride(string? localeOverride, ILogger logger, IStartupErrorWriter errorWriter)
    {
        if (localeOverride is not null)
        {
            var result = LocaleHelpers.TrySetLocaleOverride(localeOverride);

            string errorMessage;
            switch (result)
            {
                case SetLocaleResult.Success:
                    return;
                case SetLocaleResult.InvalidLocale:
                    errorMessage = string.Format(CultureInfo.CurrentCulture, ErrorStrings.UnsupportedLocaleProvided, localeOverride, string.Join(", ", LocaleHelpers.SupportedLocales));
                    break;
                case SetLocaleResult.UnsupportedLocale:
                    errorMessage = string.Format(CultureInfo.CurrentCulture, ErrorStrings.InvalidLocaleProvided, localeOverride);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected result: {result}");
            }

            logger.LogError("Locale override failed: {ErrorMessage}", errorMessage);
            errorWriter.WriteLine(errorMessage);
        }
    }

    private static IConfigurationService BuildConfigurationService(IServiceProvider serviceProvider)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var executionContext = serviceProvider.GetRequiredService<CliExecutionContext>();
        var logger = serviceProvider.GetRequiredService<ILogger<ConfigurationService>>();
        var globalSettingsFile = new FileInfo(GetGlobalSettingsPath(logger));
        return new ConfigurationService(configuration, executionContext, globalSettingsFile, logger);
    }

    internal static async Task DisplayFirstTimeUseNoticeIfNeededAsync(IServiceProvider serviceProvider, string[] args, CancellationToken cancellationToken = default)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var isInformationalCommand = args.Any(a => CommonOptionNames.InformationalOptionNames.Contains(a));
        var noLogo = args.Any(a => a == CommonOptionNames.NoLogo) || configuration.GetBool(CliConfigNames.NoLogo, defaultValue: false) || isInformationalCommand;
        var showBanner = args.Any(a => a == CommonOptionNames.Banner);

        var sentinel = serviceProvider.GetRequiredService<IFirstTimeUseNoticeSentinel>();
        var isFirstRun = !sentinel.Exists();

        var hostEnvironment = serviceProvider.GetRequiredService<ICliHostEnvironment>();

        // Show banner if explicitly requested OR on first run (unless suppressed by noLogo or non-interactive output)
        if (showBanner || (isFirstRun && !noLogo && hostEnvironment.SupportsInteractiveOutput))
        {
            var bannerService = serviceProvider.GetRequiredService<IBannerService>();
            await bannerService.DisplayBannerAsync(cancellationToken);
        }

        // Only show telemetry notice on first run (not when banner is explicitly requested)
        if (isFirstRun)
        {
            if (!noLogo)
            {
                // Write to stderr to avoid interfering with tools that parse stdout
                var consoleEnvironment = serviceProvider.GetRequiredService<ConsoleEnvironment>();

                const string telemetryUrl = "https://aka.ms/aspire/cli-telemetry";

                consoleEnvironment.Error.WriteLine();
                consoleEnvironment.Error.MarkupLine(string.Format(CultureInfo.CurrentCulture, RootCommandStrings.FirstTimeUseTelemetryNotice, $"[link]{telemetryUrl}[/]"));
                consoleEnvironment.Error.WriteLine();
            }

            // Don't persist the sentinel for informational commands (--version, --help, etc.)
            // so the first-run experience is shown on the next real command invocation.
            if (!isInformationalCommand)
            {
                sentinel.CreateIfNotExists();
            }
        }
    }

    private static IAnsiConsole BuildAnsiConsole(IServiceProvider serviceProvider, TextWriter writer)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var hostEnvironment = serviceProvider.GetRequiredService<ICliHostEnvironment>();
        var isPlayground = CliHostEnvironment.IsPlaygroundMode(configuration);
        var supportsAnsi = hostEnvironment.SupportsAnsi;
        var supportsInteractiveOutput = hostEnvironment.SupportsInteractiveOutput;
        var usePlaygroundFormatting = isPlayground && supportsAnsi && supportsInteractiveOutput;

        // Create custom output that handles width detection better in CI environments
        // and encapsulates ASPIRE_CONSOLE_WIDTH environment variable handling
        var output = new AspireAnsiConsoleOutput(writer, configuration);

        var settings = new AnsiConsoleSettings()
        {
            Ansi = supportsAnsi ? AnsiSupport.Yes : AnsiSupport.No,
            Interactive = supportsInteractiveOutput ? InteractionSupport.Detect : InteractionSupport.No,
            ColorSystem = supportsAnsi ? ColorSystemSupport.EightBit : ColorSystemSupport.NoColors,
            Out = output,
        };

        if (usePlaygroundFormatting)
        {
            settings.Interactive = InteractionSupport.Yes;

            // Enrichers interfere with interactive playground experience so
            // this suppresses the default enrichers so that the CLI experience
            // is more like what we would get in an interactive experience.
            settings.Enrichment.UseDefaultEnrichers = false;
            settings.Enrichment.Enrichers = new()
            {
                new AspirePlaygroundEnricher()
            };
        }

        return AnsiConsole.Create(settings);
    }

    public static async Task<int> Main(string[] args)
    {
        // Setup handling of CTRL-C as early as possible so that if
        // we get a CTRL-C anywhere that is not handled by Spectre Console
        // already that we know to trigger cancellation.
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            cts.Cancel();
            eventArgs.Cancel = true;
        };
        using var sigTermRegistration = OperatingSystem.IsWindows()
            ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                cts.Cancel();
                context.Cancel = true;
            });

        Console.OutputEncoding = Encoding.UTF8;

        var loggingOptions = ParseLoggingOptions(args);
        var errorWriter = new StartupErrorWriter(loggingOptions.LogFilePath);
        var (loggerFactory, fileLoggerProvider) = CreateLoggerFactory(args, loggingOptions, errorWriter);
        var logger = loggerFactory.CreateLogger<Program>();
        using var startupContext = new CliStartupContext(loggingOptions, errorWriter, loggerFactory, fileLoggerProvider, logger);

        logger.LogInformation("Version: {Version}", AspireCliTelemetry.GetCliVersion());
        logger.LogInformation("Build ID: {BuildId}", AspireCliTelemetry.GetCliBuildId());
        logger.LogInformation("Working directory: {WorkingDirectory}", Environment.CurrentDirectory);
        // Logging the log file path is useful so that when console logging is enabled (for example with --log-level debug),
        // the path is written to the console logger (stderr) for easier discovery.
        logger.LogInformation("Log file: {LogFilePath}", loggingOptions.LogFilePath);
        logger.LogInformation("CLI process ID: {ProcessId}", Environment.ProcessId);

        IHost? app = null;
        try
        {
            app = await BuildApplicationAsync(args, startupContext);
            await app.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            app?.Dispose();

            logger.LogError(ex, "Failed to load configuration or start CLI.");
            errorWriter.WriteLine(ex.Message);
            return ExitCodeConstants.FailedToStartCli;
        }

        // Ensure dispose of app when Main exits.
        using var _ = app;

        // Immediately get telemetry and telemetry manager so they are created by DI and telemetry is configured.
        var telemetry = app.Services.GetRequiredService<AspireCliTelemetry>();
        var telemetryManager = app.Services.GetRequiredService<TelemetryManager>();

        // Log feature state at startup for diagnostics
        app.Services.GetRequiredService<IFeatures>().LogFeatureState();

        // Display first run experience if this is the first time the CLI is run on this machine
        await DisplayFirstTimeUseNoticeIfNeededAsync(app.Services, args, cts.Token);

        var rootCommand = app.Services.GetRequiredService<RootCommand>();
        var invokeConfig = new InvocationConfiguration()
        {
            // Disable default exception handler so we can log exceptions to telemetry.
            EnableDefaultExceptionHandler = false
        };

        using var mainActivity = telemetry.StartReportedActivity(name: TelemetryConstants.Activities.Main, kind: ActivityKind.Internal);

        if (mainActivity != null)
        {
            var currentProcess = Process.GetCurrentProcess();
            mainActivity.SetStartTime(currentProcess.StartTime);
            mainActivity.AddTag(TelemetryConstants.Tags.ProcessPid, currentProcess.Id);
            mainActivity.AddTag(TelemetryConstants.Tags.ProcessExecutableName, "aspire");
        }

        try
        {
            // Log command invocation details for debugging
            var commandLine = args.Length > 0 ? $"aspire {string.Join(" ", args)}" : "aspire";
            logger.LogInformation("Command: {CommandLine}", commandLine);

            logger.LogDebug("Parsing arguments: {Args}", string.Join(" ", args));
            var parseResult = rootCommand.Parse(args);

            var commandName = GetCommandName(parseResult);
            logger.LogDebug("Executing command: {CommandName}", commandName);

            mainActivity?.SetTag(TelemetryConstants.Tags.CommandName, commandName);

            var exitCode = await parseResult.InvokeAsync(invokeConfig, cts.Token);

            // Log exit code for debugging
            logger.LogInformation("Exit code: {ExitCode}", exitCode);

            mainActivity?.SetTag(TelemetryConstants.Tags.ProcessExitCode, exitCode);
            mainActivity?.Stop();

            return exitCode;
        }
        catch (Exception ex)
        {
            const int unknownErrorExitCode = 1;
            // Catch block is used instead of System.Commandline's default handler behavior.
            // Allows logging of exceptions to telemetry.

            // Don't log or display cancellation exceptions.
            // Check both Ctrl+C cancellation (cts.IsCancellationRequested) and
            // extension prompt cancellation (ExtensionOperationCanceledException).
            if (!(ex is OperationCanceledException && cts.IsCancellationRequested) && ex is not ExtensionOperationCanceledException)
            {
                logger.LogError(ex, "An unexpected error occurred.");

                telemetry.RecordError("An unexpected error occurred.", ex);

                errorWriter.WriteLine(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.UnexpectedErrorOccurred, ex.Message));
            }

            // Log exit code for debugging
            logger.LogError("Exit code: {ExitCode} (exception)", unknownErrorExitCode);

            mainActivity?.SetTag(TelemetryConstants.Tags.ProcessExitCode, unknownErrorExitCode);
            mainActivity?.Stop();

            return unknownErrorExitCode;
        }
        finally
        {
            // Shutting down telemetry manager to flush any remaining telemetry and will take time.
            // Start shutdown of telemetry manager immediately and run concurrently with app shutdown.
            var shutdownTelemetryTask = telemetryManager.ShutdownAsync();

            await app.StopAsync().ConfigureAwait(false);
            await shutdownTelemetryTask;
        }
    }

    private static string GetCommandName(ParseResult r)
    {
        // Walk the parent command tree to find the top-level command name and get the full command name for this parseresult.
        var parentNames = new List<string> { r.CommandResult.Command.Name };
        var current = r.CommandResult.Parent;
        while (current is CommandResult parentCommandResult)
        {
            parentNames.Add(parentCommandResult.Command.Name);
            current = parentCommandResult.Parent;
        }
        parentNames.Reverse();
        return string.Join(' ', parentNames);
    }

    private static void AddInteractionServices(HostApplicationBuilder builder)
    {
        var extensionEndpoint = builder.Configuration[KnownConfigNames.ExtensionEndpoint];

        if (extensionEndpoint is not null)
        {
            builder.Services.AddSingleton<IExtensionRpcTarget, ExtensionRpcTarget>();
            builder.Services.AddSingleton<IExtensionBackchannel, ExtensionBackchannel>();

            var extensionPromptEnabled = builder.Configuration[KnownConfigNames.ExtensionPromptEnabled] is "true";
            builder.Services.AddSingleton<IInteractionService>(provider =>
            {
                var consoleEnvironment = provider.GetRequiredService<ConsoleEnvironment>();
                consoleEnvironment.Out.Profile.Width = 256; // VS code terminal will handle wrapping so set a large width here.
                var executionContext = provider.GetRequiredService<CliExecutionContext>();
                var hostEnvironment = provider.GetRequiredService<ICliHostEnvironment>();
                var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
                var consoleInteractionService = new ConsoleInteractionService(consoleEnvironment, executionContext, hostEnvironment, loggerFactory);
                return new ExtensionInteractionService(consoleInteractionService,
                    provider.GetRequiredService<IExtensionBackchannel>(),
                    extensionPromptEnabled);
            });
        }
        else
        {
            builder.Services.AddSingleton<IInteractionService>(provider =>
            {
                var consoleEnvironment = provider.GetRequiredService<ConsoleEnvironment>();
                var executionContext = provider.GetRequiredService<CliExecutionContext>();
                var hostEnvironment = provider.GetRequiredService<ICliHostEnvironment>();
                var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
                return new ConsoleInteractionService(consoleEnvironment, executionContext, hostEnvironment, loggerFactory);
            });
        }
    }
}

internal class AspirePlaygroundEnricher : IProfileEnricher
{
    public string Name => "Aspire Playground";

    public bool Enabled(IDictionary<string, string> environmentVariables)
    {
        if (!environmentVariables.TryGetValue("ASPIRE_PLAYGROUND", out var value))
        {
            return false;
        }

        if (!bool.TryParse(value, out var isEnabled))
        {
            return false;
        }

        return isEnabled;
    }

    public void Enrich(Profile profile)
    {
        profile.Capabilities.Interactive = true;
    }
}
