// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class LoggingHelpersTests
{
    [Fact]
    public void WriteDashboardSummary_WithTokenAndOtlpEndpoints_LogsSummaryAndStructuredUrls()
    {
        var sink = new TestSink();
        var logger = new TestLogger("TestLogger", sink, enabled: true);

        LoggingHelpers.WriteDashboardSummary(
            logger,
            "http://localhost:18888",
            "http://localhost:18889",
            "http://localhost:18890",
            "abc123");

        var write = Assert.Single(sink.Writes);
        Assert.Equal(LogLevel.Information, write.LogLevel);
        Assert.NotNull(write.Message);
        var lines = GetMessageLines(write.Message!);

        Assert.Collection(lines,
            line => Assert.Equal("Aspire Dashboard", line),
            line => Assert.Equal(string.Empty, line),
            line => Assert.Equal("Dashboard:    http://localhost:18888", line),
            line => Assert.Equal("Login URL:    http://localhost:18888/login?t=abc123", line),
            line => Assert.Equal("OTLP/gRPC:    http://localhost:18889", line),
            line => Assert.Equal("OTLP/HTTP:    http://localhost:18890", line),
            line => Assert.Equal(string.Empty, line));

        Assert.Equal("http://localhost:18888", LogTestHelpers.GetValue(write, "DashboardUrl"));
        Assert.Equal("http://localhost:18889", LogTestHelpers.GetValue(write, "OtlpGrpcUrl"));
        Assert.Equal("http://localhost:18890", LogTestHelpers.GetValue(write, "OtlpHttpUrl"));
        Assert.Equal("http://localhost:18888/login?t=abc123", LogTestHelpers.GetValue(write, "LoginUrl"));
    }

    [Fact]
    public void WriteDashboardSummary_WithoutToken_DoesNotIncludeLoginUrl()
    {
        var sink = new TestSink();
        var logger = new TestLogger("TestLogger", sink, enabled: true);

        LoggingHelpers.WriteDashboardSummary(
            logger,
            "http://localhost:18888",
            "http://localhost:18889",
            "http://localhost:18890",
            token: null);

        var write = Assert.Single(sink.Writes);
        Assert.Equal(LogLevel.Information, write.LogLevel);
        Assert.NotNull(write.Message);
        var lines = GetMessageLines(write.Message!);

        Assert.Collection(lines,
            line => Assert.Equal("Aspire Dashboard", line),
            line => Assert.Equal(string.Empty, line),
            line => Assert.Equal("Dashboard:    http://localhost:18888", line),
            line => Assert.Equal("OTLP/gRPC:    http://localhost:18889", line),
            line => Assert.Equal("OTLP/HTTP:    http://localhost:18890", line),
            line => Assert.Equal(string.Empty, line));

        Assert.Null(LogTestHelpers.GetValue(write, "LoginUrl"));
    }

    [Fact]
    public void WriteDashboardSummary_InvalidDashboardUrl_LogsOtlpEndpoints()
    {
        var sink = new TestSink();
        var logger = new TestLogger("TestLogger", sink, enabled: true);

        LoggingHelpers.WriteDashboardSummary(logger, "not-a-url", "http://localhost:18889", "http://localhost:18890", token: "abc123");

        var write = Assert.Single(sink.Writes);
        Assert.Equal(LogLevel.Information, write.LogLevel);
        Assert.NotNull(write.Message);
        var lines = GetMessageLines(write.Message!);

        Assert.Collection(lines,
            line => Assert.Equal("Aspire Dashboard", line),
            line => Assert.Equal(string.Empty, line),
            line => Assert.Equal("OTLP/gRPC:    http://localhost:18889", line),
            line => Assert.Equal("OTLP/HTTP:    http://localhost:18890", line),
            line => Assert.Equal(string.Empty, line));
    }

    [Fact]
    public void WriteDashboardSummary_NullDashboardUrl_LogsOtlpEndpoints()
    {
        var sink = new TestSink();
        var logger = new TestLogger("TestLogger", sink, enabled: true);

        LoggingHelpers.WriteDashboardSummary(
            logger,
            dashboardUrl: null,
            otlpGrpcUrl: "http://localhost:18889",
            otlpHttpUrl: "http://localhost:18890",
            token: "abc123");

        var write = Assert.Single(sink.Writes);
        Assert.Equal(LogLevel.Information, write.LogLevel);
        Assert.NotNull(write.Message);
        var lines = GetMessageLines(write.Message!);

        Assert.Collection(lines,
            line => Assert.Equal("Aspire Dashboard", line),
            line => Assert.Equal(string.Empty, line),
            line => Assert.Equal("OTLP/gRPC:    http://localhost:18889", line),
            line => Assert.Equal("OTLP/HTTP:    http://localhost:18890", line),
            line => Assert.Equal(string.Empty, line));

        Assert.Null(LogTestHelpers.GetValue(write, "DashboardUrl"));
        Assert.Null(LogTestHelpers.GetValue(write, "LoginUrl"));
        Assert.Equal("http://localhost:18889", LogTestHelpers.GetValue(write, "OtlpGrpcUrl"));
        Assert.Equal("http://localhost:18890", LogTestHelpers.GetValue(write, "OtlpHttpUrl"));
    }

    [Fact]
    public void WriteDashboardSummary_AllUrlsInvalid_DoesNotLog()
    {
        var sink = new TestSink();
        var logger = new TestLogger("TestLogger", sink, enabled: true);

        LoggingHelpers.WriteDashboardSummary(
            logger,
            dashboardUrl: "not-a-url",
            otlpGrpcUrl: "also-invalid",
            otlpHttpUrl: "nope",
            token: "abc123");

        Assert.Empty(sink.Writes);
    }

    [Fact]
    public void WriteDashboardSummary_AllUrlsNull_DoesNotLog()
    {
        var sink = new TestSink();
        var logger = new TestLogger("TestLogger", sink, enabled: true);

        LoggingHelpers.WriteDashboardSummary(
            logger,
            dashboardUrl: null,
            otlpGrpcUrl: null,
            otlpHttpUrl: null,
            token: "abc123");

        Assert.Empty(sink.Writes);
    }

    [Fact]
    public void WriteDashboardSummary_WithoutOtlpEndpoints_DoesNotIncludeOtlpLines()
    {
        var sink = new TestSink();
        var logger = new TestLogger("TestLogger", sink, enabled: true);

        LoggingHelpers.WriteDashboardSummary(
            logger,
            "http://localhost:18888",
            otlpGrpcUrl: null,
            otlpHttpUrl: null,
            token: "abc123");

        var write = Assert.Single(sink.Writes);
        Assert.NotNull(write.Message);
        var lines = GetMessageLines(write.Message!);

        Assert.Collection(lines,
            line => Assert.Equal("Aspire Dashboard", line),
            line => Assert.Equal(string.Empty, line),
            line => Assert.Equal("Dashboard:    http://localhost:18888", line),
            line => Assert.Equal("Login URL:    http://localhost:18888/login?t=abc123", line),
            line => Assert.Equal(string.Empty, line));

        Assert.Null(LogTestHelpers.GetValue(write, "OtlpGrpcUrl"));
        Assert.Null(LogTestHelpers.GetValue(write, "OtlpHttpUrl"));
    }

    [Fact]
    public void WriteDashboardSummary_IsContainer_IncludesContainerMessage()
    {
        var sink = new TestSink();
        var logger = new TestLogger("TestLogger", sink, enabled: true);

        LoggingHelpers.WriteDashboardSummary(
            logger,
            "http://localhost:18888",
            otlpGrpcUrl: null,
            otlpHttpUrl: null,
            token: "abc123",
            isContainer: true);

        var write = Assert.Single(sink.Writes);
        Assert.NotNull(write.Message);
        var containerMessage = "URLs may need changes depending on how network access to the container is configured.";

        Assert.Contains(containerMessage, write.Message);
    }

    private static string[] GetMessageLines(string message)
    {
        return message.Replace("\r", string.Empty).Split('\n');
    }
}
