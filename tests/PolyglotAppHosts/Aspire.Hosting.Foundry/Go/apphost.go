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

	foundry := builder.AddFoundry("foundry")
	chat := foundry.AddDeployment("chat", "Phi-4", "1", "Microsoft")
	chat.WithProperties(func(deployment aspire.FoundryDeploymentResource) {
		deployment.SetDeploymentName("chat-deployment")
		deployment.SetSkuCapacity(10)
		_, _ = deployment.SkuCapacity()
	})

	model := &aspire.FoundryModel{
		Name:    "gpt-4.1-mini",
		Version: "1",
		Format:  "OpenAI",
	}
	foundry.AddDeploymentFromModel("chat-from-model", model)

	localFoundry := builder.AddFoundry("local-foundry")
	localFoundry.RunAsFoundryLocal()
	localFoundry.AddDeployment("local-chat", "Phi-3.5-mini-instruct", "1", "Microsoft")

	registry := builder.AddAzureContainerRegistry("registry")
	keyVault := builder.AddAzureKeyVault("vault")
	appInsights := builder.AddAzureApplicationInsights("insights")
	cosmos := builder.AddAzureCosmosDB("cosmos")
	storage := builder.AddAzureStorage("storage")
	search := builder.AddAzureSearch("search")

	project := foundry.AddProject("project")
	project.WithContainerRegistry(registry)
	project.WithKeyVault(keyVault)
	project.WithAppInsights(appInsights)
	project.AddCapabilityHost("cap-host")
	project.WithCapabilityHost(cosmos)
	project.WithCapabilityHost(storage)
	project.WithCapabilityHost(search)
	project.WithCapabilityHost(foundry)

	project.AddCosmosConnection(cosmos)
	project.AddStorageConnection(storage)
	project.AddContainerRegistryConnection(registry)
	project.AddKeyVaultConnection(keyVault)

	builderProjectFoundry := builder.AddFoundry("builder-project-foundry")
	builderProject := builderProjectFoundry.AddProject("builder-project")
	builderProject.AddModelDeployment("builder-project-model", "Phi-4-mini", "1", "Microsoft")
	project.AddModelDeploymentFromModel("project-model", model)

	hostedAgent := builder.AddExecutable(
		"hosted-agent",
		"node",
		".",
		[]string{
			"-e",
			`
const http = require('node:http');
const port = Number(process.env.DEFAULT_AD_PORT ?? '8088');
const server = http.createServer((req, res) => {
  if (req.url === '/liveness' || req.url === '/readiness') {
    res.writeHead(200, { 'content-type': 'text/plain' });
    res.end('ok');
    return;
  }
  if (req.url === '/responses') {
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ output: 'hello from validation app host' }));
    return;
  }
  res.writeHead(404);
  res.end();
});
server.listen(port, '127.0.0.1');
`,
		})

	hostedAgent.PublishAsHostedAgent(&aspire.PublishAsHostedAgentOptions{
		Project: &project,
		Configure: func(cfg aspire.HostedAgentConfiguration) {
			cfg.SetDescription("Validation hosted agent")
			cfg.SetCpu(1)
			cfg.SetMemory(2)
			_ = cfg.Metadata().Set("scenario", "validation")
			_ = cfg.EnvironmentVariables().Set("VALIDATION_MODE", "true")
		},
	})

	api := builder.AddContainer("api", "nginx")
	api.WithRoleAssignments(foundry, []aspire.FoundryRole{
		aspire.FoundryRoleCognitiveServicesOpenAIUser,
		aspire.FoundryRoleCognitiveServicesUser,
	})

	_, _ = chat.DeploymentName()
	_, _ = chat.ModelName()
	_, _ = chat.Format()
	_, _ = chat.ModelVersion()
	_ = chat.ConnectionStringExpression()
	_ = chat.Parent()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
