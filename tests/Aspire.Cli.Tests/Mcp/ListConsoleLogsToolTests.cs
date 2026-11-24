// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Tests.Mcp;

public class ListConsoleLogsToolTests
{
    [Fact]
    public async Task ListConsoleLogsTool_ThrowsException_WhenNoAppHostRunning()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"test-resource\"").RootElement
        };

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Contains("No Aspire AppHost", exception.Message);
        Assert.Contains("aspire start", exception.Message);
    }

    [Fact]
    public async Task ListConsoleLogsTool_ThrowsException_WhenResourceNameNotProvided()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel();
        monitor.AddConnection("socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Contains("resourceName", exception.Message);
    }

    [Fact]
    public async Task ListConsoleLogsTool_DoesNotLeakCredentialUrlFromBackchannelFailure()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            GetResourceLogsHandler = (_, _, cancellationToken) => ThrowCredentialUrlAsync(cancellationToken)
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);
        var sink = new TestSink();
        var logger = new TestLogger<ListConsoleLogsTool>(
            new TestLoggerFactory(sink, enabled: true));
        var tool = new ListConsoleLogsTool(monitor, logger);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"api-service\"").RootElement
        };

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(arguments),
            CancellationToken.None).DefaultTimeout();

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text;
        foreach (var diagnostic in new[] { text }.Concat(
            sink.Writes.Select(write => $"{write.Message} {write.Exception}")))
        {
            Assert.DoesNotContain("request-user", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("request-password", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("request-secret", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("request-fragment", diagnostic, StringComparison.Ordinal);
        }

        static async IAsyncEnumerable<ResourceLogLine> ThrowCredentialUrlAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                "Backchannel failed at https://" + "request-user" + ":" + "request-password" +
                "@example.com?token=request-secret#request-fragment");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public async Task ListConsoleLogsTool_RethrowsOperationCanceledExceptionWithIdentity()
    {
        var expectedException = new OperationCanceledException("Console log request canceled");
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            GetResourceLogsHandler = (_, _, _) => ThrowCancellationAsync(expectedException)
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);
        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"api-service\"").RootElement
        };

        var actualException = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            tool.CallToolAsync(
                CallToolContextTestHelper.Create(arguments),
                CancellationToken.None).AsTask());

        Assert.Same(expectedException, actualException);

        static async IAsyncEnumerable<ResourceLogLine> ThrowCancellationAsync(
            OperationCanceledException exception)
        {
            await Task.Yield();
            throw exception;
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public async Task ListConsoleLogsTool_ReturnsLogs_WhenResourceHasNoLogs()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines = []
        };
        monitor.AddConnection("socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"test-resource\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.NotNull(result.Content);
        Assert.Single(result.Content);
        var textContent = result.Content[0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        var codeBlockContent = ExtractCodeBlockContent(textContent.Text);
        Assert.Equal("", codeBlockContent);
        Assert.StartsWith("Returned 0 console logs.", textContent.Text);
    }

    [Fact]
    public async Task ListConsoleLogsTool_ReturnsLogs_ForSpecificResource()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines =
            [
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 1, Content = "Starting application...", IsError = false },
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 2, Content = "Application started", IsError = false },
                new ResourceLogLine { ResourceName = "other-service", LineNumber = 1, Content = "Different service log", IsError = false }
            ]
        };
        monitor.AddConnection("socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"api-service\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        var textContent = result.Content![0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        var codeBlockContent = ExtractCodeBlockContent(textContent.Text);
        Assert.Equal(
            """
            Starting application...
            Application started
            """, codeBlockContent);
    }

    [Fact]
    public async Task ListConsoleLogsTool_ReturnsPlainTextFormat()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines =
            [
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 1, Content = "Test log line", IsError = false }
            ]
        };
        monitor.AddConnection("socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"api-service\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        var textContent = result.Content![0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        var codeBlockContent = ExtractCodeBlockContent(textContent.Text);
        Assert.Equal("Test log line", codeBlockContent);
        Assert.StartsWith("Returned 1 console log.", textContent.Text);
    }

    [Fact]
    public async Task ListConsoleLogsTool_StripsTimestamps()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines =
            [
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 1, Content = "2024-01-15T10:30:00.123Z Log message after timestamp", IsError = false }
            ]
        };
        monitor.AddConnection("socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"api-service\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        var textContent = result.Content![0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        var codeBlockContent = ExtractCodeBlockContent(textContent.Text);
        Assert.Equal("Log message after timestamp", codeBlockContent);
    }

    [Fact]
    public async Task ListConsoleLogsTool_StripsAnsiSequences()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines =
            [
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 1, Content = "\u001b[32mGreen text\u001b[0m normal text", IsError = false }
            ]
        };
        monitor.AddConnection("socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"api-service\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        var textContent = result.Content![0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        var codeBlockContent = ExtractCodeBlockContent(textContent.Text);
        Assert.Equal("Green text normal text", codeBlockContent);
    }

    [Fact]
    public async Task ListConsoleLogsTool_WithSearch_FiltersLogsByContent()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines =
            [
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 1, Content = "Starting application...", IsError = false },
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 2, Content = "Connection established", IsError = false },
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 3, Content = "Request received", IsError = false }
            ]
        };
        monitor.AddConnection("socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"api-service\"").RootElement,
            ["search"] = JsonDocument.Parse("\"Connection\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        var textContent = result.Content![0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        var codeBlockContent = ExtractCodeBlockContent(textContent.Text);
        Assert.Contains("Connection established", codeBlockContent);
        Assert.DoesNotContain("Starting application", codeBlockContent);
        Assert.DoesNotContain("Request received", codeBlockContent);
    }

    [Fact]
    public async Task ListConsoleLogsTool_WithSearch_IsCaseInsensitive()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines =
            [
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 1, Content = "ERROR: Something failed", IsError = true },
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 2, Content = "Normal operation", IsError = false }
            ]
        };
        monitor.AddConnection("socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"api-service\"").RootElement,
            ["search"] = JsonDocument.Parse("\"error\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        var textContent = result.Content![0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        var codeBlockContent = ExtractCodeBlockContent(textContent.Text);
        Assert.Contains("Something failed", codeBlockContent);
        Assert.DoesNotContain("Normal operation", codeBlockContent);
    }

    [Fact]
    public async Task ListConsoleLogsTool_WithSearch_NoMatch_ReturnsEmpty()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines =
            [
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 1, Content = "Hello world", IsError = false }
            ]
        };
        monitor.AddConnection("socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"api-service\"").RootElement,
            ["search"] = JsonDocument.Parse("\"nonexistent\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        var textContent = result.Content![0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        var codeBlockContent = ExtractCodeBlockContent(textContent.Text);
        Assert.Equal("", codeBlockContent);
    }

    [Fact]
    public async Task ListConsoleLogsTool_WithSearch_ReturnsSummaryWithFilteredCount()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines =
            [
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 1, Content = "Starting application...", IsError = false },
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 2, Content = "Connection established", IsError = false },
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 3, Content = "Request received", IsError = false },
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 4, Content = "Connection closed", IsError = false }
            ]
        };
        monitor.AddConnection("socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"api-service\"").RootElement,
            ["search"] = JsonDocument.Parse("\"Connection\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        var textContent = result.Content![0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        // With search applied, totalLogsCount reflects matching entries (2), not the
        // last LineNumber (4). Since all matching entries fit within the limit, the summary
        // should say "Returned 2 console logs."
        Assert.Contains("Returned 2 console logs.", textContent.Text);
    }

    [Fact]
    public async Task ListConsoleLogsTool_WithNonStringSearchValue_IgnoresSearch()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines =
            [
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 1, Content = "Hello world", IsError = false },
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 2, Content = "Goodbye world", IsError = false }
            ]
        };
        monitor.AddConnection("socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        // Pass a numeric value for search instead of a string
        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"api-service\"").RootElement,
            ["search"] = JsonDocument.Parse("42").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        // Should not throw - search is ignored when ValueKind is not String
        Assert.True(result.IsError is null or false);
        var textContent = result.Content![0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        // All logs should be returned since search was not applied
        var codeBlockContent = ExtractCodeBlockContent(textContent.Text);
        Assert.Contains("Hello world", codeBlockContent);
        Assert.Contains("Goodbye world", codeBlockContent);
    }

    [Fact]
    public async Task ListConsoleLogsTool_WithSearch_MultipleWords_MatchesEachFragmentSeparately()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines =
            [
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 1, Content = "Connection timeout error on port 5000", IsError = true },
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 2, Content = "Connection established successfully", IsError = false },
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 3, Content = "Timeout waiting for response", IsError = true },
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 4, Content = "Ready to accept connections", IsError = false }
            ]
        };
        monitor.AddConnection("socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"api-service\"").RootElement,
            // Two words: both "Connection" AND "timeout" must appear in the same log line
            ["search"] = JsonDocument.Parse("\"Connection timeout\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        var textContent = result.Content![0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        var codeBlockContent = ExtractCodeBlockContent(textContent.Text);
        // Only the line containing BOTH fragments should match
        Assert.Contains("Connection timeout error", codeBlockContent);
        Assert.DoesNotContain("established", codeBlockContent);
        Assert.DoesNotContain("Timeout waiting", codeBlockContent);
        Assert.DoesNotContain("Ready to accept", codeBlockContent);
    }

    [Fact]
    public async Task ListConsoleLogsTool_WithSearch_QualifierSyntaxTreatedAsFreeText()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines =
            [
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 1, Content = "level:error something failed", IsError = true },
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 2, Content = "Normal operation", IsError = false }
            ]
        };
        monitor.AddConnection("socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"api-service\"").RootElement,
            // Qualifier-like syntax should be treated as free text for logs
            ["search"] = JsonDocument.Parse("\"level:error\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        var textContent = result.Content![0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        var codeBlockContent = ExtractCodeBlockContent(textContent.Text);
        // The qualifier value "error" is treated as a text fragment and matches
        Assert.Contains("level:error something failed", codeBlockContent);
        Assert.DoesNotContain("Normal operation", codeBlockContent);
    }

    [Theory]
    [InlineData(null, "Returned latest 2 console logs. Earlier 3 console logs not returned because of size limits.", "line 4\nline 5")]
    [InlineData("tail", "Returned latest 2 console logs. Earlier 3 console logs not returned because of size limits.", "line 4\nline 5")]
    [InlineData("head", "Returned first 2 console logs. Later 3 console logs not returned because of size limits.", "line 1\nline 2")]
    public async Task ListConsoleLogsTool_ReturnsLinesFromChosenEndUpToLimit(string? from, string expectedSummary, string expectedLines)
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines = [.. Enumerable.Range(1, 5).Select(index => new ResourceLogLine { ResourceName = "api-service", LineNumber = index, Content = $"line {index}", IsError = false })]
        };
        monitor.AddConnection("socket.hash1", connection);
        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonSerializer.SerializeToElement("api-service"),
            ["limit"] = JsonSerializer.SerializeToElement(2)
        };
        if (from is not null)
        {
            arguments["from"] = JsonSerializer.SerializeToElement(from);
        }

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text;
        Assert.StartsWith(expectedSummary, text, StringComparison.Ordinal);
        Assert.Equal(expectedLines, ExtractCodeBlockContent(text).ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task ListConsoleLogsTool_Head_CountsOnlyMatchingLines()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines =
            [
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 1, Content = "Starting", IsError = false },
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 2, Content = "fail: first", IsError = true },
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 3, Content = "Running", IsError = false },
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 4, Content = "fail: second", IsError = true }
            ]
        };
        monitor.AddConnection("socket.hash1", connection);
        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonSerializer.SerializeToElement("api-service"),
            ["search"] = JsonSerializer.SerializeToElement("fail"),
            ["from"] = JsonSerializer.SerializeToElement("head"),
            ["limit"] = JsonSerializer.SerializeToElement(1)
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text;
        Assert.StartsWith("Returned first 1 console log. Later 1 console log not returned because of size limits.", text, StringComparison.Ordinal);
        Assert.Equal("fail: first", ExtractCodeBlockContent(text));
    }

    [Fact]
    public async Task ListConsoleLogsTool_Head_ReturnsEarliestLinesAcrossInterleavedReplicas()
    {
        // Replicas stream their histories concurrently, so a slower replica's startup failure
        // can arrive after a faster replica's later lines.
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines =
            [
                new ResourceLogLine { ResourceName = "api-service-abc", LineNumber = 1, Content = "2026-01-01T00:00:03.0000000Z replica a started", IsError = false },
                new ResourceLogLine { ResourceName = "api-service-abc", LineNumber = 2, Content = "2026-01-01T00:00:04.0000000Z replica a serving", IsError = false },
                new ResourceLogLine { ResourceName = "api-service-def", LineNumber = 1, Content = "2026-01-01T00:00:01.0000000Z replica b failed to bind port", IsError = true },
                new ResourceLogLine { ResourceName = "api-service-abc", LineNumber = 3, Content = "2026-01-01T00:00:05.0000000Z replica a healthy", IsError = false },
                new ResourceLogLine { ResourceName = "api-service-def", LineNumber = 2, Content = "2026-01-01T00:00:02.0000000Z replica b exited", IsError = true }
            ]
        };
        monitor.AddConnection("socket.hash1", connection);
        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonSerializer.SerializeToElement("api-service"),
            ["from"] = JsonSerializer.SerializeToElement("head"),
            ["limit"] = JsonSerializer.SerializeToElement(3)
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text;
        Assert.StartsWith("Returned first 3 console logs. Later 2 console logs not returned because of size limits.", text, StringComparison.Ordinal);
        Assert.Equal(
            "replica b failed to bind port\nreplica b exited\nreplica a started",
            ExtractCodeBlockContent(text).ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task ListConsoleLogsTool_HeadWithSearch_OrdersUntimestampedMatchesByPrecedingReplicaTimestamp()
    {
        // The untimestamped continuation line from replica a matches the search, but the timestamped
        // line it continues doesn't. It must still be ordered at 00:00:03, after replica b's 00:00:01
        // failure that arrives later.
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines =
            [
                new ResourceLogLine { ResourceName = "api-service-abc", LineNumber = 1, Content = "2026-01-01T00:00:03.0000000Z replica a unhandled exception", IsError = true },
                new ResourceLogLine { ResourceName = "api-service-abc", LineNumber = 2, Content = "   at ReplicaA.Startup() failure", IsError = true },
                new ResourceLogLine { ResourceName = "api-service-def", LineNumber = 1, Content = "2026-01-01T00:00:01.0000000Z replica b failure binding port", IsError = true }
            ]
        };
        monitor.AddConnection("socket.hash1", connection);
        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonSerializer.SerializeToElement("api-service"),
            ["from"] = JsonSerializer.SerializeToElement("head"),
            ["search"] = JsonSerializer.SerializeToElement("failure"),
            ["limit"] = JsonSerializer.SerializeToElement(1)
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text;
        Assert.StartsWith("Returned first 1 console log. Later 1 console log not returned because of size limits.", text, StringComparison.Ordinal);
        Assert.Equal("replica b failure binding port", ExtractCodeBlockContent(text).ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("""{"from":"middle"}""", "Argument 'from' must be one of: tail, head.")]
    [InlineData("""{"limit":2001}""", "Argument 'limit' must be an integer from 1 through 2000.")]
    public async Task ListConsoleLogsTool_RejectsInvalidArguments(string json, string expectedMessage)
    {
        using var document = JsonDocument.Parse(json);
        var arguments = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        arguments["resourceName"] = JsonSerializer.SerializeToElement("api-service");
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("socket.hash1", new TestAppHostAuxiliaryBackchannel());
        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Equal(ModelContextProtocol.McpErrorCode.InvalidParams, exception.ErrorCode);
        Assert.Equal(expectedMessage, exception.Message);
    }

    private static string ExtractCodeBlockContent(string text)
    {
        var match = Regex.Match(text, @"```plaintext\s*(.*?)\s*```", RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}
