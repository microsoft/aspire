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

	openai := builder.AddAzureOpenAI("openai")
	chat := openai.AddDeployment("chat", "gpt-4o-mini", "2024-07-18")

	api := builder.AddContainer("api", "redis:latest")
	api.WithCognitiveServicesRoleAssignments(openai, []aspire.AzureOpenAIRole{aspire.AzureOpenAIRoleCognitiveServicesOpenAIUser})
	if err = api.Err(); err != nil {
		log.Fatalf("api: %v", err)
	}

	_, _ = chat.Parent()
	_, _ = chat.ConnectionStringExpression()
	if err = chat.Err(); err != nil {
		log.Fatalf("chat: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
