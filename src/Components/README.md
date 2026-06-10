# Overview

Aspire client integrations are classic .NET NuGet packages which are designed as highly usable libraries. Aspire client integrations feature rich production-ready telemetry, health checks, configurability, testability, and documentation. For the current state of the client integrations included in this repo and tracked for Aspire's first preview, please check out the [Aspire Client Integrations Progress](./Aspire_Components_Progress.md) page.

## Contribution guidelines

We aim to have a diverse set of high quality Aspire client integrations, making it easy to pick from many different technologies when building Aspire apps. We expect to continue to add more client integrations, and we welcome contributions of others, but we explicitly don't want to include every possible client integration. The set will be gently curated: in order to make sure that client integrations are useful and dependable, we have some broad criteria below for client integrations contributed to microsoft/aspire. These will likely evolve over time based on feedback, but we expect some requirements (such as actively supported) to remain firm:

1. We expect to welcome any client integrations that would have value to Aspire users and align with what Aspire is intended to do, subject to the below.
2. We don't expect to choose preferred techs. For example, if there are two commonly used providers for database XYZ, we are comfortable with having one client integration for each. We would like client integration naming and granularity to be clear enough that customers can make informed decisions. Aspire is agnostic to your choice of cloud provider, too.
3. We will require that the tech represented by the client integration is being actively supported. In most cases we expect that it is widely used, although we expect that part will be a judgement call.
4. Client integrations contributed to microsoft/aspire must meet the same quality and completeness bar of other contributions. ie., we won't have a lower quality bar for experimental or niche client integrations.
5. Where there's a client integration that meets the above criteria, but that isn't something we expect to be a high priority for the Aspire committers to maintain, we'll ask for a plan to sustain it (eg., motivated contributors ready to fix bugs in it)

Note: only client integrations that are built from microsoft/aspire will be able to use the Aspire package name prefix. There is no technical barrier to using client integrations built elsewhere, without the Aspire prefix, but currently our idea is that all broadly available Aspire client integrations will live here in microsoft/aspire and have the Aspire package prefix. We welcome feedback on this and all the other principles listed here, though.

In summary we encourage and are excited to accept contributions of client integrations, but it's probably a good idea to first open an issue to discuss any new potential client integration before offering a PR, to make sure we're all in agreement that it's a good fit with these principles.

## Versioning and Releases

Each client integration is in its own NuGet package, and can version independently, including declaring itself in a preview state using the standard SemVer and NuGet mechanisms. However we expect the major and minor version of client integrations to follow the core Aspire packages to make it easier to reason about dependencies. We expect to typically push updates to all client integrations at the same time we update the core Aspire packages, but we have the ability to push an updated client integration at any other time if necessary, for example where changes to the underlying client library makes it necessary.

### Target Framework(s)

