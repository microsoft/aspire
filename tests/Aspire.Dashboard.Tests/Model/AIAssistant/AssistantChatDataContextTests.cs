// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Assistant;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Shared.ConsoleLogs;
using Aspire.Tests.Shared.DashboardModel;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.Model.AIAssistant;

public class AssistantChatDataContextTests
{
    private static readonly DateTime s_testTime = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetStructuredLogs_ExceedTokenLimit_ReturnMostRecentItems()
    {
        // Arrange
        var repository = CreateRepository();

        var scopeLogs = new ScopeLogs();
        for (var i = 0; i < 20; i++)
        {
            var logRecord = CreateLogRecord(message: $"Log {i}: {new string((char)('a' + i), 10_000)}", time: s_testTime.AddMinutes(i));
            scopeLogs.LogRecords.Add(logRecord);
        }
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs = { scopeLogs }
            }
        });
        var dataContext = CreateAssistantChatDataContext(telemetryRepository: repository);

        // Act
        var result = await dataContext.GetStructuredLogsAsync(resourceName: null, CancellationToken.None);

        // Assert
        for (var i = 6; i < 20; i++)
        {
            Assert.Contains($"Log {i}:", result);
        }
        Assert.Contains("Returned latest 14 log entries. Earlier 6 log entries not returned because of size limits.", result);
    }

    [Fact]
    public async Task GetTraces_ExceedTokenLimit_ReturnMostRecentItems()
    {
        // Arrange
        var repository = CreateRepository();

        var scopeSpans = new ScopeSpans();
        for (var i = 0; i < 20; i++)
        {
            var span = CreateSpan(traceId: $"{i}", spanId: $"{i}-1", startTime: s_testTime.AddMinutes(i), endTime: s_testTime.AddMinutes(10), attributes: [new KeyValuePair<string, string>("message", $"Log {i}: {new string((char)('a' + i), 10_000)}")]);
            scopeSpans.Spans.Add(span);
        }
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans = { scopeSpans }
            }
        });
        var dataContext = CreateAssistantChatDataContext(telemetryRepository: repository);

        // Act
        var result = await dataContext.GetTracesAsync(resourceName: null, CancellationToken.None);

        // Assert
        for (var i = 7; i < 20; i++)
        {
            Assert.Contains($"Test span. Id: {i}", result);
        }
        Assert.Contains("Returned latest 13 traces. Earlier 7 traces not returned because of size limits.", result);
    }

    [Fact]
    public async Task GetConsoleLogs_ExceedTokenLimit_ReturnMostRecentItems()
    {
        // Arrange
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var testResource = ModelTestHelpers.CreateResource(resourceName: "test-resource", state: KnownResourceState.Running);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: name =>
            {
                return consoleLogsChannel;
            },
            initialResources: [testResource]);

        var dataContext = CreateAssistantChatDataContext(dashboardClient: dashboardClient);

        for (var i = 0; i < 20; i++)
        {
            var line = new string((char)('a' + i), 10_000);
            consoleLogsChannel.Writer.TryWrite([new ResourceLogLine(i + 1, line, IsErrorMessage: false)]);
        }
        consoleLogsChannel.Writer.Complete();

        // Act
        var result = await dataContext.GetConsoleLogsAsync(resourceName: "test-resource", CancellationToken.None);

        // Assert
        for (var i = 5; i < 20; i++)
        {
            var line = SharedAIHelpers.LimitLength(new string((char)('a' + i), 10_000));
            Assert.Contains(line, result);
        }
        Assert.Contains("Returned latest 15 console logs. Earlier 5 console logs not returned because of size limits.", result);
    }

    internal static AssistantChatDataContext CreateAssistantChatDataContext(TelemetryRepository? telemetryRepository = null, IDashboardClient? dashboardClient = null)
    {
        var dashboardOptions = new DashboardOptions
        {
            Frontend = new FrontendOptions
            {
                EndpointUrls = "http://localhost:5000"
            }
        };
        Assert.True(dashboardOptions.Frontend.TryParseOptions(out _));

        var context = new AssistantChatDataContext(
            telemetryRepository ?? CreateRepository(),
            dashboardClient ?? new MockDashboardClient(),
            [],
            new TestStringLocalizer<Dashboard.Resources.AIAssistant>(),
            new TestOptionsMonitor<DashboardOptions>(dashboardOptions));

        return context;
    }
}
