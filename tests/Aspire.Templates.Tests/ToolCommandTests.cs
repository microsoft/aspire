// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Templates.Tests;

public class ToolCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("category", "basic-build")]
    public void RemoveInheritedMSBuildPathsPreservesUnrelatedEnvironment(bool hasInheritedPaths)
    {
        var environment = new Dictionary<string, string?>
        {
            ["DOTNET_ROOT"] = "test-sdk",
            ["PATH"] = "test-path"
        };
        if (hasInheritedPaths)
        {
            environment["MSBuildSDKsPath"] = "repo-sdk/Sdks";
            environment["MSBuildExtensionsPath"] = "repo-sdk";
        }

        ToolCommand.RemoveInheritedMSBuildPaths(environment);

        Assert.Equal(
            new Dictionary<string, string?>
            {
                ["DOTNET_ROOT"] = "test-sdk",
                ["PATH"] = "test-path"
            },
            environment);
    }
}
