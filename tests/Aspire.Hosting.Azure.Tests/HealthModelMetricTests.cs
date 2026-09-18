// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.RegularExpressions;
using HealthModelPlayground;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.Azure.Tests;

public class HealthModelMetricTests
{
    [Fact]
    public void ScenarioPreservesTheLocalTopologyAndValidators()
    {
        Assert.Collection(HealthModelScenario.Resources,
            resource => AssertResource(resource, "storefront", "Serving customers.", HealthStatus.Healthy, null, null, "checkout-api", "catalog-api", "identity-api"),
            resource => AssertResource(resource, "checkout-api", "Accepting orders.", HealthStatus.Healthy, null, null, "orders-db-server", "payments-gateway"),
            resource => AssertResource(resource, "orders-db-server", "Accepting connections.", HealthStatus.Healthy, null, null),
            resource => AssertResource(resource, "orders-db", "Migrations applied.", HealthStatus.Healthy, "orders-db-server", null),
            resource => AssertResource(resource, "payments-gateway", "Elevated latency from the payment provider.", HealthStatus.Degraded, null, null),
            resource => AssertResource(resource, "catalog-api", "Serving product data.", HealthStatus.Healthy, null, null, "catalog-db-server", "search-index"),
            resource => AssertResource(resource, "catalog-db-server", "Accepting connections.", HealthStatus.Healthy, null, null),
            resource => AssertResource(resource, "catalog-db", "Migrations applied.", HealthStatus.Healthy, "catalog-db-server", null),
            resource => AssertResource(resource, "search-index", "Index rebuild failed.", HealthStatus.Unhealthy, null, "Shard 3 is offline."),
            resource => AssertResource(resource, "identity-api", "Issuing tokens.", HealthStatus.Healthy, null, null, "identity-cache"),
            resource => AssertResource(resource, "identity-cache", "Cache warm.", HealthStatus.Healthy, null, null));
    }

    [Theory]
    [InlineData("not-a-resource")]
    [InlineData("Storefront")]
    [InlineData("")]
    public void UnknownResourceDoesNotEvaluateAsHealthy(string resourceName)
    {
        Assert.Throws<ArgumentException>(() => HealthModelScenario.Evaluate(resourceName));
    }

