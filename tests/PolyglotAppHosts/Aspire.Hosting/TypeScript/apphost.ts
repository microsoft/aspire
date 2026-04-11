import {
    createBuilder,
    CertificateTrustScope,
    IconVariant,
    ProbeType,
    refExpr,
} from './.modules/aspire.js';

const builder = await createBuilder();

// ===================================================================
// Factory methods on builder
// ===================================================================

// addContainer (pre-existing)
const container = await builder.addContainer("mycontainer", "nginx");
const taggedContainer = await builder.addContainer("mytaggedcontainer", {
    image: "nginx",
    tag: "stable-alpine",
});

// addDockerfile
const dockerContainer = await builder.addDockerfile("dockerapp", "./app");

// addExecutable (pre-existing)
const exe = await builder.addExecutable("myexe", "echo", ".", ["hello"]);

// addProject (pre-existing)
const project = await builder.addProject("myproject", "./src/MyProject", "https");
const projectWithoutLaunchProfile = await builder.addProjectWithoutLaunchProfile("myproject-noprofile", "./src/MyProject");
// ATS exports ReferenceEnvironmentInjectionFlags as a DTO-shaped object in TypeScript.
const referenceEnvironmentOptions = {
    connectionString: true,
    serviceDiscovery: true,
};
await project.withReferenceEnvironment(referenceEnvironmentOptions);

// addCSharpApp
const csharpApp = await builder.addCSharpApp("csharpapp", "./src/CSharpApp");

// addRedis
const cache = await builder.addRedis("cache");

// addDotnetTool
const tool = await builder.addDotnetTool("mytool", "dotnet-ef");

// addParameterFromConfiguration
const configParam = await builder.addParameterFromConfiguration("myconfig", "MyConfig:Key");
const secretParam = await builder.addParameterFromConfiguration("mysecret", "MyConfig:Secret", { secret: true });
const generatedParam = await builder.addParameterWithGeneratedValue("generated-secret", {
    minLength: 24,
    lower: true,
    upper: true,
    numeric: true,
    special: false,
    minUpper: 2,
    minNumeric: 2,
}, {
    secret: true,
    persist: true,
});

// ===================================================================
// Container-specific methods on ContainerResource
// ===================================================================

// withDockerfileBaseImage
await container.withDockerfileBaseImage({ buildImage: "mcr.microsoft.com/dotnet/sdk:8.0" });
await dockerContainer.withBuildArg("STATIC_BRANDING", "/app/static/branding/custom");
await dockerContainer.withBuildArg("CONFIG_BRANDING", configParam);
await container.withContainerCertificatePaths({
    customCertificatesDestination: "/usr/lib/ssl/aspire/custom",
    defaultCertificateBundlePaths: ["/etc/ssl/certs/ca-certificates.crt"],
    defaultCertificateDirectoryPaths: ["/etc/ssl/certs", "/usr/local/share/ca-certificates"],
});

// withImageRegistry
await container.withImageRegistry("docker.io");

// ===================================================================
// ConnectionStringBuilderExtensions.cs — NEW exports
// ===================================================================

await dockerContainer.withHttpEndpoint({ name: "http", targetPort: 80 });
const endpoint = await dockerContainer.getEndpoint("http");
const expr = refExpr`Host=${endpoint}`;

const builtConnectionString = await builder.addConnectionStringBuilder("customcs", async (connectionStringBuilder) => {
    const _isEmpty: boolean = await connectionStringBuilder.isEmpty.get();

    await connectionStringBuilder.appendLiteral("Host=");
    await connectionStringBuilder.appendValueProvider(endpoint);
    await connectionStringBuilder.appendLiteral(";Key=");
    await connectionStringBuilder.appendValueProvider(secretParam);

    const _builtExpression = await connectionStringBuilder.build();
});

await builtConnectionString.withConnectionProperty("Host", expr);
await builtConnectionString.withConnectionProperty("Mode", "Development");

const envConnectionString = await builder.addConnectionString("envcs");

