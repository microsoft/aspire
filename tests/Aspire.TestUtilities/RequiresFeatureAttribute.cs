// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.Utils;
using Microsoft.DotNet.XUnitExtensions;

namespace Aspire.TestUtilities;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class RequiresFeatureAttribute(TestFeature feature) : Attribute, ITraitAttribute
{
    // Duplicated from Aspire.Shared.KnownContainerRuntimes rather than linked in, because this file is
    // source-included by projects (e.g. Aspire.Playground.Tests) that deploy outside the repo and so
    // cannot pull in files from src/Shared.
    private const string DockerExecutable = "docker";
    private const string PodmanExecutable = "podman";

    private static bool? s_isPlaywrightSupported;
    private static readonly ConcurrentDictionary<string, bool> s_executablesOnPath = new();

    public TestFeature Feature { get; } = feature;

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits()
    {
        if (!IsFeatureSupported(Feature))
        {
            return [new KeyValuePair<string, string>(XunitConstants.Category, "failing")];
        }

        return [];
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

    // Logic from RequiresDockerAttribute, generalized to any container runtime.
    //
    // On CI a hard-coded *expected* value is used so that container-dependent tests *fail* if the runtime
    // is not available/usable in an environment where it is expected to be available. A run-time check
    // would let those tests skip silently, which is not desirable.
    //
    // scenarios:
    // - Windows: assume installed only for *local* runs as containers aren't supported on CI yet
    //                - https://github.com/microsoft/aspire/issues/4291
    // - Linux - Local, or CI: always assume that a container runtime is installed
    //
    // Locally there is no such expectation, so probe PATH instead: a developer machine may have Podman
    // only, Docker only, or neither, and tests should skip rather than fail against a runtime that is
    // not installed. Only PATH is inspected (no process is spawned) because trait attributes are
    // evaluated during discovery, once per test.
    private static bool IsContainerRuntimeSupported()
    {
        if (PlatformDetection.IsRunningOnCI)
        {
            return OperatingSystem.IsLinux(); // non-linux on CI does not support containers
        }

        return IsOnPath(DockerExecutable) || IsOnPath(PodmanExecutable);
    }

    // Same CI expectation as IsContainerRuntimeSupported, but locally requires Docker itself. Used by
    // tests that depend on Docker rather than on containers in general (e.g. the Docker socket).
    private static bool IsDockerSupported()
    {
        if (PlatformDetection.IsRunningOnCI)
        {
            return OperatingSystem.IsLinux();
        }

        return IsOnPath(DockerExecutable);
    }

    // Building an image from a Dockerfile needs the buildx plugin under Docker, which is not available on
    // Azure DevOps build machines or Helix. See: https://github.com/dotnet/dnceng/issues/6232
    // Podman has no such dependency — `podman build` is built into the CLI — so a Podman-only machine
    // satisfies this feature.
    private static bool IsContainerImageBuildSupported()
    {
        if (PlatformDetection.IsRunningOnCI)
        {
            // Deliberately does NOT also require IsContainerRuntimeSupported(): this feature only describes
            // image-build capability, and callers that additionally need a live runtime already combine it
            // with TestFeature.ContainerRuntime. Adding that conjunct here would stop tests which build
            // images purely against fakes from running on GitHub Actions Windows.
            return !PlatformDetection.IsRunningFromAzdo;
        }

        // buildx ships with modern Docker installations, so presence of either CLI is enough locally.
        return IsOnPath(DockerExecutable) || IsOnPath(PodmanExecutable);
    }

    private static bool IsOnPath(string executable)
    {
        // Cached because trait attributes are constructed for every test in the assembly.
        return s_executablesOnPath.GetOrAdd(executable, static e => FileUtil.FindFullPathFromPath(e) is not null);
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
        if ((feature & TestFeature.ContainerRuntime) == TestFeature.ContainerRuntime && !IsContainerRuntimeSupported())
        {
            return false;
        }
        if ((feature & TestFeature.ContainerImageBuild) == TestFeature.ContainerImageBuild && !IsContainerImageBuildSupported())
        {
            return false;
        }
        if ((feature & TestFeature.Docker) == TestFeature.Docker && !IsDockerSupported())
        {
            return false;
        }
        return true;
    }
}
