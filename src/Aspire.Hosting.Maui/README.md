# Aspire.Hosting.Maui

This library provides support for running .NET MAUI applications within an Aspire application model. It enables local development and debugging of MAUI apps alongside other services in your distributed application.

## Getting Started

### Adding a MAUI Application

Add a MAUI project to your Aspire app host:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var mauiApp = builder.AddMauiProject("mauiapp", "../MauiApp/MauiApp.csproj");
```

### Adding Platform Targets

Add specific platform targets for your MAUI application:

```csharp
// Windows
mauiApp.AddWindowsDevice();

// macOS Catalyst
mauiApp.AddMacCatalystDevice();

// iOS Simulator
mauiApp.AddiOSSimulator();

// iOS Device
mauiApp.AddiOSDevice();

// Android Device
mauiApp.AddAndroidDevice();

// Android Emulator
mauiApp.AddAndroidEmulator();
```

You can optionally specify custom names and device/simulator IDs:

```csharp
mauiApp.AddWindowsDevice("my-windows-app");

// iOS with specific simulator UDID
mauiApp.AddiOSSimulator("iphone-15-sim", "E25BBE37-69BA-4720-B6FD-D54C97791E79");

// iOS with specific device UDID (requires device provisioning)
mauiApp.AddiOSDevice("my-iphone", "00008030-001234567890123A");

// Android with specific emulator
mauiApp.AddAndroidEmulator("pixel-7-emulator", "Pixel_7_API_33");

// Android with specific device serial
mauiApp.AddAndroidDevice("my-pixel", "abc12345");
```

> **Note on Device/Simulator ID Validation**: The iOS methods include validation to help detect common mistakes:
> - `AddiOSDevice()` will fail at startup if you pass a GUID-format ID (which is typical for Simulator UDIDs)
> - `AddiOSSimulator()` will fail at startup if you pass a non-GUID format ID (which is typical for device UDIDs)
> 
> These validation errors appear in the dashboard when you try to start the resource, making it clear if you've accidentally swapped device and simulator IDs.

## OpenTelemetry Connectivity for Mobile Platforms

Mobile devices, Android emulators, and iOS simulators cannot reach `localhost` where the Aspire dashboard's OTLP endpoint typically runs. This library provides a simple way to configure OpenTelemetry connectivity using dev tunnels.

### Using Dev Tunnel

Automatically create and configure a dev tunnel for the dashboard's OTLP endpoint. This is needed when running a .NET MAUI app on:
- Android emulator or device
- iOS simulator or device

You should not need this when running on Windows or Mac Catalyst (they can access localhost directly).

```csharp
// Android emulator with OTLP dev tunnel
mauiApp.AddAndroidEmulator()
    .WithOtlpDevTunnel();

// iOS simulator with OTLP dev tunnel
mauiApp.AddiOSSimulator()
    .WithOtlpDevTunnel();

// iOS device with OTLP dev tunnel
mauiApp.AddiOSDevice()
    .WithOtlpDevTunnel();
```

When `WithOtlpDevTunnel()` is not added, things will still work, however tracing, metrics and telemetry data will not be complete.

This method automatically:
- Resolves the dashboard's OTLP endpoint from configuration
- Creates a dev tunnel for it
- Configures the MAUI platform to use the tunneled endpoint
- Handles all service discovery and environment variable configuration

**Requirements:**
- Aspire.Hosting.DevTunnels package must be referenced
- Dev tunnel CLI must be installed (automatic prompt if missing)
- User must be logged in to dev tunnel service (automatic prompt if needed)

### Environment Variables Set

When you configure OTLP with dev tunnel, the following environment variables are automatically set:

- `OTEL_EXPORTER_OTLP_ENDPOINT`: The dev tunnel URL for the OTLP endpoint
- `OTEL_EXPORTER_OTLP_PROTOCOL`: Set to `grpc` (standard Aspire configuration)
- `OTEL_SERVICE_NAME`: The resource name
- `OTEL_RESOURCE_ATTRIBUTES`: Service instance ID

## Example: Complete Aspire App with MAUI

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Add backend services
var apiService = builder.AddProject<Projects.ApiService>("apiservice");

// Create a dev tunnel for the API service
var apiTunnel = builder.AddDevTunnel("api-tunnel")
    .WithAnonymousAccess()
    .WithReference(apiService.GetEndpoint("https"));

// Add MAUI app with multiple platform targets
var mauiApp = builder.AddMauiProject("mauiapp", "../MauiApp/MauiApp.csproj");

// Windows - can access localhost directly
mauiApp.AddWindowsDevice()
    .WithReference(apiService);

// Android Emulator - needs dev tunnels for both API and OTLP
mauiApp.AddAndroidEmulator()
    .WithOtlpDevTunnel() // For telemetry
    .WithReference(apiService, apiTunnel); // For API calls

// Android Device - same configuration
mauiApp.AddAndroidDevice()
    .WithOtlpDevTunnel()
    .WithReference(apiService, apiTunnel);

// iOS Simulator - needs dev tunnels for both API and OTLP
mauiApp.AddiOSSimulator()
    .WithOtlpDevTunnel() // For telemetry
    .WithReference(apiService, apiTunnel); // For API calls

// iOS Device - same configuration
mauiApp.AddiOSDevice()
    .WithOtlpDevTunnel()
    .WithReference(apiService, apiTunnel);

builder.Build().Run();
```

## Platform-Specific Notes

### iOS

**Finding Simulator UDIDs:**
```bash
# Using simctl
/Applications/Xcode.app/Contents/Developer/usr/bin/simctl list

# Or use Xcode: Window > Devices and Simulators
```

**Device Requirements:**
- iOS development requires macOS
- Physical devices require provisioning: [Microsoft Learn - iOS Device Provisioning](https://learn.microsoft.com/dotnet/maui/ios/device-provisioning)
- Find device UDID in Xcode: Window > Devices and Simulators

### Android

**Finding Emulator Names:**
```bash
# List all available Android virtual devices
%ANDROID_HOME%\emulator\emulator.exe -list-avds

# Or use Android Studio: Tools > Device Manager
```

**Finding Device Serials:**
```bash
# List connected Android devices
adb devices
```

## Requirements

- .NET 10.0 or later
- MAUI workload must be installed: `dotnet workload install maui`
- Platform-specific SDKs:
  - Windows: Windows SDK 10.0.19041.0 or later
  - macOS: Xcode with command-line tools
  - Android: Android SDK via Visual Studio or Android Studio

## Feedback & Issues

Please file issues at https://github.com/microsoft/aspire/issues
