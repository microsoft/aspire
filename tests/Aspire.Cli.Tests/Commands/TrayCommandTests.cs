// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Bundles;
using Aspire.Cli.Commands;
using Aspire.Cli.Layout;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Commands;

public class TrayCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void ManagedBuildIsNotClassifiedAsNative()
    {
        Assert.False(new Aspire.Cli.Utils.EnvironmentProcessPathProvider().IsNativeAot);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    public async Task HelperArgumentsAndLeaseHandoffAreBoundToInvokingBundle(string action)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (services, bundle, factory, interaction, cliPath) = CreateServices(workspace);
        var layout = bundle.Layout!;
        var root = layout.LayoutPath!;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BundleVersionLease? companionLease = null;
        factory.AsyncAttemptCallback = async (_, _, token) =>
        {
            Assert.True(BundleVersionLease.HasActiveLease(root));
            started.SetResult();
            await acknowledge.Task.WaitAsync(token);
            if (action == "start")
            {
                companionLease = BundleVersionLease.Acquire(root, "tray", "gui");
            }
            return (0, null);
        };
        using var provider = services.BuildServiceProvider();
        try
        {
            var invocation = provider.GetRequiredService<RootCommand>()
                .Parse($"tray {action} --non-interactive --nologo").InvokeAsync();
            await started.Task.DefaultTimeout();

            Assert.False(invocation.IsCompleted);
            Assert.Empty(interaction.DisplayedSuccess);
            Assert.True(BundleVersionLease.HasActiveLease(root));
            Assert.Equal(layout.GetTrayPath(), factory.LastFileName);
            Assert.Equal(action == "start" ? ["start", "--cli", cliPath, "--bundle-root", root] : ["stop"], factory.LastArguments);
            Assert.Equal(root, factory.LastEnvironmentVariables![BundleDiscovery.BundleVersionDirectoryEnvVar]);
            Assert.Equal(root, factory.LastWorkingDirectory!.FullName);
            Assert.False(factory.LastProcessInvocationOptions!.Detached);
            Assert.Equal("cli", bundle.LastHolderKind);
            Assert.Equal($"tray {action}", bundle.LastCommandName);

            acknowledge.SetResult();
            Assert.Equal(CliExitCodes.Success, await invocation.DefaultTimeout());
            Assert.Equal(action == "start" ? TrayCommandStrings.Started : TrayCommandStrings.Stopped, Assert.Single(interaction.DisplayedSuccess));
            Assert.Empty(interaction.DisplayedErrors);
            Assert.Equal(1, Assert.IsType<TestProcessExecution>(Assert.Single(factory.CreatedExecutions)).DisposeCount);
            Assert.Equal(action == "start", BundleVersionLease.HasActiveLease(root));
            Assert.Equal(action == "start" ? 1 : 0, Directory.Exists(Path.Combine(root, ".leases"))
                ? Directory.GetFiles(Path.Combine(root, ".leases"), "*.lease").Length : 0);
        }
        finally
        {
            companionLease?.Dispose();
        }
        Assert.False(BundleVersionLease.HasActiveLease(root));
    }

    [Fact]
    public async Task RepeatedStartUsesSameInvokingCliAndAcknowledgedHelper()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (services, bundle, factory, _, cliPath) = CreateServices(workspace);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(CliExitCodes.Success, await command.Parse("tray start --non-interactive --nologo").InvokeAsync().DefaultTimeout());
            Assert.Equal(["start", "--cli", cliPath, "--bundle-root", bundle.Layout!.LayoutPath!], factory.LastArguments!);
            Assert.False(BundleVersionLease.HasActiveLease(bundle.Layout.LayoutPath!));
        }
        Assert.Equal(2, factory.AttemptCount);
        Assert.Equal(2, bundle.AcquireLayoutCallCount);
    }

    [Theory]
    [InlineData("start", true)]
    [InlineData("stop", true)]
    [InlineData("start", false)]
    [InlineData("stop", false)]
    public async Task UnsupportedPlatformDoesNotAcquireBundleOrLaunchHelper(string action, bool windows)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (services, bundle, factory, interaction, _) = CreateServices(workspace);
        services.AddSingleton<IEnvironment>(windows ? TestEnvironment.CreateWindows() : TestEnvironment.CreateLinux());
        using var provider = services.BuildServiceProvider();

        Assert.Equal(CliExitCodes.InvalidCommand, await provider.GetRequiredService<RootCommand>()
            .Parse($"tray {action}").InvokeAsync().DefaultTimeout());

        Assert.Equal(TrayCommandStrings.MacOSOnly, Assert.Single(interaction.DisplayedErrors));
        Assert.Equal(0, bundle.AcquireLayoutCallCount);
        Assert.Empty(factory.CreatedExecutions);
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("apphost")]
    [InlineData("missing")]
    [InlineData("relative")]
    [InlineData("null")]
    public async Task StartRejectsManagedOrUnavailableInvokingExecutable(string scenario)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (services, bundle, factory, interaction, cliPath) = CreateServices(workspace);
        var path = scenario switch
        {
            "dotnet" => Path.Combine(workspace.WorkspaceRoot.FullName, "dotnet"),
            "missing" => Path.Combine(workspace.WorkspaceRoot.FullName, "missing-aspire"),
            "relative" => "aspire",
            "null" => null,
            _ => cliPath
        };
        if (scenario == "dotnet")
        {
            File.WriteAllText(path!, "fake dotnet");
        }
        services.AddSingleton<Aspire.Cli.Utils.IProcessPathProvider>(
            new TestProcessPathProvider(path) { IsNativeAot = scenario is not ("dotnet" or "apphost") });
        using var provider = services.BuildServiceProvider();

        Assert.Equal(CliExitCodes.InvalidCommand, await provider.GetRequiredService<RootCommand>()
            .Parse("tray start").InvokeAsync().DefaultTimeout());

        Assert.Equal(TrayCommandStrings.NativeCliRequired, Assert.Single(interaction.DisplayedErrors));
        Assert.Equal(0, bundle.AcquireLayoutCallCount);
        Assert.Empty(factory.CreatedExecutions);
    }

    [Theory]
    [InlineData("start", "layout")]
    [InlineData("stop", "layout")]
    [InlineData("start", "lease")]
    [InlineData("stop", "lease")]
    [InlineData("start", "component")]
    [InlineData("stop", "component")]
    [InlineData("start", "payload")]
    [InlineData("stop", "payload")]
    public async Task MissingBundleOrPayloadFailsWithoutFallback(string action, string missing)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (services, bundle, factory, interaction, _) = CreateServices(workspace);
        var root = bundle.Layout!.LayoutPath!;
        switch (missing)
        {
            case "layout":
                bundle.Layout = null;
                bundle.CreateLayoutLease = null;
                break;
            case "lease":
                bundle.CreateLayoutLease = null;
                break;
            case "component":
                bundle.Layout.Components.Tray = null;
                break;
            case "payload":
                File.Delete(bundle.Layout.GetTrayPath()!);
                break;
        }
        using var provider = services.BuildServiceProvider();

        Assert.Equal(CliExitCodes.InvalidCommand, await provider.GetRequiredService<RootCommand>()
            .Parse($"tray {action}").InvokeAsync().DefaultTimeout());

        Assert.Equal(missing is "layout" or "lease" ? TrayCommandStrings.BundleRequired : TrayCommandStrings.PayloadMissing,
            Assert.Single(interaction.DisplayedErrors));
        Assert.Empty(factory.CreatedExecutions);
        Assert.False(BundleVersionLease.HasActiveLease(root));
    }

    [Theory]
    [InlineData("start", null)]
    [InlineData("stop", null)]
    [InlineData("start", "")]
    [InlineData("stop", "")]
    [InlineData("start", "  ")]
    [InlineData("stop", "  ")]
    [InlineData("start", "Tray readiness timed out. See logs at /bundle/tray.log. Stop an older Aspire tray and retry.")]
    [InlineData("stop", "The running tray uses an older control protocol. Quit it from the menu bar.")]
    public async Task HelperFailureShowsDiagnosticsPreservesExitCodeAndReleasesLease(string action, string? error)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (services, bundle, factory, interaction, _) = CreateServices(workspace);
        factory.AttemptCallback = (_, options) =>
        {
            if (error is not null)
            {
                options.StandardErrorCallback?.Invoke(error);
            }
            return (42, null);
        };
        using var provider = services.BuildServiceProvider();

        Assert.Equal(42, await provider.GetRequiredService<RootCommand>()
            .Parse($"tray {action}").InvokeAsync().DefaultTimeout());

        var expectedMessage = string.IsNullOrWhiteSpace(error)
            ? string.Format(CultureInfo.CurrentCulture, TrayCommandStrings.HelperFailed, action, 42)
            : string.Format(CultureInfo.CurrentCulture, TrayCommandStrings.OperationFailed, action, error.Trim());
        Assert.Equal(expectedMessage, Assert.Single(interaction.DisplayedErrors));
        Assert.Empty(interaction.DisplayedSuccess);
        Assert.False(BundleVersionLease.HasActiveLease(bundle.Layout!.LayoutPath!));
    }

    [Theory]
    [InlineData("start", true)]
    [InlineData("stop", true)]
    [InlineData("start", false)]
    [InlineData("stop", false)]
    public async Task ExtractionOrProcessStartFailureDoesNotLeakLease(string action, bool extraction)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (services, bundle, factory, interaction, _) = CreateServices(workspace);
        if (extraction)
        {
            bundle.EnsureExtractedException = new IOException("Extraction failed.");
        }
        else
        {
            factory.CreateExecutionCallback = (_, _, _, _) => throw new IOException("Helper launch failed.");
        }
        using var provider = services.BuildServiceProvider();

        Assert.Equal(CliExitCodes.InvalidCommand, await provider.GetRequiredService<RootCommand>()
            .Parse($"tray {action}").InvokeAsync().DefaultTimeout());

        Assert.Single(interaction.DisplayedErrors);
        Assert.Empty(interaction.DisplayedSuccess);
        Assert.False(BundleVersionLease.HasActiveLease(bundle.Layout!.LayoutPath!));
        Assert.Equal(extraction ? 0 : 1, factory.AttemptCount);
    }

    [Theory]
    [InlineData("start", true)]
    [InlineData("stop", true)]
    [InlineData("stop", false)]
    public async Task TimeoutAndCancellationObserveHelperBeforeReleasingLease(string action, bool timeout)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (services, bundle, factory, interaction, _) = CreateServices(workspace);
        var time = new FakeTimeProvider();
        services.AddSingleton<TimeProvider>(time);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helperObserved = false;
        var root = bundle.Layout!.LayoutPath!;
        factory.AsyncAttemptCallback = async (_, _, token) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return (0, null);
            }
            finally
            {
                Assert.True(BundleVersionLease.HasActiveLease(root));
                helperObserved = true;
            }
        };
        using var provider = services.BuildServiceProvider();
        var invocation = provider.GetRequiredService<RootCommand>()
            .Parse($"tray {action}").InvokeAsync(cancellationToken: cancellation.Token);
        await started.Task.DefaultTimeout();
        if (timeout)
        {
            if (action == "start")
            {
                cancellation.Cancel();
            }
            time.Advance(TrayLifecycleService.HelperTimeout);
        }
        else
        {
            cancellation.Cancel();
        }

        Assert.Equal(timeout ? CliExitCodes.WaitTimeout : CliExitCodes.Cancelled, await invocation.DefaultTimeout());
        Assert.True(helperObserved);
        Assert.False(BundleVersionLease.HasActiveLease(root));
        Assert.Equal(1, Assert.IsType<TestProcessExecution>(Assert.Single(factory.CreatedExecutions)).DisposeCount);
        Assert.Empty(interaction.DisplayedSuccess);
        if (timeout)
        {
            Assert.Equal(string.Format(CultureInfo.CurrentCulture, TrayCommandStrings.HelperTimedOut, action), Assert.Single(interaction.DisplayedErrors));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    public async Task StartFinishesLeaseHandoffAfterFirstTerminationSignal(int helperExitCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (services, bundle, factory, interaction, _) = CreateServices(workspace);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = bundle.Layout!.LayoutPath!;
        BundleVersionLease? companionLease = null;
        factory.AsyncAttemptCallback = async (_, _, token) =>
        {
            started.SetResult(token);
            await acknowledge.Task.WaitAsync(token);
            Assert.True(BundleVersionLease.HasActiveLease(root));
            if (helperExitCode == 0)
            {
                companionLease = BundleVersionLease.Acquire(root, "tray", "gui");
            }
            return (helperExitCode, null);
        };
        using var provider = services.BuildServiceProvider();
        var cancellation = provider.GetRequiredService<ConsoleCancellationManager>();
        try
        {
            var invocation = provider.GetRequiredService<RootCommand>()
                .Parse("tray start").InvokeAsync(cancellationToken: cancellation.Token);
            var helperToken = await started.Task.DefaultTimeout();
            cancellation.Cancel(CliExitCodes.Cancelled);

            Assert.True(cancellation.IsEnabled);
            Assert.False(cancellation.GracefulShutdownToken.IsCancellationRequested);
            Assert.False(helperToken.IsCancellationRequested);
            Assert.False(invocation.IsCompleted);
            Assert.True(BundleVersionLease.HasActiveLease(root));
            Assert.Empty(interaction.DisplayedSuccess);

            acknowledge.SetResult();
            Assert.Equal(helperExitCode, await invocation.DefaultTimeout());
            Assert.Equal(helperExitCode == 0, BundleVersionLease.HasActiveLease(root));
            Assert.Equal(1, Assert.IsType<TestProcessExecution>(Assert.Single(factory.CreatedExecutions)).DisposeCount);
        }
        finally
        {
            companionLease?.Dispose();
        }
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    public async Task CancellationBeforeSpawnReleasesLeaseWithoutLaunchingHelper(string action)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (services, bundle, factory, _, _) = CreateServices(workspace);
        using var cancellation = new CancellationTokenSource();
        bundle.EnsureExtractedAndAcquireLayoutAsyncCallback = _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse($"tray {action}").InvokeAsync(cancellationToken: cancellation.Token).DefaultTimeout();

        Assert.Equal(CliExitCodes.Cancelled, exitCode);
        Assert.Empty(factory.CreatedExecutions);
        Assert.False(BundleVersionLease.HasActiveLease(bundle.Layout!.LayoutPath!));
    }

    [Theory]
    [InlineData("tray start --smoke-seconds 5")]
    [InlineData("tray stop --smoke-seconds 5")]
    public async Task TrayCommandsDoNotPassThroughUnrecognizedHelperOptions(string arguments)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (services, bundle, factory, _, _) = CreateServices(workspace);
        using var provider = services.BuildServiceProvider();
        var result = provider.GetRequiredService<RootCommand>().Parse(arguments);

        Assert.NotEmpty(result.Errors);
        Assert.NotEqual(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(0, bundle.AcquireLayoutCallCount);
        Assert.Empty(factory.CreatedExecutions);
    }

    [Fact]
    public async Task TrayHelpDoesNotAcquireBundleOrLaunchHelper()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (services, bundle, factory, _, _) = CreateServices(workspace);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var tray = Assert.IsType<TrayCommand>(Assert.Single(command.Subcommands, c => c.Name == "tray"));
        Assert.Equal(TrayCommandStrings.Description, tray.Description);
        Assert.Collection(tray.Subcommands,
            c => Assert.IsType<TrayStartCommand>(c),
            c => Assert.IsType<TrayStopCommand>(c));

        Assert.Equal(CliExitCodes.Success, await command.Parse("tray --help").InvokeAsync().DefaultTimeout());
        Assert.Equal(0, bundle.AcquireLayoutCallCount);
        Assert.Empty(factory.CreatedExecutions);
    }

    private (IServiceCollection Services, TestBundleService Bundle, TestProcessExecutionFactory Factory, TestInteractionService Interaction, string CliPath) CreateServices(TemporaryWorkspace workspace)
    {
        var root = Path.Combine(workspace.WorkspaceRoot.FullName, "bundle versions", "version-1");
        var layout = new LayoutConfiguration { LayoutPath = root, Components = new LayoutComponents { Tray = LayoutComponents.MacTrayExecutablePath } };
        var trayPath = layout.GetTrayPath()!;
        Directory.CreateDirectory(Path.GetDirectoryName(trayPath)!);
        File.WriteAllText(trayPath, "fake native helper");
        var cliPath = Path.Combine(workspace.WorkspaceRoot.FullName, "invoking aspire");
        File.WriteAllText(cliPath, "fake native CLI");
        var bundle = new TestBundleService(isBundle: true)
        {
            Layout = layout,
            CreateLayoutLease = () => new BundleLayoutLease(layout, BundleVersionLease.Acquire(root, "cli", "tray"))
        };
        var factory = new TestProcessExecutionFactory();
        var interaction = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.BundleServiceFactory = _ => bundle;
            options.ProcessPathProviderFactory = _ => new TestProcessPathProvider(cliPath) { IsNativeAot = true };
            options.DotNetCliExecutionFactoryFactory = _ => factory;
            options.InteractionServiceFactory = _ => interaction;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
        });
        services.AddSingleton<IEnvironment>(TestEnvironment.CreateMacOS());
        return (services, bundle, factory, interaction, cliPath);
    }
}
