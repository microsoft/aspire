// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Tests.Dcp;

public class ConfigureDefaultDcpOptionsTests
{
    [Fact]
    public void KubernetesApiTimeoutDefaultsToFortySecondsWithTwentyAdditionalInitializationSeconds()
    {
        var options = ConfigureWithDcpPublisher([]);

        Assert.Equal(TimeSpan.FromSeconds(20), options.KubernetesInitializationAdditionalTimeout);
        Assert.Equal(TimeSpan.FromSeconds(40), options.KubernetesApiTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), options.KubernetesApiTimeout + options.KubernetesInitializationAdditionalTimeout);
    }

    [Fact]
    public void KubernetesApiTimeoutsCanBeConfigured()
    {
        var options = ConfigureWithDcpPublisher(new()
        {
            ["DcpPublisher:KubernetesInitializationAdditionalTimeout"] = "00:00:30",
            ["DcpPublisher:KubernetesApiTimeout"] = "00:00:50",
        });

        Assert.Equal(TimeSpan.FromSeconds(30), options.KubernetesInitializationAdditionalTimeout);
        Assert.Equal(TimeSpan.FromSeconds(50), options.KubernetesApiTimeout);
        Assert.Equal(TimeSpan.FromSeconds(80), options.KubernetesApiTimeout + options.KubernetesInitializationAdditionalTimeout);
    }

    [Fact]
    public void KubernetesApiTimeoutOverrideAlsoIncreasesInitializationBudget()
    {
        var options = ConfigureWithDcpPublisher(new()
        {
            ["DcpPublisher:KubernetesApiTimeout"] = "00:00:50",
        });

        Assert.Equal(TimeSpan.FromSeconds(20), options.KubernetesInitializationAdditionalTimeout);
        Assert.Equal(TimeSpan.FromSeconds(70), options.KubernetesApiTimeout + options.KubernetesInitializationAdditionalTimeout);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    [InlineData("00:00:00.999")]
    [InlineData("00:10:00.001")]
    [InlineData("00:00:00.010")]
    [InlineData("1.00:00:00")]
    public void KubernetesApiTimeoutMustBeWithinSupportedRange(string configuredTimeout)
    {
        var options = ConfigureWithDcpPublisher(new()
        {
            ["DcpPublisher:KubernetesApiTimeout"] = configuredTimeout,
        });
        var validator = new ValidateDcpOptions(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Equal("Property KubernetesApiTimeout: The Kubernetes API timeout must be between one second and ten minutes.", Assert.Single(result.Failures!));
    }

    [Theory]
    [InlineData("00:00:01", 1000)]
    [InlineData("00:10:00", 600000)]
    public void KubernetesApiTimeoutAcceptsSupportedBoundaries(string configuredTimeout, int expectedMilliseconds)
    {
        var options = ConfigureWithDcpPublisher(new()
        {
            ["DcpPublisher:KubernetesInitializationAdditionalTimeout"] = "00:00:00",
            ["DcpPublisher:KubernetesApiTimeout"] = configuredTimeout,
        });
        var validator = new ValidateDcpOptions(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));

        var result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
        Assert.Equal(TimeSpan.Zero, options.KubernetesInitializationAdditionalTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), options.KubernetesApiTimeout);
    }

    [Theory]
    [InlineData("00:00:40", "00:00:00", 40000)]
    [InlineData("00:00:40", "00:09:20", 600000)]
    [InlineData("00:00:01", "00:09:59", 600000)]
    public void KubernetesInitializationAdditionalTimeoutAcceptsSupportedBoundaries(string apiTimeout, string additionalTimeout, int expectedMilliseconds)
    {
        var options = ConfigureWithDcpPublisher(new()
        {
            ["DcpPublisher:KubernetesApiTimeout"] = apiTimeout,
            ["DcpPublisher:KubernetesInitializationAdditionalTimeout"] = additionalTimeout,
        });
        var validator = new ValidateDcpOptions(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));

        var result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), options.KubernetesApiTimeout + options.KubernetesInitializationAdditionalTimeout);
    }

    [Theory]
    [InlineData("-00:00:00.001")]
    [InlineData("-00:00:01")]
    public void KubernetesInitializationAdditionalTimeoutMustBeNonNegative(string configuredTimeout)
    {
        var options = ConfigureWithDcpPublisher(new()
        {
            ["DcpPublisher:KubernetesInitializationAdditionalTimeout"] = configuredTimeout,
        });
        var validator = new ValidateDcpOptions(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Equal("Property KubernetesInitializationAdditionalTimeout: The Kubernetes additional initialization timeout must be non-negative.", Assert.Single(result.Failures!));
    }

    [Theory]
    [InlineData("00:00:40", "00:09:20.001")]
    [InlineData("00:10:00", "00:00:00.001")]
    [InlineData("00:00:01", "00:10:00")]
    public void KubernetesInitializationBudgetMustNotExceedTenMinutes(string apiTimeout, string additionalTimeout)
    {
        var options = ConfigureWithDcpPublisher(new()
        {
            ["DcpPublisher:KubernetesApiTimeout"] = apiTimeout,
            ["DcpPublisher:KubernetesInitializationAdditionalTimeout"] = additionalTimeout,
        });
        var validator = new ValidateDcpOptions(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Equal("Property KubernetesInitializationAdditionalTimeout: The combined Kubernetes API and additional initialization timeouts must not exceed ten minutes.", Assert.Single(result.Failures!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KubernetesInitializationBudgetValidationDoesNotOverflow(bool apiTimeoutAlsoOverflows)
    {
        var options = ConfigureWithDcpPublisher([]);
        options.KubernetesInitializationAdditionalTimeout = TimeSpan.MaxValue;
        if (apiTimeoutAlsoOverflows)
        {
            options.KubernetesApiTimeout = TimeSpan.MaxValue;
        }
        var validator = new ValidateDcpOptions(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Equal(apiTimeoutAlsoOverflows
            ? "Property KubernetesApiTimeout: The Kubernetes API timeout must be between one second and ten minutes."
            : "Property KubernetesInitializationAdditionalTimeout: The combined Kubernetes API and additional initialization timeouts must not exceed ten minutes.",
            Assert.Single(result.Failures!));
    }

    [Fact]
    public void KubernetesCreateRecoveryTimeoutDefaultsToTwoMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), ConfigureWithDcpPublisher([]).KubernetesCreateRecoveryTimeout);
    }

    [Fact]
    public void KubernetesCreateRecoveryTimeoutCanBeConfigured()
    {
        var options = ConfigureWithDcpPublisher(new()
        {
            ["DcpPublisher:KubernetesCreateRecoveryTimeout"] = "00:03:00",
        });

        Assert.Equal(TimeSpan.FromMinutes(3), options.KubernetesCreateRecoveryTimeout);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    [InlineData("00:00:00.999")]
    [InlineData("00:10:00.001")]
    [InlineData("00:00:00.010")]
    [InlineData("1.00:00:00")]
    public void KubernetesCreateRecoveryTimeoutMustBeWithinSupportedRange(string configuredTimeout)
    {
        var options = ConfigureWithDcpPublisher(new()
        {
            ["DcpPublisher:KubernetesCreateRecoveryTimeout"] = configuredTimeout,
        });
        var validator = new ValidateDcpOptions(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Equal("Property KubernetesCreateRecoveryTimeout: The Kubernetes create recovery timeout must be between one second and ten minutes.", Assert.Single(result.Failures!));
    }

    [Theory]
    [InlineData("00:00:01", 1000)]
    [InlineData("00:10:00", 600000)]
    public void KubernetesCreateRecoveryTimeoutAcceptsSupportedBoundaries(string configuredTimeout, int expectedMilliseconds)
    {
        var options = ConfigureWithDcpPublisher(new()
        {
            ["DcpPublisher:KubernetesCreateRecoveryTimeout"] = configuredTimeout,
        });
        var validator = new ValidateDcpOptions(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));

        var result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), options.KubernetesCreateRecoveryTimeout);
    }

    [Fact]
    public void TerminalHostFallsBackToAspireManagedDashboardPath()
    {
        // The CLI bundle launcher (PrebuiltAppHostServer/DotNetAppHostProject) sets
        // ASPIRE_DASHBOARD_PATH to point at the multi-mode aspire-managed exe but doesn't
        // set ASPIRE_TERMINAL_HOST_PATH. This regression test pins the fallback that
        // reuses the aspire-managed binary as the terminal host with "terminalhost" as
        // its dispatcher arg so .WithTerminal() works in TS-based and other prebuilt
        // AppHost scenarios.
        var managedExe = OperatingSystem.IsWindows() ? "aspire-managed.exe" : "aspire-managed";
        var managedPath = Path.Combine(Path.GetTempPath(), "aspire-fake-bundle", "managed", managedExe);

        var options = ConfigureWithDcpPublisher(new()
        {
            ["DcpPublisher:DashboardPath"] = managedPath,
        });

        Assert.Equal(managedPath, options.DashboardPath);
        Assert.Equal(managedPath, options.TerminalHostPath);
        Assert.Equal("terminalhost", options.TerminalHostInvocationArgs);
    }
    [Fact]
    public void TerminalHostFallbackDoesNotApplyWhenDashboardPathIsNotAspireManaged()
    {
        // Standalone NuGet-package scenario: DashboardPath points at a per-RID dashboard
        // binary. The fallback must not hijack TerminalHostPath in that case (the standalone
        // terminal host nupkg supplies its own aspireterminalhostpath assembly metadata).
        var dashboardDll = Path.Combine(Path.GetTempPath(), "fake-dashboard", "Aspire.Dashboard.dll");

        var options = ConfigureWithDcpPublisher(new()
        {
            ["DcpPublisher:DashboardPath"] = dashboardDll,
            // Neutralize any ambient aspireterminalhostpath assembly metadata in the test
            // assembly so we can observe the fallback's null behaviour cleanly.
            ["DcpPublisher:TerminalHostPath"] = "  ",
        });

        Assert.Equal(dashboardDll, options.DashboardPath);
        // Either the explicit whitespace value falls through to assembly metadata (dev-only,
        // varies by environment) or stays empty. The point is the fallback didn't fire — i.e.
        // TerminalHostPath !== DashboardPath and InvocationArgs wasn't auto-set to "terminalhost".
        Assert.NotEqual(dashboardDll, options.TerminalHostPath);
        Assert.NotEqual("terminalhost", options.TerminalHostInvocationArgs);
    }

    [Fact]
    public void TerminalHostFallbackDoesNotOverrideExplicitTerminalHostPath()
    {
        // If both paths are explicitly set (e.g. an integrator points at a separate
        // terminal host binary while still using bundled aspire-managed for the dashboard),
        // honour the explicit TerminalHostPath and don't auto-rewrite it.
        var managedExe = OperatingSystem.IsWindows() ? "aspire-managed.exe" : "aspire-managed";
        var managedPath = Path.Combine(Path.GetTempPath(), "aspire-fake-bundle", "managed", managedExe);
        var explicitTerminalHostPath = Path.Combine(Path.GetTempPath(), "custom-terminalhost", "Aspire.TerminalHost.exe");

        var options = ConfigureWithDcpPublisher(new()
        {
            ["DcpPublisher:DashboardPath"] = managedPath,
            ["DcpPublisher:TerminalHostPath"] = explicitTerminalHostPath,
        });

        Assert.Equal(managedPath, options.DashboardPath);
        Assert.Equal(explicitTerminalHostPath, options.TerminalHostPath);
        // InvocationArgs left untouched (the explicit path may or may not be aspire-managed;
        // the consumer is responsible for supplying its own dispatcher arg if needed).
        Assert.NotEqual("terminalhost", options.TerminalHostInvocationArgs);
    }

    [Fact]
    public void TerminalHostFallbackHonoursExplicitInvocationArgs()
    {
        // If the consumer explicitly sets the InvocationArgs, the bundle fallback must NOT
        // clobber it — the explicit value wins even when we synthesise the path from the
        // dashboard.
        var managedExe = OperatingSystem.IsWindows() ? "aspire-managed.exe" : "aspire-managed";
        var managedPath = Path.Combine(Path.GetTempPath(), "aspire-fake-bundle", "managed", managedExe);

        var options = ConfigureWithDcpPublisher(new()
        {
            ["DcpPublisher:DashboardPath"] = managedPath,
            ["DcpPublisher:TerminalHostInvocationArgs"] = "custom-subcommand",
        });

        Assert.Equal(managedPath, options.TerminalHostPath);
        Assert.Equal("custom-subcommand", options.TerminalHostInvocationArgs);
    }

    private static DcpOptions ConfigureWithDcpPublisher(Dictionary<string, string?> dcpPublisherSettings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(dcpPublisherSettings)
            .Build();

        // Point AssemblyName at Aspire.Hosting itself so the resolver picks up an assembly
        // that has no aspire{dashboard,terminalhost}path metadata — without this, the test
        // runner's own assembly metadata (added by the build for inner-loop dev) leaks in
        // and short-circuits the explicit configuration.
        var appOptions = new DistributedApplicationOptions
        {
            AssemblyName = typeof(DcpOptions).Assembly.GetName().Name,
        };
        var configurer = new ConfigureDefaultDcpOptions(appOptions, configuration);
        var options = new DcpOptions();
        configurer.Configure(options);

        return options;
    }
}