    [Fact]
    public async Task MetricsRepresentRegisteredChecksWithoutDependencyAggregation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        HealthModelScenario.AddHealthChecks(services);
        using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HealthModelScenario.Resources.Count, report.Entries.Count);
        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        foreach (var resource in HealthModelScenario.Resources)
        {
            var expected = HealthModelScenario.Evaluate(resource.Name);
            var actual = report.Entries[resource.HealthCheckName];
            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(expected.Description, actual.Description);
            Assert.Equal(expected.Exception?.Message, actual.Exception?.Message);
        }

        Assert.Equal(
        [
            ("catalog-api", "catalog-api_check", 2),
            ("catalog-api", "resource-state", 2),
            ("catalog-db", "catalog-db_check", 2),
            ("catalog-db", "resource-state", 2),
            ("catalog-db-server", "catalog-db-server_check", 2),
            ("catalog-db-server", "resource-state", 2),
            ("checkout-api", "checkout-api_check", 2),
            ("checkout-api", "resource-state", 2),
            ("identity-api", "identity-api_check", 2),
            ("identity-api", "resource-state", 2),
            ("identity-cache", "identity-cache_check", 2),
            ("identity-cache", "resource-state", 2),
            ("orders-db", "orders-db_check", 2),
            ("orders-db", "resource-state", 2),
            ("orders-db-server", "orders-db-server_check", 2),
            ("orders-db-server", "resource-state", 2),
            ("payments-gateway", "payments-gateway_check", 1),
            ("payments-gateway", "resource-state", 2),
            ("search-index", "search-index_check", 0),
            ("search-index", "resource-state", 2),
            ("storefront", "storefront_check", 2),
            ("storefront", "resource-state", 2)
        ],
        ReadSamples(HealthModelMetrics.Format(report, HealthModelScenario.Resources)));
    }

    [Theory]
    [InlineData(HealthStatus.Healthy, 2)]
    [InlineData(HealthStatus.Degraded, 1)]
    [InlineData(HealthStatus.Unhealthy, 0)]
    public void MetricsUseReportedStatusInsteadOfExpectedStatusAndDoNotExposeDetails(HealthStatus status, int expectedValue)
    {
        var resource = HealthModelScenario.Resources.Single(resource => resource.Name == "storefront");
        var report = new HealthReport(new Dictionary<string, HealthReportEntry>
        {
            [resource.HealthCheckName] = new(
                status,
                "Private check description",
                TimeSpan.FromSeconds(1),
                new InvalidOperationException("Private exception"),
                new Dictionary<string, object> { ["secret"] = "Private data" })
        }, TimeSpan.FromSeconds(1));

        Assert.Equal(
        [
            ("storefront", "storefront_check", expectedValue),
            ("storefront", "resource-state", 2)
        ],
        ReadSamples(HealthModelMetrics.Format(report, [resource])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingCheckDoesNotManufactureAHealthyReading(bool includeUnrelatedCheck)
    {
        var resource = HealthModelScenario.Resources.Single(resource => resource.Name == "storefront");
        var entries = new Dictionary<string, HealthReportEntry>();
        if (includeUnrelatedCheck)
        {
            entries["unrelated_check"] = new(HealthStatus.Healthy, null, TimeSpan.Zero, null, null);
        }

        var report = new HealthReport(entries, TimeSpan.Zero);

        Assert.Equal(
            [("storefront", "resource-state", 2)],
            ReadSamples(HealthModelMetrics.Format(report, [resource])));
    }

    [Fact]
    public void LabelsEscapeQuotesBackslashesAndLineFeeds()
    {
        var resource = new HealthModelScenarioResource("api\"\\name\nnext", "Private description", HealthStatus.Unhealthy, null, [], null);
        var report = new HealthReport(new Dictionary<string, HealthReportEntry>
        {
            [resource.HealthCheckName] = new(HealthStatus.Unhealthy, null, TimeSpan.Zero, null, null)
        }, TimeSpan.Zero);

        Assert.Equal(
        [
            ("""api\"\\name\nnext""", """api\"\\name\nnext_check""", 0),
            ("""api\"\\name\nnext""", "resource-state", 2)
        ],
        ReadSamples(HealthModelMetrics.Format(report, [resource])));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    [InlineData("tr-TR")]
    public void MetricsUseInvariantNumbersAndOrdinalOrdering(string culture)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            HealthModelScenarioResource[] resources =
            [
                new("ä", "", HealthStatus.Healthy, null, [], null),
                new("z", "", HealthStatus.Degraded, null, [], null),
                new("I", "", HealthStatus.Unhealthy, null, [], null)
            ];
            var entries = resources.ToDictionary(
                resource => resource.HealthCheckName,
                resource => new HealthReportEntry(resource.HealthStatus, null, TimeSpan.Zero, null, null));
            var report = new HealthReport(entries, TimeSpan.Zero);

            (string, string, int)[] expected =
            [
                ("I", "I_check", 0),
                ("I", "resource-state", 2),
                ("z", "z_check", 1),
                ("z", "resource-state", 2),
                ("ä", "ä_check", 2),
                ("ä", "resource-state", 2)
            ];

            Assert.Equal(expected, ReadSamples(HealthModelMetrics.Format(report, resources)));
            Assert.Equal(expected, ReadSamples(HealthModelMetrics.Format(report, resources.AsEnumerable().Reverse())));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public async Task SharedChecksHonorCancellation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        HealthModelScenario.AddHealthChecks(services);
        using var provider = services.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(cancellation.Token));
    }

    private static void AssertResource(
        HealthModelScenarioResource resource,
        string name,
        string description,
        HealthStatus status,
        string? parentName,
        string? exceptionMessage,
        params string[] dependencies)
    {
        Assert.Equal(name, resource.Name);
        Assert.Equal($"{name}_check", resource.HealthCheckName);
        Assert.Equal(description, resource.Description);
        Assert.Equal(status, resource.HealthStatus);
        Assert.Equal(parentName, resource.ParentName);
        Assert.Equal(exceptionMessage, resource.ExceptionMessage);
        Assert.Equal(dependencies, resource.Dependencies);

        var result = HealthModelScenario.Evaluate(name);
        Assert.Equal(status, result.Status);
        Assert.Equal(description, result.Description);
        if (exceptionMessage is null)
        {
            Assert.Null(result.Exception);
        }
        else
        {
            Assert.Equal(exceptionMessage, Assert.IsType<InvalidOperationException>(result.Exception).Message);
        }
    }

    private static (string ResourceName, string HealthCheck, int Value)[] ReadSamples(string text)
    {
        var lines = text.Split('\n');
        Assert.Equal("# HELP aspire_health_status Simulated resource health: 0 = Unhealthy, 1 = Degraded, 2 = Healthy.", lines[0]);
        Assert.Equal("# TYPE aspire_health_status gauge", lines[1]);
        Assert.Equal("", lines[^1]);

        // Parse the entire exposition line, including the exact label set and order.
        // E.g. aspire_health_status{resource_name="api\"\\name\nnext",health_check="api_check",replica_index="1"} 0
        // Only Prometheus's three label escapes are allowed; extra labels, timestamps,
        // descriptions, raw line feeds, and numeric culture artifacts all fail this grammar.
        return lines[2..^1].Select(line =>
        {
            var match = Regex.Match(
                line,
                """\Aaspire_health_status\{resource_name="((?:[^"\\\r\n]|\\["\\n])*)",health_check="((?:[^"\\\r\n]|\\["\\n])*)",replica_index="1"\} ([012])\z""",
                RegexOptions.CultureInvariant);
            Assert.True(match.Success, $"Invalid metric line: {line}");

            return (match.Groups[1].Value, match.Groups[2].Value, int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
        }).ToArray();
    }
}
