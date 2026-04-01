import { createBuilder, GitHubModelName } from './.modules/aspire.js';

const builder = await createBuilder();

// 1) addGitHubModel — using the GitHubModelName enum
const githubModel = await builder.addGitHubModel("chat", GitHubModelName.OpenAIGpt4o);

// 2) addGitHubModel — with organization parameter
const orgParam = await builder.addParameter("gh-org");
const githubModelWithOrg = await builder.addGitHubModel("chat-org", GitHubModelName.OpenAIGpt4oMini, { organization: orgParam });

// 3) addGitHubModelById — using a model identifier string for models not in the enum
const customModel = await builder.addGitHubModelById("custom-chat", "custom-vendor/custom-model");

// 3) withApiKey — configure a custom API key parameter
const apiKey = await builder.addParameter("gh-api-key", { secret: true });
await githubModel.withApiKey(apiKey);

// 4) enableHealthCheck — integration-specific no-args health check
await githubModel.enableHealthCheck();

// 5) withReference — pass GitHubModelResource as a connection string source to a container
const container = await builder.addContainer("my-service", "mcr.microsoft.com/dotnet/samples:latest");
await container.withReference(githubModel);

// 6) withReference — pass GitHubModelResource as a source to another container with custom connection name
await container.withReference(githubModelWithOrg, { connectionName: "github-model-org" });

const app = await builder.build();
await app.run();
