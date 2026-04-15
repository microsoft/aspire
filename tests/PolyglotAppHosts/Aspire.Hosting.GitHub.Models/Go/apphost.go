package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder(nil)
	if err != nil {
		log.Fatalf("CreateBuilder: %v", err)
	}

	// 1) addGitHubModel — using the GitHubModelName enum
	githubModel := builder.AddGitHubModel("chat", aspire.GitHubModelNameOpenAIGpt4o)
	if err = githubModel.Err(); err != nil {
		log.Fatalf("githubModel: %v", err)
	}

	// 2) addGitHubModel — with organization parameter
	orgParam := builder.AddParameter("gh-org")
	if err = orgParam.Err(); err != nil {
		log.Fatalf("orgParam: %v", err)
	}
	githubModelWithOrg := builder.AddGitHubModelWithOpts("chat-org", aspire.GitHubModelNameOpenAIGpt4oMini, &aspire.AddGitHubModelOptions{
		Organization: orgParam,
	})
	if err = githubModelWithOrg.Err(); err != nil {
		log.Fatalf("githubModelWithOrg: %v", err)
	}

	// 3) addGitHubModelById — using a model identifier string for models not in the enum
	customModel := builder.AddGitHubModelById("custom-chat", "custom-vendor/custom-model")
	if err = customModel.Err(); err != nil {
		log.Fatalf("customModel: %v", err)
	}

	// 4) withApiKey — configure a custom API key parameter
	apiKey := builder.AddParameterWithOpts("gh-api-key", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	if err = apiKey.Err(); err != nil {
		log.Fatalf("apiKey: %v", err)
	}
	githubModel.WithApiKey(apiKey)

	// 5) enableHealthCheck — integration-specific no-args health check
	githubModel.EnableHealthCheck()

	// 6) withReference — pass GitHubModelResource as a connection string source to a container
	container := builder.AddContainer("my-service", "mcr.microsoft.com/dotnet/samples:latest")
	container.WithReference(aspire.NewIResource(githubModel.Handle(), githubModel.Client()))
	if err = container.Err(); err != nil {
		log.Fatalf("container: %v", err)
	}

	// 7) withReference — with custom connection name
	container.WithReferenceWithOpts(aspire.NewIResource(githubModelWithOrg.Handle(), githubModelWithOrg.Client()), &aspire.WithReferenceOptions{
		ConnectionName: aspire.StringPtr("github-model-org"),
	})

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
