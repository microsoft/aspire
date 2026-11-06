package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	openai := builder.AddAzureOpenAI("openai")
	_ = openai.ConfigureInfrastructure(func(infrastructure aspire.AzureResourceInfrastructure) {
		account := infrastructure.GetCognitiveServicesAccount()
		account.Tags().Set("provisioning-proxy", "go")
	})
	if openai.Err() != nil {
		log.Fatal(aspire.FormatError(openai.Err()))
	}

	chat := openai.AddDeployment("chat", "gpt-4o-mini", "2024-07-18")
	if chat.Err() != nil {
		log.Fatal(aspire.FormatError(chat.Err()))
	}

	api := builder.AddContainer("api", "redis:latest").
		WithCognitiveServicesRoleAssignments(openai, []aspire.AzureOpenAIRole{aspire.AzureOpenAIRoleCognitiveServicesOpenAIUser})
	if api.Err() != nil {
		log.Fatal(aspire.FormatError(api.Err()))
	}

	_ = chat.Parent()
	_ = chat.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}
}
