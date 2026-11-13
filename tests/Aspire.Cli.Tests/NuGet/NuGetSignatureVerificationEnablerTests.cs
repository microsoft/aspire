// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.DotNet.RemoteExecutor;

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

    // The ApplyToCurrentProcess tests change the real process environment and a process-wide scope count, so each
    // one runs in its own process. The variable is also set inside the child, because passing it through the child's
    // start info alone was not reliable when other test classes ran alongside.

    [Fact]
    public void ApplyToCurrentProcess_RestoresUnsetValueWhenScopeEnds()
    {
        RemoteExecutor.Invoke(static () =>
        {
            Environment.SetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification, null);

            using (NuGetSignatureVerificationEnabler.ApplyToCurrentProcess(new TestFeatures(), TestEnvironment.CreateLinux()))
            {
                Assert.Equal("True", Environment.GetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification));
            }

            Assert.Null(Environment.GetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification));
        }).Dispose();
    }

    [Fact]
    public void ApplyToCurrentProcess_PreservesExplicitFalse()
    {
        RemoteExecutor.Invoke(static () =>
        {
            Environment.SetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification, "false");
            var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
            {
                [NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification] = "false"
            });

            using (NuGetSignatureVerificationEnabler.ApplyToCurrentProcess(new TestFeatures(), environment))
            {
                Assert.Equal("False", Environment.GetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification));
            }

            // The user's original spelling comes back, not the normalized value applied during the scope.
            Assert.Equal("false", Environment.GetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification));
        }).Dispose();
    }

    [Fact]
    public void ApplyToCurrentProcess_RestoresPreviousValueWhenScopeEnds()
    {
        RemoteExecutor.Invoke(static () =>
        {
            Environment.SetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification, "not-a-bool");
            var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
            {
                [NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification] = "not-a-bool"
            });

            using (NuGetSignatureVerificationEnabler.ApplyToCurrentProcess(new TestFeatures(), environment))
            {
                Assert.Equal("True", Environment.GetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification));
            }

            Assert.Equal("not-a-bool", Environment.GetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification));
        }).Dispose();
    }

    [Fact]
    public void ApplyToCurrentProcess_KeepsValueUntilLastOverlappingScopeEnds()
    {
        RemoteExecutor.Invoke(static () =>
        {
            Environment.SetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification, null);
            var features = new TestFeatures();
            var environment = TestEnvironment.CreateLinux();

            var first = NuGetSignatureVerificationEnabler.ApplyToCurrentProcess(features, environment);
            var second = NuGetSignatureVerificationEnabler.ApplyToCurrentProcess(features, environment);

            first.Dispose();
            Assert.Equal("True", Environment.GetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification));

            second.Dispose();
            Assert.Null(Environment.GetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification));
        }).Dispose();
    }

    [Fact]
    public void ApplyToCurrentProcess_IgnoresRepeatedDisposal()
    {
        RemoteExecutor.Invoke(static () =>
        {
            Environment.SetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification, null);
            var features = new TestFeatures();
            var environment = TestEnvironment.CreateLinux();

            var first = NuGetSignatureVerificationEnabler.ApplyToCurrentProcess(features, environment);
            var second = NuGetSignatureVerificationEnabler.ApplyToCurrentProcess(features, environment);

            // Disposing one scope twice must not end the other scope early.
            first.Dispose();
            first.Dispose();
            Assert.Equal("True", Environment.GetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification));

            second.Dispose();
            Assert.Null(Environment.GetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification));
        }).Dispose();
    }

    [Fact]
    public void ApplyToCurrentProcess_FeatureDisabled_LeavesEnvironmentUnchanged()
    {
        RemoteExecutor.Invoke(static () =>
        {
            Environment.SetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification, null);
            var features = new TestFeatures().SetFeature(KnownFeatures.NuGetSignatureVerificationEnabled, false);

            using (NuGetSignatureVerificationEnabler.ApplyToCurrentProcess(features, TestEnvironment.CreateLinux()))
            {
                Assert.Null(Environment.GetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification));
            }

            Assert.Null(Environment.GetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification));
        }).Dispose();
    }
}
