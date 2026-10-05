// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Hosting;

namespace Aspire.Cli.Tests.Projects;

public class AppHostEnvironmentDefaultsTests
{
    [Fact]
    public void ApplyEffectiveEnvironment_UsesDefaultWhenNoEnvironmentVariablesAreSet()
    {
        var env = new Dictionary<string, string>();

        AppHostEnvironmentDefaults.ApplyEffectiveEnvironment(env, AppHostEnvironmentDefaults.ProductionEnvironmentName);

        Assert.Equal("Production", env[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.False(env.ContainsKey(KnownAspNetCoreConfigNames.Environment));
    }

    [Fact]
    public void ApplyEffectiveEnvironment_DotnetEnvironmentTakesPrecedenceOverAspireEnvironment()
    {
        var env = new Dictionary<string, string>
        {
            [KnownAspNetCoreConfigNames.DotNetEnvironment] = "Production",
            [AppHostEnvironmentDefaults.AspireEnvironmentVariableName] = "Staging"
        };

        AppHostEnvironmentDefaults.ApplyEffectiveEnvironment(env, AppHostEnvironmentDefaults.DevelopmentEnvironmentName);

        Assert.Equal("Production", env[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.False(env.ContainsKey(KnownAspNetCoreConfigNames.Environment));
        Assert.Equal("Staging", env["ASPIRE_ENVIRONMENT"]);
    }

    [Fact]
    public void ApplyEffectiveEnvironment_EnvironmentArgumentTakesPrecedenceOverEnvironmentVariables()
    {
        var env = new Dictionary<string, string>
        {
            [KnownAspNetCoreConfigNames.DotNetEnvironment] = "Production",
            [AppHostEnvironmentDefaults.AspireEnvironmentVariableName] = "Development"
        };

        AppHostEnvironmentDefaults.ApplyEffectiveEnvironment(
            env,
            AppHostEnvironmentDefaults.DevelopmentEnvironmentName,
            args: ["--environment", "Staging"]);

        Assert.Equal("Staging", env[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.False(env.ContainsKey(KnownAspNetCoreConfigNames.Environment));
        Assert.Equal("Development", env["ASPIRE_ENVIRONMENT"]);
    }

    [Fact]
    public void ApplyEffectiveEnvironment_AspireEnvironmentTakesPrecedenceOverAspNetCoreEnvironment()
    {
        var env = new Dictionary<string, string>
        {
            [AppHostEnvironmentDefaults.AspireEnvironmentVariableName] = "Testing",
            [KnownAspNetCoreConfigNames.Environment] = "Staging"
        };

        AppHostEnvironmentDefaults.ApplyEffectiveEnvironment(env, AppHostEnvironmentDefaults.DevelopmentEnvironmentName);

        Assert.Equal("Testing", env[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.Equal("Staging", env[KnownAspNetCoreConfigNames.Environment]);
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

        Assert.Equal("Staging", env[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.False(env.ContainsKey(KnownAspNetCoreConfigNames.Environment));
    }
}
