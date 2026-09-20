// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.TestServices;

internal static class TerminalCommandTestServices
{
    public static (ServiceProvider Provider, TestAppHostAuxiliaryBackchannel Backchannel) CreateProvider(
        TemporaryWorkspace workspace,
        ITestOutputHelper outputHelper,
        Action<TestAppHostAuxiliaryBackchannel> configure,
        Action<CliServiceCollectionTestOptions>? configureOptions = null)
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj"),
                ProcessId = 1234
            },
            SupportsTerminalsV1 = true
        };
        configure(backchannel);
        monitor.AddConnection("socket.hash1", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.EnabledFeatures = [KnownFeatures.TerminalCommandsEnabled];
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            configureOptions?.Invoke(options);
        });

        return (services.BuildServiceProvider(), backchannel);
    }
}
