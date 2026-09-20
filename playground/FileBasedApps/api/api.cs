#!/usr/bin/env dotnet

#:sdk Microsoft.NET.Sdk.Web
#:property CentralPackageTransitivePinningEnabled=false
#:package Microsoft.AspNetCore.OpenApi
#:project ../../Playground.ServiceDefaults

using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

builder.Services.AddOpenApi();

var app = builder.Build();

app.MapGet("/", () => new HelloResponse { Message = "Hello, World!" })
    .WithName("HelloWorld");

app.MapDefaultEndpoints();

app.Run();

sealed class HelloResponse
{
    public string Message { get; set; } = "Hello, World!";
}

[JsonSerializable(typeof(HelloResponse))]
sealed partial class AppJsonSerializerContext : JsonSerializerContext
{

}