// ===================================================================
// ResourceBuilderExtensions.cs — NEW exports on ContainerResource
// ===================================================================

// withEnvironment - EndpointReference
await container.withEnvironment("MY_ENDPOINT", endpoint);

// withEnvironment — with ReferenceExpression
await container.withEnvironment("MY_EXPR", expr);

// withEnvironment — with ParameterResource
await container.withEnvironment("MY_PARAM", configParam);
await container.withEnvironment("MY_GENERATED_PARAM", generatedParam);

// withEnvironment — with connection string resource
await container.withEnvironment("MY_CONN", envConnectionString);

// withConnectionProperty — with ReferenceExpression
await builtConnectionString.withConnectionProperty("Endpoint", expr);

// withConnectionProperty — with string
await builtConnectionString.withConnectionProperty("Protocol", "https");

// excludeFromManifest
await container.excludeFromManifest();

// excludeFromMcp
await container.excludeFromMcp();

// waitForCompletion (pre-existing)
await container.waitForCompletion(exe);

// withDeveloperCertificateTrust
await container.withDeveloperCertificateTrust(true);

// withCertificateTrustScope
await container.withCertificateTrustScope(CertificateTrustScope.System);

// withHttpsDeveloperCertificate
await container.withHttpsDeveloperCertificate();

// withoutHttpsCertificate
await container.withoutHttpsCertificate();

// withChildRelationship
await container.withChildRelationship(exe);

// withRelationship
await container.withRelationship(taggedContainer, "peer");
await project.withReference(cache);

// withIconName
await container.withIconName("Database", { iconVariant: IconVariant.Filled });

// withHttpProbe
await container.withHttpProbe(ProbeType.Liveness, { path: "/health" });

// withRemoteImageName
await container.withRemoteImageName("myregistry.azurecr.io/myapp");

// withRemoteImageTag
await container.withRemoteImageTag("latest");

// withMcpServer
await container.withMcpServer({ path: "/mcp" });

// withRequiredCommand
await container.withRequiredCommand("docker");

// ===================================================================
// DotnetToolResourceExtensions.cs — NEW exports
// ===================================================================

// withToolIgnoreExistingFeeds
await tool.withToolIgnoreExistingFeeds();

// withToolIgnoreFailedSources
await tool.withToolIgnoreFailedSources();

// withToolPackage
await tool.withToolPackage("dotnet-ef");

// withToolPrerelease
await tool.withToolPrerelease();

// withToolSource
await tool.withToolSource("https://api.nuget.org/v3/index.json");

// withToolVersion
await tool.withToolVersion("8.0.0");

// publishAsDockerFile
await tool.publishAsDockerFile();

// PipelineStepFactoryExtensions.cs — NEW exports
// ===================================================================

