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

	// ===================================================================
	// Factory methods on builder
	// ===================================================================

	// AddContainer (pre-existing)
	container := builder.AddContainer("mycontainer", "nginx")
	if err = container.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	taggedContainer := builder.AddContainer("mytaggedcontainer", &aspire.AddContainerOptions{
		Image: "nginx",
		Tag:   "stable-alpine",
	})
	if err = taggedContainer.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// AddDockerfile
	dockerContainer := builder.AddDockerfile("dockerapp", "./app")
	if err = dockerContainer.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// AddExecutable (pre-existing)
	exe := builder.AddExecutable("myexe", "echo", ".", []string{"hello"})
	if err = exe.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// AddProject (pre-existing)
	project := builder.AddProject("myproject", "./src/MyProject", &aspire.AddProjectOptions{
		LaunchProfileOrOptions: "https",
	})
	if err = project.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	projectWithoutLaunchProfile := builder.AddProject("myproject-noprofile", "./src/MyProject")
	if err = projectWithoutLaunchProfile.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	// ATS exports ReferenceEnvironmentInjectionFlags as a DTO-shaped object in TypeScript.
	referenceEnvironmentOptions := &aspire.ReferenceEnvironmentInjectionOptions{
		ConnectionString: true,
		ServiceDiscovery: true,
	}
	project.WithReferenceEnvironment(referenceEnvironmentOptions)

	// AddCSharpApp
	csharpApp := builder.AddCSharpApp("csharpapp", "./src/CSharpApp")
	if err = csharpApp.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// AddContainer
	cache := builder.AddRedis("cache")
	if err = cache.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// AddDotnetTool
	tool := builder.AddDotnetTool("mytool", "dotnet-ef")
	if err = tool.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// AddParameterFromConfiguration
	configParam := builder.AddParameterFromConfiguration("myconfig", "MyConfig:Key")
	secretParam := builder.AddParameterFromConfiguration("mysecret", "MyConfig:Secret",
		&aspire.AddParameterFromConfigurationOptions{Secret: aspire.BoolPtr(true)})
	generatedParam := builder.AddParameterWithGeneratedValue("generated-secret",
		&aspire.GenerateParameterDefault{
			MinLength:  24,
			Lower:      true,
			Upper:      true,
			Numeric:    true,
			Special:    false,
			MinUpper:   2,
			MinNumeric: 2,
		},
		&aspire.AddParameterWithGeneratedValueOptions{
			Secret:  aspire.BoolPtr(true),
			Persist: aspire.BoolPtr(true),
		})

	// ===================================================================
	// Container-specific methods on ContainerResource
	// ===================================================================

	// WithDockerfileBaseImage
	container.WithDockerfileBaseImage(&aspire.WithDockerfileBaseImageOptions{
		BuildImage: aspire.StringPtr("mcr.microsoft.com/dotnet/sdk:8.0"),
	})

	// WithBuildArg
	dockerContainer.WithBuildArg("STATIC_BRANDING", "/app/static/branding/custom")
	dockerContainer.WithBuildArg("CONFIG_BRANDING", configParam)

	// WithContainerCertificatePaths
	container.WithContainerCertificatePaths(&aspire.WithContainerCertificatePathsOptions{
		CustomCertificatesDestination:    aspire.StringPtr("/usr/lib/ssl/aspire/custom"),
		DefaultCertificateBundlePaths:    []string{"/etc/ssl/certs/ca-certificates.crt"},
		DefaultCertificateDirectoryPaths: []string{"/etc/ssl/certs", "/usr/local/share/ca-certificates"},
	})

	// WithImageRegistry
	container.WithImageRegistry("docker.io")

	// ===================================================================
	// Endpoints and connection strings
	// ===================================================================

	dockerContainer.WithHttpEndpoint(&aspire.WithHttpEndpointOptions{
		Name:       aspire.StringPtr("http"),
		TargetPort: aspire.Float64Ptr(80),
	})

	endpoint := dockerContainer.GetEndpoint("http")
	if endpoint.Err() != nil {
		log.Fatalf(aspire.FormatError(endpoint.Err()))
	}
	expr := aspire.RefExpr("Host=%v", endpoint)
	endpointHost := endpoint.Property(aspire.EndpointPropertyHost)
	endpointPort := endpoint.Property(aspire.EndpointPropertyPort)
	endpointURL := aspire.RefExpr("http://%v:%v", endpointHost, endpointPort)

	_ = builder.AddConnectionString("customcs",
		&aspire.AddConnectionStringOptions{EnvironmentVariableNameOrExpression: expr})

	envConnectionString := builder.AddConnectionString("envcs")

	// ===================================================================
	// ResourceBuilderExtensions on ContainerResource
	// ===================================================================

	// WithEnvironment - EndpointReference
	container.WithEnvironment("MY_ENDPOINT", endpoint)
	container.WithEnvironment("MY_ENDPOINT_URL", endpointURL)

	// WithEnvironment — with ReferenceExpression (via WithEnvironment any overload)
	container.WithEnvironment("MY_EXPR", expr)

	// WithEnvironment — with ParameterResource
	container.WithEnvironment("MY_PARAM", configParam)
	container.WithEnvironment("MY_SECRET_PARAM", secretParam)
	container.WithEnvironment("MY_GENERATED_PARAM", generatedParam)

	// WithEnvironment — with connection string resource
	container.WithEnvironment("MY_CONN", envConnectionString)

	// ExcludeFromManifest
	container.ExcludeFromManifest()

	// ExcludeFromMcp
	container.ExcludeFromMcp()

	// WaitForCompletion
	container.WaitForCompletion(exe)

	// WithDeveloperCertificateTrust
	container.WithDeveloperCertificateTrust(true)

	// WithCertificateTrustScope
	container.WithCertificateTrustScope(aspire.CertificateTrustScopeSystem)

	// WithHttpsDeveloperCertificate
	container.WithHttpsDeveloperCertificate()

	// WithoutHttpsCertificate
	container.WithoutHttpsCertificate()

	// WithChildRelationship
	container.WithChildRelationship(exe)

	// WithRelationship
	container.WithRelationship(taggedContainer, "peer")

	// WithRelationship
	project.WithReference(cache)

	// WithIconName
	iconVariant := aspire.IconVariantFilled
	container.WithIconName("Database", &aspire.WithIconNameOptions{
		IconVariant: &iconVariant,
	})

	// WithHttpProbe
	container.WithHttpProbe(aspire.ProbeTypeLiveness, &aspire.WithHttpProbeOptions{
		Path: aspire.StringPtr("/health"),
	})

	// WithRemoteImageName
	container.WithRemoteImageName("myregistry.azurecr.io/myapp")

	// WithRemoteImageTag
	container.WithRemoteImageTag("latest")

	// WithMcpServer ( variant for path)
	container.WithMcpServer(&aspire.WithMcpServerOptions{
		Path: aspire.StringPtr("/mcp"),
	})

	// WithRequiredCommand
	container.WithRequiredCommand("docker")

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
		log.Fatalf(aspire.FormatError(err))
	}

	// publishAsDockerFile
	tool.PublishAsDockerFile(func(_ aspire.ContainerResource) {})

	// ===================================================================
	// Pipeline step factory
	// ===================================================================

	_ = container.WithPipelineStepFactory("custom-build-step",
		func(stepContext aspire.PipelineStepContext) {
			pipelineContext := stepContext.PipelineContext()
			pipelineModel := pipelineContext.Model()
			_, _ = pipelineModel.GetResources()
			_ = pipelineModel.FindResourceByName("mycontainer")
			pipelineServices := pipelineContext.Services()
			pipelineLoggerFactory := pipelineServices.GetLoggerFactory()
			pipelineFactoryLogger := pipelineLoggerFactory.CreateLogger("ValidationAppHost.PipelineContext")
			_ = pipelineFactoryLogger.LogInformation("Pipeline factory context logger")
			pipelineLogger := pipelineContext.Logger()
			_ = pipelineLogger.LogDebug("Pipeline context logger")
			pipelineSummary := pipelineContext.Summary()
			_ = pipelineSummary.Add("PipelineContext", "Validated")
			_ = pipelineSummary.AddMarkdown("PipelineMarkdown", "**Validated**")

			executionContext := stepContext.ExecutionContext()
			_, _ = executionContext.IsPublishMode()
			stepServices := stepContext.Services()
			stepLogger := stepContext.Logger()
			_ = stepLogger.LogInformation("Pipeline step context logger")
			stepSummary := stepContext.Summary()
			_ = stepSummary.Add("PipelineStepContext", "Validated")
			reportingStep := stepContext.ReportingStep()
			_ = reportingStep.LogStep("information", "Reporting step log")
			_ = reportingStep.LogStepMarkdown("information", "**Reporting step markdown log**")
			reportingTask := reportingStep.CreateTask("Task created")
			_ = reportingTask.UpdateTask("Task updated")
			_ = reportingTask.UpdateTaskMarkdown("**Task markdown updated**")
			_ = reportingTask.CompleteTask(&aspire.CompleteTaskOptions{CompletionMessage: aspire.StringPtr("Task complete")})
			markdownTask := reportingStep.CreateMarkdownTask("**Markdown task created**")
			_ = markdownTask.CompleteTaskMarkdown("**Markdown task complete**",
				&aspire.CompleteTaskMarkdownOptions{CompletionState: aspire.StringPtr("completed-with-warning")})
			_ = reportingStep.CompleteStep("Reporting step complete")
			_ = reportingStep.CompleteStepMarkdown("**Reporting step markdown complete**",
				&aspire.CompleteStepMarkdownOptions{CompletionState: aspire.StringPtr("completed-with-warning")})
			stepModel := stepContext.Model()
			_, _ = stepModel.GetResources()
			_ = stepModel.FindResourceByName("mycontainer")
			stepLoggerFactory := stepServices.GetLoggerFactory()
			stepFactoryLogger := stepLoggerFactory.CreateLogger("ValidationAppHost.PipelineStepContext")
			_ = stepFactoryLogger.LogDebug("Pipeline step factory logger")
			cancellationToken, _ := stepContext.CancellationToken()
			cacheUriExpression := cache.UriExpression()
			_, _ = cacheUriExpression.GetValue(cancellationToken)
		}, &aspire.WithPipelineStepFactoryOptions{
			DependsOn:   []string{"build"},
			RequiredBy:  []string{"deploy"},
			Tags:        []string{"custom-build"},
			Description: aspire.StringPtr("Custom pipeline step"),
		})

	_ = container.WithPipelineConfiguration(func(configContext aspire.PipelineConfigurationContext) {
		configLog := configContext.Log()
		_ = configLog.Info("Pipeline configuration logger")
		configPipeline := configContext.Pipeline()
		allSteps, _ := configPipeline.Steps()
		taggedSteps, _ := configPipeline.StepsByTag(aspire.WellKnownPipelineTags.BuildCompute)
		if len(allSteps) > 0 {
			_, _ = allSteps[0].Name()
			_, _ = allSteps[0].Description()
			_ = allSteps[0].AddTag("validated")
			_ = allSteps[0].DependsOn("restore")
			_ = allSteps[0].DependsOn("build")
		}
		if len(taggedSteps) > 0 {
			_ = taggedSteps[0].RequiredBy(aspire.WellKnownPipelineSteps.Publish)
		}
	})

	_ = container.WithPipelineConfiguration(func(configContext aspire.PipelineConfigurationContext) {
		configPipeline := configContext.Pipeline()
		_, _ = configPipeline.Steps()
		_, _ = configPipeline.StepsByTag(aspire.WellKnownPipelineTags.BuildCompute)
	})

	// ===================================================================
	// Builder properties
	// ===================================================================

	_, _ = builder.AppHostDirectory()
	hostEnvironment := builder.Environment()
	_, _ = hostEnvironment.IsDevelopment()
	_, _ = hostEnvironment.IsProduction()
	_, _ = hostEnvironment.IsStaging()
	_, _ = hostEnvironment.IsEnvironment("Development")

	builderConfiguration := builder.GetConfiguration()
	_, _ = builderConfiguration.GetConfigValue("MyConfig:Key")
	_, _ = builderConfiguration.GetConnectionString("customcs")
	_ = builderConfiguration.GetSection("MyConfig")
	_, _ = builderConfiguration.GetChildren()
	_, _ = builderConfiguration.Exists("MyConfig:Key")

	builderExecutionContext := builder.ExecutionContext()
	executionContextServiceProvider := builderExecutionContext.ServiceProvider()
	_ = executionContextServiceProvider.GetDistributedApplicationModel()

	// Subscriptions (typed callbacks)
	beforeStartSub := builder.SubscribeBeforeStart(func(e aspire.BeforeStartEvent) {
		beforeStartModel := e.Model()
		_, _ = beforeStartModel.GetResources()
		_ = beforeStartModel.FindResourceByName("mycontainer")
		beforeStartServices := e.Services()
		_ = beforeStartServices.GetEventing()
		beforeStartLoggerFactory := beforeStartServices.GetLoggerFactory()
		beforeStartLogger := beforeStartLoggerFactory.CreateLogger("ValidationAppHost.BeforeStart")
		_ = beforeStartLogger.LogInformation("BeforeStart information")
		_ = beforeStartLogger.LogWarning("BeforeStart warning")
		_ = beforeStartLogger.LogError("BeforeStart error")
		_ = beforeStartLogger.LogDebug("BeforeStart debug")
		_ = beforeStartLogger.Log("critical", "BeforeStart critical")
		beforeStartResourceLoggerService := beforeStartServices.GetResourceLoggerService()
		_ = beforeStartResourceLoggerService.CompleteLog(container)
		_ = beforeStartResourceLoggerService.CompleteLogByName("mycontainer")
		beforeStartNotifications := beforeStartServices.GetResourceNotificationService()
		_ = beforeStartNotifications.WaitForResourceState("mycontainer", &aspire.WaitForResourceStateOptions{
			TargetState: aspire.StringPtr("Running"),
		})
		_, _ = beforeStartNotifications.WaitForResourceStates("mycontainer", []string{"Running", "FailedToStart"})
		_, _ = beforeStartNotifications.WaitForResourceHealthy("mycontainer")
		_ = beforeStartNotifications.WaitForDependencies(container)
		_, _ = beforeStartNotifications.TryGetResourceState("mycontainer")
		_ = beforeStartNotifications.PublishResourceUpdate(container, &aspire.PublishResourceUpdateOptions{
			State:      aspire.StringPtr("Validated"),
			StateStyle: aspire.StringPtr("info"),
		})
		beforeStartUserSecrets := beforeStartServices.GetUserSecretsManager()
		_, _ = beforeStartUserSecrets.IsAvailable()
		_, _ = beforeStartUserSecrets.FilePath()
		_, _ = beforeStartUserSecrets.TrySetSecret("Validation:Key", "value")
		_ = beforeStartUserSecrets.GetOrSetSecret(container, "Validation:GeneratedKey", "generated-value")
		_, _ = builderConfiguration.GetConfigValue("Validation:GeneratedKey")
		_ = beforeStartUserSecrets.SaveStateJson("{\"Validation\":\"Value\"}")
		_ = beforeStartServices.GetDistributedApplicationModel()
	})

	afterResourcesSub := builder.SubscribeAfterResourcesCreated(func(e aspire.AfterResourcesCreatedEvent) {
		afterResourcesModel := e.Model()
		_, _ = afterResourcesModel.GetResources()
		_ = afterResourcesModel.FindResourceByName("mycontainer")
		afterResourcesServices := e.Services()
		afterResourcesLoggerFactory := afterResourcesServices.GetLoggerFactory()
		afterResourcesLogger := afterResourcesLoggerFactory.CreateLogger("ValidationAppHost.AfterResourcesCreated")
		_ = afterResourcesLogger.LogInformation("AfterResourcesCreated")
	})

	builderEventing := builder.Eventing()
	_ = builderEventing.Unsubscribe(beforeStartSub)
	_ = builderEventing.Unsubscribe(afterResourcesSub)

	// Resource events — typed callbacks
	_ = container.OnBeforeResourceStarted(func(e aspire.BeforeResourceStartedEvent) {
		_ = e.Resource()
		services := e.Services()
		loggerFactory := services.GetLoggerFactory()
		logger := loggerFactory.CreateLogger("ValidationAppHost.BeforeResourceStarted")
		_ = logger.LogInformation("BeforeResourceStarted")
	})

	_ = container.OnResourceStopped(func(e aspire.ResourceStoppedEvent) {
		_ = e.Resource()
		services := e.Services()
		loggerFactory := services.GetLoggerFactory()
		logger := loggerFactory.CreateLogger("ValidationAppHost.ResourceStopped")
		_ = logger.LogWarning("ResourceStopped")
	})

	_ = container.OnInitializeResource(func(e aspire.InitializeResourceEvent) {
		_ = e.Resource()
		_ = e.Eventing()
		initializeLogger := e.Logger()
		initializeNotifications := e.Notifications()
		initializeServices := e.Services()
		_ = initializeLogger.LogDebug("InitializeResource")
		_ = initializeNotifications.WaitForDependencies(container)
		_ = initializeServices.GetDistributedApplicationModel()
		_ = initializeServices.GetEventing()
	})

	_ = container.OnResourceEndpointsAllocated(func(e aspire.ResourceEndpointsAllocatedEvent) {
		_ = e.Resource()
		services := e.Services()
		loggerFactory := services.GetLoggerFactory()
		logger := loggerFactory.CreateLogger("ValidationAppHost.ResourceEndpointsAllocated")
		_ = logger.LogInformation("ResourceEndpointsAllocated")
	})

	_ = container.OnResourceReady(func(e aspire.ResourceReadyEvent) {
		_ = e.Resource()
		services := e.Services()
		loggerFactory := services.GetLoggerFactory()
		logger := loggerFactory.CreateLogger("ValidationAppHost.ResourceReady")
		_ = logger.LogInformation("ResourceReady")
	})

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
	_ = container.WithParentRelationship(exe)
	_ = projectWithoutLaunchProfile.WithParentRelationship(project)
	_ = container.WithExplicitStart()
	_ = container.WithUrl("http://localhost:8080")
	_ = container.WithUrl(expr)
	_ = container.WithHttpHealthCheck()
	_ = container.WithHttpHealthCheck()
	_ = container.WithCommand("restart", "Restart", func(ctx aspire.ExecuteCommandContext) *aspire.ExecuteCommandResult {
		_ = ctx
		return &aspire.ExecuteCommandResult{}
	})

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
