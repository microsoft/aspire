// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// Creates bundle readers using the same configuration as the Aspire asset source.
/// </summary>
internal static class TestBundleProviderFactory
{
    public static AspireSkillsBundleProvider CreateSkills()
    {
        // physical-binary-version-by-design (see docs/specs/cli-identity-sidecar.md):
        // these fixtures use the build identity; the production source uses CliExecutionContext.
        var version = VersionHelper.GetDefaultSdkVersion();
        return CreateSkills(version, version);
    }

    public static AspireSkillsBundleProvider CreateSkills(string currentCliVersion, string currentSdkVersion)
        => new(AspireSkillsBundleDescriptor.Skills, currentCliVersion, currentSdkVersion, NullLogger.Instance);

    public static AspireSkillsBundleProvider CreateExtensions()
    {
        var version = VersionHelper.GetDefaultSdkVersion();
        return new(AspireSkillsBundleDescriptor.Extensions, version, version, NullLogger.Instance);
    }
}
