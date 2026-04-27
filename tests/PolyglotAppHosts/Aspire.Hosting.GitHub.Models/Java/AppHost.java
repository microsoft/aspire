import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        // 1) addGitHubModel - using the GitHubModelName enum
        var githubModel = builder.addGitHubModel("chat", GitHubModelName.OPEN_AIGPT4O);
        // 2) addGitHubModel - with organization parameter
        var orgParam = builder.addParameter("gh-org");
        var githubModelWithOrg = builder.addGitHubModel("chat-org", GitHubModelName.OPEN_AIGPT4O_MINI, orgParam);
        // 3) addGitHubModelById - using a model identifier string for models not in the enum
        var customModel = builder.addGitHubModelById("custom-chat", "custom-vendor/custom-model");
        // 3) withApiKey - configure a custom API key parameter
        var apiKey = builder.addParameter("gh-api-key", new AddParameterOptions().secret(true));
        githubModel.withApiKey(apiKey);
        // 4) enableHealthCheck - integration-specific no-args health check
        githubModel.withHealthCheck();
        // 5) withReference - pass GitHubModelResource as a connection string source to a container
        var container = builder.addContainer("my-service", "mcr.microsoft.com/dotnet/samples:latest");
        container.withReference(githubModel, new WithReferenceOptions());
        // 6) withReference - pass GitHubModelResource as a source to another container with custom connection name
        container.withReference(githubModelWithOrg, new WithReferenceOptions().connectionName("github-model-org"));
        var app = builder.build();
        app.run();
    }
