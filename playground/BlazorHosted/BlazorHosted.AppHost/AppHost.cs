var builder = DistributedApplication.CreateBuilder(args);

var weatherApi = builder.AddProject<Projects.BlazorHosted_WeatherApi>("weatherapi");

var timeApi = builder.AddProject<Projects.BlazorHosted_TimeApi>("timeapi");

builder.AddProject<Projects.BlazorHosted>("blazorapp")
    .ProxyBlazorService(weatherApi)
    .ProxyBlazorService(timeApi)
    .ProxyBlazorTelemetry();

builder.Build().Run();
