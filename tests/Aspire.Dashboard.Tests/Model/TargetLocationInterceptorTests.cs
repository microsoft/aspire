// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class TargetLocationInterceptorTests
{
    [Fact]
    public void InterceptTargetLocation_RelativeRoot_Redirect()
    {
        Assert.True(TargetLocationInterceptor.InterceptTargetLocation("http://localhost", "/", out var newTargetLocation));
        Assert.Equal(TargetLocationInterceptor.StructuredLogsPath, newTargetLocation);
    }

    [Fact]
    public void InterceptTargetLocation_Absolute_Redirect()
    {
        Assert.True(TargetLocationInterceptor.InterceptTargetLocation("http://localhost", "http://localhost/", out var newTargetLocation));
        Assert.Equal(TargetLocationInterceptor.StructuredLogsPath, newTargetLocation);
    }

    [Fact]
    public void InterceptTargetLocation_Absolute_WithoutTrailingSlash_Redirect()
    {
        Assert.True(TargetLocationInterceptor.InterceptTargetLocation("http://localhost", "http://localhost", out var newTargetLocation));
        Assert.Equal(TargetLocationInterceptor.StructuredLogsPath, newTargetLocation);
    }

    [Fact]
    public void InterceptTargetLocation_AbsoluteDifferentCase_Redirect()
    {
        Assert.True(TargetLocationInterceptor.InterceptTargetLocation("http://LOCALHOST", "http://localhost/", out var newTargetLocation));
        Assert.Equal(TargetLocationInterceptor.StructuredLogsPath, newTargetLocation);
    }

    [Fact]
    public void InterceptTargetLocation_StructuredLogs_Unchanged()
    {
        Assert.False(TargetLocationInterceptor.InterceptTargetLocation("http://localhost", TargetLocationInterceptor.StructuredLogsPath, out _));
    }

    [Fact]
    public void InterceptTargetLocation_DifferentHost_Unchanged()
    {
        Assert.False(TargetLocationInterceptor.InterceptTargetLocation("http://localhost", "http://localhost:8888/", out _));
    }

    [Fact]
    public void InterceptTargetLocation_DifferentHost_TrailingSlash_Unchanged()
    {
        Assert.False(TargetLocationInterceptor.InterceptTargetLocation("http://localhost/", "http://localhost:8888/", out _));
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/resources", true)]
    [InlineData("/resources/frontend", true)]
    [InlineData("/RESOURCES/frontend", true)]
    [InlineData("/parameters", true)]
    [InlineData("/graph", true)]
    [InlineData("/graph/other", false)]
    [InlineData("/structuredlogs", false)]
    [InlineData("/traces/detail/abc", false)]
    [InlineData("/consolelogs/resource/frontend", false)]
    public void IsResourceServicePath_ReturnsWhetherPageRequiresResourceService(string path, bool expected)
    {
        Assert.Equal(expected, TargetLocationInterceptor.IsResourceServicePath(path));
    }

    // The previous experience keeps main's behavior, where only the resources root needs the resource service.
    [Theory]
    [InlineData("/", "/structuredlogs")]
    [InlineData("/resources/frontend", "/structuredlogs")]
    [InlineData("/legacy", "/legacy/structuredlogs")]
    [InlineData("/legacy/", "/legacy/structuredlogs")]
    [InlineData("/LEGACY", "/legacy/structuredlogs")]
    [InlineData("/legacy/resources", "/legacy/structuredlogs")]
    [InlineData("/legacy/Resources/", "/legacy/structuredlogs")]
    [InlineData("/legacy/structuredlogs", null)]
    [InlineData("/legacy/traces", null)]
    [InlineData("/structuredlogs", null)]
    public void GetStandaloneLocation_KeepsTheRequestedExperience(string path, string? expected)
    {
        Assert.Equal(expected, TargetLocationInterceptor.GetStandaloneLocation(path));
    }

    [Fact]
    public void InterceptTargetLocation_LegacyRoot_RedirectsToLegacyStructuredLogs()
    {
        Assert.True(TargetLocationInterceptor.InterceptTargetLocation("http://localhost", "http://localhost/legacy", out var newTargetLocation));
        Assert.Equal(TargetLocationInterceptor.LegacyStructuredLogsPath, newTargetLocation);
    }
}
