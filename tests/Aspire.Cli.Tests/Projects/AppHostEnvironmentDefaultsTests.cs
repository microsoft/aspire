// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.Projects;

public class AppHostEnvironmentDefaultsTests
{
    private const string AspNetCoreEnvironmentVariableName = "ASPNETCORE_ENVIRONMENT";

    [Fact]
    public void ApplyEffectiveEnvironment_UsesDefaultWhenNoEnvironmentVariablesAreSet()
    {
        var env = new Dictionary<string, string>();

        AppHostEnvironmentDefaults.ApplyEffectiveEnvironment(env, AppHostEnvironmentDefaults.ProductionEnvironmentName);

        Assert.Equal("Production", env["DOTNET_ENVIRONMENT"]);
        Assert.False(env.ContainsKey(AspNetCoreEnvironmentVariableName));
    }

    [Fact]
    public void ApplyEffectiveEnvironment_DotnetEnvironmentTakesPrecedenceOverAspireEnvironment()
    {
        var env = new Dictionary<string, string>
        {
            [AppHostEnvironmentDefaults.DotNetEnvironmentVariableName] = "Production",
            [AppHostEnvironmentDefaults.AspireEnvironmentVariableName] = "Staging"
        };

        AppHostEnvironmentDefaults.ApplyEffectiveEnvironment(env, AppHostEnvironmentDefaults.DevelopmentEnvironmentName);

        Assert.Equal("Production", env["DOTNET_ENVIRONMENT"]);
        Assert.False(env.ContainsKey(AspNetCoreEnvironmentVariableName));
        Assert.Equal("Staging", env["ASPIRE_ENVIRONMENT"]);
    }

    [Fact]
    public void ApplyEffectiveEnvironment_EnvironmentArgumentTakesPrecedenceOverEnvironmentVariables()
    {
        var env = new Dictionary<string, string>
        {
            [AppHostEnvironmentDefaults.DotNetEnvironmentVariableName] = "Production",
            [AppHostEnvironmentDefaults.AspireEnvironmentVariableName] = "Development"
        };

        AppHostEnvironmentDefaults.ApplyEffectiveEnvironment(
            env,
            AppHostEnvironmentDefaults.DevelopmentEnvironmentName,
            args: ["--environment", "Staging"]);

        Assert.Equal("Staging", env["DOTNET_ENVIRONMENT"]);
        Assert.False(env.ContainsKey(AspNetCoreEnvironmentVariableName));
        Assert.Equal("Development", env["ASPIRE_ENVIRONMENT"]);
    }

    [Fact]
    public void ApplyEffectiveEnvironment_AspireEnvironmentTakesPrecedenceOverAspNetCoreEnvironment()
    {
        var env = new Dictionary<string, string>
        {
            [AppHostEnvironmentDefaults.AspireEnvironmentVariableName] = "Testing",
            [AspNetCoreEnvironmentVariableName] = "Staging"
        };

        AppHostEnvironmentDefaults.ApplyEffectiveEnvironment(env, AppHostEnvironmentDefaults.DevelopmentEnvironmentName);

        Assert.Equal("Testing", env["DOTNET_ENVIRONMENT"]);
        Assert.Equal("Staging", env["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("Testing", env["ASPIRE_ENVIRONMENT"]);
    }

    [Fact]
    public void ApplyEffectiveEnvironment_UsesInheritedAspireEnvironmentWhenContextDoesNotSetOne()
    {
        var env = new Dictionary<string, string>();
        var inherited = new Dictionary<string, string?>
        {
            [AppHostEnvironmentDefaults.AspireEnvironmentVariableName] = "Staging"
        };

        AppHostEnvironmentDefaults.ApplyEffectiveEnvironment(
            env,
            AppHostEnvironmentDefaults.ProductionEnvironmentName,
            inherited);

        Assert.Equal("Staging", env["DOTNET_ENVIRONMENT"]);
        Assert.False(env.ContainsKey(AspNetCoreEnvironmentVariableName));
    }
}
