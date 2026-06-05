# Aspire.Hosting.Docker library

Provides publishing extensions to Aspire for Docker Compose.

## Getting started

### Install the package

In your AppHost project, install the Aspire Docker Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Docker
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add the environment:

```csharp
builder.AddDockerComposeEnvironment("compose");
```

```shell
aspire publish -o docker-compose-artifacts
```

## Feedback & contributing

https://github.com/microsoft/aspire
