// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using HealthModelPlayground;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
HealthModelScenario.AddHealthChecks(builder.Services);

var app = builder.Build();

app.MapGet("/metrics", async (HealthCheckService healthChecks, CancellationToken cancellationToken) =>
{
    var report = await healthChecks.CheckHealthAsync(cancellationToken);
    return Results.Text(HealthModelMetrics.Format(report, HealthModelScenario.Resources), HealthModelMetrics.ContentType);
});

// Process liveness is independent of the intentionally degraded and unhealthy simulated resources.
app.MapGet("/alive", () => Results.Text("Alive\n", "text/plain"));

app.Run();
