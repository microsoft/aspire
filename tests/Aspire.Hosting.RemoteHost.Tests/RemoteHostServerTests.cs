// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class RemoteHostServerTests
{
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