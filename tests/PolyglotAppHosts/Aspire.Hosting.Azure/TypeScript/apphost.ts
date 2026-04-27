import { createBuilder, DeploymentScope, refExpr } from './.modules/aspire.js';

const builder = await createBuilder();

await builder.addAzureProvisioning();

const location = await builder.addParameter("location");
const resourceGroup = await builder.addParameter("resource-group");
const existingName = await builder.addParameter("existing-name");
const existingResourceGroup = await builder.addParameter("existing-resource-group");
const connectionString = await builder.addConnectionString("azure-validation");

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
await fileBicep.runAsExisting(existingName, { resourceGroup: existingResourceGroup });
await fileBicep.publishAsExisting("file-bicep-existing", { resourceGroup: "rg-bicep" });
await fileBicep.publishAsExisting(existingName, { resourceGroup: existingResourceGroup });
await fileBicep.asExisting(existingName, { resourceGroup: existingResourceGroup });

const inlineBicep = await builder.addBicepTemplateString("inline-bicep", `
output inlineUrl string = 'https://inline.example.com'
`);
await inlineBicep.publishAsConnectionString();
await inlineBicep.clearDefaultRoleAssignments();
await inlineBicep.getBicepIdentifier();
await inlineBicep.isExisting();

const infrastructure = await builder.addAzureInfrastructure("infra", async infrastructureContext => {
    await infrastructureContext.bicepName();
    await infrastructureContext.targetScope.set(DeploymentScope.Subscription);
});
const infrastructureOutput = await infrastructure.getOutput("serviceUrl");
await infrastructureOutput.name();
await infrastructureOutput.value();
await infrastructureOutput.valueExpression();
await infrastructure.withParameter("empty");
await infrastructure.withParameter("plain", { value: "value" });
await infrastructure.withParameter("list", { value: ["one", "two"] });
await infrastructure.withParameter("fromParam", { value: existingName });
await infrastructure.withParameter("fromConnection", { value: connectionString });
await infrastructure.withParameter("fromOutput", { value: infrastructureOutput });
await infrastructure.withParameter("fromExpression", { value: refExpr`https://${endpoint}` });
await infrastructure.withParameter("fromEndpoint", { value: endpoint });
await infrastructure.publishAsConnectionString();
await infrastructure.clearDefaultRoleAssignments();
await infrastructure.getBicepIdentifier();
await infrastructure.isExisting();
await infrastructure.runAsExisting("infra-existing", { resourceGroup: "rg-infra" });
await infrastructure.runAsExisting(existingName, { resourceGroup: existingResourceGroup });
await infrastructure.publishAsExisting("infra-existing", { resourceGroup: "rg-infra" });
await infrastructure.publishAsExisting(existingName, { resourceGroup: existingResourceGroup });
await infrastructure.asExisting(existingName, { resourceGroup: existingResourceGroup });

const identity = await builder.addAzureUserAssignedIdentity("identity");
await identity.configureInfrastructure(async infrastructureContext => {
    await infrastructureContext.bicepName();
    await infrastructureContext.targetScope.set(DeploymentScope.Subscription);
});
await identity.withParameter("identityEmpty");
await identity.withParameter("identityPlain", { value: "value" });
await identity.withParameter("identityList", { value: ["a", "b"] });
await identity.withParameter("identityFromParam", { value: existingName });
await identity.withParameter("identityFromConnection", { value: connectionString });
await identity.withParameter("identityFromOutput", { value: infrastructureOutput });
await identity.withParameter("identityFromExpression", { value: refExpr`${location}` });
await identity.withParameter("identityFromEndpoint", { value: endpoint });
await identity.publishAsConnectionString();
await identity.clearDefaultRoleAssignments();
await identity.getBicepIdentifier();
await identity.isExisting();
await identity.runAsExisting("identity-existing", { resourceGroup: "rg-identity" });
await identity.runAsExisting(existingName, { resourceGroup: existingResourceGroup });
await identity.publishAsExisting("identity-existing", { resourceGroup: "rg-identity" });
await identity.publishAsExisting(existingName, { resourceGroup: existingResourceGroup });
await identity.asExisting(existingName, { resourceGroup: existingResourceGroup });
const identityClientId = await identity.getOutput("clientId");

await container.withEnvironment("INFRA_URL", infrastructureOutput);
await container.withEnvironment("SECRET_FROM_IDENTITY", identityClientId);
await container.withAzureUserAssignedIdentity(identity);

await executable.withEnvironment("INFRA_URL", infrastructureOutput);
await executable.withEnvironment("SECRET_FROM_IDENTITY", identityClientId);
await executable.withAzureUserAssignedIdentity(identity);

await builder.build().run();
