// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;

namespace Aspire.Cli.Tests.Packaging;

public class NuGetConfigEnvironmentVariablesTests
{
    [Fact]
    public void FindReferencedNames_ReturnsSupportedEnvironmentVariableForms()
    {
        const string content = """
            <configuration>
              <config>
                <add key="windows" value="%USERPROFILE%\.nuget\packages" />
                <add key="unix" value="$HOME/.nuget/packages" />
                <add key="braced" value="${ASPIRE_PACKAGES}/packages" />
                <add key="duplicate" value="%home%/packages" />
                <add key="program-files" value="%ProgramFiles(x86)%/NuGet" />
                <add key="private-feed" value="%PRIVATE-FEED%/packages" />
              </config>
            </configuration>
            """;

        var expectedNames = OperatingSystem.IsWindows()
            ? new[] { "ASPIRE_PACKAGES", "HOME", "PRIVATE-FEED", "ProgramFiles(x86)", "USERPROFILE" }
            : ["ASPIRE_PACKAGES", "HOME", "PRIVATE-FEED", "ProgramFiles(x86)", "USERPROFILE", "home"];

        Assert.Equal(expectedNames, NuGetConfigEnvironmentVariables.FindReferencedNames(content));
    }

    [Fact]
    public void FindReferencedNames_IgnoresNonEnvironmentDollarAndPercentSyntax()
    {
        const string content = """
            <configuration>
              <packageSources>
                <add key="encoded" value="https://example.com/feed%20name" />
                <add key="msbuild" value="$(RepoRoot)/packages" />
                <add key="literal" value="$9.99" />
              </packageSources>
            </configuration>
            """;

        Assert.Empty(NuGetConfigEnvironmentVariables.FindReferencedNames(content));
    }
}
