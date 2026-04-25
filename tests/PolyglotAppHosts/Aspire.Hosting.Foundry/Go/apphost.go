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

	foundry := builder.AddFoundry("foundry")
	chat := foundry.AddDeployment("chat", "Phi-4", "1", "Microsoft")
	chat.WithProperties(func(deployment *aspire.FoundryDeploymentResource) {
		deployment.SetDeploymentName("chat-deployment")
		deployment.SetSkuCapacity(10)
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

	hostedAgent.PublishAsHostedAgent(func(cfg *aspire.HostedAgentConfiguration) {
		_, _ = cfg.SetDescription("Validation hosted agent")
		_, _ = cfg.SetCpu(1)
		_, _ = cfg.SetMemory(2)
		meta := aspire.NewAspireDict[string, string](cfg.Handle(), cfg.Client())
		// TODO: add meta "scenario": "validation"
		_, _ = cfg.SetMetadata(meta)
		env := aspire.NewAspireDict[string, string](cfg.Handle(), cfg.Client())
		// TODO: add env "VALIDATION_MODE": "true"
		_, _ = cfg.SetEnvironmentVariables(env)
	})

	api := builder.AddContainer("api", "nginx")
	api.WithRoleAssignments(foundry, []aspire.FoundryRole{
		aspire.FoundryRoleCognitiveServicesOpenAIUser,
		aspire.FoundryRoleCognitiveServicesUser,
	})

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
