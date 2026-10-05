# Aspire.Hosting.Testing

Use this package to write integration tests that start an Aspire AppHost and interact with its resources. It works with .NET test frameworks such as xUnit.net, NUnit, and MSTest.

## Getting started

### Prerequisites

A .NET test project and an Aspire AppHost project. The test machine needs the same tools as running the AppHost normally, including a container runtime if the application uses containers.

### Install the package

From your test project directory, add `Aspire.Hosting.Testing` and a project reference to the AppHost:

```bash
dotnet add package Aspire.Hosting.Testing
dotnet add reference ../MyApp.AppHost/MyApp.AppHost.csproj
```

Use the same Aspire package version as your AppHost. Install this package in the test project, not the AppHost.

## Usage example

The following xUnit.net test starts the AppHost, waits for its `webfrontend` resource to become healthy, and sends an HTTP request:

```csharp
using System.Net;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Xunit;

public class AppHostTests
{
    [Fact]
    public async Task WebFrontendReturnsOk()
    {
        using var startupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.MyApp_AppHost>(startupTimeout.Token);
        await using var app = await builder.BuildAsync(startupTimeout.Token);
        await app.StartAsync(startupTimeout.Token);

        using var requestTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await app.ResourceNotifications.WaitForResourceHealthyAsync(
            "webfrontend", requestTimeout.Token);

        using var client = app.CreateHttpClient("webfrontend");
        using var response = await client.GetAsync("/", requestTimeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

Replace `Projects.MyApp_AppHost` with the generated project type for your AppHost, and `webfrontend` with a resource that returns HTTP 200 at `/`. Configure a health check on that resource so the readiness wait reflects when it can serve requests.

The testing builder randomizes proxied ports and disables the dashboard by default. Disposing the application and builder cleans up the resources started by the test.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/testing/overview/
* https://aspire.dev/testing/write-your-first-test/
* https://aspire.dev/testing/accessing-resources/

## Feedback & contributing

https://github.com/microsoft/aspire
