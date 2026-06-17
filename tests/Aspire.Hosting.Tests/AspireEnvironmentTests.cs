// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class AspireEnvironmentTests
{
    [Fact]
    public void AspireEnvironmentSetsBuilderEnvironment()
    {
        var options = CreateEnvironmentOptions(aspireEnvironment: "Staging");

        RemoteExecutor.Invoke(static () =>
        {
            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true });
            Assert.Equal("Staging", builder.Environment.EnvironmentName);
        }, options).Dispose();
    }

    [Fact]
    public void DotnetEnvironmentTakesPrecedenceOverAspireEnvironment()
    {
        var options = CreateEnvironmentOptions(aspireEnvironment: "Staging", dotnetEnvironment: "Production");

        RemoteExecutor.Invoke(static () =>
        {
            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true });
            Assert.Equal("Production", builder.Environment.EnvironmentName);
        }, options).Dispose();
    }

    [Fact]
    public void DotnetEnvironmentTakesPrecedenceOverAspNetCoreEnvironment()
    {
        var options = CreateEnvironmentOptions(dotnetEnvironment: "Production", aspNetCoreEnvironment: "Staging");

        RemoteExecutor.Invoke(static () =>
        {
            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true });
            Assert.Equal("Production", builder.Environment.EnvironmentName);
        }, options).Dispose();
    }

    [Fact]
    public void AspireEnvironmentTakesPrecedenceOverAspNetCoreEnvironment()
    {
        var options = CreateEnvironmentOptions(aspireEnvironment: "Testing", aspNetCoreEnvironment: "Staging");

        RemoteExecutor.Invoke(static () =>
        {
            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true });
            Assert.Equal("Testing", builder.Environment.EnvironmentName);
        }, options).Dispose();
    }

    [Fact]
    public void AspNetCoreEnvironmentDoesNotSetBuilderEnvironment()
    {
        var options = CreateEnvironmentOptions(aspNetCoreEnvironment: "Staging");

        RemoteExecutor.Invoke(static () =>
        {
            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true });
            Assert.Equal("Production", builder.Environment.EnvironmentName);
        }, options).Dispose();
    }

    [Fact]
    public void EnvironmentFlagTakesPrecedenceOverAspireEnvironment()
    {
        var options = CreateEnvironmentOptions(aspireEnvironment: "Staging");

        RemoteExecutor.Invoke(static () =>
        {
            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
            {
                DisableDashboard = true,
                Args = ["--environment", "Production"]
            });
            Assert.Equal("Production", builder.Environment.EnvironmentName);
        }, options).Dispose();
    }

    [Fact]
    public void DefaultEnvironmentIsProductionWithNoEnvVars()
    {
        var options = CreateEnvironmentOptions();

        RemoteExecutor.Invoke(static () =>
        {
            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true });
            Assert.Equal("Production", builder.Environment.EnvironmentName);
        }, options).Dispose();
    }

    [Fact]
    public void AspireEnvironmentSetsCustomEnvironmentName()
    {
        var options = CreateEnvironmentOptions(aspireEnvironment: "Testing");

        RemoteExecutor.Invoke(static () =>
        {
            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true });
            Assert.Equal("Testing", builder.Environment.EnvironmentName);
            Assert.False(builder.Environment.IsDevelopment());
            Assert.False(builder.Environment.IsProduction());
            Assert.True(builder.Environment.IsEnvironment("Testing"));
        }, options).Dispose();
    }

    private static RemoteInvokeOptions CreateEnvironmentOptions(
        string? aspireEnvironment = null,
        string? dotnetEnvironment = null,
        string? aspNetCoreEnvironment = null)
    {
        var options = new RemoteInvokeOptions();

        if (aspireEnvironment is not null)
        {
            options.StartInfo.Environment["ASPIRE_ENVIRONMENT"] = aspireEnvironment;
        }
        else
        {
            options.StartInfo.Environment.Remove("ASPIRE_ENVIRONMENT");
        }

        if (dotnetEnvironment is not null)
        {
            options.StartInfo.Environment["DOTNET_ENVIRONMENT"] = dotnetEnvironment;
        }
        else
        {
            options.StartInfo.Environment.Remove("DOTNET_ENVIRONMENT");
        }

        if (aspNetCoreEnvironment is not null)
        {
            options.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = aspNetCoreEnvironment;
        }
        else
        {
            options.StartInfo.Environment.Remove("ASPNETCORE_ENVIRONMENT");
        }

        return options;
    }
}
