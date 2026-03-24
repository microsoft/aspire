# Aspire.Hosting.Seq library

Provides extension methods and resource definitions for an Aspire AppHost to configure a Seq resource.

## Getting started

### Install the package

In your AppHost project, install the Aspire Seq Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Seq
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a Seq resource and consume the connection using the following methods:

```csharp
var seq = builder.AddSeq("seq");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(seq);
```

## Connection Properties

When you reference a Seq resource using `WithReference`, the following connection properties are made available to the consuming project:

### Seq

The Seq resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the Seq server |
| `Port` | The port number the Seq server is listening on |
| `Uri` | The connection URI, with the format `http://{Host}:{Port}` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

* https://aspire.dev/integrations/observability/seq/

## Feedback & contributing

https://github.com/microsoft/aspire
