// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Certificates;
using Aspire.Cli.Commands;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Projects;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Aspire.Cli.Configuration;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Aspire.Cli.Packaging;

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

        services.AddLogging();

        services.AddMemoryCache();

        services.AddSingleton(options.AnsiConsoleFactory);
        services.AddSingleton(options.TelemetryFactory);
        services.AddSingleton(options.ProjectLocatorFactory);
        services.AddSingleton(options.ExtensionRpcTargetFactory);
        services.AddTransient(options.ExtensionBackchannelFactory);
        services.AddSingleton(options.InteractionServiceFactory);
        services.AddSingleton(options.CertificateServiceFactory);
        services.AddSingleton(options.NewCommandPrompterFactory);
        services.AddSingleton(options.AddCommandPrompterFactory);
        services.AddSingleton(options.PublishCommandPrompterFactory);
        services.AddTransient(options.DotNetCliRunnerFactory);
        services.AddTransient(options.NuGetPackageCacheFactory);
        services.AddSingleton(options.TemplateProviderFactory);
        services.AddSingleton(options.ConfigurationServiceFactory);
        services.AddSingleton(options.FeatureFlagsFactory);
        services.AddSingleton(options.CliUpdateNotifierFactory);
        services.AddSingleton(options.DotNetSdkInstallerFactory);
        services.AddSingleton(options.DotNetRuntimeSelectorFactory);
        services.AddSingleton(options.PackagingServiceFactory);
        services.AddSingleton(options.CliExecutionContextFactory);
        services.AddTransient<RootCommand>();
        services.AddTransient<NewCommand>();
        services.AddTransient<RunCommand>();
        services.AddTransient<ExecCommand>();
        services.AddTransient<AddCommand>();
        services.AddTransient<DeployCommand>();
        services.AddTransient<PublishCommand>();
        services.AddTransient<ConfigCommand>();
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
        ConfigurationServiceFactory = CreateDefaultConfigurationServiceFactory;
        CliExecutionContextFactory = CreateDefaultCliExecutionContextFactory;
    }

    private CliExecutionContext CreateDefaultCliExecutionContextFactory(IServiceProvider provider)
    {
        var hivesDirectory = new DirectoryInfo(Path.Combine(WorkingDirectory.FullName, ".aspire", "hives"));
        return new CliExecutionContext(WorkingDirectory, hivesDirectory);
    }

    public DirectoryInfo WorkingDirectory { get; set; }

    public Action<Dictionary<string, string?>> ConfigurationCallback { get; set; } = (Dictionary<string, string?> config) =>
    {
    };

    public string[] EnabledFeatures { get; set; } = Array.Empty<string>();
    public string[] DisabledFeatures { get; set; } = Array.Empty<string>();

    public Func<IServiceProvider, IAnsiConsole> AnsiConsoleFactory => (IServiceProvider serviceProvider) =>
    {
        AnsiConsoleSettings settings = new AnsiConsoleSettings()
        {
            Ansi = AnsiSupport.Yes,
            Interactive = InteractionSupport.Yes,
            ColorSystem = ColorSystemSupport.Standard,
            Out = new AnsiConsoleOutput(new TestOutputTextWriter(_outputHelper))
        };
        var ansiConsole = AnsiConsole.Create(settings);
        return ansiConsole;
    };

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
        return new ConfigurationService(configuration, executionContext, GetGlobalSettingsFile(WorkingDirectory));
    }

    private static FileInfo GetGlobalSettingsFile(DirectoryInfo workingDirectory)
    {
        var globalSettingsFilePath = Path.Combine(workingDirectory.FullName, ".aspire", "settings.global.json");
        return new FileInfo(globalSettingsFilePath);
    }

    public Func<IServiceProvider, IProjectLocator> ProjectLocatorFactory { get; set; }
    public Func<IServiceProvider, CliExecutionContext> CliExecutionContextFactory { get; set; }

    public IProjectLocator CreateDefaultProjectLocatorFactory(IServiceProvider serviceProvider)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<ProjectLocator>>();
        var runner = serviceProvider.GetRequiredService<IDotNetCliRunner>();
        var executionContext = serviceProvider.GetRequiredService<CliExecutionContext>();
        var interactionService = serviceProvider.GetRequiredService<IInteractionService>();
        var configurationService = serviceProvider.GetRequiredService<IConfigurationService>();
        var telemetry = serviceProvider.GetRequiredService<AspireCliTelemetry>();
        return new ProjectLocator(logger, runner, executionContext, interactionService, configurationService, telemetry);
    }

    public Func<IServiceProvider, AspireCliTelemetry> TelemetryFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        return new AspireCliTelemetry();
    };

    public Func<IServiceProvider, IInteractionService> InteractionServiceFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var ansiConsole = serviceProvider.GetRequiredService<IAnsiConsole>();
        return new ConsoleInteractionService(ansiConsole);
    };

    public Func<IServiceProvider, ICertificateService> CertificateServiceFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var interactiveService = serviceProvider.GetRequiredService<IInteractionService>();
        var telemetry = serviceProvider.GetRequiredService<AspireCliTelemetry>();
        return new CertificateService(interactiveService, telemetry);
    };

    public Func<IServiceProvider, IDotNetCliRunner> DotNetCliRunnerFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var logger = serviceProvider.GetRequiredService<ILogger<DotNetCliRunner>>();
        var telemetry = serviceProvider.GetRequiredService<AspireCliTelemetry>();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var features = serviceProvider.GetRequiredService<IFeatures>();
        var interactionService = serviceProvider.GetRequiredService<IInteractionService>();
        var executionContext = serviceProvider.GetRequiredService<CliExecutionContext>();

        return new DotNetCliRunner(logger, serviceProvider, telemetry, configuration, features, interactionService, executionContext);
    };

    public Func<IServiceProvider, IDotNetSdkInstaller> DotNetSdkInstallerFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        return new TestDotNetSdkInstaller();
    };

    public Func<IServiceProvider, IDotNetRuntimeSelector> DotNetRuntimeSelectorFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        return new TestDotNetRuntimeSelector();
    };

    public Func<IServiceProvider, INuGetPackageCache> NuGetPackageCacheFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var logger = serviceProvider.GetRequiredService<ILogger<NuGetPackageCache>>();
        var runner = serviceProvider.GetRequiredService<IDotNetCliRunner>();
        var cache = serviceProvider.GetRequiredService<IMemoryCache>();
        var telemetry = serviceProvider.GetRequiredService<AspireCliTelemetry>();
        return new NuGetPackageCache(logger, runner, cache, telemetry);
    };

    public Func<IServiceProvider, IAppHostBackchannel> AppHostBackchannelFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var logger = serviceProvider.GetRequiredService<ILogger<AppHostBackchannel>>();
        var telemetry = serviceProvider.GetRequiredService<AspireCliTelemetry>();
        return new AppHostBackchannel(logger, telemetry);
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
        return new Features(configuration);
    };

    public Func<IServiceProvider, ITemplateProvider> TemplateProviderFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var interactionService = serviceProvider.GetRequiredService<IInteractionService>();
        var runner = serviceProvider.GetRequiredService<IDotNetCliRunner>();
        var certificateService = serviceProvider.GetRequiredService<ICertificateService>();
        var packagingService = serviceProvider.GetRequiredService<IPackagingService>();
        var prompter = serviceProvider.GetRequiredService<INewCommandPrompter>();
        var executionContext = serviceProvider.GetRequiredService<CliExecutionContext>();
        var factory = new DotNetTemplateFactory(interactionService, runner, certificateService, packagingService, prompter, executionContext);
        return new TemplateProvider([factory]);
    };

    public Func<IServiceProvider, IPackagingService> PackagingServiceFactory { get; set; } = (IServiceProvider serviceProvider) =>
    {
        var executionContext = serviceProvider.GetRequiredService<CliExecutionContext>();
        var nuGetPackageCache = serviceProvider.GetRequiredService<INuGetPackageCache>();
        return new PackagingService(executionContext, nuGetPackageCache);
    };
}

internal sealed class TestOutputTextWriter : TextWriter
{
    private readonly ITestOutputHelper _outputHelper;
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
        _outputHelper.WriteLine(message!);
        if (message is not null)
        {
            Logs.Add(message);
        }
    }

    public override void Write(string? message)
    {
        _outputHelper.Write(message!);
        if (message is not null)
        {
            Logs.Add(message);
        }
    }

}
