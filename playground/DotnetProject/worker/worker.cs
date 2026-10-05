#!/usr/bin/env dotnet

// A file-based C# app (a single .cs launched with `dotnet run --file`). Added to the app model via
// AddDotnetProject/addDotnetProject by path, this dogfoods the file-based launch path of the resource.
// File-based apps require .NET 10 or later.

#:sdk Microsoft.NET.Sdk.Web
#:project ../../Playground.ServiceDefaults

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// "apiservice" is resolved by service discovery to the ApiService endpoint the app host injects.
builder.Services.AddHttpClient("apiservice", client => client.BaseAddress = new("http://apiservice"));

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/", async (IHttpClientFactory factory) =>
{
    var client = factory.CreateClient("apiservice");
    var upstream = await client.GetStringAsync("/");
    return $"Hello from the file-based worker!{Environment.NewLine}Upstream apiservice said: {upstream}";
});

app.Run();
