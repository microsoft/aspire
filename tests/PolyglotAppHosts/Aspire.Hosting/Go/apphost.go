// Aspire Go validation AppHost - Aspire.Hosting (core)
// Mirrors the TypeScript/Python/Java fixture for API surface validation.
// Run `aspire restore --apphost apphost.go` to generate the SDK, then `go build ./...`.
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

	// ===================================================================
	// Factory methods on builder
	// ===================================================================

	// addContainer (pre-existing)
	container, err := builder.AddContainer("mycontainer", "nginx")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}

	// addDockerfile
	dockerContainer, err := builder.AddDockerfile("dockerapp", "./app", nil, nil)
	if err != nil {
		log.Fatalf("AddDockerfile: %v", err)
	}

	// addExecutable (pre-existing)
	exe, err := builder.AddExecutable("myexe", "echo", ".", []string{"hello"})
	if err != nil {
		log.Fatalf("AddExecutable: %v", err)
	}

	// addProject (pre-existing)
	_, err = builder.AddProject("myproject", "./src/MyProject", "https")
	if err != nil {
		log.Fatalf("AddProject: %v", err)
	}

	// addCSharpApp
	_, err = builder.AddCSharpApp("csharpapp", "./src/CSharpApp")
	if err != nil {
		log.Fatalf("AddCSharpApp: %v", err)
	}

	// addDotnetTool
	tool, err := builder.AddDotnetTool("mytool", "dotnet-ef")
	if err != nil {
		log.Fatalf("AddDotnetTool: %v", err)
	}

	// addParameterFromConfiguration
	configParam, err := builder.AddParameterFromConfiguration("myconfig", "MyConfig:Key", nil)
	if err != nil {
		log.Fatalf("AddParameterFromConfiguration: %v", err)
	}
	secret := true
	_, err = builder.AddParameterFromConfiguration("mysecret", "MyConfig:Secret", &secret)
	if err != nil {
		log.Fatalf("AddParameterFromConfiguration: %v", err)
	}

	// ===================================================================
	// Container-specific methods on ContainerResource
	// ===================================================================

	// withImageRegistry
	if _, err = container.WithImageRegistry("docker.io"); err != nil {
		log.Fatalf("WithImageRegistry: %v", err)
	}

	// withDockerfileBaseImage
	if _, err = container.WithDockerfileBaseImage(nil, nil); err != nil {
		log.Fatalf("WithDockerfileBaseImage: %v", err)
	}

	// ===================================================================
	// Endpoints and connection strings
	// ===================================================================

	if _, err = dockerContainer.WithHttpEndpoint(nil, nil, nil, nil, nil); err != nil {
		log.Fatalf("WithHttpEndpoint: %v", err)
	}

	endpoint, err := dockerContainer.GetEndpoint("http")
	if err != nil {
		log.Fatalf("GetEndpoint: %v", err)
	}

	expr := aspire.RefExpr("Host={0}", endpoint)

	_, err = builder.AddConnectionStringBuilder("customcs", func(args ...any) any {
		return nil
	})
	if err != nil {
		log.Fatalf("AddConnectionStringBuilder: %v", err)
	}

	envConnectionString, err := builder.AddConnectionString("envcs", nil)
	if err != nil {
		log.Fatalf("AddConnectionString: %v", err)
	}

	// ===================================================================
	// ResourceBuilderExtensions on ContainerResource
	// ===================================================================

	// withEnvironment - EndpointReference
	if _, err = container.WithEnvironmentEndpoint("MY_ENDPOINT", endpoint); err != nil {
		log.Fatalf("WithEnvironmentEndpoint: %v", err)
	}

	// withEnvironment — with ReferenceExpression
	if _, err = container.WithEnvironmentExpression("MY_EXPR", expr); err != nil {
		log.Fatalf("WithEnvironmentExpression: %v", err)
	}

	// withEnvironment — with ParameterResource
	if _, err = container.WithEnvironmentParameter("MY_PARAM", configParam); err != nil {
		log.Fatalf("WithEnvironmentParameter: %v", err)
	}

	// withEnvironment — with connection string resource
	if _, err = container.WithEnvironmentConnectionString("MY_CONN", envConnectionString); err != nil {
		log.Fatalf("WithEnvironmentConnectionString: %v", err)
	}

	// excludeFromManifest
	if _, err = container.ExcludeFromManifest(); err != nil {
		log.Fatalf("ExcludeFromManifest: %v", err)
	}

	// excludeFromMcp
	if _, err = container.ExcludeFromMcp(); err != nil {
		log.Fatalf("ExcludeFromMcp: %v", err)
	}

	// waitForCompletion (pre-existing)
	if _, err = container.WaitForCompletion(exe); err != nil {
		log.Fatalf("WaitForCompletion: %v", err)
	}

	// withChildRelationship
	if _, err = container.WithChildRelationship(exe); err != nil {
		log.Fatalf("WithChildRelationship: %v", err)
	}

	// withRemoteImageName
	if _, err = container.WithRemoteImageName("myregistry.azurecr.io/myapp"); err != nil {
		log.Fatalf("WithRemoteImageName: %v", err)
	}

	// withRemoteImageTag
	if _, err = container.WithRemoteImageTag("latest"); err != nil {
		log.Fatalf("WithRemoteImageTag: %v", err)
	}

	// withRequiredCommand
	if _, err = container.WithRequiredCommand("docker", nil); err != nil {
		log.Fatalf("WithRequiredCommand: %v", err)
	}

	// withMcpServer
	if _, err = container.WithMcpServer(nil, nil); err != nil {
		log.Fatalf("WithMcpServer: %v", err)
	}

	// ===================================================================
	// DotnetToolResourceExtensions
	// ===================================================================

	if _, err = tool.WithToolIgnoreExistingFeeds(); err != nil {
		log.Fatalf("WithToolIgnoreExistingFeeds: %v", err)
	}
	if _, err = tool.WithToolIgnoreFailedSources(); err != nil {
		log.Fatalf("WithToolIgnoreFailedSources: %v", err)
	}
	if _, err = tool.WithToolPackage("dotnet-ef"); err != nil {
		log.Fatalf("WithToolPackage: %v", err)
	}
	if _, err = tool.WithToolPrerelease(); err != nil {
		log.Fatalf("WithToolPrerelease: %v", err)
	}
	if _, err = tool.WithToolSource("https://api.nuget.org/v3/index.json"); err != nil {
		log.Fatalf("WithToolSource: %v", err)
	}
	if _, err = tool.WithToolVersion("8.0.0"); err != nil {
		log.Fatalf("WithToolVersion: %v", err)
	}
	if _, err = tool.PublishAsDockerFile(); err != nil {
		log.Fatalf("PublishAsDockerFile: %v", err)
	}

	// ===================================================================
	// Pipeline step factory
	// ===================================================================

	if _, err = container.WithPipelineStepFactory("custom-build-step", func(args ...any) any {
		return nil
	}, nil); err != nil {
		log.Fatalf("WithPipelineStepFactory: %v", err)
	}

	if _, err = container.WithPipelineConfiguration(func(args ...any) any {
		return nil
	}); err != nil {
		log.Fatalf("WithPipelineConfiguration: %v", err)
	}

	// ===================================================================
	// Builder properties
	// ===================================================================

	_, _ = builder.AppHostDirectory()
	hostEnvironment, err := builder.Environment()
	if err == nil && hostEnvironment != nil {
		_, _ = hostEnvironment.IsDevelopment()
		_, _ = hostEnvironment.IsProduction()
		_, _ = hostEnvironment.IsStaging()
	}

	builderConfiguration, err := builder.GetConfiguration()
	if err == nil && builderConfiguration != nil {
		_, _ = builderConfiguration.GetConfigValue("MyConfig:Key")
		_, _ = builderConfiguration.GetConnectionString("customcs")
		_, _ = builderConfiguration.GetSection("MyConfig")
		_, _ = builderConfiguration.GetChildren()
		_, _ = builderConfiguration.Exists("MyConfig:Key")
	}

	// Subscriptions
	beforeStartSub, err := builder.SubscribeBeforeStart(func(args ...any) any {
		return nil
	})
	if err != nil {
		log.Fatalf("SubscribeBeforeStart: %v", err)
	}

	afterResourcesSub, err := builder.SubscribeAfterResourcesCreated(func(args ...any) any {
		return nil
	})
	if err != nil {
		log.Fatalf("SubscribeAfterResourcesCreated: %v", err)
	}

	builderEventing, err := builder.Eventing()
	if err == nil && builderEventing != nil {
		_, _ = builderEventing.Unsubscribe(beforeStartSub)
		_, _ = builderEventing.Unsubscribe(afterResourcesSub)
	}

	// Resource events
	if _, err = container.OnBeforeResourceStarted(func(args ...any) any { return nil }); err != nil {
		log.Fatalf("OnBeforeResourceStarted: %v", err)
	}
	if _, err = container.OnResourceStopped(func(args ...any) any { return nil }); err != nil {
		log.Fatalf("OnResourceStopped: %v", err)
	}
	if _, err = container.OnInitializeResource(func(args ...any) any { return nil }); err != nil {
		log.Fatalf("OnInitializeResource: %v", err)
	}
	if _, err = container.OnResourceEndpointsAllocated(func(args ...any) any { return nil }); err != nil {
		log.Fatalf("OnResourceEndpointsAllocated: %v", err)
	}
	if _, err = container.OnResourceReady(func(args ...any) any { return nil }); err != nil {
		log.Fatalf("OnResourceReady: %v", err)
	}

	// ===================================================================
	// Pre-existing exports
	// ===================================================================

	if _, err = container.WithEnvironment("MY_VAR", "value"); err != nil {
		log.Fatalf("WithEnvironment: %v", err)
	}
	if _, err = container.WithEndpoint(nil, nil, nil, nil, nil, nil, nil, nil); err != nil {
		log.Fatalf("WithEndpoint: %v", err)
	}
	if _, err = container.WithHttpEndpoint(nil, nil, nil, nil, nil); err != nil {
		log.Fatalf("WithHttpEndpoint: %v", err)
	}
	if _, err = container.WithHttpsEndpoint(nil, nil, nil, nil, nil); err != nil {
		log.Fatalf("WithHttpsEndpoint: %v", err)
	}
	if _, err = container.WithExternalHttpEndpoints(); err != nil {
		log.Fatalf("WithExternalHttpEndpoints: %v", err)
	}
	if _, err = container.AsHttp2Service(); err != nil {
		log.Fatalf("AsHttp2Service: %v", err)
	}
	if _, err = container.WithArgs([]string{"--verbose"}); err != nil {
		log.Fatalf("WithArgs: %v", err)
	}
	if _, err = container.WithParentRelationship(exe); err != nil {
		log.Fatalf("WithParentRelationship: %v", err)
	}
	if _, err = container.WithExplicitStart(); err != nil {
		log.Fatalf("WithExplicitStart: %v", err)
	}
	if _, err = container.WithUrl("http://localhost:8080"); err != nil {
		log.Fatalf("WithUrl: %v", err)
	}
	if _, err = container.WithUrlExpression(expr); err != nil {
		log.Fatalf("WithUrlExpression: %v", err)
	}
	if _, err = container.WithHttpHealthCheck(nil); err != nil {
		log.Fatalf("WithHttpHealthCheck: %v", err)
	}
	if _, err = container.WithCommand("restart", "Restart", func(args ...any) any {
		return &aspire.ExecuteCommandResult{Success: true}
	}); err != nil {
		log.Fatalf("WithCommand: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
