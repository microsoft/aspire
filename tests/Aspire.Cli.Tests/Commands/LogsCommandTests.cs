// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class LogsCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task LogsCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("logs --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Help should return success
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public void LogsCommand_JsonSerialization_PreservesNonAsciiCharacters()
    {
        // Arrange - test with Chinese, Japanese, and accented characters
        var logLine = new LogLineJson
        {
            ResourceName = "测试资源",  // Chinese: "test resource"
            Content = "日本語ログ émojis",  // Japanese log with accented characters
            IsError = false
        };

        // Act
        var json = JsonSerializer.Serialize(logLine, LogsCommandJsonContext.Ndjson.LogLineJson);

        // Assert - verify the non-ASCII characters are NOT escaped
        Assert.Contains("测试资源", json);  // Chinese should appear as-is
        Assert.Contains("日本語ログ", json);  // Japanese should appear as-is
        Assert.Contains("émojis", json);  // Accented characters should appear as-is

        // Verify it's still valid JSON that can be deserialized
        var deserialized = JsonSerializer.Deserialize(json, LogsCommandJsonContext.Ndjson.LogLineJson);
        Assert.NotNull(deserialized);
        Assert.Equal(logLine.ResourceName, deserialized.ResourceName);
        Assert.Equal(logLine.Content, deserialized.Content);
        Assert.Equal(logLine.IsError, deserialized.IsError);
    }

    [Fact]
    public void LogsCommand_JsonSerialization_DefaultContext_EscapesNonAsciiCharacters()
    {
        // This test demonstrates why we need RelaxedEscaping - the default escapes non-ASCII
        var logLine = new LogLineJson
        {
            ResourceName = "测试",  // Chinese
            Content = "Test",
            IsError = false
        };

        // Act - serialize with default context (no relaxed escaping)
        var json = JsonSerializer.Serialize(logLine, LogsCommandJsonContext.Default.LogLineJson);

        // Assert - the default context should escape non-ASCII characters
        Assert.Contains("\\u", json);  // Unicode escape sequences should be present
        Assert.DoesNotContain("测试", json);  // Chinese characters should be escaped
    }

    [Fact]
    public void LogsCommand_JsonSerialization_HandlesSpecialCharacters()
    {
        // Test special characters that need escaping in JSON
        var logLine = new LogLineJson
        {
            ResourceName = "test-resource",
            Content = "Line with \"quotes\" and \\ backslash and\ttab",
            IsError = true
        };

        var json = JsonSerializer.Serialize(logLine, LogsCommandJsonContext.Ndjson.LogLineJson);

        // Verify it's valid JSON by deserializing
        var deserialized = JsonSerializer.Deserialize(json, LogsCommandJsonContext.Ndjson.LogLineJson);
        Assert.NotNull(deserialized);
        Assert.Equal(logLine.Content, deserialized.Content);
        Assert.True(deserialized.IsError);
    }

    [Fact]
    public void LogsCommand_JsonSerialization_HandlesNewlines()
    {
        var logLine = new LogLineJson
        {
            ResourceName = "multiline",
            Content = "First line\nSecond line\r\nThird line",
            IsError = false
        };

        var json = JsonSerializer.Serialize(logLine, LogsCommandJsonContext.Ndjson.LogLineJson);
        var deserialized = JsonSerializer.Deserialize(json, LogsCommandJsonContext.Ndjson.LogLineJson);

        Assert.NotNull(deserialized);
        Assert.Equal(logLine.Content, deserialized.Content);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task LogsCommand_WithInvalidTailValue_ReturnsError(int tailValue)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"logs --tail {tailValue}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Should fail validation
        Assert.NotEqual(ExitCodeConstants.Success, exitCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task LogsCommand_WithValidTailValue_PassesValidation(int tailValue)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // Use --help to avoid needing a running AppHost
        var result = command.Parse($"logs --tail {tailValue} --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Help should succeed (validation passed)
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task LogsCommand_WhenNoAppHostRunning_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // Without --follow and no running AppHost, should succeed (like Unix ps with no processes)
        var result = command.Parse("logs myresource");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Should succeed - no running AppHost is not an error
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("Json")]
    [InlineData("JSON")]
    public async Task LogsCommand_FormatOption_IsCaseInsensitive(string format)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // Use --help to verify the option is parsed correctly
        var result = command.Parse($"logs --format {format} --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("Table")]
    [InlineData("TABLE")]
    public async Task LogsCommand_FormatOption_AcceptsTable(string format)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"logs --format {format} --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task LogsCommand_FormatOption_RejectsInvalidValue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("logs --format invalid");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Invalid format should cause parsing error
        Assert.NotEqual(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task LogsCommand_FollowOption_CanBeCombinedWithTail()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("logs --follow --tail 50 --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task LogsCommand_AllOptions_CanBeCombined()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("logs myresource --follow --tail 100 --format json --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task LogsCommand_ShortFormOptions_Work()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // -f is short for --follow, -n is short for --tail
        var result = command.Parse("logs -f -n 10 --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public void LogsCommand_NdjsonFormat_OutputsOneObjectPerLine()
    {
        // Arrange - multiple log lines
        var logLines = new[]
        {
            new LogLineJson { ResourceName = "frontend", Content = "Starting...", IsError = false },
            new LogLineJson { ResourceName = "frontend", Content = "Ready", IsError = false },
            new LogLineJson { ResourceName = "backend", Content = "Error occurred", IsError = true }
        };

        // Act - serialize each line separately (simulating NDJSON streaming output)
        var ndjsonLines = logLines
            .Select(l => JsonSerializer.Serialize(l, LogsCommandJsonContext.Ndjson.LogLineJson))
            .ToList();

        // Assert - each line is a complete, valid JSON object
        foreach (var line in ndjsonLines)
        {
            // Verify no newlines within the JSON (compact format)
            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain('\r', line);

            // Verify it's valid JSON that can be deserialized
            var deserialized = JsonSerializer.Deserialize(line, LogsCommandJsonContext.Ndjson.LogLineJson);
            Assert.NotNull(deserialized);
        }

        // Verify NDJSON format: joining with newlines creates parseable multi-line output
        var ndjsonOutput = string.Join('\n', ndjsonLines);
        var parsedLines = ndjsonOutput.Split('\n')
            .Select(line => JsonSerializer.Deserialize(line, LogsCommandJsonContext.Ndjson.LogLineJson))
            .ToList();

        Assert.Equal(3, parsedLines.Count);
        Assert.Equal("frontend", parsedLines[0]!.ResourceName);
        Assert.Equal("backend", parsedLines[2]!.ResourceName);
        Assert.True(parsedLines[2]!.IsError);
    }

    [Fact]
    public void LogsCommand_SnapshotFormat_OutputsWrappedJsonArray()
    {
        // Arrange - multiple log lines for snapshot
        var logsOutput = new LogsOutput
        {
            Logs =
            [
                new LogLineJson { ResourceName = "frontend", Content = "Line 1", IsError = false },
                new LogLineJson { ResourceName = "frontend", Content = "Line 2", IsError = false },
                new LogLineJson { ResourceName = "backend", Content = "Error", IsError = true }
            ]
        };

        // Act - serialize as snapshot (wrapped JSON)
        var json = JsonSerializer.Serialize(logsOutput, LogsCommandJsonContext.Snapshot.LogsOutput);

        // Assert - it's a single JSON object with "logs" array
        Assert.Contains("\"logs\"", json);
        Assert.StartsWith("{", json.TrimStart());
        Assert.EndsWith("}", json.TrimEnd());

        // Verify it can be deserialized back
        var deserialized = JsonSerializer.Deserialize(json, LogsCommandJsonContext.Snapshot.LogsOutput);
        Assert.NotNull(deserialized);
        Assert.Equal(3, deserialized.Logs.Length);
        Assert.Equal("frontend", deserialized.Logs[0].ResourceName);
        Assert.True(deserialized.Logs[2].IsError);
    }

    [Fact]
    public void LogsCommand_NdjsonFormat_HandlesSpecialCharactersInContent()
    {
        // Arrange - log line with special characters that could break line-delimited parsing
        var logLine = new LogLineJson
        {
            ResourceName = "test",
            Content = "Line with\nnewline and\ttab and \"quotes\" and \\backslash",
            IsError = false
        };

        // Act
        var json = JsonSerializer.Serialize(logLine, LogsCommandJsonContext.Ndjson.LogLineJson);

        // Assert - the output should be a single line (newlines in content are escaped)
        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain('\r', json);

        // The escaped content should be present
        Assert.Contains("\\n", json);  // Escaped newline
        Assert.Contains("\\t", json);  // Escaped tab
        Assert.Contains("\\\"", json); // Escaped quotes

        // Verify round-trip works
        var deserialized = JsonSerializer.Deserialize(json, LogsCommandJsonContext.Ndjson.LogLineJson);
        Assert.NotNull(deserialized);
        Assert.Equal(logLine.Content, deserialized.Content);
    }

    [Fact]
    public async Task LogsCommand_JsonOutput_ResolvesResourceNames()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var provider = CreateLogsTestServices(workspace, outputWriter);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("logs --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonOutput = outputWriter.Logs.FirstOrDefault(l => l.Contains("\"logs\""));
        Assert.NotNull(jsonOutput);

        var logsOutput = JsonSerializer.Deserialize(jsonOutput, LogsCommandJsonContext.Snapshot.LogsOutput);
        Assert.NotNull(logsOutput);
        Assert.Equal(3, logsOutput.Logs.Length);

        // Logs are sorted by timestamp.
        // Unique display name should be used for the redis resource
        Assert.Equal("redis", logsOutput.Logs[0].ResourceName);

        // Replicas share the same DisplayName, so the unique Name should be used instead
        Assert.Equal("apiservice-abc123", logsOutput.Logs[1].ResourceName);
        Assert.Equal("apiservice-def456", logsOutput.Logs[2].ResourceName);
    }

    [Fact]
    public async Task LogsCommand_TextOutput_ResolvesResourceNames()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var provider = CreateLogsTestServices(workspace, outputWriter, disableAnsi: true);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("logs");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Plain text output uses "[resourceName] content" format
        // Replicas share the same DisplayName, so the unique Name should be used instead
        Assert.Contains(outputWriter.Logs, l => l.Contains("[apiservice-abc123]"));
        Assert.Contains(outputWriter.Logs, l => l.Contains("[apiservice-def456]"));

        // Unique display name should be used for the redis resource
        Assert.Contains(outputWriter.Logs, l => l.Contains("[redis]"));
    }

    [Theory]
    [InlineData("nonexistent", true)]
    [InlineData("redis", false)]
    [InlineData("apiservice-abc123", false)]
    [InlineData("apiservice", false)]
    public async Task LogsCommand_WithResourceName_ValidatesAgainstNameAndDisplayName(string resourceName, bool expectError)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var provider = CreateLogsTestServices(workspace, outputWriter);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"logs {resourceName} --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        if (expectError)
        {
            Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
        }
        else
        {
            Assert.Equal(ExitCodeConstants.Success, exitCode);
        }
    }

    [Fact]
    public async Task LogsCommand_JsonOutput_WithTimestamps_IncludesTimestampField()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var provider = CreateLogsTestServices(workspace, outputWriter);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("logs --format json --timestamps");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonOutput = outputWriter.Logs.FirstOrDefault(l => l.Contains("\"logs\""));
        Assert.NotNull(jsonOutput);

        var logsOutput = JsonSerializer.Deserialize(jsonOutput, LogsCommandJsonContext.Snapshot.LogsOutput);
        Assert.NotNull(logsOutput);
        Assert.Equal(3, logsOutput.Logs.Length);

        // Logs are sorted by timestamp
        Assert.Equal("redis", logsOutput.Logs[0].ResourceName);
        Assert.Equal("2025-01-15T10:30:00.000Z", logsOutput.Logs[0].Timestamp);
        Assert.Equal("Ready to accept connections", logsOutput.Logs[0].Content);
        Assert.False(logsOutput.Logs[0].IsError);

        Assert.Equal("apiservice-abc123", logsOutput.Logs[1].ResourceName);
        Assert.Equal("2025-01-15T10:30:01.000Z", logsOutput.Logs[1].Timestamp);
        Assert.Equal("Hello from replica 1", logsOutput.Logs[1].Content);
        Assert.False(logsOutput.Logs[1].IsError);

        Assert.Equal("apiservice-def456", logsOutput.Logs[2].ResourceName);
        Assert.Equal("2025-01-15T10:30:02.000Z", logsOutput.Logs[2].Timestamp);
        Assert.Equal("Hello from replica 2", logsOutput.Logs[2].Content);
        Assert.False(logsOutput.Logs[2].IsError);
    }

    [Fact]
    public async Task LogsCommand_JsonOutput_WithoutTimestamps_OmitsTimestampField()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var provider = CreateLogsTestServices(workspace, outputWriter);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("logs --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonOutput = outputWriter.Logs.FirstOrDefault(l => l.Contains("\"logs\""));
        Assert.NotNull(jsonOutput);

        var logsOutput = JsonSerializer.Deserialize(jsonOutput, LogsCommandJsonContext.Snapshot.LogsOutput);
        Assert.NotNull(logsOutput);
        Assert.Equal(3, logsOutput.Logs.Length);

        // Timestamp should be null when --timestamps is not specified
        // Logs are sorted by timestamp
        Assert.Equal("redis", logsOutput.Logs[0].ResourceName);
        Assert.Null(logsOutput.Logs[0].Timestamp);
        Assert.Equal("Ready to accept connections", logsOutput.Logs[0].Content);
        Assert.False(logsOutput.Logs[0].IsError);

        Assert.Equal("apiservice-abc123", logsOutput.Logs[1].ResourceName);
        Assert.Null(logsOutput.Logs[1].Timestamp);
        Assert.Equal("Hello from replica 1", logsOutput.Logs[1].Content);
        Assert.False(logsOutput.Logs[1].IsError);

        Assert.Equal("apiservice-def456", logsOutput.Logs[2].ResourceName);
        Assert.Null(logsOutput.Logs[2].Timestamp);
        Assert.Equal("Hello from replica 2", logsOutput.Logs[2].Content);
        Assert.False(logsOutput.Logs[2].IsError);
    }

    [Fact]
    public async Task LogsCommand_TextOutput_WithTimestamps_IncludesTimestampPrefix()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var provider = CreateLogsTestServices(workspace, outputWriter, disableAnsi: true);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("logs --timestamps");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Logs are sorted by timestamp, timestamp prefix is ISO 8601 round-trip format
        var logLines = outputWriter.Logs.Where(l => l.StartsWith("2025-", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, logLines.Count);
        Assert.Equal("2025-01-15T10:30:00.000Z [redis] Ready to accept connections", logLines[0]);
        Assert.Equal("2025-01-15T10:30:01.000Z [apiservice-abc123] Hello from replica 1", logLines[1]);
        Assert.Equal("2025-01-15T10:30:02.000Z [apiservice-def456] Hello from replica 2", logLines[2]);
    }

    [Fact]
    public async Task LogsCommand_TextOutput_WithoutTimestamps_NoTimestampPrefix()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var provider = CreateLogsTestServices(workspace, outputWriter, disableAnsi: true);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("logs");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Without --timestamps, log lines start with "[resourceName]" with no timestamp prefix
        // Logs are sorted by timestamp
        var logLines = outputWriter.Logs.Where(l => l.StartsWith("[", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, logLines.Count);
        Assert.Equal("[redis] Ready to accept connections", logLines[0]);
        Assert.Equal("[apiservice-abc123] Hello from replica 1", logLines[1]);
        Assert.Equal("[apiservice-def456] Hello from replica 2", logLines[2]);
    }

    private ServiceProvider CreateLogsTestServices(
        TemporaryWorkspace workspace,
        TestOutputTextWriter outputWriter,
        Action<Dictionary<string, string?>>? configureOptions = null,
        bool disableAnsi = false)
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj"),
                ProcessId = 1234
            },
            ResourceSnapshots =
            [
                // Unique resource - DisplayName is unique across all resources
                new ResourceSnapshot
                {
                    Name = "redis",
                    DisplayName = "redis",
                    ResourceType = "Container",
                    State = "Running"
                },
                // Replicas - two resources share the same DisplayName
                new ResourceSnapshot
                {
                    Name = "apiservice-abc123",
                    DisplayName = "apiservice",
                    ResourceType = "Project",
                    State = "Running"
                },
                new ResourceSnapshot
                {
                    Name = "apiservice-def456",
                    DisplayName = "apiservice",
                    ResourceType = "Project",
                    State = "Running"
                }
            ],
            LogLines =
            [
                // Log lines are intentionally out of timestamp order to verify sorting
                new ResourceLogLine
                {
                    ResourceName = "apiservice-def456",
                    LineNumber = 1,
                    Content = "2025-01-15T10:30:02Z Hello from replica 2",
                    IsError = false
                },
                new ResourceLogLine
                {
                    ResourceName = "redis",
                    LineNumber = 1,
                    Content = "2025-01-15T10:30:00Z Ready to accept connections",
                    IsError = false
                },
                new ResourceLogLine
                {
                    ResourceName = "apiservice-abc123",
                    LineNumber = 1,
                    Content = "2025-01-15T10:30:01Z Hello from replica 1",
                    IsError = false
                }
            ]
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = disableAnsi;

            if (configureOptions is not null)
            {
                options.ConfigurationCallback += configureOptions;
            }
        });

        return services.BuildServiceProvider();
    }
}
