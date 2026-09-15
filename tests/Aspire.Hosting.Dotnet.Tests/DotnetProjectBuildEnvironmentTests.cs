// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetProjectBuildEnvironmentTests
{
    [Fact]
    public async Task ResponseFileProtectsAndEscapesBuildEnvironmentValues()
    {
        const string environmentValue = "custom\u2003\"flavor\";\r\nvalue%";
        var responseFile = Assert.IsType<MsBuildResponseFile>(
            await DotnetProjectBuildEnvironment.CreateResponseFileAsync(
                new Dictionary<string, string>
                {
                    ["BUILD_FLAVOR"] = environmentValue,
                    ["BUILD\nINJECTED"] = "safe",
                },
                NullLogger.Instance,
                TestContext.Current.CancellationToken));
        var responseFilePath = responseFile.FilePath;
        var responseFileDirectory = Path.GetDirectoryName(responseFilePath)!;

        try
        {
            Assert.Equal($"@{responseFilePath}", responseFile.Argument);
            Assert.Equal(
                $"\"--property:BUILD_FLAVOR=custom\u2003%22flavor%22%3B%0D%0Avalue%25\"{Environment.NewLine}" +
                $"\"--property:BUILD%0AINJECTED=safe\"{Environment.NewLine}",
                await File.ReadAllTextAsync(responseFilePath, TestContext.Current.CancellationToken));

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(responseFilePath));
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(responseFileDirectory));
            }
        }
        finally
        {
            responseFile.Dispose();
        }

        Assert.False(Directory.Exists(responseFileDirectory));
        responseFile.Dispose();
    }

    [Fact]
    public async Task EmptyBuildEnvironmentDoesNotCreateResponseFile()
    {
        var responseFile = await DotnetProjectBuildEnvironment.CreateResponseFileAsync(
            new Dictionary<string, string>(),
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Null(responseFile);
    }
}
