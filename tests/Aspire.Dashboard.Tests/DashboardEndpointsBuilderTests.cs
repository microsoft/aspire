// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Dashboard.Api;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class DashboardEndpointsBuilderTests
{
    [Theory]
    [InlineData("/api/telemetry/spans", "?limit=0")]
    [InlineData("/api/telemetry/spans", "?limit=-1")]
    [InlineData("/api/telemetry/spans", "?limit=-2147483648")]
    [InlineData("/api/telemetry/spans", "?limit=0&follow=true")]
    [InlineData("/api/telemetry/logs", "?limit=0")]
    [InlineData("/api/telemetry/logs", "?limit=-1")]
    [InlineData("/api/telemetry/logs", "?limit=0&follow=true")]
    [InlineData("/api/telemetry/traces", "?limit=0")]
    [InlineData("/api/telemetry/traces", "?limit=-1")]
    public async Task TelemetryEndpoint_NonPositiveLimit_ReturnsBadRequest(string path, string queryString)
    {
        using var repositoryContext = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository();
        await using var app = CreateApp(repositoryContext.Repository);

        var endpoint = GetGetEndpoint(app, path);
        var httpContext = new DefaultHttpContext
        {
            RequestServices = app.Services
        };
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Path = path;
        httpContext.Request.QueryString = new QueryString(queryString);
        httpContext.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(httpContext);

        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
        httpContext.Response.Body.Position = 0;
        var problemDetails = await JsonSerializer.DeserializeAsync<ProblemDetails>(
            httpContext.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(problemDetails);
        Assert.Equal("Invalid limit", problemDetails.Title);
        Assert.Equal("The limit value must be greater than 0.", problemDetails.Detail);
    }

    private static WebApplication CreateApp(SqliteTelemetryRepository repository)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new TelemetryApiService(repository));
        var app = builder.Build();
        app.MapTelemetryApi(new DashboardOptions());
        return app;
    }

    private static RouteEndpoint GetGetEndpoint(IEndpointRouteBuilder endpoints, string path)
    {
        return endpoints.DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint =>
                $"/{endpoint.RoutePattern.RawText?.TrimStart('/')}" == path &&
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Get) == true);
    }
}
