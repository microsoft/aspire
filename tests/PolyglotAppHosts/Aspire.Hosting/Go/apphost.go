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
	container := builder.AddContainer("mycontainer", "nginx")
	if err = container.Err(); err != nil {
		log.Fatalf("container: %v", err)
	}

	// addContainer with tag options
	taggedContainer := builder.AddContainer("mytaggedcontainer", "nginx")
	taggedContainer.WithImageWithOpts("nginx", &aspire.WithImageOptions{Tag: aspire.StringPtr("stable-alpine")})

	// addDockerfile
	dockerContainer := builder.AddDockerfile("dockerapp", "./app")
	if err = dockerContainer.Err(); err != nil {
		log.Fatalf("dockerContainer: %v", err)
	}

	// addExecutable (pre-existing)
	exe := builder.AddExecutable("myexe", "echo", ".", []string{"hello"})

	// addProject (pre-existing)
	project := builder.AddProject("myproject", "./src/MyProject", "https")
	_ = builder.AddProjectWithoutLaunchProfile("myproject-noprofile", "./src/MyProject")

	// addCSharpApp
	_ = builder.AddCSharpApp("csharpapp", "./src/CSharpApp")

	// addContainer as cache reference (base SDK doesn't have addRedis)
	cache := builder.AddContainer("cache", "redis")

	// addDotnetTool
	tool := builder.AddDotnetTool("mytool", "dotnet-ef")

	// addParameterFromConfiguration
	configParam := builder.AddParameterFromConfiguration("myconfig", "MyConfig:Key")
	secretParam := builder.AddParameterFromConfigurationWithOpts("mysecret", "MyConfig:Secret",
		&aspire.AddParameterFromConfigurationOptions{Secret: aspire.BoolPtr(true)})

	// addParameterWithGeneratedValue with opts (secret + persist)
	generatedParam := builder.AddParameterWithGeneratedValueWithOpts("generated-secret",
		&aspire.GenerateParameterDefault{
			MinLength: 24,
			Lower:     true,
			Upper:     true,
			Numeric:   true,
			Special:   false,
			MinUpper:  2,
			MinNumeric: 2,
		},
		&aspire.AddParameterWithGeneratedValueOptions{
			Secret:  aspire.BoolPtr(true),
			Persist: aspire.BoolPtr(true),
		})

	// ===================================================================
	// Container-specific methods on ContainerResource
	// ===================================================================

	// withDockerfileBaseImage (WithOpts variant for build image option)
	container.WithDockerfileBaseImageWithOpts(&aspire.WithDockerfileBaseImageOptions{
		BuildImage: aspire.StringPtr("mcr.microsoft.com/dotnet/sdk:8.0"),
	})

	// withBuildArg
	dockerContainer.WithBuildArg("STATIC_BRANDING", "/app/static/branding/custom")
	dockerContainer.WithBuildArg("CONFIG_BRANDING", configParam)

	// withContainerCertificatePaths (WithOpts variant)
	container.WithContainerCertificatePathsWithOpts(&aspire.WithContainerCertificatePathsOptions{
		CustomCertificatesDestination:   aspire.StringPtr("/usr/lib/ssl/aspire/custom"),
		DefaultCertificateBundlePaths:   []string{"/etc/ssl/certs/ca-certificates.crt"},
		DefaultCertificateDirectoryPaths: []string{"/etc/ssl/certs", "/usr/local/share/ca-certificates"},
	})

	// withImageRegistry
	container.WithImageRegistry("docker.io")

	// ===================================================================
	// Endpoints and connection strings
	// ===================================================================

	dockerContainer.WithHttpEndpointWithOpts(&aspire.WithHttpEndpointOptions{
		Name:       aspire.StringPtr("http"),
		TargetPort: aspire.Float64Ptr(80),
	})

	endpoint, err := dockerContainer.GetEndpoint("http")
	if err != nil {
		log.Fatalf("GetEndpoint: %v", err)
	}

	expr := aspire.RefExpr("Host=%s", endpoint)

	// addConnectionStringBuilder (typed callback)
	builtConnectionString := builder.AddConnectionStringBuilder("customcs",
		func(csBuilder *aspire.ReferenceExpressionBuilder) {
			_, _ = csBuilder.IsEmpty()
			_ = csBuilder.AppendLiteral("Host=")
			_ = csBuilder.AppendValueProvider(endpoint)
			_ = csBuilder.AppendLiteral(";Key=")
			_ = csBuilder.AppendValueProvider(secretParam)
			_, _ = csBuilder.Build()
		})

	builtConnectionString.WithConnectionProperty("Host", expr)
	builtConnectionString.WithConnectionPropertyValue("Mode", "Development")

	envConnectionString := builder.AddConnectionString("envcs")

	// ===================================================================
	// ResourceBuilderExtensions on ContainerResource
	// ===================================================================

	// withEnvironment - EndpointReference
	container.WithEnvironmentEndpoint("MY_ENDPOINT", endpoint)

	// withEnvironment — with ReferenceExpression (via WithEnvironment any overload)
	container.WithEnvironment("MY_EXPR", expr)

	// withEnvironment — with ParameterResource
	container.WithEnvironmentParameter("MY_PARAM", configParam)
	container.WithEnvironmentParameter("MY_GENERATED_PARAM", generatedParam)

	// withEnvironment — with connection string resource
	container.WithEnvironmentConnectionString("MY_CONN", envConnectionString)

	// withConnectionProperty
	builtConnectionString.WithConnectionProperty("Endpoint", expr)
	builtConnectionString.WithConnectionPropertyValue("Protocol", "https")

	// excludeFromManifest
	_ = container.ExcludeFromManifest()

	// excludeFromMcp
	_ = container.ExcludeFromMcp()

	// waitForCompletion
	_ = container.WaitForCompletion(aspire.NewIResource(exe.Handle(), exe.Client()))

	// withDeveloperCertificateTrust
	container.WithDeveloperCertificateTrust(true)

	// withCertificateTrustScope
	container.WithCertificateTrustScope(aspire.CertificateTrustScopeSystem)

	// withHttpsDeveloperCertificate
	container.WithHttpsDeveloperCertificate()

	// withoutHttpsCertificate
	container.WithoutHttpsCertificate()

	// withChildRelationship
	_ = container.WithChildRelationship(aspire.NewIResource(exe.Handle(), exe.Client()))

	// withRelationship
	_ = container.WithRelationship(aspire.NewIResource(taggedContainer.Handle(), taggedContainer.Client()), "peer")

	// project.withReference(cache)
	_ = project.WithReference(aspire.NewIResource(cache.Handle(), cache.Client()))

	// withIconName (WithOpts variant for iconVariant)
	iconVariant := aspire.IconVariantFilled
	container.WithIconNameWithOpts("Database", &aspire.WithIconNameOptions{
		IconVariant: &iconVariant,
	})

	// withHttpProbe (WithOpts variant for path)
	container.WithHttpProbeWithOpts(aspire.ProbeTypeLiveness, &aspire.WithHttpProbeOptions{
		Path: aspire.StringPtr("/health"),
	})

	// withRemoteImageName
	if _, err = container.WithRemoteImageName("myregistry.azurecr.io/myapp"); err != nil {
		log.Fatalf("WithRemoteImageName: %v", err)
	}

	// withRemoteImageTag
	if _, err = container.WithRemoteImageTag("latest"); err != nil {
		log.Fatalf("WithRemoteImageTag: %v", err)
	}

	// withMcpServer (WithOpts variant for path)
	_ = container.WithMcpServerWithOpts(&aspire.WithMcpServerOptions{
		Path: aspire.StringPtr("/mcp"),
	})

	// withRequiredCommand
	_ = container.WithRequiredCommand("docker")

	// ===================================================================
	// DotnetToolResourceExtensions — all With-tool methods are fluent
	// ===================================================================

	tool.
		WithToolIgnoreExistingFeeds().
		WithToolIgnoreFailedSources().
		WithToolPackage("dotnet-ef").
		WithToolPrerelease().
		WithToolSource("https://api.nuget.org/v3/index.json").
		WithToolVersion("8.0.0")
	if err = tool.Err(); err != nil {
		log.Fatalf("tool: %v", err)
	}

	// publishAsDockerFile
	_ = tool.PublishAsDockerFile()

	// withReferenceEnvironment (project)
	_ = project.WithReferenceEnvironment(&aspire.ReferenceEnvironmentInjectionOptions{
		ConnectionString: true,
		ServiceDiscovery: true,
	})

	// ===================================================================
	// Pipeline step factory
	// ===================================================================

	_ = container.WithPipelineStepFactoryWithOpts("custom-build-step",
		&aspire.WithPipelineStepFactoryOptions{
			DependsOn:   []string{"build"},
			RequiredBy:  []string{"deploy"},
			Tags:        []string{"custom-build"},
			Description: aspire.StringPtr("Custom pipeline step"),
		},
		func(stepCtx *aspire.PipelineStepContext) {
			// stepCtx holds the pipeline step execution context
			_ = stepCtx
		})

	_ = container.WithPipelineConfiguration(func(configCtx *aspire.PipelineConfigurationContext) {
		_ = configCtx
	})

	_ = container.WithPipelineConfiguration(func(configCtx *aspire.PipelineConfigurationContext) {
		_ = configCtx
	})

	// ===================================================================
	// Builder properties
	// ===================================================================

	_, _ = builder.AppHostDirectory()
	hostEnvironment, err := builder.Environment()
	if err == nil && hostEnvironment != nil {
		_, _ = hostEnvironment.IsDevelopment()
		_, _ = hostEnvironment.IsProduction()
		_, _ = hostEnvironment.IsStaging()
		_, _ = hostEnvironment.IsEnvironment("Development")
	}

	builderConfiguration, err := builder.GetConfiguration()
	if err == nil && builderConfiguration != nil {
		_, _ = builderConfiguration.GetConfigValue("MyConfig:Key")
		_, _ = builderConfiguration.GetConnectionString("customcs")
		_, _ = builderConfiguration.GetSection("MyConfig")
		_, _ = builderConfiguration.GetChildren()
		_, _ = builderConfiguration.Exists("MyConfig:Key")
	}

	builderExecutionContext, err := builder.ExecutionContext()
	if err == nil && builderExecutionContext != nil {
		serviceProvider, _ := builderExecutionContext.ServiceProvider()
		if serviceProvider != nil {
			_, _ = serviceProvider.GetDistributedApplicationModel()
		}
	}

	// Subscriptions (typed callbacks)
	beforeStartSub, err := builder.SubscribeBeforeStart(func(e *aspire.BeforeStartEvent) {
		_ = e
	})
	if err != nil {
		log.Fatalf("SubscribeBeforeStart: %v", err)
	}

	afterResourcesSub, err := builder.SubscribeAfterResourcesCreated(func(e *aspire.AfterResourcesCreatedEvent) {
		_ = e
	})
	if err != nil {
		log.Fatalf("SubscribeAfterResourcesCreated: %v", err)
	}

	builderEventing, err := builder.Eventing()
	if err == nil && builderEventing != nil {
		builderEventing.Unsubscribe(beforeStartSub)
		builderEventing.Unsubscribe(afterResourcesSub)
	}

	// Resource events — typed callbacks
	_ = container.OnBeforeResourceStarted(func(e *aspire.BeforeResourceStartedEvent) { _ = e })
	_ = container.OnResourceStopped(func(e *aspire.ResourceStoppedEvent) { _ = e })
	_ = builtConnectionString.OnConnectionStringAvailable(func(e *aspire.ConnectionStringAvailableEvent) { _ = e })
	_ = container.OnInitializeResource(func(e *aspire.InitializeResourceEvent) { _ = e })
	_ = container.OnResourceEndpointsAllocated(func(e *aspire.ResourceEndpointsAllocatedEvent) { _ = e })
	_ = container.OnResourceReady(func(e *aspire.ResourceReadyEvent) { _ = e })

	// ===================================================================
	// Pre-existing exports — all return resource builder types
	// ===================================================================

	_ = container.WithEnvironment("MY_VAR", "value")
	_ = container.WithEndpoint()
	_ = container.WithHttpEndpoint()
	_ = container.WithHttpsEndpoint()
	_ = container.WithExternalHttpEndpoints()
	_ = container.AsHttp2Service()
	_ = container.WithArgs([]string{"--verbose"})
	_ = container.WithParentRelationship(aspire.NewIResource(exe.Handle(), exe.Client()))
	_ = container.WithExplicitStart()
	_ = container.WithUrl("http://localhost:8080")
	_ = container.WithUrlExpression(expr)
	_ = container.WithHttpHealthCheck()
	_ = container.WithCommand("restart", "Restart", func(ctx *aspire.ExecuteCommandContext) {
		_ = ctx
	})

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
