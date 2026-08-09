// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;

namespace Aspire.Cli.Tests.NuGet;

public class NuGetSignatureVerificationEnablerTests
{
    private static IEnvironment CreateEnvironment(Dictionary<string, string?>? envVars = null)
    {
        return new TestEnvironment(envVars);
    }

    [Fact]
    public void Apply_FeatureFlagDisabled_DoesNotSetEnvVar()
    {
        var envVars = new Dictionary<string, string>();
        var features = new TestFeatures().SetFeature(KnownFeatures.NuGetSignatureVerificationEnabled, false);
        var environment = CreateEnvironment();

        NuGetSignatureVerificationEnabler.Apply(envVars, features, environment);

        Assert.Empty(envVars);
    }

    [Fact]
    public void Apply_FeatureEnabled_NoUserOverride_SetsTrueOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var envVars = new Dictionary<string, string>();
        var features = new TestFeatures(); // default is true for this feature
        var environment = CreateEnvironment(new Dictionary<string, string?>()); // empty env — no user override

        NuGetSignatureVerificationEnabler.Apply(envVars, features, environment);

        Assert.True(envVars.ContainsKey(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification));
        Assert.Equal("True", envVars[NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification]);
    }

    [Fact]
    public void Apply_FeatureEnabled_UserOverrideFalse_PropagatesFalseOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var envVars = new Dictionary<string, string>();
        var features = new TestFeatures();
        var environment = CreateEnvironment(new Dictionary<string, string?>
        {
            [NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification] = "false"
        });

        NuGetSignatureVerificationEnabler.Apply(envVars, features, environment);

        Assert.True(envVars.ContainsKey(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification));
        Assert.Equal("False", envVars[NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification]);
    }

    [Fact]
    public void Apply_FeatureEnabled_UserOverrideTrue_SetsTrueOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var envVars = new Dictionary<string, string>();
        var features = new TestFeatures();
        var environment = CreateEnvironment(new Dictionary<string, string?>
        {
            [NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification] = "true"
        });

        NuGetSignatureVerificationEnabler.Apply(envVars, features, environment);

        Assert.True(envVars.ContainsKey(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification));
        Assert.Equal("True", envVars[NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification]);
    }

    [Fact]
    public void Apply_NonLinux_DoesNotSetEnvVar()
    {
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        var envVars = new Dictionary<string, string>();
        var features = new TestFeatures(); // default is true
        var environment = CreateEnvironment();

        NuGetSignatureVerificationEnabler.Apply(envVars, features, environment);

        Assert.Empty(envVars);
    }
}
