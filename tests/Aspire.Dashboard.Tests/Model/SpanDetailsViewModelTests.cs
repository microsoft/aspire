// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class SpanDetailsViewModelTests
{
    [Theory]
    [InlineData("db.query.text", "db.system.name", "postgresql", DashboardUIHelpers.SqlFormat)]
    [InlineData("db.statement", "db.system", "mssql", DashboardUIHelpers.SqlFormat)]
    [InlineData("db.query.text", "db.system.name", "redis", null)]
    public void Create_QueryAttribute_SetsVisualizerFormat(string name, string systemKey, string system, string? expectedFormat)
    {
        using var repositoryContext = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository();
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var resource = new OtlpResource("app", "instance", uninstrumentedPeer: false, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var span = TelemetryTestHelpers.CreateOtlpSpan(resource, trace, TelemetryTestHelpers.CreateOtlpScope(context),
            spanId: "1", parentSpanId: null, startDate: DateTime.UtcNow, attributes:
            [
                KeyValuePair.Create(name, "SELECT 1"),
                KeyValuePair.Create(systemKey, system)
            ]);

        var vm = SpanDetailsViewModel.Create(span, repositoryContext.Repository, [resource]);

        Assert.Equal(expectedFormat, vm.Properties.Single(p => p.Name == name).TextVisualizerFallbackFormat);
        Assert.All(vm.Properties.Where(p => p.Name != name), p => Assert.Null(p.TextVisualizerFallbackFormat));
    }
}
