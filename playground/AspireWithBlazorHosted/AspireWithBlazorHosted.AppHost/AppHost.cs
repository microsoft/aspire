var builder = DistributedApplication.CreateBuilder(args);

var weatherApi = builder.AddProject<Projects.AspireWithBlazorHosted_WeatherApi>("weatherapi");

builder.AddProject<Projects.AspireWithBlazorHosted>("blazorapp")
    .ProxyService(weatherApi)
    .ProxyTelemetry();

builder.Build().Run();
