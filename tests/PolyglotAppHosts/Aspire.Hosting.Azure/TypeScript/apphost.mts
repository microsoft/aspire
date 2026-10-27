import { createBuilder, DeploymentScope, EntraIdSignInAudience, refExpr } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

await builder.addAzureProvisioning();

const frontDoor = await builder.addAzureFrontDoor("frontdoor");
await frontDoor.configureInfrastructure(async infrastructure => {
    const profile = await infrastructure.getCdnProfile();
    await profile.originResponseTimeoutSeconds.set(60);
    const _originTimeout = await profile.originResponseTimeoutSeconds.get();
});

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
const identityBicepIdentifier = await identity.getBicepIdentifier();
await identity.configureInfrastructure(async infrastructureContext => {
    await infrastructureContext.bicepName();
    await infrastructureContext.targetScope.set(DeploymentScope.Subscription);
    const provisionedIdentity = await infrastructureContext.getSqlUserAssignedIdentityByIdentifier(identityBicepIdentifier);
    const bicep = infrastructureContext.bicep();
    const resourceGroup = await bicep.resourceGroup();
    const resourceGroupId = await bicep.member(resourceGroup, "id");
    const uniqueName = await bicep.uniqueString([resourceGroupId]);
    const identityName = bicep.createStringBuilder()
        .appendLiteral("mi-")
        .appendValue(uniqueName)
        .build();
    const deploymentLocation = bicep.location("westus2");
    await provisionedIdentity.name.set(identityName);
    await provisionedIdentity.location.set(deploymentLocation);
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

const tenantId = await builder.addParameter("entra-tenant");
const clientId = await builder.addParameter("entra-client");
const clientSecret = await builder.addParameter("entra-secret", { secret: true });
const certificatePassword = await builder.addParameter("certificate-password", { secret: true });
const redirectUris = await builder.addParameter("redirect-uris");
const redirectHost = await builder.addParameter("redirect-host");
const ownerId = await builder.addParameter("entra-owner");
const exampleTenant = "aaaabbbb-0000-cccc-1111-dddd2222eeee";
const exampleClient = "00001111-aaaa-2222-bbbb-3333cccc4444";

const existingEntra = await builder.addExistingEntraApplication("existing-entra", tenantId, exampleClient);
await builder.addExistingEntraApplication("existing-entra-literals", exampleTenant, exampleClient);
await builder.addExistingEntraApplication("existing-entra-parameters", tenantId, clientId);
await existingEntra.withInstance("https://login.microsoftonline.com/");
await existingEntra.withSignInAudience(EntraIdSignInAudience.AzureADMultipleOrgs);
await existingEntra.withAudience("api://example");
await existingEntra.withClientSecret(clientSecret);
await existingEntra.withFederatedManagedIdentityCredential(exampleClient, {
    tokenExchangeUrl: "api://AzureADTokenExchange",
    tokenExchangeAuthority: "https://login.microsoftonline.com/",
});
await existingEntra.withCertificateFromKeyVault("https://vault.vault.azure.net", "certificate");
await existingEntra.withCertificateThumbprint("CurrentUser/My", "thumbprint");
await existingEntra.withCertificateDistinguishedName("CurrentUser/My", "CN=certificate");
await existingEntra.withCertificateFromFile("/certs/client.pfx", { password: certificatePassword });
await existingEntra.withSignedAssertionFile({ filePath: "/tokens/assertion" });
await existingEntra.withSignedAssertionFile();
await existingEntra.withSendX5C();
await existingEntra.withClientCapability("cp1");
await existingEntra.withAllowWebApiToBeAuthorizedByACL();
await existingEntra.withExtraQueryParameter("domain_hint", "contoso.com");
await container.withReference(existingEntra, { connectionName: "AUTH" });
await executable.withReference(existingEntra);

const fluentExistingEntra = await builder.addEntraIdApplication("fluent-existing-entra");
await fluentExistingEntra.asExistingApplication(exampleTenant, clientId);

const createdEntra = await builder.addEntraIdApplication("created-entra");
await createdEntra.withOwners(exampleClient);
await createdEntra.withOwners(ownerId);
await createdEntra.withApplicationOwners(ownerId);
await createdEntra.withServicePrincipalOwners(exampleClient);
await createdEntra.withIdTokenIssuance();
await createdEntra.withRedirectUris("https://localhost/signin");
await createdEntra.withRedirectUris(redirectUris);
await createdEntra.withRedirectUris(redirectHost, { callbackPaths: ["/signin", "/signout"] });
await createdEntra.withRedirectUris(endpoint, { callbackPaths: ["/signin"] });
await container.withReference(createdEntra);
await createdEntra.publishWithFederatedManagedIdentityCredential();
await createdEntra.publishWithFederatedManagedIdentityCredential({ identity: container });
await createdEntra.publishWithFederatedManagedIdentityCredential({ identity });

await builder.build().run();