The Aspire client integration must support [all supported versions of .NET](https://dotnet.microsoft.com/platform/support/policy/dotnet-core) at the time that specific version of Aspire is initially released. For example:

| Aspire Version | Targets                       |
|---------------------|-------------------------------|
| 8.x                 | `net8.0`                      |
| 9.x                 | `net8.0` (+`net9.0` optional) |
| 13.x                | `net8.0` (+`net9.0`, `net10.0` optional) |

### Dependency Versioning

Applications usually have a direct reference to an Aspire client integration package (e.g `Aspire.StackExchange.Redis`) but have indirect references to the associated client libraries (e.g. `StackExchange.Redis`). This means that the version of the client libraries used by the application is derived from what the client integration package is built against.

Aspire client integration packages will be serviced regularly, capturing the latest available versions of the client libraries and Microsoft extensions they depend on, making it possible for applications built on Aspire to always benefit from the latest features and fixes.

#### Breaking Changes

In the situation that a client library associated with an Aspire client integration package releases an update with a breaking change, the nature of the change will be assessed to determine its impact severity on the associated Aspire client integration package and Aspire applications that depend on it. If it’s decided that the change has high enough impact such that it would constitute a breaking change necessary to address, the Aspire client integration package will be split into 2 packages to support both versions.

To understand how this will work, an example of this is the `RabbitMQ.Client` library made many large breaking changes between version `6.8.1` and `7.0.0`. To handle this:

1. For the current `Aspire.RabbitMQ.Client` package, we put a NuGet version limit on our dependency: `[6.8.1,7.0.0)`. This way people won't be able to update to the `7.0.0` version, which will break their app.
2. When `RabbitMQ.Client` ships an official `7.0.0` stable package during the Aspire `8.x` lifetime, we can add a new, forked client integration named `Aspire.RabbitMQ.Client.v7` which will have a dependency on `7.0.0` and contain any updates so the Aspire client integration will work with v7. People who explicitly want to use v7 can opt into using this package.
3. When Aspire 9 ships, we can "swap" the dependencies around.
    - The `Aspire.RabbitMQ.Client` package will be updated to depend on v7 of `RabbitMQ.Client`.
    - If `RabbitMQ.Client` v6 is still in support, we can create `Aspire.RabbitMQ.Client.v6` which has the dependency limit `[6.8.1, 7.0.0)` and works with the version 6 of RabbitMQ.Client.
    - `Aspire.RabbitMQ.Client.v7` will be dead-ended. We won't make new Aspire 9 versions of this package.

## Icon

Where the client integration represents some client technology that has a widely recognized logo, we would like to use that for the package icon if we can. Take a look at the MySql client integration for an example. We can only do this if the owner of the logo allows it - often you can find posted guidelines describing acceptable usage. Otherwise we can add reach out for explicit permission and do a follow up commit to add the icon if and when the use is approved.

## Naming

- Each client integration's name must have the prefix `Aspire.`.
- When client integration is built around `ABC` client library, it should contain the client library name in its name. Example: `Aspire.ABC`. Where the technology has a particular casing we have preferred that: for example `Aspire.RabbitMQ` rather than `Aspire.RabbitMq`.
- When the client library is just one of many libraries that allows to consume given service, the names that refer to client integration need to be specific, not generic. Example: Npgsql is not the only db driver for PostgreSQL database, so the extension method should be called `AddNpgsql` rather than `AddPostgreSQL`. The goal here is to help app authors make an informed decision about which to choose.

## Public API

- Each client integration should have an `AddXXX` extension method extending `IHostApplicationBuilder`
    - Consider adding support for [keyed DI](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-8#keyed-di-services), if applicable.
    - A client integration can have more APIs, if necessary, but it is an explicit goal of Aspire Client Integrations to **not** wrap the underlying client library's APIs in new, convenient, higher-level APIs.
- Each client integration should provide it's own public and `sealed` `Settings` type.
  > [!NOTE]
  > This type does not use the name `Options` because it is not an [IOptions](https://learn.microsoft.com/dotnet/core/extensions/options). `IOptions` objects can be configured through dependency injection. These settings need to be read before the DI container is built, so they can't be `IOptions`.
- The settings type name should be unique (no generic names like `ConfigurationOptions`), not contain an `Aspire` prefix and follow the client-lib name. Example: when a client integration wraps an `ABC` client library, the package is called `Aspire.ABC` and the settings type is named `ABCSettings` and is either in the `Aspire.ABC` namespace or a sub namespace.

## Configuration

- When a new instance of the settings type is created, its properties should return the recommended/default values (so when they are bound to an empty config they still return the right values).
- Settings should be bound to a section of `IConfiguration` exposed by `IHostApplicationBuilder.Configuration`.
- Each client integration should determine a constant configuration section name for its settings under the `Aspire` config section.
- Each client integration should support binding arguments from the configuration section and from named configuration as defined below. Settings bound from named configuration should override those bound from integration-specific configuration.
- All configuration knobs exposed by the settings type should be public and mutable, so they can be changed in the config and applied without a need for re-compiling the application.
- Each client integration should expose an optional lambda that accepts an instance of given settings type. By doing that, we provide the users with a possibility to override the bound config values (make final changes).
- When a mandatory config property is missing, an exception should be thrown, and it should contain information about the config path that was used to read it.

```csharp
public static void AddAbc(this IHostApplicationBuilder builder, Action<AbcSettings>? configureSettings = null)
{
    ArgumentNullException.ThrowIfNull(builder);

    var settings = new AbcSettings();
    builder.Configuration.GetSection("Aspire:Abc").Bind(settings);

    configureSettings?.Invoke(settings);

    if (settings.MandatoryPropertyIsMissing)
        throw new MeaningfulException($"MandatoryPropertyName was not found at configuration path 'Aspire:Abc:{nameof(AbcSettings.MandatoryPropertyName)}'");
}
```

### Named Configuration

- Client integrations should allow for multiple (named) instances to be registered in the application.
- The configuration for each named instance will come from a section under the client integration's section with the name corresponding to the name provided when the client integration was registered.

```json
{
  "Aspire": {
    "Abc": {
      "named_one": {

      },
      "named_two": {

      }
    }
  }
}
```

## Health Checks

Aspire client integrations expose health checks enabling applications to track and respond to the remote service’s health.

- Health checks should be enabled by default, but the users should be able to disable them via configuration.
- [AddHealthChecks(this IServiceCollection)](https://learn.microsoft.com/dotnet/api/microsoft.extensions.dependencyinjection.healthcheckservicecollectionextensions) should be used to register health checks.
- If the client library provides an integration with `HealthCheckService` (example: [Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore](https://learn.microsoft.com/aspnet/core/host-and-deploy/health-checks#entity-framework-core-dbcontext-probe)) it should be used.
- If there is an established open-source health check (example: [AspNetCore.HealthChecks.Redis](https://www.nuget.org/packages/AspNetCore.HealthChecks.Redis)) it should be used. If the existing health check library doesn't meet our requirements, efforts should be made to add the necessary functionality to the existing library.
- Otherwise we need to implement [IHealthCheck](https://learn.microsoft.com/dotnet/api/microsoft.extensions.diagnostics.healthchecks.ihealthcheck) and register it via [HealthCheckRegistration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.diagnostics.healthchecks.healthcheckregistration).
- Consider whether the Health Check should reuse the same client object registered in DI by the client integration or not. Reusing the same client object has the advantages of getting the same configuration, logging, etc during the health check.
- Calling [MapHealthChecks](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.builder.healthcheckendpointroutebuilderextensions.maphealthchecks) is outside of the scope of a Component.

## Resilience

Aspire client integrations leverage configurable resilience patterns such as retries, timeouts, and circuit breakers to maximize availability. This functionality is configurable and seamlessly integrates with higher level resilience strategies implemented at the application level.

- Each client integration must ensure that by default reasonable timeouts are enabled. It should be possible to configure the timeouts.
- If the client library provides connection pooling, it should be enabled by default (to scale proportionally). It should be possible to disable it via configuration.
- If given client library provides built in mechanism for retries, it should be enabled by default and configurable.
- It's not always possible to implement retries. Example: A raw db driver does not know whether the currently executed command is part of a transaction or not. If it is, re-trying a failed command won't help as the whole transaction has already failed.

## Telemetry

Aspire client integrations offer integrated logging, metrics, and tracing using modern .NET abstractions (ILogger, Meter, Activity). Telemetry is schematized and part of a component’s contract, ensuring backward compatibility across versions of the client integration.

- The client integration's telemetry names should conform to [OpenTelemetry's Semantic Conventions](https://github.com/open-telemetry/semantic-conventions) when available.
- Components are allowed to use OpenTelemetry [Instrumentation Libraries](https://opentelemetry.io/docs/specs/otel/glossary/#instrumentation-library), if available. (example: [OpenTelemetry.Instrumentation.StackExchangeRedis](https://www.nuget.org/packages/OpenTelemetry.Instrumentation.StackExchangeRedis)).
- The client integration should use `ILoggerFactory`/`ILogger` objects that come from DI.
- If possible, no information should be logged twice (example: raw db driver and Entity Framework can both log SQL queries, when they are used together only one should be logging).
- Defining [telemetry exporters](https://opentelemetry.io/docs/instrumentation/net/exporters/) is outside of the scope of a client integration.

## Performance

- Entity Framework DbContext pooling should be enabled by default, but it should be possible to disable it (example: multi-tenant application where `DbContext` may contain data specific to customer and should not be reused for other customers).
- Registering a `DbDataSource` and obtaining a `DbConnection` from it should be preferred over instantiating `DbConnection` directly with a connection string.
- Before every client integration is shipped, we should measure how applying the recommended settings affect performance.

## Azure Components

### Configuration

- Azure SDK libraries already have a `ClientOptions` type that is an `IOptions`. These options provide general HTTP options like `RetryOptions` and `DiagnosticsOptions`, but also service-specific options, like `ServiceBusClientOptions.ConnectionIdleTimeout`.
- Since users will need to be able to configure both `Aspire` settings and these `ClientOptions` options, we will nest the `ClientOptions` configuration under the client integrations configuration section.

```json
{
  "Aspire": {
    "Azure": {
      "Messaging:ServiceBus": {
        // Aspire settings
        "HealthChecks": false,
        "Namespace": "aspire-servicebus.servicebus.windows.net",

        // Azure SDK's ServiceBusClientOptions
        "ClientOptions": {
          "RetryOptions": {
            "MaxRetries": 2,
            "Delay": "00:00:01"
          }
        }
      }
    }
  }
}
```

### Security

- If the underlying client library supports passwordless/[RBAC](https://learn.microsoft.com/azure/role-based-access-control/overview) authentication, which Credential to use should be configurable through the Aspire Settings object. For example:

```csharp
builder.AddAzureServiceBus(settings =>
{
    settings.Credential =
        new ChainedTokenCredential(
            new VisualStudioCredential(),
            new VisualStudioCodeCredential());
});
```

- If the underlying client library supports secret credentials (like a connection string), this should be read from `IConfiguration`. This can be placed either in the client integration-specific section, or under the global `ConnectionStrings` section. If both are specified, the `ConnectionStrings` section is used. For example:

```json
{
  "ConnectionStrings": {
    "myServiceBus": "Endpoint=sb://foo;..."
  },
  "Aspire": {
    "Azure": {
      "Messaging:ServiceBus": {
        "ConnectionString": "Endpoint=sb://foo;..."
      }
    }
  }
}
```

- Alternatively, the ConnectionString should be able to be configured through the Aspire Settings object. For example:

```csharp
builder.AddAzureServiceBus(settings =>
{
    settings.ConnectionString = "xyz";
});
```

- If both secret and passwordless mechanisms are configured, the secret credential overrides the passwordless identity setting.

## Checklist for new client integrations

New client integrations MUST have:

* README.md
* ConfigurationSchema.json file
* Public APIs
* Tests
    * [ConformanceTests](../../tests/Aspire.Components.Common.TestUtilities/ConformanceTests.cs)
    * [EndToEndTests](../../tests/Aspire.EndToEnd.Tests/README.md#adding-tests-for-new-client-integrations)
    * Other unit tests as needed
* Tracing support

New client integrations SHOULD* have:

* Logging support
* Metrics support
* Health checks

`*` Client integrations need to have justification for why these are not supported.
