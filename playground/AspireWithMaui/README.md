# AspireWithMaui Playground

This playground demonstrates Aspire integration with .NET MAUI applications.

## Prerequisites

- .NET 10 or later
- .NET MAUI workload

## Getting Started

### Initial Setup

Before building or running the playground, you must restore dependencies and install the MAUI workload.

Run the following commands from the repository root:

**Windows:**
```cmd
.\restore.cmd -mauirestore
```

**Linux/macOS:**
```bash
./restore.sh --mauirestore
```

This will:
1. Restore all Aspire dependencies and set up the local .dotnet SDK
2. Install the MAUI workload into the repository's local `.dotnet` folder (does not affect your global installation)
3. Generate the `AspireWithMaui.slnx` solution file including the playground project

> **Note:** The MAUI workload is installed only in the repository's local `.dotnet` folder and will not interfere with your system-wide .NET installation.
> This also means that you will still need to do this even if you have the MAUI workload already installed in your system-wide .NET installation.

### Running the Playground

After running the restore script with `-mauirestore`, you can build and run the playground:

**Using Visual Studio:**
1. Run `.\restore.cmd -mauirestore` from the repository root (Windows)
2. Open `AspireWithMaui.AppHost` project
3. Set it as the startup project
4. Press F5 to run

**Using VS Code:**
1. Run `.\restore.cmd -mauirestore` (Windows) or `./restore.sh --mauirestore` (Linux/macOS) from the repository root
2. From the repository root, run: `./start-code.sh` or `start-code.cmd`
3. Open the `AspireWithMaui` folder
4. Use the debugger to run the AppHost

**Using Command Line:**
1. Run `.\restore.cmd -mauirestore` (Windows) or `./restore.sh --mauirestore` (Linux/macOS) from the repository root
2. Navigate to `playground/AspireWithMaui/AspireWithMaui.AppHost` directory
3. Run: `dotnet run`

## What's Included

- **AspireWithMaui.AppHost** - The Aspire app host that orchestrates all services
- **AspireWithMaui.MauiClient** - A .NET MAUI application that connects to the backend (Windows and Mac Catalyst platforms)
- **AspireWithMaui.WeatherApi** - An ASP.NET Core Web API providing weather data
- **AspireWithMaui.ServiceDefaults** - Shared service defaults for non-MAUI projects
- **AspireWithMaui.MauiServiceDefaults** - Shared service defaults specific to MAUI projects

## Features Demonstrated

