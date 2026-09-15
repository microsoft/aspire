# GitHub Models hosting integration

> [!WARNING]
> **This integration is deprecated and no longer supported.**
> GitHub Models is no longer available to new customers, so the
> `Aspire.Hosting.GitHub.Models` integration has been sunset. It will not receive
> further updates (including model-list refreshes) and will be removed in a future
> release. Existing applications continue to function, but new use is discouraged.
> See [microsoft/aspire#18402](https://github.com/microsoft/aspire/issues/18402)
> for details.

Use this integration to model, configure, and orchestrate GitHub Models in an Aspire solution.

## Getting started

### Prerequisites

- GitHub account with access to GitHub Models
- GitHub [personal access token](https://docs.github.com/en/github-models/use-github-models/prototyping-with-ai-models#experimenting-with-ai-models-using-the-api) with appropriate permissions (`models: read`)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.GitHub.Models` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.GitHub.Models
```

## Usage example

In the AppHost, add a GitHub Model resource and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var chat = builder.AddGitHubModel("chat", "openai/gpt-4o-mini");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(chat);
```

**TypeScript**

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

const chat = await builder.addGitHubModelById("chat", "openai/gpt-4o-mini");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(chat);
```

## Configuration

The GitHub Model resource can be configured with the following options:

### API Key

The API key can be set as a configuration value using the default name `{resource_name}-gh-apikey` or the `GITHUB_TOKEN` environment variable.

From your AppHost directory, set the parameter value with `aspire secret set`:

```bash
aspire secret set Parameters:chat-gh-apikey "YOUR_GITHUB_TOKEN_HERE"
```

Furthermore, the API key can be configured using a custom parameter:

```csharp
var apiKey = builder.AddParameter("my-api-key", secret: true);
var chat = builder.AddGitHubModel("chat", "openai/gpt-4o-mini")
                  .WithApiKey(apiKey);
```

From your AppHost directory, set the custom parameter value with `aspire secret set`:

```bash
aspire secret set Parameters:my-api-key "YOUR_GITHUB_TOKEN_HERE"
```

## Connection Properties

When you reference a GitHub Model resource using `WithReference`, the following connection properties are made available to the consuming project:

### GitHub Model

The GitHub Model resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Uri` | The GitHub Models inference endpoint URI, with the format `https://models.github.ai/inference` |
| `Key` | The API key (PAT or GitHub App token) for authentication |
| `ModelName` | The model identifier for inference requests, for instance `openai/gpt-4o-mini` |
| `Organization` | The organization attributed to the request (available when configured) |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `chat` becomes `CHAT_URI`.

## Available Models

GitHub Models supports various AI models. Some popular options include:

- `openai/gpt-4o-mini`
- `openai/gpt-4o`
- `deepseek/DeepSeek-V3-0324`
- `microsoft/Phi-4-mini-instruct`

Check the [GitHub Models documentation](https://docs.github.com/en/github-models) for the most up-to-date list of available models.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/ai/github-models/github-models-host/
* https://docs.github.com/en/github-models

## Feedback & contributing

https://github.com/microsoft/aspire
