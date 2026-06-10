# Aspire.Seq Library

Adds OTLP exporters to send logs and traces to a Seq server. Can be configured to persist logs and traces across application restarts.

By default, Seq is not added to the Aspire manifest for deployment.

## Getting started

### Prerequisites

- Seq server

### Install the package

Install the Aspire Seq library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Seq
```

## Usage example

In the _Program.cs_ file of your projects, call the `AddSeqEndpoint` extension method to register OpenTelemetry Protocol exporters to send logs and traces to Seq. The method takes a connection name parameter.

```csharp
builder.AddSeqEndpoint("seq");
```

Logs and traces will then be sent to Seq, in addition to the Aspire dashboard.

## Configuration

The Aspire Seq component provides options to configure the connection to Seq.

### Use configuration providers

The Aspire Seq component supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `SeqSettings` from configuration by using the `Aspire:Seq` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "Seq": {
      "DisableHealthChecks": true,
      "ServerUrl": "http://localhost:5341"
    }
  }
}
```

### Use inline delegates

Also you can pass the `Action<SeqSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
builder.AddSeqEndpoint("seq", settings => {
    settings.DisableHealthChecks = true;
    settings.ServerUrl = "http://localhost:5341";
    settings.Logs.TimeoutMilliseconds = 10000;
});
```

## AppHost extensions

In your AppHost project, install the `Aspire.Hosting.Seq` library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Seq
```

Then, in the _AppHost.cs_ file of `AppHost`, register a Seq server and propagate its configuration using the following methods (note that you must accept the [Seq End User License Agreement](https://datalust.co/doc/eula-current.pdf) for Seq to start):

```csharp
var seq = builder.AddSeq("seq");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(seq);
```

In the _Program.cs_ file of `MyService`, logging and tracing to Seq can be configured with:

```csharp
builder.AddSeqEndpoint("seq");
```

### Persistent logs and traces

To retain Seq's data and configuration across application restarts register Seq with a data directory in your AppHost project.

```csharp
var seq = builder.AddSeq("seq", seqDataDirectory: "./seqdata");
```

Note that the directory specified must already exist.

### Seq in the Aspire manifest

Seq is not part of the Aspire deployment manifest. It is recommended to set up a secure production Seq server outside of Aspire.

## Additional documentation

* https://docs.datalust.co/docs/the-seq-query-language
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
