import { createBuilder, DeploymentScope, refExpr } from './.modules/aspire.js';

const builder = await createBuilder();

await builder.addAzureProvisioning();

const location = await builder.addParameter("location");
const resourceGroup = await builder.addParameter("resource-group");
const existingName = await builder.addParameter("existing-name");
const existingResourceGroup = await builder.addParameter("existing-resource-group");
const connectionString = await builder.addConnectionString("azure-validation", {
    environmentVariableName: "AZURE_VALIDATION_CONNECTION_STRING"
});

const azureEnvironment = await builder.addAzureEnvironment();
await azureEnvironment.withLocation(location).withResourceGroup(resourceGroup);

const container = await builder.addContainer("api", "mcr.microsoft.com/dotnet/samples:aspnetapp")
    .withHttpEndpoint({ name: "http", targetPort: 8080 });
const executable = await builder.addExecutable("worker", "dotnet", ".", ["--info"])
    .withHttpEndpoint({ name: "http", targetPort: 8081 });
const endpoint = await container.getEndpoint("http");

const fileBicep = await builder.addBicepTemplate("file-bicep", "./validation.bicep");
await fileBicep.publishAsConnectionString();
await fileBicep.clearDefaultRoleAssignments();
await fileBicep.getBicepIdentifier();
await fileBicep.isExisting();
await fileBicep.runAsExisting("file-bicep-existing", { resourceGroup: "rg-bicep" });
await fileBicep.publishAsExisting("file-bicep-existing", { resourceGroup: "rg-bicep" });
await fileBicep.asExisting("file-bicep-existing", { resourceGroup: "rg-bicep" });

const inlineBicep = await builder.addBicepTemplateString("inline-bicep", `
output inlineUrl string = 'https://inline.example.com'
`);
await inlineBicep.publishAsConnectionString();
await inlineBicep.clearDefaultRoleAssignments();
await inlineBicep.getBicepIdentifier();
await inlineBicep.isExisting();

const infrastructure = await builder.addAzureInfrastructure("infra", async infrastructureContext => {
    await infrastructureContext.bicepName.get();
    await infrastructureContext.targetScope.set(DeploymentScope.Subscription);
});
const infrastructureOutput = await infrastructure.getOutput("serviceUrl");
await infrastructureOutput.name.get();
await infrastructureOutput.value.get();
await infrastructureOutput.valueExpression.get();
await infrastructure.withParameter("empty");
await infrastructure.withParameterStringValue("plain", "value");
await infrastructure.withParameterStringValues("list", ["one", "two"]);
await infrastructure.withParameterFromParameter("fromParam", existingName);
await infrastructure.withParameterFromConnectionString("fromConnection", connectionString);
await infrastructure.withParameterFromOutput("fromOutput", infrastructureOutput);
await infrastructure.withParameterFromReferenceExpression("fromExpression", refExpr`https://${endpoint}`);
await infrastructure.withParameterFromEndpoint("fromEndpoint", endpoint);
await infrastructure.publishAsConnectionString();
await infrastructure.clearDefaultRoleAssignments();
await infrastructure.getBicepIdentifier();
await infrastructure.isExisting();
await infrastructure.runAsExisting(existingName, { resourceGroup: existingResourceGroup });
await infrastructure.publishAsExisting(existingName, { resourceGroup: existingResourceGroup });
await infrastructure.asExisting(existingName, { resourceGroup: existingResourceGroup });

const identity = await builder.addAzureUserAssignedIdentity("identity");
await identity.configureInfrastructure(async infrastructureContext => {
    await infrastructureContext.bicepName.get();
    await infrastructureContext.targetScope.set(DeploymentScope.Subscription);
});
await identity.withParameter("identityEmpty");
await identity.withParameterStringValue("identityPlain", "value");
await identity.withParameterStringValues("identityList", ["a", "b"]);
await identity.withParameterFromParameter("identityFromParam", existingName);
await identity.withParameterFromConnectionString("identityFromConnection", connectionString);
await identity.withParameterFromOutput("identityFromOutput", infrastructureOutput);
await identity.withParameterFromReferenceExpression("identityFromExpression", refExpr`${location}`);
await identity.withParameterFromEndpoint("identityFromEndpoint", endpoint);
await identity.publishAsConnectionString();
await identity.clearDefaultRoleAssignments();
await identity.getBicepIdentifier();
await identity.isExisting();
await identity.runAsExisting("identity-existing", { resourceGroup: "rg-identity" });
await identity.publishAsExisting("identity-existing", { resourceGroup: "rg-identity" });
await identity.publishAsExisting(existingName, { resourceGroup: existingResourceGroup });
await identity.asExisting("identity-existing", { resourceGroup: "rg-identity" });

await container.withEnvironmentFromOutput("INFRA_URL", infrastructureOutput);
await container.withEnvironmentFromKeyVaultSecret("SECRET_FROM_IDENTITY", identity);
await container.withAzureUserAssignedIdentity(identity);

await executable.withEnvironmentFromOutput("INFRA_URL", infrastructureOutput);
await executable.withEnvironmentFromKeyVaultSecret("SECRET_FROM_IDENTITY", identity);
await executable.withAzureUserAssignedIdentity(identity);

await builder.build().run();
