// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Tests.Shared.Telemetry;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Proto.Common.V1;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class StructuredLogDetailsTests : DashboardTestContext
{
    [Fact]
    public void Render_ManyDuplicateAttributes_NoDuplicateKeys()
    {
        // Arrange
        StructuredLogsSetupHelpers.SetupStructuredLogsDetails(this);

        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app = new OtlpResource("app1", "instance1", uninstrumentedPeer: false, context);
        var view = new OtlpResourceView(app, new RepeatedField<KeyValue>
        {
            new KeyValue { Key = "Message", Value = new AnyValue { StringValue = "value1" } },
            new KeyValue { Key = "Message", Value = new AnyValue { StringValue = "value2" } },
            new KeyValue { Key = OtlpResource.SERVICE_NAME, Value = new AnyValue { StringValue = "value1" } }
        });
        var model = new StructureLogsDetailsViewModel
        {
            LogEntry = TelemetryTestHelpers.CreateOtlpLogEntry(
                record: TelemetryTestHelpers.CreateLogRecord(attributes:
                [
                    KeyValuePair.Create("Message", "value1"),
                    KeyValuePair.Create("Message", "value2"),
                    KeyValuePair.Create("event.name", "value1"),
                    KeyValuePair.Create("event.name", "value2")
                ]),
                resourceView: view,
                scope: TelemetryTestHelpers.CreateOtlpScope(
                    context,
                    attributes:
                    [
                        KeyValuePair.Create("Message", "value1"),
                        KeyValuePair.Create("Message", "value2")
                    ]),
                context: context)
        };

        // Act
        var cut = RenderComponent<StructuredLogDetails>(builder =>
        {
            builder.Add(p => p.ViewModel, model);
        });

        // Assert
        AssertUniqueKeys(cut.Instance.FilteredContextItems);
        AssertUniqueKeys(cut.Instance.FilteredExceptionItems);
        AssertUniqueKeys(cut.Instance.FilteredResourceItems);
        AssertUniqueKeys(cut.Instance.FilteredItems);

        static void AssertUniqueKeys(IEnumerable<TelemetryPropertyViewModel> properties)
        {
            var duplicate = properties.GroupBy(p => p.Key).Where(g => g.Count() >= 2).FirstOrDefault();
            if (duplicate != null)
            {
                Assert.Fail($"Duplicate properties with key '{duplicate.Key}'.");
            }
        }
    }
}
