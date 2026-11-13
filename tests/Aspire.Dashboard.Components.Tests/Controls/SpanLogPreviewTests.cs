// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class SpanLogPreviewTests : DashboardTestContext
{
    [Theory]
    [InlineData("Test event", "Test event")]
    [InlineData(null, "Log entry details")]
    public async Task Show_FillsThePreviewWithTheMarkersEntry(string? eventName, string expectedEventName)
    {
        Services.AddLocalization();
        Services.AddSingleton<BrowserTimeProvider, TestTimeProvider>();
        var cut = Render<SpanLogPreview>();

        // Nothing is shown until a marker is reached, so the page holds one empty preview however many logs it has.
        var preview = cut.Find($"#{SpanLogPreview.ElementId}");
        Assert.True(preview.HasAttribute("hidden"));
        Assert.Empty(preview.Children);

        var time = new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);
        foreach (var (id, message) in new[] { (1L, "Test <message>\nwith details"), (2L, "Second message") })
        {
            var entry = CreateLogSummary(id, eventName, message, time.AddSeconds(id));
            await cut.InvokeAsync(() => cut.Instance.Show(entry));

            preview = cut.Find($"#{SpanLogPreview.ElementId}");
            Assert.True(preview.HasAttribute("hidden"));
            Assert.Equal(["DIV", "TABLE"], preview.Children.Select(child => child.TagName));
            Assert.Equal(expectedEventName, preview.QuerySelector(".log-tooltip-title")!.TextContent);
            Assert.Equal("test-scope", preview.QuerySelector(".log-tooltip-subtitle")!.TextContent);
            Assert.Collection(preview.QuerySelectorAll("tr"),
                row => Assert.Equal(["Level", "Information"], row.QuerySelectorAll("td").Select(cell => cell.TextContent)),
                row => Assert.Equal(["Timestamp", FormatHelpers.FormatTimeWithOptionalDate(Services.GetRequiredService<BrowserTimeProvider>(), time.AddSeconds(id), MillisecondsDisplay.Truncated)], row.QuerySelectorAll("td").Select(cell => cell.TextContent)),
                row => Assert.Equal(["Message", message], row.QuerySelectorAll("td").Select(cell => cell.TextContent)));
        }
    }

    private static LogSummary CreateLogSummary(long id, string? eventName, string message, DateTime time)
    {
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        return new LogSummary
        {
            InternalId = id,
            TimeStamp = time,
            Severity = LogLevel.Information,
            Message = message,
            SpanId = "span",
            TraceId = "trace",
            ScopeName = "test-scope",
            EventName = eventName,
            Resource = new OtlpResource("app", "instance", uninstrumentedPeer: false, context),
            ExceptionText = null,
            HasGenAI = false
        };
    }
}
