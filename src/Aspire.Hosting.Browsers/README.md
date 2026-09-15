# Browser logs hosting integration

Use this integration to model, configure, and orchestrate tracked Chromium browser sessions for web resources in an Aspire solution during local development.

## Getting started

### Prerequisites

* A Chromium-based browser, such as Microsoft Edge, Google Chrome, or Chromium, installed on the development machine.
* A resource that exposes an HTTP or HTTPS endpoint for a browser to open.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Browsers` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Browsers
```

The browser logs API is experimental. C# callers must opt in by suppressing `ASPIREBROWSERLOGS001`, as shown below.

## Usage example

Then, in the AppHost, add browser logs to a web frontend resource with either C# or TypeScript:

**C#**

```csharp
#pragma warning disable ASPIREBROWSERLOGS001

var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.WebFrontend>("web")
    .WithExternalHttpEndpoints()
    .WithBrowserLogs();

builder.Build().Run();

#pragma warning restore ASPIREBROWSERLOGS001
```

**TypeScript**

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

await builder.addProject("web", "../WebFrontend/WebFrontend.csproj")
    .withExternalHttpEndpoints()
    .withBrowserLogs();

await builder.build().run();
```

Start the AppHost, then select **Open tracked browser** on the `web-browser-logs` child resource in the dashboard. Browser console messages and errors appear in that resource's console logs. Use **Capture screenshot** to save a screenshot as a command artifact.

The tracked browser uses an Aspire-managed user data directory, not your normal browser profile. Browser log resources are excluded from publishing.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/devtools/browser-logs/
* https://chromedevtools.github.io/devtools-protocol/

## Feedback & contributing

https://github.com/microsoft/aspire
