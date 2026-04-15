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

	apiKey := builder.AddParameterWithOpts("openai-api-key", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})

	openai := builder.AddOpenAI("openai")
	openai.WithEndpoint("https://api.openai.com/v1")
	openai.WithApiKey(apiKey)
	if err = openai.Err(); err != nil {
		log.Fatalf("openai: %v", err)
	}

	openai.AddModel("chat-model", "gpt-4o-mini")

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
