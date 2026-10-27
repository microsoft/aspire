// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;

namespace Aspire.Hosting.Native.Core.Tests;

public class DcpEnvironmentTests
{
    [Theory]
    [InlineData("ASPNETCORE_URLS")]
    [InlineData("DOTNET_LAUNCH_PROFILE")]
    [InlineData("ASPNETCORE_ENVIRONMENT")]
    [InlineData("DOTNET_ENVIRONMENT")]
    [InlineData("ASPIRE_LOGLEVEL")]
    public void ExcludesOnlyExactHostRuntimeNamesUsingHostingComparisonRules(string name)
    {
        Assert.True(DcpEnvironmentVariables.IsExcluded(name));
        Assert.True(DcpEnvironmentVariables.IsExcluded(name.ToLowerInvariant()));
        Assert.False(DcpEnvironmentVariables.IsExcluded(name + "_EXTRA"));
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("APPLICATION_SETTING")]
    [InlineData("ASPIRE_PARAMETER_PASSWORD")]
    [InlineData("ASPIRE_NATIVE_RPC_TOKEN")]
    [InlineData("ASPIRE_REMOTE_APPHOST_TOKEN")]
    [InlineData("AppHost__BrowserToken")]
    public void PreservesAmbientEnvironmentWithinTheSameUserTrustBoundary(string name)
    {
        Assert.False(DcpEnvironmentVariables.IsExcluded(name));
    }
}