await container.withPipelineStepFactory("custom-build-step", async (stepContext) => {
    const pipelineContext = await stepContext.pipelineContext.get();
    const pipelineModel = await pipelineContext.model.get();
    const _pipelineResources = await pipelineModel.getResources();
    const _pipelineContainer = await pipelineModel.findResourceByName("mycontainer");
    const pipelineServices = await pipelineContext.services.get();
    const pipelineLoggerFactory = await pipelineServices.getLoggerFactory();
    const pipelineFactoryLogger = await pipelineLoggerFactory.createLogger("ValidationAppHost.PipelineContext");
    await pipelineFactoryLogger.logInformation("Pipeline factory context logger");
    const pipelineLogger = await pipelineContext.logger.get();
    await pipelineLogger.logDebug("Pipeline context logger");
    const pipelineSummary = await pipelineContext.summary.get();
    await pipelineSummary.add("PipelineContext", "Validated");
    await pipelineSummary.addMarkdown("PipelineMarkdown", "**Validated**");

    const executionContext = await stepContext.executionContext.get();
    const _isPublishMode: boolean = await executionContext.isPublishMode.get();
    const stepServices = await stepContext.services.get();
    const stepLogger = await stepContext.logger.get();
    await stepLogger.logInformation("Pipeline step context logger");
    const stepSummary = await stepContext.summary.get();
    await stepSummary.add("PipelineStepContext", "Validated");
    const reportingStep = await stepContext.reportingStep.get();
    await reportingStep.logStep("information", "Reporting step log");
    await reportingStep.logStepMarkdown("information", "**Reporting step markdown log**");
    const reportingTask = await reportingStep.createTask("Task created");
    await reportingTask.updateTask("Task updated");
    await reportingTask.updateTaskMarkdown("**Task markdown updated**");
    await reportingTask.completeTask({ completionMessage: "Task complete" });
    const markdownTask = await reportingStep.createMarkdownTask("**Markdown task created**");
    await markdownTask.completeTaskMarkdown("**Markdown task complete**", { completionState: "completed-with-warning" });
    await reportingStep.completeStep("Reporting step complete");
    await reportingStep.completeStepMarkdown("**Reporting step markdown complete**", { completionState: "completed-with-warning" });
    const stepModel = await stepContext.model.get();
    const _stepResources = await stepModel.getResources();
    const _stepContainer = await stepModel.findResourceByName("mycontainer");
    const stepLoggerFactory = await stepServices.getLoggerFactory();
    const stepFactoryLogger = await stepLoggerFactory.createLogger("ValidationAppHost.PipelineStepContext");
    await stepFactoryLogger.logDebug("Pipeline step factory logger");
    const cancellationToken = await stepContext.cancellationToken.get();
    const cacheUriExpression = await cache.uriExpression.get();
    const _cacheUri = await cacheUriExpression.getValue(cancellationToken);
}, {
    dependsOn: ["build"],
    requiredBy: ["deploy"],
    tags: ["custom-build"],
    description: "Custom pipeline step"
});

await container.withPipelineConfiguration(async (configContext) => {
    const configServices = await configContext.services.get();
    const configModel = await configContext.model.get();
    const _configResources = await configModel.getResources();
    const _configContainer = await configModel.findResourceByName("mycontainer");
    const configLoggerFactory = await configServices.getLoggerFactory();
    const configLogger = await configLoggerFactory.createLogger("ValidationAppHost.PipelineConfigurationContext");
    await configLogger.logInformation("Pipeline configuration logger");

    const allSteps = await configContext.steps.get();
    const taggedSteps = await configContext.getStepsByTag("custom-build");

    const _stepName: string = await allSteps[0].name.get();
    const _description: string = await allSteps[0].description.get();

    await allSteps[0].tags.add("validated");
    await allSteps[0].dependsOnSteps.add("restore");
    await taggedSteps[0].requiredBySteps.add("publish");
    await taggedSteps[0].requiredBy("publish");
    await allSteps[0].dependsOn("build");
});

await container.withPipelineConfiguration(async (configContext) => {
    const _configServices = await configContext.services.get();
    const _configModel = await configContext.model.get();
    const _resourceSteps = await configContext.steps.get();
    const _taggedSteps = await configContext.getStepsByTag("custom-build");
});

// ===================================================================
// Builder, eventing, logging, model, notification, and user secrets
// ===================================================================

const _appHostDirectory: string = await builder.appHostDirectory.get();
const hostEnvironment = await builder.environment.get();
const _isDevelopment: boolean = await hostEnvironment.isDevelopment();
const _isProduction: boolean = await hostEnvironment.isProduction();
const _isStaging: boolean = await hostEnvironment.isStaging();
const _isSpecificEnvironment: boolean = await hostEnvironment.isEnvironment("Development");

const builderConfiguration = await builder.getConfiguration();
const _configValue: string = await builderConfiguration.getConfigValue("MyConfig:Key");
const _connectionString: string = await builderConfiguration.getConnectionString("customcs");
const _configSection = await builderConfiguration.getSection("MyConfig");
const _configChildren = await builderConfiguration.getChildren();
const _configExists: boolean = await builderConfiguration.exists("MyConfig:Key");

