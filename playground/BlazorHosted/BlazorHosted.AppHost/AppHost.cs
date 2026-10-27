var builder = DistributedApplication.CreateBuilder(args);

var weatherApi = builder.AddDotnetProject("weatherapi", @"..\BlazorHosted.WeatherApi\BlazorHosted.WeatherApi.csproj");

var timeApi = builder.AddDotnetProject("timeapi", @"..\BlazorHosted.TimeApi\BlazorHosted.TimeApi.csproj");

builder.AddDotnetProject("blazorapp", @"..\BlazorHosted.Server\BlazorHosted.csproj")
    .ProxyBlazorService(weatherApi)
    .ProxyBlazorService(timeApi)
    .ProxyBlazorTelemetry();

builder.Build().Run();
