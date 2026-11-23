// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Aspire.Shared.Tests;

public class ProcessSupervisorLoggerTests
{
    [Fact]
    public void DiagnosticsPreserveSeverityIdentityAndMultilineErrors()
    {
        using var writer = new StringWriter();
        var sender = new ProcessSupervisorLogger(writer);
        var error = new InvalidOperationException("first line\nsecond line");
        sender.LogInformation("Started '{Command}'.", "a \"quoted\" path");
        sender.LogError(error, "Launch failed.");
        var sink = new TestSink();
        var receiver = new TestLogger("owner", sink, enabled: true);
        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.True(ProcessSupervisorLogger.TryForward(line, receiver)));

        Assert.Collection(sink.Writes,
            entry =>
            {
                Assert.Equal(LogLevel.Information, entry.LogLevel);
                Assert.Equal($"Process supervisor {Environment.ProcessId}: Started 'a \"quoted\" path'.", entry.Message);
            },
            entry =>
            {
                Assert.Equal(LogLevel.Error, entry.LogLevel);
                Assert.Equal($"Process supervisor {Environment.ProcessId}: Launch failed.{Environment.NewLine}{error}", entry.Message);
            });
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"Level":999,"SupervisorProcessId":1,"Message":"invalid"}""")]
    [InlineData("""{"Level":2,"SupervisorProcessId":0,"Message":"invalid"}""")]
    [InlineData("""{"Level":2,"SupervisorProcessId":1,"Message":null}""")]
    public void MalformedDiagnosticsAreReportedAndRemainAvailableAsStderr(string payload)
    {
        var sink = new TestSink();
        var receiver = new TestLogger("owner", sink, enabled: true);

        Assert.False(ProcessSupervisorLogger.TryForward("[aspire-process-supervisor] " + payload, receiver));

        var entry = Assert.Single(sink.Writes);
        Assert.Equal(LogLevel.Warning, entry.LogLevel);
        Assert.IsType<System.Text.Json.JsonException>(entry.Exception);
        Assert.Equal("Malformed process supervisor diagnostic; preserving the original stderr line.", entry.Message);
    }

    [Fact]
    public void RuntimeStderrIsNotInterpretedAsSupervisorDiagnostics()
    {
        var sink = new TestSink();

        Assert.False(ProcessSupervisorLogger.TryForward("runtime error", new TestLogger("owner", sink, enabled: true)));

        Assert.Empty(sink.Writes);
    }
}