const builderExecutionContext = await builder.executionContext.get();
const executionContextServiceProvider = await builderExecutionContext.serviceProvider.get();
const _distributedApplicationModelFromExecutionContext = await executionContextServiceProvider.getDistributedApplicationModel();

const beforeStartSubscription = await builder.subscribeBeforeStart(async (beforeStartEvent) => {
    const beforeStartServices = await beforeStartEvent.services.get();
    const beforeStartModel = await beforeStartEvent.model.get();
    const _beforeStartResources = await beforeStartModel.getResources();
    const _beforeStartContainer = await beforeStartModel.findResourceByName("mycontainer");

    const _beforeStartEventing = await beforeStartServices.getEventing();
    const beforeStartLoggerFactory = await beforeStartServices.getLoggerFactory();
    const beforeStartLogger = await beforeStartLoggerFactory.createLogger("ValidationAppHost.BeforeStart");
    await beforeStartLogger.logInformation("BeforeStart information");
    await beforeStartLogger.logWarning("BeforeStart warning");
    await beforeStartLogger.logError("BeforeStart error");
    await beforeStartLogger.logDebug("BeforeStart debug");
    await beforeStartLogger.log("critical", "BeforeStart critical");

    const beforeStartResourceLoggerService = await beforeStartServices.getResourceLoggerService();
    await beforeStartResourceLoggerService.completeLog(container);
    await beforeStartResourceLoggerService.completeLogByName("mycontainer");

    const beforeStartNotificationService = await beforeStartServices.getResourceNotificationService();
    await beforeStartNotificationService.waitForResourceState("mycontainer", { targetState: "Running" });
    const _matchedResourceState: string = await beforeStartNotificationService.waitForResourceStates("mycontainer", ["Running", "FailedToStart"]);
    const _healthyResourceEvent = await beforeStartNotificationService.waitForResourceHealthy("mycontainer");
    await beforeStartNotificationService.waitForDependencies(container);
    const _currentResourceState = await beforeStartNotificationService.tryGetResourceState("mycontainer");
    await beforeStartNotificationService.publishResourceUpdate(container, { state: "Validated", stateStyle: "info" });

    const userSecretsManager = await beforeStartServices.getUserSecretsManager();
    const _userSecretsAvailable: boolean = await userSecretsManager.isAvailable.get();
    const _userSecretsFilePath: string = await userSecretsManager.filePath.get();
    const _secretSet: boolean = await userSecretsManager.trySetSecret("Validation:Key", "value");
    await userSecretsManager.getOrSetSecret(container, "Validation:GeneratedKey", "generated-value");
    const _generatedSecretValue: string = await builderConfiguration.getConfigValue("Validation:GeneratedKey");
    await userSecretsManager.saveStateJson("{\"Validation\":\"Value\"}");

    const _modelFromServices = await beforeStartServices.getDistributedApplicationModel();
});

const afterResourcesCreatedSubscription = await builder.subscribeAfterResourcesCreated(async (afterResourcesCreatedEvent) => {
    const afterResourcesCreatedServices = await afterResourcesCreatedEvent.services.get();
    const afterResourcesCreatedModel = await afterResourcesCreatedEvent.model.get();
    const _afterResources = await afterResourcesCreatedModel.getResources();
    const _afterResourcesContainer = await afterResourcesCreatedModel.findResourceByName("mycontainer");
    const afterResourcesCreatedLoggerFactory = await afterResourcesCreatedServices.getLoggerFactory();
    const afterResourcesCreatedLogger = await afterResourcesCreatedLoggerFactory.createLogger("ValidationAppHost.AfterResourcesCreated");
    await afterResourcesCreatedLogger.logInformation("AfterResourcesCreated");
});

const builderEventing = await builder.eventing.get();
await builderEventing.unsubscribe(beforeStartSubscription);
await builderEventing.unsubscribe(afterResourcesCreatedSubscription);

