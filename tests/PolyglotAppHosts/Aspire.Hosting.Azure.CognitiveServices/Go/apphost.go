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

	openai := builder.AddAzureOpenAI("openai")
	if openai.Err() != nil {
		log.Fatalf(aspire.FormatError(openai.Err()))
	}

	chat := openai.AddDeployment("chat", "gpt-4o-mini", "2024-07-18")
	if chat.Err() != nil {
		log.Fatalf(aspire.FormatError(chat.Err()))
	}

	api := builder.AddContainer("api", "redis:latest").
		WithRoleAssignments(openai, []aspire.AzureOpenAIRole{aspire.AzureOpenAIRoleCognitiveServicesOpenAIUser})
	if api.Err() != nil {
		log.Fatalf(aspire.FormatError(api.Err()))
	}

	_ = chat.Parent()
	_ = chat.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
