// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.Telemetry;
using Bunit;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using OpenTelemetry.Proto.Common.V1;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class StructuredLogDetailsTests : DashboardTestContext
{
    [Theory]
    [InlineData("commandText", "mssql", "Microsoft.EntityFrameworkCore.Database.Command", DashboardUIHelpers.SqlFormat, DashboardUIHelpers.SqlFormat)]
    [InlineData("db.query.text", "postgresql", "Npgsql.Command", DashboardUIHelpers.SqlFormat, DashboardUIHelpers.SqlFormat)]
    [InlineData("db.statement", "mysql", "MySqlConnector.MySqlCommand", DashboardUIHelpers.SqlFormat, DashboardUIHelpers.SqlFormat)]
    [InlineData("commandText", "mssql", "NHibernate.SQL", DashboardUIHelpers.SqlFormat, DashboardUIHelpers.SqlFormat)]
    [InlineData("db.statement", "sqlite", "Microsoft.EntityFrameworkCore.Database.Command", DashboardUIHelpers.SqlFormat, DashboardUIHelpers.SqlFormat)]
    [InlineData("db.query.text", "redis", "Microsoft.EntityFrameworkCore.Database.Command", null, DashboardUIHelpers.SqlFormat)]
    [InlineData("commandText", "mssql", "MyApp.Controllers.CommandsController", DashboardUIHelpers.SqlFormat, null)]
    [InlineData("sql", "postgresql", "MyApp.Services.QueryService", DashboardUIHelpers.SqlFormat, null)]
    [InlineData("commandText", "mssql", "Microsoft.EntityFrameworkCore.Query", DashboardUIHelpers.SqlFormat, null)]
    [InlineData("db.query.text", "postgresql", "MyApp.Services.QueryService", DashboardUIHelpers.SqlFormat, null)]
    [InlineData("db.statement", "mongodb", "MyApp.Services.QueryService", null, null)]
    [InlineData("db.query.text", null, "MyApp.Services.QueryService", null, null)]
    [InlineData("sql", null, "MyApp.Services.QueryService", null, null)]
    [InlineData("commandText", null, "Npgsql.Command", DashboardUIHelpers.SqlFormat, DashboardUIHelpers.SqlFormat)]
    public void Render_QueryField_SetsVisualizerFormatOnlyOnValue(string name, string? system, string source, string? expectedFormat, string? expectedMessageFormat)
    {
        StructuredLogsSetupHelpers.SetupStructuredLogsDetails(this);
        var attributes = new List<KeyValuePair<string, string>> { KeyValuePair.Create(name, "SELECT 1") };
        if (system is not null)
        {
            attributes.Add(KeyValuePair.Create("db.system.name", system));
        }

        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var resource = new OtlpResource("app", "instance", uninstrumentedPeer: false, context);
        var model = new StructureLogsDetailsViewModel
        {
            LogEntry = TelemetryTestHelpers.CreateOtlpLogEntry(
                record: TelemetryTestHelpers.CreateLogRecord(message: "Executed DbCommand\nSELECT 1", attributes: attributes),
                resourceView: resource.GetView([]),
                scope: TelemetryTestHelpers.CreateOtlpScope(context, name: source),
                context: context)
        };

        var cut = Render<StructuredLogDetails>(parameters => parameters.Add(p => p.ViewModel, model));

        Assert.Equal(expectedFormat, cut.Instance.FilteredItems.Single(p => p.Name == name).TextVisualizerFormat);
        Assert.Equal(expectedMessageFormat, cut.Instance.FilteredItems.Single(p => p.Name == "Message").TextVisualizerFormat);
        var values = cut.FindComponents<GridValue>();
        var queryValue = Assert.Single(values, v => v.Instance.ValueDescription == name);
        Assert.Equal(expectedFormat, queryValue.Instance.TextVisualizerFormat);
        var queryName = Assert.Single(values, v => v.Instance.Value == name);
        Assert.Null(queryName.Instance.TextVisualizerFormat);
        var messageValue = Assert.Single(values, v => v.Instance.ValueDescription == "Message");
        Assert.Equal(expectedMessageFormat, messageValue.Instance.TextVisualizerFormat);
        Assert.Equal("Executed DbCommand\nSELECT 1", messageValue.Instance.Value);
        var messageName = Assert.Single(values, v => v.Instance.Value == "Message");
        Assert.Null(messageName.Instance.TextVisualizerFormat);
    }

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
        var cut = Render<StructuredLogDetails>(builder =>
        {
            builder.Add(p => p.ViewModel, model);
        });

        // Assert
        AssertUniqueKeys(cut.Instance.FilteredContextItems);
        AssertUniqueKeys(cut.Instance.FilteredExceptionItems);
        AssertUniqueKeys(cut.Instance.FilteredResourceItems);
        AssertUniqueKeys(cut.Instance.FilteredItems);

        Assert.True(cut.FindComponent<FluentAccordion>().Instance.Block);
        Assert.All(cut.FindComponents<FluentAccordionItem>(), item => Assert.False(string.IsNullOrEmpty(item.Instance.Header)));
        Assert.Empty(cut.FindComponents<FluentDivider>());
        var actionsButton = cut.Find(".structured-log-details-actions");
        Assert.Contains("toolbar-button", actionsButton.ClassList);
        Assert.Contains("details-toolbar-button", actionsButton.ClassList);
        Assert.False(actionsButton.HasAttribute("appearance"));

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
