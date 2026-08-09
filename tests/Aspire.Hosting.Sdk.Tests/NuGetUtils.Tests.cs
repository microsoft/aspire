// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using NuGet.RuntimeModel;
using Xunit;

namespace Aspire.Hosting.Sdk.Tests;

public class NuGetUtilsTests
{
    [Theory]
    // Matching RID cases
    [InlineData("win-x64", "win-x64")]
    [InlineData("win-x86", "win-x86")]
    [InlineData("win-arm64", "win-arm64")]
    [InlineData("linux-x64", "linux-x64")]
    [InlineData("linux-arm64", "linux-arm64")]
    [InlineData("osx-x64", "osx-x64")]
    [InlineData("osx-arm64", "osx-arm64")]

    //Compatible RID cases
    [InlineData("rhel.8-x64", "linux-x64")] // https://github.com/microsoft/aspire/issues/5486
    [InlineData("ubuntu.23.04-x64", "linux-x64")]
    [InlineData("fedora.39-x64", "linux-x64")]
    [InlineData("linux-musl-x64", "linux-x64")]
    public void RightRIDIsSelected(string inputRID, string expectedRID)
    {
        RuntimeGraph graph = JsonRuntimeFormat.ReadRuntimeGraph("RuntimeIdentifierGraph.json");

        var result = NuGetUtils.GetBestMatchingRid(graph, inputRID, new[] { "win-x64", "win-arm64", "win-x86",
            "linux-x64", "linux-arm64",
            "osx-x64", "osx-arm64"}, out bool wasInGraph);

        Assert.Equal(expectedRID, result);
        Assert.True(wasInGraph);
    }
}