### MAUI Multi-Platform Support
The playground demonstrates Aspire's ability to manage MAUI apps on multiple platforms:
- **Windows**: Configures the MAUI app with `.AddWindowsDevice()`
- **Mac Catalyst**: Configures the MAUI app with `.AddMacCatalystDevice()`
- **iOS Device**: Configures the MAUI app with `.AddiOSDevice()` to run on physical iOS devices
  - Requires device provisioning before deployment (see https://learn.microsoft.com/dotnet/maui/ios/device-provisioning)
  - Use `.AddiOSDevice()` to target the only attached device (default, requires exactly one device)
  - Use `.AddiOSDevice("device-name", "00008030-001234567890123A")` to target a specific device by UDID
  - Find device UDID in Xcode under Window > Devices and Simulators > Devices tab, or right-click device and select "Copy Identifier"
  - Requires macOS to run (iOS development is macOS-only)
  - Use `.WithOtlpDevTunnel()` to send telemetry to the dashboard (iOS devices cannot reach localhost)
- **iOS Simulator**: Configures the MAUI app with `.AddiOSSimulator()` to run on iOS simulators
  - Use `.AddiOSSimulator()` to target the default simulator
  - Use `.AddiOSSimulator("simulator-name", "E25BBE37-69BA-4720-B6FD-D54C97791E79")` to target a specific simulator by UDID
  - Find simulator UDIDs in Xcode under Window > Devices and Simulators > Simulators tab, or use `/Applications/Xcode.app/Contents/Developer/usr/bin/simctl list`
  - Requires macOS to run (iOS development is macOS-only)
  - Use `.WithOtlpDevTunnel()` to send telemetry to the dashboard (simulators cannot reach localhost)
- **Android Device**: Configures the MAUI app with `.AddAndroidDevice()` to run on physical Android devices
  - Use `.AddAndroidDevice()` to target the only attached device (default, requires exactly one device)
  - Use `.AddAndroidDevice("device-name", "abc12345")` to target a specific device by serial number or IP
  - Works with USB-connected devices and WiFi debugging (e.g., "192.168.1.100:5555")
  - Get device IDs from `adb devices` command
  - Use `.WithOtlpDevTunnel()` to send telemetry to the dashboard (Android cannot reach localhost)
- **Android Emulator**: Configures the MAUI app with `.AddAndroidEmulator()` to run on Android emulators
  - Use `.AddAndroidEmulator()` to target the only running emulator (default)
  - Use `.AddAndroidEmulator("emulator-name", "Pixel_5_API_33")` to target a specific emulator by AVD name
  - Can also use emulator serial number like "emulator-5554"
  - Get emulator names from `adb devices` or `emulator -list-avds` command
  - Use `.WithOtlpDevTunnel()` to send telemetry to the dashboard (emulators cannot reach localhost)
- Automatically detects platform-specific target frameworks from the project file
- Shows "Unsupported" state in dashboard when running on incompatible host OS
- Sets up dev tunnels for MAUI app communication with backend services

### OpenTelemetry Integration
The MAUI client uses OpenTelemetry to send traces and metrics to the Aspire dashboard. For mobile platforms that cannot reach `localhost`, the playground demonstrates using dev tunnels to expose the dashboard's OTLP endpoint:

```csharp
// Android devices and emulators need dev tunnel for OTLP
mauiapp.AddAndroidEmulator()
    .WithOtlpDevTunnel()  // Automatically creates and configures a dev tunnel for telemetry
    .WithReference(weatherApi, publicDevTunnel);  // Dev tunnel for API communication

// iOS simulators and devices also need dev tunnel for OTLP
mauiapp.AddiOSSimulator()
    .WithOtlpDevTunnel()  // Automatically creates and configures a dev tunnel for telemetry
    .WithReference(weatherApi, publicDevTunnel);  // Dev tunnel for API communication
```

The `.WithOtlpDevTunnel()` method:
- Automatically resolves the dashboard's OTLP endpoint from configuration
- Creates a dev tunnel for the OTLP endpoint
- Configures the MAUI platform to send telemetry through the tunnel
- Handles all service discovery and environment variable setup

**Requirements for dev tunnels:**
- Dev tunnel CLI must be installed (automatic prompt if missing)
- User must be logged in to dev tunnel service (automatic prompt if needed)

### Service Discovery
The MAUI app discovers and connects to backend services (WeatherApi) using Aspire's service discovery.

### Environment Variables

All MAUI platform resources support environment variables using the standard `.WithEnvironment()` method. Environment variables are automatically forwarded to the MAUI application regardless of platform:

```csharp
// For Windows and Mac Catalyst, environment variables are passed directly:
mauiapp.AddWindowsDevice()
    .WithEnvironment("DEBUG_MODE", "true")
    .WithEnvironment("API_TIMEOUT", "30");

// For Android, environment variables are passed via an intermediate MSBuild targets file, but the syntax is identical:
mauiapp.AddAndroidDevice("my-device", "abc12345")
    .WithEnvironment("DEBUG_MODE", "true")
    .WithEnvironment("API_TIMEOUT", "30")
    .WithEnvironment("LOG_LEVEL", "Debug");

mauiapp.AddAndroidEmulator("my-emulator", "Pixel_5_API_33")
    .WithEnvironment("CUSTOM_VAR", "value")
    .WithReference(weatherApi);  // Service discovery environment variables also forwarded

// For iOS, environment variables are also passed via an intermediate MSBuild targets file:
mauiapp.AddiOSSimulator("my-simulator", "E25BBE37-69BA-4720-B6FD-D54C97791E79")
    .WithEnvironment("DEBUG_MODE", "true")
    .WithEnvironment("API_TIMEOUT", "30")
    .WithReference(weatherApi);  // Service discovery environment variables also forwarded
```

#### What Gets Forwarded

**ALL Aspire-managed environment variables** are automatically forwarded to MAUI applications:
- **Custom variables**: Set via `.WithEnvironment(key, value)`
- **Service discovery**: Connection strings and endpoints from `.WithReference(service)`
- **OpenTelemetry**: OTEL configuration from `.WithOtlpExporter()`
- **Resource metadata**: Automatically added by Aspire

#### Platform-Specific Implementation

- **Windows & Mac Catalyst**: Environment variables are passed directly through the process environment when launching via `dotnet run`.
- **Android**: Due to Android platform limitations, environment variables are written to a temporary MSBuild targets file that gets imported during the build. The targets file is generated automatically before each build and cleaned up after 24 hours (when a next build happens). Environment variable names are normalized to UPPERCASE (Android requirement), and semicolons are encoded as `%3B`.
- **iOS**: Due to iOS platform limitations, environment variables are written to a temporary MSBuild targets file that gets imported during the build. The targets file is generated automatically before each build and cleaned up after 24 hours. Environment variables are passed via the `--setenv` argument to `mtouch`/`mlaunch`.

Environment variables are available in your MAUI app code regardless of platform through standard .NET environment APIs (`Environment.GetEnvironmentVariable()`).

### Future Platform Support
The architecture is designed to support additional platforms:
- Android support: `.AddAndroidDevice()` for physical devices, `.AddAndroidEmulator()` for emulators (implemented)
- iOS support: `.AddiOSDevice()` for physical devices, `.AddiOSSimulator()` for simulators (implemented)

## Troubleshooting

### "MAUI workload not detected" Warning
If you see this warning in the Aspire dashboard:
1. Make sure you ran `.\restore.cmd -mauirestore` or `./restore.sh --mauirestore` from the repository root
2. The warning indicates the MAUI workload is not installed in the local `.dotnet` folder
3. Re-run the restore command with the `-mauirestore` or `--mauirestore` flag

### Build Errors
If you encounter build errors:
1. Ensure you ran the restore script with the MAUI flag first: `.\restore.cmd -mauirestore`
2. Make sure you're using .NET 10 RC or later
3. Try running `dotnet build` from the repository root first

### Platform-Specific Issues
- **Windows**: Requires Windows 10 build 19041 or higher for WinUI support. Mac Catalyst and iOS devices will show as "Unsupported" when running on Windows.
- **Mac Catalyst**: Requires macOS to run. Windows, Android, and iOS devices will show as "Unsupported" when running on non-macOS platforms.
- **iOS Device**: Requires macOS and a physical iOS device connected via USB. Device must be provisioned before deployment (https://learn.microsoft.com/dotnet/maui/ios/device-provisioning). Find device UDID in Xcode under Window > Devices and Simulators.
- **iOS Simulator**: Requires macOS and Xcode with iOS simulator runtimes installed. To target a specific simulator:
  1. List available simulators: `/Applications/Xcode.app/Contents/Developer/usr/bin/simctl list`
  2. Or find in Xcode: Window > Devices and Simulators > Simulators tab
  3. Right-click simulator and select "Copy Identifier" for UDID
  4. Use in code: `.AddiOSSimulator(simulatorId: "E25BBE37-69BA-4720-B6FD-D54C97791E79")`
- **Android Device**: Requires a physical Android device connected via USB/WiFi debugging. Ensure the device is visible via `adb devices`. Works on Windows, macOS, and Linux.
- **Android Emulator**: Requires an Android emulator running and visible via `adb devices`. To target a specific emulator:
  1. List available emulators: `adb devices` (shows emulator IDs like "emulator-5554")
  2. Or list AVDs: `emulator -list-avds` (shows AVD names like "Pixel_5_API_33")
  3. Use either ID format in code: `.AddAndroidEmulator(emulatorId: "Pixel_5_API_33")` or `.AddAndroidEmulator(emulatorId: "emulator-5554")`
  4. Works on Windows, macOS, and Linux.

## Current Status

✅ **Implemented:**
- Windows platform support via `AddWindowsDevice()`
- Mac Catalyst platform support via `AddMacCatalystDevice()`
- iOS device support via `AddiOSDevice()`
- iOS simulator support via `AddiOSSimulator()`
- Android device support via `AddAndroidDevice()`
- Android emulator support via `AddAndroidEmulator()`
- Automatic platform-specific TFM detection from project file
- Platform validation with "Unsupported" state for incompatible hosts
- Dev tunnel configuration for MAUI-to-backend communication
- Service discovery integration
- OpenTelemetry integration

🚧 **Coming Soon:**
- Multi-platform simultaneous debugging

## Learn More

- [Aspire Documentation](https://aspire.dev/)
- [.NET MAUI Documentation](https://learn.microsoft.com/dotnet/maui/)
- [OpenTelemetry in .NET](https://learn.microsoft.com/dotnet/core/diagnostics/observability-with-otel)
