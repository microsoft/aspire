# Vendoring code sync instructions

## OpenTelemetry.Instrumentation.ConfluentKafka

```console
git clone https://github.com/open-telemetry/opentelemetry-dotnet-contrib.git
git fetch --tags
git checkout tags/Instrumentation.ConfluentKafka-0.2.0-alpha.2
```

### Instructions

- Copy files from `src/OpenTelemetry.Instrumentation.ConfluentKafka` to `src/Vendoring/OpenTelemetry.Instrumentation.ConfluentKafka`:
    - `**\*.cs` minus `AssemblyInfo.cs`, `ConfluentKafkaInstrumentedConsumerBuilderOptions.cs`, `ConfluentKafkaInstrumentedProducerBuilderOptions.cs`, `OpenTelemetryConsumerBuilderExtensions.cs`, `OpenTelemetryProducerBuilderExtensions.cs`, `ReflectionHelpers.cs`
- Copy files from `src/Shared` to `src/Vendoring/OpenTelemetry.Instrumentation.ConfluentKafka/Shared`:
    - `Guard.cs`
    - `SemanticConventions.cs`
- Preserve the existing AOT-compatible `PropertyFetcher.AOT.cs` instead of copying the reflection-based `PropertyFetcher.cs` used upstream.
- In `ConfluentKafkaCommon.cs`:
    - Set `InstrumentationName` to `"OpenTelemetry.Instrumentation.ConfluentKafka"`.
    - Set `InstrumentationVersion` to `new Version(0, 2, 0, 0).ToString()`.
    - Construct `ActivitySource` and `Meter` directly with the instrumentation name/version and the v1.43.0 telemetry schema URL instead of copying `ActivitySourceFactory.cs`, `AssemblyVersionExtensions.cs`, and `MeterFactory.cs`.

## OpenTelemetry.Instrumentation.StackExchangeRedis

```console
git clone https://github.com/open-telemetry/opentelemetry-dotnet-contrib.git
git fetch --tags
git checkout tags/Instrumentation.StackExchangeRedis-1.16.0-beta.1
```

### Instructions

- Copy files from `src/OpenTelemetry.Instrumentation.StackExchangeRedis` to `src/Vendoring/OpenTelemetry.Instrumentation.StackExchangeRedis`:
    - `**\*.cs` minus `IsExternalInit.cs`
- Copy files from `src/Shared` to `src/Vendoring/OpenTelemetry.Instrumentation.StackExchangeRedis/Shared`:
    - `ActivitySourceFactory.cs`
    - `AssemblyVersionExtensions.cs`
    - `DatabaseSemanticConventionHelper.cs`
    - `Guard.cs`
    - `PropertyFetcher.cs`
    - `SemanticConventions.cs`
- In `StackExchangeRedisConnectionInstrumentation.cs` ensure that the activity source name is overridden to `OpenTelemetry.Instrumentation.StackExchangeRedis`.

## Customizations

- Add `#nullable disable` in files that require it.
- Change all `public` classes to `internal`.
- Update `src/Vendoring/.editorconfig` with the required exemptions.