await container.onBeforeResourceStarted(async (beforeResourceStartedEvent) => {
    const _resource = await beforeResourceStartedEvent.resource.get();
    const services = await beforeResourceStartedEvent.services.get();
    const loggerFactory = await services.getLoggerFactory();
    const logger = await loggerFactory.createLogger("ValidationAppHost.BeforeResourceStarted");
    await logger.logInformation("BeforeResourceStarted");
});

await container.onResourceStopped(async (resourceStoppedEvent) => {
    const _resource = await resourceStoppedEvent.resource.get();
    const services = await resourceStoppedEvent.services.get();
    const loggerFactory = await services.getLoggerFactory();
    const logger = await loggerFactory.createLogger("ValidationAppHost.ResourceStopped");
    await logger.logWarning("ResourceStopped");
});

await builtConnectionString.onConnectionStringAvailable(async (connectionStringAvailableEvent) => {
    const _resource = await connectionStringAvailableEvent.resource.get();
    const services = await connectionStringAvailableEvent.services.get();
    const notifications = await services.getResourceNotificationService();
    const _connectionState = await notifications.tryGetResourceState("customcs");
});

await container.onInitializeResource(async (initializeResourceEvent) => {
    const _resource = await initializeResourceEvent.resource.get();
    const _initializeEventing = await initializeResourceEvent.eventing.get();
    const initializeLogger = await initializeResourceEvent.logger.get();
    const initializeNotifications = await initializeResourceEvent.notifications.get();
    const initializeServices = await initializeResourceEvent.services.get();
    await initializeLogger.logDebug("InitializeResource");
    await initializeNotifications.waitForDependencies(container);
    const _initializeModel = await initializeServices.getDistributedApplicationModel();
    const _initializeEventingFromServices = await initializeServices.getEventing();
});

await container.onResourceEndpointsAllocated(async (resourceEndpointsAllocatedEvent) => {
    const _resource = await resourceEndpointsAllocatedEvent.resource.get();
    const services = await resourceEndpointsAllocatedEvent.services.get();
    const loggerFactory = await services.getLoggerFactory();
    const logger = await loggerFactory.createLogger("ValidationAppHost.ResourceEndpointsAllocated");
    await logger.logInformation("ResourceEndpointsAllocated");
});

await container.onResourceReady(async (resourceReadyEvent) => {
    const _resource = await resourceReadyEvent.resource.get();
    const services = await resourceReadyEvent.services.get();
    const loggerFactory = await services.getLoggerFactory();
    const logger = await loggerFactory.createLogger("ValidationAppHost.ResourceReady");
    await logger.logInformation("ResourceReady");
});

// ===================================================================
// Pre-existing exports verification (sanity check)
// ===================================================================

// withEnvironment
await container.withEnvironment("MY_VAR", "value");

// withEndpoint
await container.withEndpoint();

// withHttpEndpoint
await container.withHttpEndpoint();

// withHttpsEndpoint
await container.withHttpsEndpoint();

// withExternalHttpEndpoints
await container.withExternalHttpEndpoints();

// asHttp2Service
await container.asHttp2Service();

// withArgs
await container.withArgs(["--verbose"]);

// withParentRelationship
await container.withParentRelationship(exe);
await projectWithoutLaunchProfile.withParentRelationship(project);

// withExplicitStart
await container.withExplicitStart();

// withUrl
await container.withUrl("http://localhost:8080");

// withUrlExpression
await container.withUrlExpression(refExpr`http://${endpoint}`);

// withHttpHealthCheck
await container.withHttpHealthCheck();

// withHttpHealthCheck
await container.withHttpHealthCheck();

// withCommand
await container.withCommand("restart", "Restart", async (_ctx) => {
    return { success: true };
});

// withHttpCommand
await container.withHttpCommand("/health", "Health Check");
await container.withHttpCommand("/api/reset", "Reset", { methodName: "POST", confirmationMessage: "Are you sure?" });

const app = await builder.build();
await app.run();
