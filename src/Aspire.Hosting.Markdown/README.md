# Aspire.Hosting.Markdown library

Provides extension methods and resource definitions for an Aspire AppHost to configure Markdown preview resources.

## Getting started

### Install the package

In your AppHost project, install the Aspire Markdown Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Markdown
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a Markdown preview resource for a local documentation file:

```csharp
var readme = builder.AddMarkdownPreview("readme", "./README.md");
```

The Markdown preview resource appears in the Aspire dashboard with a highlighted command that opens the file in the Markdown viewer. Relative paths are resolved from the AppHost directory.

## Feedback & contributing

https://github.com/microsoft/aspire
