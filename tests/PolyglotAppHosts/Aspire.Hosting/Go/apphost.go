package main

import (
	"fmt"
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
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
	genericOtlpProtocol := aspire.OtlpProtocolHttpJson
	container.WithOtlpExporter(&aspire.WithOtlpExporterOptions{Protocol: &genericOtlpProtocol})
	taggedContainer := builder.AddContainer("mytaggedcontainer", &aspire.AddContainerOptions{
		Image: "nginx",
		Tag:   aspire.StringPtr("stable-alpine"),
	})
	if err = taggedContainer.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// AddDockerfile
	dockerContainer := builder.AddDockerfile("dockerapp", "./app")
	if err = dockerContainer.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	dockerfileFactory := func(factoryContext aspire.DockerfileFactoryContext) string {
		_ = factoryContext.Resource()
		return `FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
ENTRYPOINT ["dotnet", "App.dll"]
`
	}
	dockerFactoryContainer := builder.AddDockerfileFactory("dockerfactoryapp", "./app", dockerfileFactory,
		&aspire.AddDockerfileFactoryOptions{Stage: aspire.StringPtr("runtime")})
	if err = dockerFactoryContainer.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	configureDockerfileBuilder := func(dockerfileContext aspire.DockerfileBuilderCallbackContext) {
		dockerfileBuilder := dockerfileContext.Builder()
		dockerfileBuilder.Arg("BASE_IMAGE", &aspire.ArgOptions{DefaultValue: aspire.StringPtr("mcr.microsoft.com/dotnet/runtime:8.0")})
		buildStage := dockerfileBuilder.From("mcr.microsoft.com/dotnet/sdk:8.0", &aspire.FromOptions{StageName: aspire.StringPtr("build")})
		buildStage.WorkDir("/src")
		buildStage.Copy("./src", "/src")
		buildStage.Run("echo building dockerfile")
		runtimeStage := dockerfileBuilder.From("mcr.microsoft.com/dotnet/runtime:8.0", &aspire.FromOptions{StageName: aspire.StringPtr("runtime")})
		runtimeStage.CopyFrom("build", "/src", "/app")
		runtimeStage.Entrypoint([]string{"dotnet", "App.dll"})
	}
	dockerBuilderContainer := builder.AddDockerfileBuilder("dockerbuilderapp", "./app", configureDockerfileBuilder,
		&aspire.AddDockerfileBuilderOptions{Stage: aspire.StringPtr("runtime")})
	if err = dockerBuilderContainer.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	dockerContainer.WithDockerfileBuilder("./app", configureDockerfileBuilder,
		&aspire.WithDockerfileBuilderOptions{Stage: aspire.StringPtr("runtime")})
	dockerFactoryContainer.WithDockerfileFactory("./app", dockerfileFactory,
		&aspire.WithDockerfileFactoryOptions{Stage: aspire.StringPtr("runtime")})

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
	customInputType := aspire.InputTypeNumber
	customInputParam := builder.AddParameter("custom-input")
	customInputParam.WithCustomInput(&aspire.ParameterCustomInputOptions{
		InputType:   &customInputType,
		Label:       "Worker Count",
		Placeholder: "Enter number (1-10)",
		Options: map[string]string{
			"one": "One",
			"two": "Two",
		},
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
	container.WithContainerFiles("/usr/lib/aspire/container-files", ".", &aspire.WithContainerFilesOptions{
		Options: &aspire.ContainerFilesOptions{
			DefaultOwner: aspire.Float64Ptr(1000),
			DefaultGroup: aspire.Float64Ptr(1000),
			Umask:        aspire.Float64Ptr(0o022),
		},
	})

	// WithContainerFilesCallback — build entries dynamically via the context factory methods
	container.WithContainerFilesCallback("/usr/lib/aspire/container-files", func(filesCtx aspire.ContainerFileSystemCallbackContext, _ *aspire.CancellationToken) []aspire.ContainerFileSystemItem {
		filesServices := filesCtx.Services()
		filesLoggerFactory := filesServices.GetLoggerFactory()
		filesLogger := filesLoggerFactory.CreateLogger("ValidationAppHost.ContainerFilesCallback")
		_ = filesLogger.LogInformation("ContainerFilesCallback services")

		appConfig := filesCtx.CreateFile("app.conf", &aspire.CreateFileOptions{Contents: aspire.StringPtr("key=value"), Mode: aspire.Float64Ptr(0o644)})
		nestedConfig := filesCtx.CreateFile("nested.conf", &aspire.CreateFileOptions{Contents: aspire.StringPtr("nested=true")})
		confDir := filesCtx.CreateDirectory("conf.d", []aspire.ContainerFileSystemItem{nestedConfig}, &aspire.CreateDirectoryOptions{Mode: aspire.Float64Ptr(0o755)})
		cert := filesCtx.CreateCertificateFile("server.pem", &aspire.CreateCertificateFileOptions{Contents: aspire.StringPtr("-----BEGIN CERTIFICATE-----")})
		return []aspire.ContainerFileSystemItem{appConfig, confDir, cert}
	}, &aspire.WithContainerFilesCallbackOptions{
		Options: &aspire.ContainerFilesOptions{
			DefaultOwner: aspire.Float64Ptr(1000),
			DefaultGroup: aspire.Float64Ptr(1000),
			Umask:        aspire.Float64Ptr(0o022),
		},
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
	externalService := builder.AddExternalService("external-service", "https://example.com")

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

	// WithEnvironment — with external service resource
	container.WithEnvironment("MY_EXTERNAL_SERVICE", externalService)

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
	project.WithEndpointsInEnvironment([]string{"https"})
	if err = builder.AddHealthCheck("custom_check", func(args ...any) *aspire.HealthCheckResult {
		return &aspire.HealthCheckResult{
			Status:      aspire.HealthStatusHealthy,
			Description: aspire.StringPtr("custom health check"),
			Data:        map[string]string{"custom": "value"},
		}
	}); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

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

	// WithRequiredCommandValidation
	container.WithRequiredCommandValidation("docker", func(validationCtx aspire.RequiredCommandValidationContext) aspire.RequiredCommandValidationResult {
		validationServices := validationCtx.Services()
		validationLoggerFactory := validationServices.GetLoggerFactory()
		validationLogger := validationLoggerFactory.CreateLogger("ValidationAppHost.RequiredCommandValidation")
		_ = validationLogger.LogInformation("RequiredCommandValidation services")
		return validationCtx.Success()
	})

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
	resourceCommandService := executionContextServiceProvider.GetResourceCommandService()

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

	beforePublishSub := builder.SubscribeBeforePublish(func(e aspire.BeforePublishEvent) {
		beforePublishModel := e.Model()
		_, _ = beforePublishModel.GetResources()
		_ = beforePublishModel.FindResourceByName("mycontainer")
		beforePublishServices := e.Services()
		beforePublishLoggerFactory := beforePublishServices.GetLoggerFactory()
		beforePublishLogger := beforePublishLoggerFactory.CreateLogger("ValidationAppHost.BeforePublish")
		_ = beforePublishLogger.LogInformation("BeforePublish")
	})

	afterPublishSub := builder.SubscribeAfterPublish(func(e aspire.AfterPublishEvent) {
		afterPublishModel := e.Model()
		_, _ = afterPublishModel.GetResources()
		_ = afterPublishModel.FindResourceByName("mycontainer")
		afterPublishServices := e.Services()
		afterPublishLoggerFactory := afterPublishServices.GetLoggerFactory()
		afterPublishLogger := afterPublishLoggerFactory.CreateLogger("ValidationAppHost.AfterPublish")
		_ = afterPublishLogger.LogInformation("AfterPublish")
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
	_ = builderEventing.Unsubscribe(beforePublishSub)
	_ = builderEventing.Unsubscribe(afterPublishSub)

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
	_ = container.WithHealthCheck("custom_check")
	_ = container.WithHttpHealthCheck()
	_ = container.WithHttpHealthCheck()
	updateCommandState := func(ctx aspire.UpdateCommandStateContext) aspire.ResourceCommandState {
		updateStateServices := ctx.Services()
		updateStateLoggerFactory := updateStateServices.GetLoggerFactory()
		updateStateLogger := updateStateLoggerFactory.CreateLogger("ValidationAppHost.UpdateCommandState")
		_ = updateStateLogger.LogInformation("UpdateCommandState services")
		snapshot, err := ctx.ResourceSnapshot()
		if err != nil || snapshot.HealthStatus == nil {
			return aspire.ResourceCommandStateDisabled
		}
		if *snapshot.HealthStatus == aspire.HealthStatusHealthy {
			return aspire.ResourceCommandStateEnabled
		}
		return aspire.ResourceCommandStateDisabled
	}
	_ = container.WithCommand("noop", "Noop", func(ctx aspire.ExecuteCommandContext) *aspire.ExecuteCommandResult {
		return &aspire.ExecuteCommandResult{Success: true}
	}, &aspire.WithCommandOptions{
		CommandOptions: &aspire.CommandOptions{
			UpdateState: updateCommandState,
		},
	})
	validateCommandArguments := func(ctx aspire.InputsDialogValidationContext) {
		validationServices := ctx.Services()
		validationLoggerFactory := validationServices.GetLoggerFactory()
		validationLogger := validationLoggerFactory.CreateLogger("ValidationAppHost.ValidateCommandArguments")
		_ = validationLogger.LogInformation("Validate command arguments services")
	}
	_ = container.WithCommand("echo", "Echo", func(ctx aspire.ExecuteCommandContext) *aspire.ExecuteCommandResult {
		echoServices := ctx.Services()
		echoLoggerFactory := echoServices.GetLoggerFactory()
		echoLogger := echoLoggerFactory.CreateLogger("ValidationAppHost.EchoCommand")
		_ = echoLogger.LogInformation("Echo command services")
		message, err := ctx.Arguments().Value("message")
		if err != nil {
			return &aspire.ExecuteCommandResult{Success: false, ErrorMessage: aspire.StringPtr(aspire.FormatError(err))}
		}
		return &aspire.ExecuteCommandResult{Success: message == "hello"}
	}, &aspire.WithCommandOptions{
		CommandOptions: &aspire.CommandOptions{
			Arguments: []*aspire.InteractionInput{
				{
					Name:      "message",
					InputType: aspire.InputTypeText,
					Required:  aspire.BoolPtr(true),
				},
			},
			ValidateArguments: validateCommandArguments,
		},
	})
	_ = container.WithCommand("restart", "Restart", func(ctx aspire.ExecuteCommandContext) *aspire.ExecuteCommandResult {
		cancellationToken, err := ctx.CancellationToken()
		if err != nil {
			return &aspire.ExecuteCommandResult{Success: false, ErrorMessage: aspire.StringPtr(aspire.FormatError(err))}
		}
		result, err := resourceCommandService.ExecuteCommandAsync(container, "echo", &aspire.ExecuteCommandAsyncOptions{Arguments: map[string]string{"message": "hello"}, CancellationToken: cancellationToken})
		if err != nil {
			return &aspire.ExecuteCommandResult{Success: false, ErrorMessage: aspire.StringPtr(aspire.FormatError(err))}
		}

		return result
	})

	container.WithHttpsCertificateConfiguration(func(certCtx aspire.HttpsCertificateConfigurationCallbackAnnotationContext) {
		certificatePath := certCtx.CertificatePath()
		keyPath := certCtx.KeyPath()
		certArgs := certCtx.Arguments()
		_ = certArgs.Add("--certificate")
		_ = certArgs.Add(certificatePath)
		_ = certArgs.Add("--key")
		_ = certArgs.Add(keyPath)
		certEnv := certCtx.Environment()
		_ = certEnv.Set("Kestrel__Certificates__Path", certificatePath)
		_ = certEnv.Set("Kestrel__Certificates__KeyPath", keyPath)
	})

	_ = container.SubscribeHttpsEndpointsUpdate(func(httpsCtx aspire.HttpsEndpointUpdateCallbackContext) {
		httpsServices := httpsCtx.Services()
		httpsLoggerFactory := httpsServices.GetLoggerFactory()
		httpsLogger := httpsLoggerFactory.CreateLogger("ValidationAppHost.HttpsEndpointsUpdate")
		_ = httpsLogger.LogInformation("HttpsEndpointsUpdate services")
	})

	_ = container.WithContainerBuildOptions(func(buildCtx aspire.ContainerBuildOptionsCallbackContext) {
		buildCtx.SetDestination(aspire.ContainerImageDestinationRegistry).
			SetImageFormat(aspire.ContainerImageFormatOci).
			SetTargetPlatform(aspire.ContainerTargetPlatformLinuxAmd64).
			SetOutputPath("./artifacts/container-image").
			SetLocalImageName("validation-image").
			SetLocalImageTag("latest")
		buildServices := buildCtx.Services()
		buildLoggerFactory := buildServices.GetLoggerFactory()
		buildLogger := buildLoggerFactory.CreateLogger("ValidationAppHost.ContainerBuildOptions")
		_ = buildLogger.LogInformation("ContainerBuildOptions services")
	})

	// Test bench for the polyglot IInteractionService API: prompts for a region, then dynamically
	// loads the available zones for that region into a second choice input. Reached via the command's
	// service provider (Services().GetInteractionService()), which only prompts when the
	// interaction service is available (the interactive dashboard path).
	_ = container.WithCommand("pick-zone", "Pick Zone", func(ctx aspire.ExecuteCommandContext) *aspire.ExecuteCommandResult {
		interactionService := ctx.Services().GetInteractionService()

		available, err := interactionService.IsAvailable()
		if err != nil {
			return &aspire.ExecuteCommandResult{Success: false, ErrorMessage: aspire.StringPtr(aspire.FormatError(err))}
		}
		if !available {
			return &aspire.ExecuteCommandResult{Success: true, Message: aspire.StringPtr("Interaction service is not available.")}
		}

		regionInput := interactionService.CreateChoiceInput("region", &aspire.CreateChoiceInputOptions{
			Choices: []*aspire.InteractionChoiceOption{{Value: "us", Label: "United States"}, {Value: "eu", Label: "Europe"}},
		})

		zoneInput := interactionService.CreateChoiceInput("zone").WithDynamicLoading(func(loadContext aspire.InteractionInputLoadContext) {
			region, _ := loadContext.Inputs().Value("region")
			zones := []*aspire.InteractionChoiceOption{{Value: "us-east", Label: "US East"}, {Value: "us-west", Label: "US West"}}
			if region == "eu" {
				zones = []*aspire.InteractionChoiceOption{{Value: "eu-west", Label: "EU West"}, {Value: "eu-north", Label: "EU North"}}
			}
			_ = loadContext.Input().SetChoiceOptions(zones)
		})

		result := interactionService.PromptInputs("Pick a zone", "Choose a region, then pick a zone from the dynamically loaded options.", []aspire.InteractionInputBuilder{regionInput, zoneInput})

		canceled, err := result.Canceled()
		if err != nil {
			return &aspire.ExecuteCommandResult{Success: false, ErrorMessage: aspire.StringPtr(aspire.FormatError(err))}
		}
		return &aspire.ExecuteCommandResult{Success: !canceled, Canceled: aspire.BoolPtr(canceled)}
	})

	// Exhaustive coverage of the remaining IInteractionService surface so every newly added member is
	// exercised by the polyglot typecheck: all prompt overloads, every input factory and builder method,
	// the dynamic-loading context accessors/setters, and the option/result DTO fields.
	_ = container.WithCommand("interaction-showcase", "Interaction Showcase", func(ctx aspire.ExecuteCommandContext) *aspire.ExecuteCommandResult {
		interactionService := ctx.Services().GetInteractionService()

		available, err := interactionService.IsAvailable()
		if err != nil {
			return &aspire.ExecuteCommandResult{Success: false, ErrorMessage: aspire.StringPtr(aspire.FormatError(err))}
		}
		if !available {
			return &aspire.ExecuteCommandResult{Success: true, Message: aspire.StringPtr("Interaction service is not available.")}
		}

		confirmIntent := aspire.MessageIntentConfirmation
		confirmation, err := interactionService.PromptConfirmation("Confirm", "Proceed?", &aspire.PromptConfirmationOptions{
			Options: &aspire.InteractionMessageBoxOptions{
				PrimaryButtonText:     aspire.StringPtr("Yes"),
				SecondaryButtonText:   aspire.StringPtr("No"),
				ShowSecondaryButton:   aspire.BoolPtr(true),
				ShowDismiss:           aspire.BoolPtr(true),
				EnableMessageMarkdown: aspire.BoolPtr(true),
				Intent:                &confirmIntent,
			},
		})
		if err != nil {
			return &aspire.ExecuteCommandResult{Success: false, ErrorMessage: aspire.StringPtr(aspire.FormatError(err))}
		}

		infoIntent := aspire.MessageIntentInformation
		messageBox, err := interactionService.PromptMessageBox("Notice", "Read this.", &aspire.PromptMessageBoxOptions{
			Options: &aspire.InteractionMessageBoxOptions{PrimaryButtonText: aspire.StringPtr("OK"), Intent: &infoIntent},
		})
		if err != nil {
			return &aspire.ExecuteCommandResult{Success: false, ErrorMessage: aspire.StringPtr(aspire.FormatError(err))}
		}

		warnIntent := aspire.MessageIntentWarning
		notification, err := interactionService.PromptNotification("Heads up", "Something happened.", &aspire.PromptNotificationOptions{
			Options: &aspire.InteractionNotificationOptions{
				Intent:      &warnIntent,
				LinkText:    aspire.StringPtr("Learn more"),
				LinkUrl:     aspire.StringPtr("https://aspire.dev"),
				ShowDismiss: aspire.BoolPtr(true),
			},
		})
		if err != nil {
			return &aspire.ExecuteCommandResult{Success: false, ErrorMessage: aspire.StringPtr(aspire.FormatError(err))}
		}

		textInput := interactionService.CreateTextInput("name", &aspire.CreateTextInputOptions{
			Options: &aspire.CreateInteractionInputOptions{
				Label:                     aspire.StringPtr("Name"),
				Description:               aspire.StringPtr("Your **name**"),
				EnableDescriptionMarkdown: aspire.BoolPtr(true),
				Required:                  aspire.BoolPtr(true),
				Placeholder:               aspire.StringPtr("Jane Doe"),
				Value:                     aspire.StringPtr("Jane"),
				MaxLength:                 aspire.Float64Ptr(64),
				Disabled:                  aspire.BoolPtr(false),
			},
		})
		secretInput := interactionService.CreateSecretInput("password", &aspire.CreateSecretInputOptions{
			Options: &aspire.CreateInteractionInputOptions{Required: aspire.BoolPtr(true)},
		})
		booleanInput := interactionService.CreateBooleanInput("enabled", &aspire.CreateBooleanInputOptions{
			Options: &aspire.CreateInteractionInputOptions{Value: aspire.StringPtr("true")},
		})
		numberInput := interactionService.CreateNumberInput("count", &aspire.CreateNumberInputOptions{
			Options: &aspire.CreateInteractionInputOptions{Value: aspire.StringPtr("1")},
		})
		choiceInput := interactionService.CreateChoiceInput("color", &aspire.CreateChoiceInputOptions{
			Choices: []*aspire.InteractionChoiceOption{{Value: "r", Label: "Red"}, {Value: "g", Label: "Green"}},
			Options: &aspire.CreateInteractionInputOptions{AllowCustomChoice: aspire.BoolPtr(true)},
		})
		presetInput := interactionService.CreateTextInput("greeting").WithValue("hello")
		sizeInput := interactionService.CreateChoiceInput("size").WithChoiceOptions([]*aspire.InteractionChoiceOption{{Value: "s", Label: "Small"}, {Value: "l", Label: "Large"}})
		dependentInput := interactionService.CreateChoiceInput("shade").WithDynamicLoading(func(loadContext aspire.InteractionInputLoadContext) {
			input := loadContext.Input()
			inputName, _ := input.GetName()
			color, _ := loadContext.Inputs().Value("color")
			shades := []*aspire.InteractionChoiceOption{{Value: "lime", Label: "Lime"}, {Value: "forest", Label: "Forest"}}
			if color == "r" {
				shades = []*aspire.InteractionChoiceOption{{Value: "crimson", Label: "Crimson"}, {Value: "scarlet", Label: "Scarlet"}}
			}
			_ = input.SetChoiceOptions(shades)
			_ = input.SetValue(inputName)
		}, &aspire.WithDynamicLoadingOptions{
			Options: &aspire.DynamicLoadingOptions{AlwaysLoadOnStart: aspire.BoolPtr(true), DependsOnInputs: []string{"color"}},
		})

		single, err := interactionService.PromptInput("Single input", "Enter a value.", interactionService.CreateTextInput("solo"), &aspire.PromptInputOptions{
			Options: &aspire.InteractionInputsDialogOptions{
				PrimaryButtonText: aspire.StringPtr("Save"),
				ValidationCallback: func(validationContext aspire.InputsDialogValidationContext) {
					solo, _ := validationContext.Inputs().Value("solo")
					if solo == "" {
						_ = validationContext.AddValidationError("solo", "A value is required.")
					}
				},
			},
		})
		if err != nil {
			return &aspire.ExecuteCommandResult{Success: false, ErrorMessage: aspire.StringPtr(aspire.FormatError(err))}
		}

		multi := interactionService.PromptInputs("Multiple inputs", "Fill out the form.",
			[]aspire.InteractionInputBuilder{textInput, secretInput, booleanInput, numberInput, choiceInput, presetInput, sizeInput, dependentInput},
			&aspire.PromptInputsOptions{
				Options: &aspire.InteractionInputsDialogOptions{
					PrimaryButtonText:     aspire.StringPtr("Submit"),
					EnableMessageMarkdown: aspire.BoolPtr(true),
					ValidationCallback: func(validationContext aspire.InputsDialogValidationContext) {
						name, _ := validationContext.Inputs().Value("name")
						if name == "bad" {
							_ = validationContext.AddValidationError("name", "Name cannot be 'bad'.")
						}
					},
				},
			})

		selectedColor, _ := multi.Inputs().Value("color")
		soloValue := ""
		if single.Input != nil {
			soloValue = single.Input.Value
		}

		multiCanceled, err := multi.Canceled()
		if err != nil {
			return &aspire.ExecuteCommandResult{Success: false, ErrorMessage: aspire.StringPtr(aspire.FormatError(err))}
		}

		success := !confirmation.Canceled &&
			confirmation.Value != nil && *confirmation.Value &&
			!messageBox.Canceled &&
			!notification.Canceled &&
			!single.Canceled &&
			!multiCanceled

		return &aspire.ExecuteCommandResult{
			Success:  success,
			Canceled: aspire.BoolPtr(multiCanceled),
			Message:  aspire.StringPtr(fmt.Sprintf("color=%s solo=%s", selectedColor, soloValue)),
		}
	})

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
