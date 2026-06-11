# Vendoring code sync instructions

## OpenTelemetry.Instrumentation.ConfluentKafka

```console
git clone https://github.com/open-telemetry/opentelemetry-dotnet-contrib.git
git fetch --tags
git checkout tags/Instrumentation.ConfluentKafka-0.1.0-alpha.2
```

### Instructions

- Copy files from `src/OpenTelemetry.Instrumentation.ConfluentKafka` to `src/Vendoring/OpenTelemetry.Instrumentation.ConfluentKafka`:
    - `**\*.cs` minus `AssemblyInfo.cs`, `OpenTelemetryConsumerBuilderExtensions.cs`, `OpenTelemetryProducerBuilderExtensions.cs`, `ReflectionHelpers.cs`
- Copy files from `src/Shared` to `src/Vendoring/OpenTelemetry.Instrumentation.ConfluentKafka/Shared`:
    - `Guard.cs`
    - `PropertyFetcher.AOT.cs`
    - `SemanticConventions.cs`
- In `ConfluentKafkaCommon.cs` update `InstrumentationName` to `internal const string InstrumentationName = "OpenTelemetry.Instrumentation.ConfluentKafka";` and `InstrumentationVersion` to `internal static readonly Version Version = new Version(0, 1, 0, 0).ToString();`

## OpenTelemetry.Instrumentation.StackExchangeRedis

```console
git clone https://github.com/open-telemetry/opentelemetry-dotnet-contrib.git
git fetch --tags
git checkout tags/Instrumentation.StackExchangeRedis-1.0.0-rc9.13
```

### Instructions

- Copy files from `src/OpenTelemetry.Instrumentation.StackExchangeRedis` to `src/Vendoring/OpenTelemetry.Instrumentation.StackExchangeRedis`:
    - `**\*.cs` minus `AssemblyInfo.cs`
- Copy files from `src/Shared` to `src/Vendoring/OpenTelemetry.Instrumentation.StackExchangeRedis/Shared`:
    - `Guard.cs`
    - `PropertyFetcher.AOT.cs`
    - `SemanticConventions.cs`
- In `StackExchangeRedisConnectionInstrumentation.cs` update `ActivitySourceName` to `internal const string ActivitySourceName = "OpenTelemetry.Instrumentation.StackExchangeRedis";` and `Version` to `internal static readonly Version Version = new Version(1, 0, 0, 13);`
- Apply the changes from https://github.com/open-telemetry/opentelemetry-dotnet-contrib/pull/1625 if necessary.

## Customizations

- Add `#nullable disable` in files that require it.
- Change all `public` classes to `internal`.
- Update `src/Vendoring/.editorconfig` with the required exemptions.
