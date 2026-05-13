// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Tests.Dcp;

public class ConfigureDefaultDcpOptionsTests
{
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

