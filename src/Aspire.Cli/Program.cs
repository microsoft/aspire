// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Globalization;
using System.Text;
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
using Microsoft.AspNetCore.Certificates.Generation;
using Aspire.Cli.Commands.Sdk;
using Aspire.Cli.Configuration;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Layout;
using Aspire.Cli.Mcp;
using Aspire.Cli.Mcp.Docs;
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
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var aspirePath = Path.Combine(homeDirectory, ".aspire");
        return aspirePath;
    }

    /// <summary>
    /// Parses logging options from command-line arguments.
    /// Returns the console log level (if specified) and whether debug mode is enabled.
    /// </summary>
    private static (LogLevel? ConsoleLogLevel, bool DebugMode) ParseLoggingOptions(string[]? args)
    {
        if (args is null || args.Length == 0)
        {
            return (null, false);
        }

        // Check for --debug or -d (backward compatibility)
        var debugMode = args.Any(a => a == "--debug" || a == "-d");

        // Check for --log-level or -l
        LogLevel? logLevel = null;
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

        // --debug implies Debug log level if --verbosity not specified
        if (debugMode && logLevel is null)
        {
            logLevel = LogLevel.Debug;
        }

        return (logLevel, debugMode);
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

    private static string GetGlobalSettingsPath()
    {
        var usersAspirePath = GetUsersAspirePath();
        var globalSettingsPath = Path.Combine(usersAspirePath, "globalsettings.json");
        return globalSettingsPath;
    }

    internal static async Task<IHost> BuildApplicationAsync(string[] args, Dictionary<string, string?>? configurationValues = null)
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
        var globalSettingsFilePath = GetGlobalSettingsPath();
        var globalSettingsFile = new FileInfo(globalSettingsFilePath);
        var workingDirectory = new DirectoryInfo(Environment.CurrentDirectory);
        ConfigurationHelper.RegisterSettingsFiles(builder.Configuration, workingDirectory, globalSettingsFile);

        await TrySetLocaleOverrideAsync(LocaleHelpers.GetLocaleOverride(builder.Configuration));

#if !DEBUG
        // In release builds, limit shutdown wait time for telemetry flush to 200ms
        // to ensure the CLI exits quickly even if waiting on shutdown tasks.
        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromMilliseconds(200);
        });
