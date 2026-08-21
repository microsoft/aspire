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

## Build Queue

When multiple MAUI platform targets reference the same project (e.g., Android, iOS, and Mac Catalyst all using the same `.csproj`), MSBuild cannot handle concurrent builds of the same project file. The hosting integration automatically serializes these builds using a per-project queue.

### How It Works

1. When you start multiple platform resources simultaneously, only one builds at a time
2. Other platforms show a **"Queued"** state in the dashboard while waiting
3. Each build shows a **"Building"** state with live MSBuild output in the resource logs
4. After a build completes and the app launches, the next queued build starts
5. You can click **Stop** on a queued or building resource to cancel it — the resource shows an **"Exited"** state with an orange indicator

### Key Behaviors

- **Per-project serialization**: The queue is scoped to each `MauiProjectResource`. If you have two separate MAUI projects, they build in parallel. Only platform targets sharing the same project are serialized.
- **Cancel support**: Clicking Stop on a Queued resource removes it from the queue. Clicking Stop on a Building resource kills the `dotnet build` process.
- **Restart after cancel**: You can start a cancelled resource again — it re-enters the queue.
- **Build timeout**: Builds that take longer than 10 minutes are automatically cancelled to prevent a hung build from blocking the queue.
- **DCP launch handoff**: After the serialized build completes for the AppHost configuration and platform MSBuild properties, DCP launches the already-built app with build and restore disabled so the next queued platform can build without overlapping shared output writes.

### Architecture

The build queue is implemented via:

- **`MauiBuildQueueAnnotation`**: Added to the parent `MauiProjectResource`, holds a `SemaphoreSlim(1,1)` and per-resource cancellation tokens
- **`MauiBuildQueueEventSubscriber`**: Subscribes to `BeforeResourceStartedEvent` to manage the queue, run `dotnet build` as a subprocess, and replace the default Stop command with a queue-aware version. It also subscribes to `BeforeStartEvent` to apply launch-argument callbacks before DCP renders the launch command
- **`MauiBuildInfoAnnotation`**: Attached to each platform resource with the project path, working directory, target framework, configuration, and platform MSBuild properties used for the build subprocess
- **`ProjectLaunchArgsOverrideAnnotation`**: A core `Aspire.Hosting` annotation that overrides DCP's default `dotnet run` args, enabling `dotnet build --no-restore /t:Run -p:NoBuild=true` for MAUI projects after the serialized build

## Customizing Build and Launch Arguments

Each MAUI platform target runs in two phases: a serialized **build** compile (`dotnet build`) followed by a **launch** that starts the already-built app (`dotnet build --no-restore /t:Run -p:NoBuild=true`). You can inspect or modify the arguments for either phase with `WithMauiBuildArguments` and `WithMauiLaunchArguments`.

Both methods register a callback that receives a `MauiBuildArgumentsCallbackContext`. Mutate its `Arguments` list to influence the corresponding command:

| Property | Description |
|----------|-------------|
| `Arguments` | The mutable `dotnet` argument list for the current phase. |
| `Step` | The build step (`Build` or `Launch`) that the callback is running for. |
| `Resource` | The MAUI platform resource being built or launched. |
| `CancellationToken` | Signals shutdown or a user-initiated cancel. |

### Build Arguments

`WithMauiBuildArguments` runs before the serialized compile. The argument list contains the full `dotnet build` command (verb, project path, target framework, configuration, and any additional MSBuild properties).

```csharp
// Add an MSBuild property to the compile
mauiApp.AddAndroidEmulator()
    .WithMauiBuildArguments(context => context.Arguments.Add("-p:MyProperty=Value"));
```

```typescript
// Add an MSBuild property to the compile
await mauiApp.addAndroidEmulator("emulator")
    .withMauiBuildArguments(async context => {
        const args = await context.arguments();
        await args.add("-p:MyProperty=Value");
    });
```

### Launch Arguments

`WithMauiLaunchArguments` runs before the app starts — the launch command is rendered ahead of the first start. Use it to add or adjust MSBuild properties on the launch command:

```csharp
mauiApp.AddAndroidEmulator()
    .WithMauiLaunchArguments(context => context.Arguments.Add("-p:MyProperty=Value"));
```

```typescript
await mauiApp.addAndroidEmulator("emulator")
    .withMauiLaunchArguments(async context => {
        const args = await context.arguments();
        await args.add("-p:MyProperty=Value");
    });
```

### Sensitive Arguments

MSBuild properties can carry secrets (for example a signing key password). Add those with `AddSensitiveArgument` instead of `Arguments.Add`. The value is still passed to `dotnet` verbatim, but the build pipeline redacts it from the arguments it writes to the resource logs. Launch-step arguments are additionally masked by the dashboard's command-line display.

```csharp
mauiApp.AddAndroidEmulator()
    .WithMauiBuildArguments(context => context.AddSensitiveArgument($"-p:AndroidSigningKeyPass={keyPassword}"));
```

```typescript
await mauiApp.addAndroidEmulator("emulator")
    .withMauiBuildArguments(async context => {
        await context.addSensitiveArgument(`-p:AndroidSigningKeyPass=${keyPassword}`);
    });
```

### Notes

- Both methods have synchronous and asynchronous (`Func<..., Task>`) overloads.
- Multiple callbacks can be registered per resource; they run in registration order and share the same mutable argument list.
- Launch callbacks are applied once before the app starts, so edits do not accumulate across restarts.
- Use `AddSensitiveArgument` for any argument containing a secret so its value is redacted from the resource logs.

## Requirements

- .NET 10.0 or later
- MAUI workload must be installed: `dotnet workload install maui`
- Platform-specific SDKs:
  - Windows: Windows SDK 10.0.19041.0 or later
  - macOS: Xcode with command-line tools
  - Android: Android SDK via Visual Studio or Android Studio

## Feedback & Issues

Please file issues at https://github.com/microsoft/aspire/issues
