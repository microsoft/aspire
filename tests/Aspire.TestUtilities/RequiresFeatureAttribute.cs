// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.Utils;
using Microsoft.DotNet.XUnitExtensions;

namespace Aspire.TestUtilities;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class RequiresFeatureAttribute(TestFeature feature) : Attribute, ITraitAttribute
{
    private static bool? s_isPlaywrightSupported;

    public TestFeature Feature { get; } = feature;

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits()
    {
        if (!IsSupported())
        {
            return [new KeyValuePair<string, string>(XunitConstants.Category, "failing")];
        }

        return [];
    }

    private bool IsSupported()
    {
        // Check if ALL specified features are supported
        if ((Feature & TestFeature.SSLCertificate) == TestFeature.SSLCertificate && !IsSslCertificateSupported())
        {
            return false;
        }
        if ((Feature & TestFeature.Playwright) == TestFeature.Playwright && !IsPlaywrightSupported())
        {
            return false;
        }
        if ((Feature & TestFeature.DevCert) == TestFeature.DevCert && !IsDevCertSupported())
        {
            return false;
        }
        if ((Feature & TestFeature.Docker) == TestFeature.Docker && !IsDockerSupported())
        {
            return false;
        }
        if ((Feature & TestFeature.DockerPluginBuildx) == TestFeature.DockerPluginBuildx && !IsDockerPluginBuildxSupported())
        {
            return false;
        }
        return true;
    }

    // Logic from RequiresSSLCertificateAttribute
    // Always supported on linux (local and CI), but only local otherwise
    private static bool IsSslCertificateSupported()
    {
        return OperatingSystem.IsLinux() || !PlatformDetection.IsRunningOnCI;
    }

    // Logic from RequiresPlaywrightAttribute
    // This property is `true` when Playwright is expected to be installed on the machine.
    //
    // A hard-coded *expected* value is used here to ensure that CI can skip entire
    // jobs (one per test class) when Playwright is not available.
    //
    // Currently this is not supported on Linux agents on helix, and azdo build machines
    // https://github.com/microsoft/aspire/issues/4921
    private static bool IsPlaywrightSupported()
    {
        s_isPlaywrightSupported ??= GetIsPlaywrightSupported();
        return s_isPlaywrightSupported.Value;
    }

    private static bool GetIsPlaywrightSupported()
    {
        // Setting PLAYWRIGHT_INSTALLED environment variable takes precedence
        if (Environment.GetEnvironmentVariable("PLAYWRIGHT_INSTALLED") is var playwrightInstalled && !string.IsNullOrEmpty(playwrightInstalled))
        {
            if (bool.TryParse(playwrightInstalled, out var isInstalled))
            {
                return isInstalled;
            }
        }

        return !PlatformDetection.IsRunningOnCI // Supported on local runs
            || !OperatingSystem.IsLinux() // always supported on !linux on CI
            || PlatformDetection.IsRunningOnGithubActions;
    }

    // Logic from RequiresDevCertAttribute
    // Returns true if a valid ASP.NET Core development certificate is found in the current user's certificate store.
    private static bool IsDevCertSupported()
    {
        return DevCertInStore();
    }

    private static bool DevCertInStore()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates
            .Where(c => c.IsAspNetCoreDevelopmentCertificate())
            .Where(c => c.NotAfter > DateTime.UtcNow)
            .OrderByDescending(c => c.GetCertificateVersion())
            .ThenByDescending(c => c.NotAfter)
            .Any();
    }

    // Logic from RequiresDockerAttribute
    // This property is `true` when docker is *expected* to be available.
    //
    // A hard-coded *expected* value is used here to ensure that docker
    // dependent tests *fail* if docker is not available/usable in an environment
    // where it is expected to be available. A run-time check would allow tests
    // to fail silently, which is not desirable.
    //
    // scenarios:
    // - Windows: assume installed only for *local* runs as docker isn't supported on CI yet
    //                - https://github.com/microsoft/aspire/issues/4291
    // - Linux - Local, or CI: always assume that docker is installed
    private static bool IsDockerSupported()
    {
        return OperatingSystem.IsLinux() || !PlatformDetection.IsRunningOnCI; // non-linux on CI does not support docker
    }

    // Logic for DockerPluginBuildx
    // The Docker buildx plugin is not available on Azure DevOps build machines or Helix.
    // See: https://github.com/dotnet/dnceng/issues/6232
    private static bool IsDockerPluginBuildxSupported()
    {
        return !PlatformDetection.IsRunningFromAzdo;
    }

    /// <summary>
    /// Helper method to check if a specific feature is supported. Used for programmatic checks in test code.
    /// </summary>
    public static bool IsFeatureSupported(TestFeature feature)
    {
        // Check if ALL specified features are supported
        if ((feature & TestFeature.SSLCertificate) == TestFeature.SSLCertificate && !IsSslCertificateSupported())
        {
            return false;
        }
        if ((feature & TestFeature.Playwright) == TestFeature.Playwright && !IsPlaywrightSupported())
        {
            return false;
        }
        if ((feature & TestFeature.DevCert) == TestFeature.DevCert && !IsDevCertSupported())
        {
            return false;
        }
        if ((feature & TestFeature.Docker) == TestFeature.Docker && !IsDockerSupported())
        {
            return false;
        }
        if ((feature & TestFeature.DockerPluginBuildx) == TestFeature.DockerPluginBuildx && !IsDockerPluginBuildxSupported())
        {
            return false;
        }
        return true;
    }
}
