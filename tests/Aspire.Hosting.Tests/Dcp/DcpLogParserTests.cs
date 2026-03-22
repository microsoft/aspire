// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.Dcp;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public sealed class DcpLogParserTests
{
    [Fact]
    public void TryParseDcpLog_ValidInfoLog_ParsesSuccessfully()
    {
        // Arrange
        var logLine = "2023-09-19T20:40:50.509-0700\tinfo\tdcp.ServiceReconciler\tservice /apigateway is now in state Ready";
        var bytes = Encoding.UTF8.GetBytes(logLine);

        // Act
        var result = DcpLogParser.TryParseDcpLog(bytes.AsSpan(), out var message, out var logLevel, out var category);

        // Assert
        Assert.True(result);
        Assert.Equal("service /apigateway is now in state Ready", message);
        Assert.Equal(LogLevel.Information, logLevel);
        Assert.Equal("dcp.ServiceReconciler", category);
    }

    [Fact]
    public void TryParseDcpLog_ValidErrorLog_ParsesSuccessfully()
    {
        // Arrange
        var logLine = "2023-09-19T20:40:50.509-0700\terror\tdcp.ExecutableReconciler\tFailed to start a process";
        var bytes = Encoding.UTF8.GetBytes(logLine);

        // Act
        var result = DcpLogParser.TryParseDcpLog(bytes.AsSpan(), out var message, out var logLevel, out var category);

        // Assert
        Assert.True(result);
        Assert.Equal("Failed to start a process", message);
        Assert.Equal(LogLevel.Error, logLevel);
        Assert.Equal("dcp.ExecutableReconciler", category);
    }

    [Fact]
    public void TryParseDcpLog_ValidWarningLog_ParsesSuccessfully()
    {
        // Arrange
        var logLine = "2023-09-19T20:40:50.509-0700\twarning\tdcp.TestReconciler\tWarning message";
        var bytes = Encoding.UTF8.GetBytes(logLine);

        // Act
        var result = DcpLogParser.TryParseDcpLog(bytes.AsSpan(), out var message, out var logLevel, out var category);

        // Assert
        Assert.True(result);
        Assert.Equal("Warning message", message);
        Assert.Equal(LogLevel.Warning, logLevel);
        Assert.Equal("dcp.TestReconciler", category);
    }

    [Fact]
    public void TryParseDcpLog_ValidDebugLog_ParsesSuccessfully()
    {
        // Arrange
        var logLine = "2023-09-19T20:40:50.509-0700\tdebug\tdcp.TestReconciler\tDebug message";
        var bytes = Encoding.UTF8.GetBytes(logLine);

        // Act
        var result = DcpLogParser.TryParseDcpLog(bytes.AsSpan(), out var message, out var logLevel, out var category);

        // Assert
        Assert.True(result);
        Assert.Equal("Debug message", message);
        Assert.Equal(LogLevel.Debug, logLevel);
        Assert.Equal("dcp.TestReconciler", category);
    }

    [Fact]
    public void TryParseDcpLog_ValidTraceLog_ParsesSuccessfully()
    {
        // Arrange
        var logLine = "2023-09-19T20:40:50.509-0700\ttrace\tdcp.TestReconciler\tTrace message";
        var bytes = Encoding.UTF8.GetBytes(logLine);

        // Act
        var result = DcpLogParser.TryParseDcpLog(bytes.AsSpan(), out var message, out var logLevel, out var category);

        // Assert
        Assert.True(result);
        Assert.Equal("Trace message", message);
        Assert.Equal(LogLevel.Trace, logLevel);
        Assert.Equal("dcp.TestReconciler", category);
    }

    [Fact]
    public void TryParseDcpLog_WithCarriageReturn_TrimsSuccessfully()
    {
        // Arrange
        var logLine = "2023-09-19T20:40:50.509-0700\tinfo\tdcp.ServiceReconciler\ttest message\r";
        var bytes = Encoding.UTF8.GetBytes(logLine);

        // Act
        var result = DcpLogParser.TryParseDcpLog(bytes.AsSpan(), out var message, out var logLevel, out _);

        // Assert
        Assert.True(result);
        Assert.Equal("test message", message);
        Assert.Equal(LogLevel.Information, logLevel);
    }

    [Fact]
    public void TryParseDcpLog_WithJsonPayload_ParsesSuccessfully()
    {
        // Arrange
        var logLine = "2023-09-19T20:40:50.509-0700\tinfo\tdcp.ServiceReconciler\tservice /apigateway is now in state Ready\t{\"ServiceName\": {\"name\":\"apigateway\"}}";
        var bytes = Encoding.UTF8.GetBytes(logLine);

        // Act
        var result = DcpLogParser.TryParseDcpLog(bytes.AsSpan(), out var message, out var logLevel, out _);

        // Assert
        Assert.True(result);
        Assert.Equal("service /apigateway is now in state Ready\t{\"ServiceName\": {\"name\":\"apigateway\"}}", message);
        Assert.Equal(LogLevel.Information, logLevel);
    }

    [Fact]
    public void TryParseDcpLog_InvalidFormat_ReturnsFalse()
    {
        // Arrange
        var logLine = "This is not a DCP formatted log";
        var bytes = Encoding.UTF8.GetBytes(logLine);

        // Act
        var result = DcpLogParser.TryParseDcpLog(bytes.AsSpan(), out _, out _, out _);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void TryParseDcpLog_MissingTabs_ReturnsFalse()
    {
        // Arrange
        var logLine = "2023-09-19T20:40:50.509-0700 info message";
        var bytes = Encoding.UTF8.GetBytes(logLine);

        // Act
        var result = DcpLogParser.TryParseDcpLog(bytes.AsSpan(), out _, out _, out _);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void TryParseDcpLog_StringOverload_ValidErrorLog_ParsesSuccessfully()
    {
        // Arrange
        var logLine = "2023-09-19T20:40:50.509-0700\terror\tdcp.ExecutableReconciler\tFailed to start a process";

        // Act
        var result = DcpLogParser.TryParseDcpLog(logLine, out var message, out var logLevel, out var isErrorLevel);

        // Assert
        Assert.True(result);
        Assert.Equal("Failed to start a process", message);
        Assert.Equal(LogLevel.Error, logLevel);
        Assert.True(isErrorLevel);
    }

    [Fact]
    public void TryParseDcpLog_StringOverload_ValidInfoLog_ParsesSuccessfully()
    {
        // Arrange
        var logLine = "2023-09-19T20:40:50.509-0700\tinfo\tdcp.ServiceReconciler\tservice /apigateway is now in state Ready";

        // Act
        var result = DcpLogParser.TryParseDcpLog(logLine, out var message, out var logLevel, out var isErrorLevel);

        // Assert
        Assert.True(result);
        Assert.Equal("service /apigateway is now in state Ready", message);
        Assert.Equal(LogLevel.Information, logLevel);
        Assert.False(isErrorLevel);
    }

    [Fact]
    public void TryParseDcpLog_StringOverload_InvalidFormat_ReturnsFalse()
    {
        // Arrange
        var logLine = "This is not a DCP formatted log";

        // Act
        var result = DcpLogParser.TryParseDcpLog(logLine, out _, out _, out _);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void TryParseDcpLog_RealWorldExampleFromIssue_ParsesSuccessfully()
    {
        // Arrange - Example from the issue
        var logLine = "2023-09-19T20:40:50.509-0700\tinfo\tdcp.ExecutableReconciler\tStarting process...\t{\"Executable\": \"/python-api-duafrsvy\", \"Reconciliation\": 3, \"Cmd\": \"d:\\\\dev\\\\git\\\\dogfood\\\\hellopython\\\\pyapp\\\\.venv\\\\Scripts\\\\python.exe\", \"Args\": [\"main.py\"]}";
        var bytes = Encoding.UTF8.GetBytes(logLine);

        // Act
        var result = DcpLogParser.TryParseDcpLog(bytes.AsSpan(), out var message, out var logLevel, out var category);

        // Assert
        Assert.True(result);
        Assert.Contains("Starting process...", message);
        Assert.Equal(LogLevel.Information, logLevel);
        Assert.Equal("dcp.ExecutableReconciler", category);
    }

    [Theory]
    [InlineData("error", LogLevel.Error)]
    [InlineData("warning", LogLevel.Warning)]
    [InlineData("info", LogLevel.Information)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("trace", LogLevel.Trace)]
    public void TryParseDcpLog_ParsesAllLogLevels(string dcpLogLevel, LogLevel expectedLogLevel)
    {
        // Arrange
        var logLine = $"2023-09-19T20:40:50.509-0700\t{dcpLogLevel}\tdcp.TestReconciler\tTest message";
        var bytes = Encoding.UTF8.GetBytes(logLine);

        // Act
        var result = DcpLogParser.TryParseDcpLog(bytes.AsSpan(), out var message, out var logLevel, out var category);

        // Assert
        Assert.True(result);
        Assert.Equal("Test message", message);
        Assert.Equal(expectedLogLevel, logLevel);
        Assert.Equal("dcp.TestReconciler", category);
    }

    [Fact]
    public void FormatSystemLog_NoJson_AddsSystemPrefix()
    {
        // Arrange
        var message = "service /apigateway is now in state Ready";

        // Act
        var formatted = DcpLogParser.FormatSystemLog(message);

        // Assert
        Assert.Equal("[sys] service /apigateway is now in state Ready", formatted);
    }

    [Fact]
    public void FormatSystemLog_StartingProcessWithCmdAndArgs_FormatsCorrectly()
    {
        // Arrange
        var message = "Starting process...\t{\"Executable\": \"/foo-pwrqgpew\", \"Reconciliation\": 4, \"Cmd\": \"bla\", \"Args\": []}";

        // Act
        var formatted = DcpLogParser.FormatSystemLog(message);

        // Assert
        Assert.Equal("[sys] Starting process...: Cmd = bla, Args = []", formatted);
    }

    [Fact]
    public void FormatSystemLog_FailedToStartWithError_FormatsCorrectly()
    {
        // Arrange
        var message = "Failed to start process\t{\"Executable\": \"/foo-pwrqgpew\", \"Reconciliation\": 4, \"Cmd\": \"bla\", \"Args\": [], \"error\": \"exec: \\\"bla\\\": executable file not found in $PATH\"}";

        // Act
        var formatted = DcpLogParser.FormatSystemLog(message);

        // Assert
        Assert.Equal("[sys] Failed to start process: Cmd = bla, Args = [], Error = exec: \"bla\": executable file not found in $PATH", formatted);
    }

    [Fact]
    public void FormatSystemLog_ErrorWithoutCmdArgs_FormatsCorrectly()
    {
        // Arrange
        var message = "Failed to start Executable\t{\"Executable\": \"/foo-pwrqgpew\", \"Reconciliation\": 4, \"error\": \"exec: \\\"bla\\\": executable file not found in $PATH\"}";

        // Act
        var formatted = DcpLogParser.FormatSystemLog(message);

        // Assert
        Assert.Equal("[sys] Failed to start Executable: Error = exec: \"bla\": executable file not found in $PATH", formatted);
    }

    [Fact]
    public void FormatSystemLog_MultiLineError_FormatsWithSysPrefix()
    {
        // Arrange
        var message = "Failed to start Container\t{\"Container\": \"/nginx-duqbgyrt\", \"Reconciliation\": 16, \"ContainerID\": \"1f1f348abff1\", \"ContainerName\": \"nginx-duqbgyrt\", \"error\": \"container 'nginx-duqbgyrt' start failed (exit code 123)\\nwriter is closed\\nnot all requested objects were returned\\nonly 0 out of 1 containers were successfully started\"}";

        // Act
        var formatted = DcpLogParser.FormatSystemLog(message);

        // Assert
        var expected = "[sys] Failed to start Container: ContainerName = nginx-duqbgyrt, ContainerId = 1f1f348abff1:\n[sys] container 'nginx-duqbgyrt' start failed (exit code 123)\n[sys] writer is closed\n[sys] not all requested objects were returned\n[sys] only 0 out of 1 containers were successfully started";
        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void FormatSystemLog_ContainerWithNoError_FormatsCorrectly()
    {
        // Arrange
        var message = "Container created\t{\"Container\": \"/nginx-duqbgyrt\", \"Reconciliation\": 9, \"ContainerName\": \"nginx-duqbgyrt\", \"Network\": \"/aspire-network\"}";

        // Act
        var formatted = DcpLogParser.FormatSystemLog(message);

        // Assert
        Assert.Equal("[sys] Container created: ContainerName = nginx-duqbgyrt", formatted);
    }

    [Fact]
    public void FormatSystemLog_InvalidJson_ReturnsOriginalWithPrefix()
    {
        // Arrange
        var message = "Starting process...\t{invalid json";

        // Act
        var formatted = DcpLogParser.FormatSystemLog(message);

        // Assert
        Assert.Equal("[sys] Starting process...\t{invalid json", formatted);
    }

    [Fact]
    public void FormatSystemLog_EmptyTextWithJson_FormatsOnlyRelevantFields()
    {
        // Arrange
        var message = "\t{\"Cmd\": \"mycommand\", \"Args\": [\"arg1\", \"arg2\"]}";

        // Act
        var formatted = DcpLogParser.FormatSystemLog(message);

        // Assert
        Assert.Equal("[sys] Cmd = mycommand, Args = [\"arg1\", \"arg2\"]", formatted);
    }

    [Fact]
    public void FormatSystemLog_ContainerWithNameAndId_FormatsCorrectly()
    {
        // Arrange
        var message = "Container started\t{\"Container\": \"/nginx-abc123\", \"ContainerName\": \"nginx-abc123\", \"ContainerID\": \"1f1f348abff1\"}";

        // Act
        var formatted = DcpLogParser.FormatSystemLog(message);

        // Assert
        Assert.Equal("[sys] Container started: ContainerName = nginx-abc123, ContainerId = 1f1f348abff1", formatted);
    }

    [Fact]
    public void FormatSystemLog_ContainerWithNameIdAndError_FormatsCorrectly()
    {
        // Arrange
        var message = "Failed to start Container\t{\"Container\": \"/nginx-duqbgyrt\", \"Reconciliation\": 16, \"ContainerID\": \"1f1f348abff1\", \"ContainerName\": \"nginx-duqbgyrt\", \"error\": \"container failed to start\"}";

        // Act
        var formatted = DcpLogParser.FormatSystemLog(message);

        // Assert
        Assert.Equal("[sys] Failed to start Container: ContainerName = nginx-duqbgyrt, ContainerId = 1f1f348abff1, Error = container failed to start", formatted);
    }

    [Fact]
    public void FormatSystemLog_ContainerWithOnlyName_FormatsCorrectly()
    {
        // Arrange
        var message = "Container created\t{\"Container\": \"/nginx-xyz\", \"ContainerName\": \"nginx-xyz\"}";

        // Act
        var formatted = DcpLogParser.FormatSystemLog(message);

        // Assert
        Assert.Equal("[sys] Container created: ContainerName = nginx-xyz", formatted);
    }

    [Fact]
    public void FormatSystemLog_ContainerWithOnlyId_FormatsCorrectly()
    {
        // Arrange
        var message = "Container removed\t{\"Container\": \"/nginx-xyz\", \"ContainerID\": \"abc123def456\"}";

        // Act
        var formatted = DcpLogParser.FormatSystemLog(message);

        // Assert
        Assert.Equal("[sys] Container removed: ContainerId = abc123def456", formatted);
    }
}
