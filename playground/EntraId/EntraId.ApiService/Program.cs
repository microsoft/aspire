// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

const string AccessAsUserPolicy = "access_as_user";

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// The AppHost's WithReference(entraApi) fills in the AzureAd section. Microsoft.Identity.Web accepts tokens whose
// audience is the API's client ID or api://{clientId}, and rejects any other audience with 401.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration);

// Delegated tokens carry the scopes granted to the caller in the "scp" claim. A valid token without the scope, such as an
// app-only token, is rejected with 403.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AccessAsUserPolicy, policy => policy
        .RequireAuthenticatedUser()
        .Requirements.Add(new ScopeAuthorizationRequirement([AccessAsUserPolicy])));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

string[] summaries = ["Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"];

app.MapGet("/weatherforecast", (ClaimsPrincipal user) =>
{
    var forecast = Enumerable.Range(1, 5).Select(index => new WeatherForecast(
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]))
        .ToArray();

    // Echo the claims the web front end's token carried so the playground can show that the audience and delegated scope arrived.
    return new WeatherResponse(
        user.FindFirstValue("aud"),
        user.FindFirstValue("scp"),
        user.FindFirstValue("name") ?? user.FindFirstValue("preferred_username"),
        forecast);
}).RequireAuthorization(AccessAsUserPolicy);

app.MapDefaultEndpoints();

app.Run();

internal sealed record WeatherForecast(DateOnly Date, int TemperatureC, string Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

internal sealed record WeatherResponse(string? Audience, string? Scopes, string? User, WeatherForecast[] Forecast);