#endif

        // Always configure OpenTelemetry.
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        // Configure OpenTelemetry tracing. TelemetryManager reads configuration and creates
        // separate TracerProvider instances:
        // - Azure Monitor provider with filtering (only exports activities with EXTERNAL_TELEMETRY=true)
        // - Diagnostic provider for OTLP/console exporters (exports all activities, DEBUG only)
        builder.Services.AddSingleton(new TelemetryManager(builder.Configuration, args));

        // Parse logging options from args
        var (consoleLogLevel, debugMode) = ParseLoggingOptions(args);
        var extensionEndpoint = builder.Configuration[KnownConfigNames.ExtensionEndpoint];

        // Always register FileLoggerProvider to capture logs to disk
        // This captures complete CLI session details for diagnostics
        var logsDirectory = Path.Combine(GetUsersAspirePath(), "logs");
        var logFilePath = ParseLogFileOption(args);
        var fileLoggerProvider = logFilePath is not null
            ? new FileLoggerProvider(logFilePath)
            : new FileLoggerProvider(logsDirectory, TimeProvider.System);
        builder.Services.AddSingleton(fileLoggerProvider); // Register for direct access to LogFilePath
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider>(fileLoggerProvider));

        // Configure console logging based on --verbosity or --debug
        if (consoleLogLevel is not null && !isMcpStartCommand && extensionEndpoint is null)
        {
            builder.Logging.AddFilter("Aspire.Cli", consoleLogLevel.Value);
            builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning); // Reduce noise from hosting lifecycle
            // Use custom Spectre Console logger for clean debug output to stderr
            builder.Services.AddSingleton<ILoggerProvider>(sp =>
                new SpectreConsoleLoggerProvider(sp.GetRequiredService<ConsoleEnvironment>().Error.Profile.Out.Writer));
        }

        // For MCP start command, configure console logger to route all logs to stderr
        // This keeps stdout clean for MCP protocol JSON-RPC messages
        if (isMcpStartCommand)
        {
            if (consoleLogLevel is not null)
            {
                builder.Logging.AddFilter("Aspire.Cli", consoleLogLevel.Value);
                builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning); // Reduce noise from hosting lifecycle
            }

            builder.Logging.AddConsole(consoleLogOptions =>
            {
                // Configure all logs to go to stderr
                consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
            });
        }

        // Shared services.
        builder.Services.AddSingleton(sp =>
        {
            var logFilePath = sp.GetRequiredService<FileLoggerProvider>().LogFilePath;
            return BuildCliExecutionContext(debugMode, logsDirectory, logFilePath);
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
        builder.Services.AddSingleton<IAddCommandPrompter, AddCommandPrompter>();
        builder.Services.AddSingleton<IPublishCommandPrompter, PublishCommandPrompter>();
        builder.Services.AddSingleton<ICertificateService, CertificateService>();
        builder.Services.AddSingleton(BuildConfigurationService);
        builder.Services.AddSingleton<IFeatures, Features>();
        builder.Services.AddTelemetryServices();
        builder.Services.AddTransient<IDotNetCliExecutionFactory, DotNetCliExecutionFactory>();

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
        builder.Services.AddSingleton<IBundleService, BundleService>();
        builder.Services.AddSingleton<IAppHostServerProjectFactory, AppHostServerProjectFactory>();
        builder.Services.AddSingleton<ICliDownloader, CliDownloader>();
        builder.Services.AddSingleton<IFirstTimeUseNoticeSentinel>(_ => new FirstTimeUseNoticeSentinel(GetUsersAspirePath()));
        builder.Services.AddSingleton<IBannerService, BannerService>();
        builder.Services.AddMemoryCache();

        // MCP server: aspire.dev docs services.
        builder.Services.AddSingleton<IDocsCache, DocsCache>();
        builder.Services.AddHttpClient<IDocsFetcher, DocsFetcher>();
        builder.Services.AddSingleton<IDocsIndexService, DocsIndexService>();
        builder.Services.AddSingleton<IDocsSearchService, DocsSearchService>();

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

        // Agent environment detection.
        builder.Services.AddSingleton<IAgentEnvironmentDetector, AgentEnvironmentDetector>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentEnvironmentScanner, VsCodeAgentEnvironmentScanner>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentEnvironmentScanner, CopilotCliAgentEnvironmentScanner>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentEnvironmentScanner, OpenCodeAgentEnvironmentScanner>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentEnvironmentScanner, ClaudeCodeAgentEnvironmentScanner>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentEnvironmentScanner, DeprecatedMcpCommandScanner>());

        // Template factories.
        builder.Services.AddSingleton<ITemplateProvider, TemplateProvider>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ITemplateFactory, DotNetTemplateFactory>());

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
        builder.Services.AddSingleton<IEnvironmentCheck, DeprecatedWorkloadCheck>();
        builder.Services.AddSingleton<IEnvironmentCheck, DevCertsCheck>();
        builder.Services.AddSingleton<IEnvironmentCheck, ContainerRuntimeCheck>();
        builder.Services.AddSingleton<IEnvironmentCheck, NodeJsRuntimeCheck>();
        builder.Services.AddSingleton<IEnvironmentCheck, TypeScriptAppHostPackageManagerCheck>();
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
        builder.Services.AddTransient<RestartCommand>();
        builder.Services.AddTransient<WaitCommand>();
        builder.Services.AddTransient<ResourceCommand>();
        builder.Services.AddTransient<PsCommand>();
        builder.Services.AddTransient<DescribeCommand>();
        builder.Services.AddTransient<LogsCommand>();
        builder.Services.AddTransient<AddCommand>();
        builder.Services.AddTransient<PublishCommand>();
        builder.Services.AddTransient<ConfigCommand>();
        builder.Services.AddTransient<CacheCommand>();
        builder.Services.AddTransient<DoctorCommand>();
        builder.Services.AddTransient<UpdateCommand>();
        builder.Services.AddTransient<DeployCommand>();
        builder.Services.AddTransient<DoCommand>();
        builder.Services.AddTransient<ExecCommand>();
        builder.Services.AddTransient<McpCommand>();
        builder.Services.AddTransient<McpStartCommand>();
        builder.Services.AddTransient<McpInitCommand>();
        builder.Services.AddTransient<AgentCommand>();
        builder.Services.AddTransient<AgentMcpCommand>();
        builder.Services.AddTransient<AgentInitCommand>();
        builder.Services.AddTransient<TelemetryCommand>();
        builder.Services.AddTransient<TelemetryLogsCommand>();
        builder.Services.AddTransient<TelemetrySpansCommand>();
        builder.Services.AddTransient<TelemetryTracesCommand>();
        builder.Services.AddTransient<DocsCommand>();
        builder.Services.AddTransient<DocsListCommand>();
        builder.Services.AddTransient<DocsSearchCommand>();
        builder.Services.AddTransient<DocsGetCommand>();
        builder.Services.AddTransient<SdkCommand>();
        builder.Services.AddTransient<SdkGenerateCommand>();
        builder.Services.AddTransient<SdkDumpCommand>();
        builder.Services.AddTransient<SetupCommand>();
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

    private static CliExecutionContext BuildCliExecutionContext(bool debugMode, string logsDirectory, string logFilePath)
    {
        var workingDirectory = new DirectoryInfo(Environment.CurrentDirectory);
        var hivesDirectory = GetHivesDirectory();
        var cacheDirectory = GetCacheDirectory();
        var sdksDirectory = GetSdksDirectory();
        return new CliExecutionContext(workingDirectory, hivesDirectory, cacheDirectory, sdksDirectory, new DirectoryInfo(logsDirectory), logFilePath, debugMode);
    }

    private static DirectoryInfo GetCacheDirectory()
    {
        var homeDirectory = GetUsersAspirePath();
        var cacheDirectoryPath = Path.Combine(homeDirectory, "cache");
        return new DirectoryInfo(cacheDirectoryPath);
    }

    private static async Task TrySetLocaleOverrideAsync(string? localeOverride)
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

            // This writes directly to Console.Error because services aren't available yet.
            await Console.Error.WriteLineAsync(errorMessage);
        }
    }

    private static IConfigurationService BuildConfigurationService(IServiceProvider serviceProvider)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var executionContext = serviceProvider.GetRequiredService<CliExecutionContext>();
        var globalSettingsFile = new FileInfo(GetGlobalSettingsPath());
        return new ConfigurationService(configuration, executionContext, globalSettingsFile);
    }

    internal static async Task DisplayFirstTimeUseNoticeIfNeededAsync(IServiceProvider serviceProvider, string[] args, CancellationToken cancellationToken = default)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var isInformationalCommand = args.Any(a => CommonOptionNames.InformationalOptionNames.Contains(a));
        var noLogo = args.Any(a => a == CommonOptionNames.NoLogo) || configuration.GetBool(CliConfigNames.NoLogo, defaultValue: false) || isInformationalCommand;
        var showBanner = args.Any(a => a == CommonOptionNames.Banner);

        var sentinel = serviceProvider.GetRequiredService<IFirstTimeUseNoticeSentinel>();
        var isFirstRun = !sentinel.Exists();

        // Show banner if explicitly requested OR on first run (unless suppressed by noLogo)
        if (showBanner || (isFirstRun && !noLogo))
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

                consoleEnvironment.Error.WriteLine();
                consoleEnvironment.Error.WriteLine(RootCommandStrings.FirstTimeUseTelemetryNotice);
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

        // Create custom output that handles width detection better in CI environments
        // and encapsulates ASPIRE_CONSOLE_WIDTH environment variable handling
        var output = new AspireAnsiConsoleOutput(writer, configuration);

        var settings = new AnsiConsoleSettings()
        {
            Ansi = isPlayground ? AnsiSupport.Yes : AnsiSupport.Detect,
            Interactive = isPlayground ? InteractionSupport.Yes : InteractionSupport.Detect,
            ColorSystem = isPlayground ? ColorSystemSupport.Standard : ColorSystemSupport.Detect,
            Out = output,
        };

        // Use SupportsAnsi from hostEnvironment which already checks ASPIRE_ANSI_PASS_THRU
        if (hostEnvironment.SupportsAnsi)
        {
            settings.Ansi = AnsiSupport.Yes;
            // Using EightBit color system for better color support of Aspire brand colors in terminals that support ANSI
            settings.ColorSystem = ColorSystemSupport.EightBit;
        }

        if (isPlayground)
        {
            // Enrichers interfere with interactive playground experience so
            // this suppresses the default enrichers so that the CLI experience
            // is more like what we would get in an interactive experience.
            settings.Enrichment.UseDefaultEnrichers = false;
            settings.Enrichment.Enrichers = new()
            {
                new AspirePlaygroundEnricher()
            };
        }

        var ansiConsole = AnsiConsole.Create(settings);
        return ansiConsole;
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

        Console.OutputEncoding = Encoding.UTF8;

        using var app = await BuildApplicationAsync(args);

        await app.StartAsync().ConfigureAwait(false);

        // Display first run experience if this is the first time the CLI is run on this machine
        await DisplayFirstTimeUseNoticeIfNeededAsync(app.Services, args, cts.Token);

        var rootCommand = app.Services.GetRequiredService<RootCommand>();
        var invokeConfig = new InvocationConfiguration()
        {
            // Disable default exception handler so we can log exceptions to telemetry.
            EnableDefaultExceptionHandler = false
        };

        var telemetry = app.Services.GetRequiredService<AspireCliTelemetry>();
        var telemetryManager = app.Services.GetRequiredService<TelemetryManager>();
        var logger = app.Services.GetRequiredService<ILogger<Program>>();

        using var mainActivity = telemetry.StartReportedActivity(name: TelemetryConstants.Activities.Main, kind: ActivityKind.Internal);

        if (mainActivity != null)
        {
            var currentProcess = Process.GetCurrentProcess();
            mainActivity.SetStartTime(currentProcess.StartTime);
            mainActivity.AddTag(TelemetryConstants.Tags.ProcessPid, currentProcess.Id);
            mainActivity.AddTag(TelemetryConstants.Tags.ProcessExecutableName, "aspire");
        }

        // Create a dedicated logger for CLI session info
        var cliLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<Program>();

        try
        {
            // Log command invocation details for debugging
            var commandLine = args.Length > 0 ? $"aspire {string.Join(" ", args)}" : "aspire";
            var workingDir = Environment.CurrentDirectory;
            cliLogger.LogInformation("Command: {CommandLine}", commandLine);
            cliLogger.LogInformation("Working directory: {WorkingDirectory}", workingDir);

            logger.LogDebug("Parsing arguments: {Args}", string.Join(" ", args));
            var parseResult = rootCommand.Parse(args);

            var commandName = GetCommandName(parseResult);
            logger.LogDebug("Executing command: {CommandName}", commandName);

            mainActivity?.SetTag(TelemetryConstants.Tags.CommandName, commandName);

            var exitCode = await parseResult.InvokeAsync(invokeConfig, cts.Token);

            // Log exit code for debugging
            cliLogger.LogInformation("Exit code: {ExitCode}", exitCode);

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

                var interactionService = app.Services.GetRequiredService<IInteractionService>();
                interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.UnexpectedErrorOccurred, ex.Message));
            }

            // Log exit code for debugging
            cliLogger.LogError("Exit code: {ExitCode} (exception)", unknownErrorExitCode);

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
                var consoleInteractionService = new ConsoleInteractionService(consoleEnvironment, executionContext, hostEnvironment);
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
                return new ConsoleInteractionService(consoleEnvironment, executionContext, hostEnvironment);
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
