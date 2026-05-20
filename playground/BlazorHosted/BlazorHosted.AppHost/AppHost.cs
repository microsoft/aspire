var builder = DistributedApplication.CreateBuilder(args);

var weatherApi = builder.AddProject<Projects.BlazorHosted_WeatherApi>("weatherapi");

builder.AddProject<Projects.BlazorHosted>("blazorapp")
    .ProxyBlazorService(weatherApi)
    .ProxyBlazorTelemetry();

builder.Build().Run();
