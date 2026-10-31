// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
var app = builder.Build();

app.MapGet("/{**path}", async (string? path, IConfiguration configuration, IHttpClientFactory clients, CancellationToken cancellationToken) =>
{
    var api = configuration["API_URL"] ?? throw new InvalidOperationException("API_URL is required.");
    using var client = clients.CreateClient();
    using var response = await client.GetAsync($"{api}/{path}", cancellationToken);
    return Results.Content(await response.Content.ReadAsStringAsync(cancellationToken),
        response.Content.Headers.ContentType?.ToString(), statusCode: (int)response.StatusCode);
});

app.Run();
