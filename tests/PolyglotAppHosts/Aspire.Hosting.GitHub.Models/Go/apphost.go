package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder(nil)
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	githubModel := builder.AddGitHubModel("chat", aspire.GitHubModelNameOpenAIGpt4o)
	if err = githubModel.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	orgParam := builder.AddParameter("gh-org")
	if err = orgParam.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	githubModelWithOrg := builder.AddGitHubModel("chat-org", aspire.GitHubModelNameOpenAIGpt4oMini, &aspire.AddGitHubModelOptions{
		Organization: &orgParam,
	})
	if err = githubModelWithOrg.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	customModel := builder.AddGitHubModelById("custom-chat", "custom-vendor/custom-model")
	if err = customModel.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	apiKey := builder.AddParameter("gh-api-key", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	if err = apiKey.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	githubModel.WithApiKey(apiKey)

	githubModel.WithHealthCheck()

	container := builder.AddContainer("my-service", "mcr.microsoft.com/dotnet/samples:latest")
	container.WithReference(githubModel)
	if err = container.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	container.WithReference(githubModelWithOrg, &aspire.WithReferenceOptions{
		ConnectionName: aspire.StringPtr("github-model-org"),
	})

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
