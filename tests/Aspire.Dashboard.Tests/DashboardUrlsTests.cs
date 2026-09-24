// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Utils;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class DashboardUrlsTests
{
    private const string PlaceholderInput = "!@#";

    // There is a difference in behavior between Uri.EscapeDataString and QueryHelpers.AddQueryString
    // with relation to ! and @ - they are encoded by the former, but not the latter.
    // It is not required to encode either - some implementations do, and some do not. However, ASP.NET will decode
    // both the encoded and unencoded characters to the same character, so it has no practical effect.
    private const string PlaceholderAllCharactersEncoded = "%21%40%23";
    private const string PlaceholderAllButExclamationMarkEncoded = "!@%23";

    [Fact]
    public void ConsoleLogsUrl_HtmlValues_CorrectlyEscaped()
    {
        Assert.Equal($"/consolelogs/resource/resource{PlaceholderAllCharactersEncoded}", DashboardUrls.ConsoleLogsUrl(resource: $"resource{PlaceholderInput}"));
    }

    [Fact]
    public void StructuredLogsUrl_HtmlValues_CorrectlyEscaped()
    {
        var singleFilterUrl = DashboardUrls.StructuredLogsUrl(
            resource: $"resource{PlaceholderInput}",
            logLevel: "error",
            filters: TelemetryFilterFormatter.SerializeFiltersToString([
                new FieldTelemetryFilter { Condition = FilterCondition.Contains, Field = "test", Value = "value" }
            ]),
            traceId: PlaceholderInput,
            spanId: PlaceholderInput);

        Assert.Equal($"/structuredlogs/resource/resource{PlaceholderAllCharactersEncoded}?logLevel=error&filters=test%3Acontains%3Avalue&traceId={PlaceholderAllButExclamationMarkEncoded}&spanId={PlaceholderAllButExclamationMarkEncoded}", singleFilterUrl);

        var multipleFiltersIncludingSpacesUrl = DashboardUrls.StructuredLogsUrl(
            resource: $"resource{PlaceholderInput}",
            logLevel: "error",
            filters: TelemetryFilterFormatter.SerializeFiltersToString([
                new FieldTelemetryFilter { Condition = FilterCondition.Contains, Field = "test", Value = "value" },
                new FieldTelemetryFilter { Condition = FilterCondition.GreaterThan, Field = "fieldWithSpacedValue", Value = "!! multiple words here !!", Enabled = false },
                new FieldTelemetryFilter { Condition = FilterCondition.NotEqual, Field = "name", Value = "nameValue" },
            ]),
            traceId: PlaceholderInput,
            spanId: PlaceholderInput);
        Assert.Equal($"/structuredlogs/resource/resource{PlaceholderAllCharactersEncoded}?logLevel=error&filters=test%3Acontains%3Avalue%20fieldWithSpacedValue%3Agt%3A!!%2Bmultiple%2Bwords%2Bhere%2B!!%3Adisabled%20name%3A!equals%3AnameValue&traceId={PlaceholderAllButExclamationMarkEncoded}&spanId={PlaceholderAllButExclamationMarkEncoded}", multipleFiltersIncludingSpacesUrl);
    }

    [Fact]
    public void TracesUrl_HtmlValues_CorrectlyEscaped()
    {
        Assert.Equal($"/traces/resource/resource{PlaceholderAllCharactersEncoded}", DashboardUrls.TracesUrl(resource: $"resource{PlaceholderInput}"));
    }

    [Fact]
    public void TraceDetailUrl_HtmlValues_CorrectlyEscaped()
    {
        Assert.Equal($"/traces/detail/traceId{PlaceholderAllCharactersEncoded}", DashboardUrls.TraceDetailUrl(traceId: $"traceId{PlaceholderInput}"));
    }

    [Fact]
    public void MetricsUrl_HtmlValues_CorrectlyEscaped()
    {
        var url = DashboardUrls.MetricsUrl(
            resource: $"resource{PlaceholderInput}",
            meter: $"meter{PlaceholderInput}",
            instrument: $"meter{PlaceholderInput}",
            duration: 10,
            view: "table");

        Assert.Equal($"/metrics/resource/resource{PlaceholderAllCharactersEncoded}?meter=meter{PlaceholderAllButExclamationMarkEncoded}&instrument=meter{PlaceholderAllButExclamationMarkEncoded}&duration=10&view=table", url);
    }

    [Fact]
    public void SetLanguagesUrl_HtmlValues_CorrectlyEscaped()
    {
        Assert.Equal("/api/set-language?language=fr-FR&redirectUrl=%2Fhi", DashboardUrls.SetLanguageUrl("fr-FR", "/hi"));
    }

    [Fact]
    public void CombineUrl_ComposesBasePathQueryAndRequestFragment()
    {
        var result = DashboardUrls.CombineUrl(
            "https://localhost:18888/base?api-version=1#base-fragment",
            "/api/telemetry/logs?limit=100#request-fragment");

        Assert.Equal(
            "https://localhost:18888/base/api/telemetry/logs?api-version=1&limit=100#request-fragment",
            result);
    }

    [Fact]
    public void TelemetryLogsApiUrl_WithSearch_AppendsSearchQueryParameter()
    {
        var url = DashboardUrls.TelemetryLogsApiUrl("http://localhost:18888", search: "connection error");

        Assert.Contains("search=connection%20error", url);
    }

    [Fact]
    public void TelemetryLogsApiUrl_WithoutSearch_DoesNotAppendSearchParameter()
    {
        var url = DashboardUrls.TelemetryLogsApiUrl("http://localhost:18888");

        Assert.DoesNotContain("search", url);
    }

    [Fact]
    public void TelemetrySpansApiUrl_WithSearch_AppendsSearchQueryParameter()
    {
        var url = DashboardUrls.TelemetrySpansApiUrl("http://localhost:18888", search: "GET /api");

        Assert.Contains("search=GET%20%2Fapi", url);
    }

    [Fact]
    public void TelemetryTracesApiUrl_WithSearch_AppendsSearchQueryParameter()
    {
        var url = DashboardUrls.TelemetryTracesApiUrl("http://localhost:18888", search: "timeout");

        Assert.Contains("search=timeout", url);
    }

    [Fact]
    public void TelemetryLogsApiUrl_WithSearchAndOtherParams_AppendsAllParameters()
    {
        var url = DashboardUrls.TelemetryLogsApiUrl("http://localhost:18888", resources: ["service1"], severity: "error", search: "failed");

        Assert.Contains("resource=service1", url);
        Assert.Contains("severity=error", url);
        Assert.Contains("search=failed", url);
    }

    [Theory]
    [InlineData(
        "https://user:password@dashboard.dev.localhost:8443/base/login?t=secret&view=resources#fragment",
        true,
        "https://user:password@localhost:8443/base?t=secret&view=resources#fragment")]
    [InlineData(
        "https://example.com:8443/base?view=resources",
        false,
        "https://example.com:8443/base?view=resources")]
    [InlineData("tcp://cache.example.com:6379", false, null)]
    [InlineData("file:///repo/private.txt", false, null)]
    [InlineData("not-a-url", true, null)]
    public void NormalizeDashboardRequestUrl_PreservesRequestAuthentication(
        string input,
        bool stripLoginPath,
        string? expected)
    {
        Assert.Equal(
            expected,
            DashboardUrls.NormalizeDashboardRequestUrl(input, stripLoginPath));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("http://localhost:18888", null)]
    [InlineData("http://localhost:18888/login", null)]
    [InlineData("http://localhost:18888/login?t=authtoken123", "authtoken123")]
    [InlineData("https://localhost:16319/login?t=d8d8255df4c79aebcb5b7325828ccb20", "d8d8255df4c79aebcb5b7325828ccb20")]
    [InlineData("http://localhost/base/login?t=token123", "token123")]
    [InlineData("https://example.com:8080/app/deep/login?t=abc", "abc")]
    [InlineData("http://localhost/base/login?t=token%2Bvalue", "token+value")]
    [InlineData("http://localhost:18888/login?other=value", null)]
    [InlineData("http://localhost:18888/login?t=", null)]
    [InlineData("http://localhost/base/notlogin?t=secret", null)]
    [InlineData("invalid-url", null)]
    public void ExtractDashboardLoginToken_ReturnsOnlyLoginToken(
        string? input,
        string? expected)
    {
        Assert.Equal(expected, DashboardUrls.ExtractDashboardLoginToken(input));
    }
}
