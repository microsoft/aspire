// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class AppHostHelperTests
{
    [Theory]
    [InlineData("10.0.0", true)]
    [InlineData("9.2.0", true)]
    [InlineData("9.3.0", true)]
    [InlineData("13.0.0-preview.1", true)]
    [InlineData("9.1.0", false)]
    [InlineData("8.0.0", false)]
    [InlineData("1.0.0", false)]
    public async Task CheckAppHostCompatibility_VersionCheck(string aspireVersion, bool expectedCompatible)
    {
        var runner = new TestDotNetCliRunner
        {
            GetAppHostInformationAsyncCallback = (_, _, _) => (0, true, aspireVersion)
        };
        var interactionService = new TestInteractionService();
        var telemetry = TestTelemetryHelper.CreateInitializedTelemetry();
        var projectFile = new FileInfo(Path.Combine(Path.GetTempPath(), "test.csproj"));
        var workingDirectory = new DirectoryInfo(Path.GetTempPath());

        var (isCompatible, returnedVersion) = await AppHostHelper.CheckAppHostCompatibilityAsync(
            runner, interactionService, projectFile, telemetry, workingDirectory, "test.log", CancellationToken.None);

        Assert.Equal(expectedCompatible, isCompatible);
        Assert.Equal(aspireVersion, returnedVersion);
    }
}
