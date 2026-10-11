var builder = DistributedApplication.CreateBuilder(args);

var weatherApi = builder.AddProject("webapi", @"../AspireWithMaui.WeatherApi/AspireWithMaui.WeatherApi.csproj");

var publicDevTunnel = builder.AddDevTunnel("devtunnel-public")
    .WithAnonymousAccess() // All ports on this tunnel default to allowing anonymous access
    .WithReference(weatherApi.GetEndpoint("https"));

var mauiapp = builder.AddMauiProject("mauiapp", @"../AspireWithMaui.MauiClient/AspireWithMaui.MauiClient.csproj");

mauiapp.AddWindowsDevice()
    .WithReference(weatherApi);

mauiapp.AddMacCatalystDevice()
    .WithReference(weatherApi);

// Add iOS simulator and select from available simulators when the resource starts.
mauiapp.AddiOSSimulator()
    .WithOtlpDevTunnel() // Needed to get the OpenTelemetry data to "localhost"
    .WithReference(weatherApi, publicDevTunnel); // Needs a dev tunnel to reach "localhost"

// Add iOS device (requires device provisioning)
// Uncomment to use with a physical iOS device:
// mauiapp.AddiOSDevice()
//     .WithOtlpDevTunnel() // Needed to get the OpenTelemetry data to "localhost"
//     .WithReference(weatherApi, publicDevTunnel); // Needs a dev tunnel to reach "localhost"

// Add Android emulator and select from available AVDs when the resource starts.
mauiapp.AddAndroidEmulator()
    .WithOtlpDevTunnel() // Needed to get the OpenTelemetry data to "localhost"
    .WithReference(weatherApi, publicDevTunnel); // Needs a dev tunnel to reach "localhost"

builder.Build().Run();
