// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.RemoteHost.Ats;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class RemoteHostServerTests
{
    [Fact]
    public async Task HandleRegistry_IsSharedAcrossGuestAndIntegrationHostScopes()
    {
        var builder = RemoteHostServer.CreateBuilder([]);
        using var host = builder.Build();
        var resource = new object();
        string handleId;

        await using (var guestScope = host.Services.CreateAsyncScope())
        {
            var guestHandles = guestScope.ServiceProvider.GetRequiredService<HandleRegistry>();
            handleId = guestHandles.Register(resource, "test/Resource");
        }

        await using var integrationScope = host.Services.CreateAsyncScope();
        var integrationHandles = integrationScope.ServiceProvider.GetRequiredService<HandleRegistry>();
        Assert.Same(resource, integrationHandles.GetObject(handleId));
    }

    [Fact]
    public void AppHostLogLevelOverridesConfiguredDefaultLogLevel()
    {
        var builder = RemoteHostServer.CreateBuilder([
            "Logging:LogLevel:Default=Information",
            "ASPIRE_APPHOST_LOGLEVEL=Trace"
        ]);

        using var host = builder.Build();

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RemoteHostLogLevelTest");
        Assert.True(logger.IsEnabled(LogLevel.Trace));
    }
}