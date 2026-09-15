import { BicepValueKind, createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const deploymentSlot = await builder.addParameter('deploymentSlot');
const existingApplicationInsights = await builder.addAzureApplicationInsights('existingApplicationInsights');
const vnet = await builder.addAzureVirtualNetwork('vnet');
const subnet = await vnet.addSubnet('app-service-subnet', '10.0.0.0/24');

const environment = await builder.addAzureAppServiceEnvironment('appservice-environment')
    .withDelegatedSubnet(subnet)
    .withDashboard()
    .withDashboard({ enable: false })
    .withAzureApplicationInsights()
    .withAzureApplicationInsights({ applicationInsights: existingApplicationInsights })
    .withDeploymentSlot(deploymentSlot)
    .withDeploymentSlot('staging');

// App Service plans use an environment-derived identifier rather than the resource's root identifier.
const planIdentifier = `${await environment.getBicepIdentifier()}_asplan`;
await environment.configureInfrastructure(async infrastructure => {
    const plan = await infrastructure.getAppServicePlanByIdentifier(planIdentifier);
    await plan.isPerSiteScaling.set(true);
    const _perSiteScaling = await plan.isPerSiteScaling.get();
    // SKU location metadata is exercised without adding it to the deployment's plan SKU.
    const sku = await infrastructure.createAppServiceSkuDescription();
    const locations = await sku.locations.get();
    await locations.add(await infrastructure.createAppServiceAzureLocation("westus2"));
    const location = await locations.get(0);
    const _locationName = await location.name();
    // Exercise writable addresses on a detached model, not service output lists.
    const connection = await infrastructure.createRemotePrivateEndpointConnection();
    const addresses = await connection.iPAddresses.get();
    await addresses.add("192.0.2.1");
    await addresses.insert(0, "2001:db8::1");
    await addresses.set(1, "192.0.2.2");
    const literal = await addresses.get(0);
    await addresses.set(1, literal);
    const bicep = await infrastructure.bicep();
    // Index creates an untyped expression rather than a typed BicepValue<string>.
    const expression = await bicep.index(await bicep.parseJson(await bicep.string('["192.0.2.3"]')), 0);
    await addresses.add(expression);
    await addresses.insert(1, expression);
    await addresses.set(0, await addresses.get(3));
    const kinds = await Promise.all([0, 1, 2, 3].map(async index => (await addresses.get(index)).kind()));
    if (await addresses.count() !== 4 ||
        kinds.join(",") !== [BicepValueKind.Expression, BicepValueKind.Expression, BicepValueKind.Literal, BicepValueKind.Expression].join(",")) {
        throw new Error("IP address list count or literal/expression round-trip failed");
    }
    const networking = await infrastructure.createAseV3NetworkingConfigurationData();
    for (const output of [
        await networking.externalInboundIPAddresses(),
        await networking.internalInboundIPAddresses(),
        await networking.linuxOutboundIPAddresses(),
        await networking.windowsOutboundIPAddresses(),
    ]) {
        if (await output.count() !== 0) {
            throw new Error("Detached networking output list should be empty");
        }
    }
});

const website = await builder.addContainer('frontend', 'nginx')
    .publishAsAzureAppServiceWebsite({
        configure: async (infrastructure, appService) => {
            await appService.configureSiteConfig({ isAlwaysOn: true });
            const site = await infrastructure.getWebSiteByIdentifier("webapp");
            await site.isHttpsOnly.set(true);
            const _httpsOnly = await site.isHttpsOnly.get();
        },
        configureSlot: async (_infrastructure, appServiceSlot) => {
            await appServiceSlot.configureSlotSiteConfig({ isAlwaysOn: false });
        }
    })
    .skipEnvironmentVariableNameChecks();

await builder.addExecutable('worker', 'dotnet', '.', ['run'])
    .publishAsAzureAppServiceWebsite({
        configure: async (_infrastructure, appService) => {
            await appService.configureSiteConfig({ isAlwaysOn: true });
        }
    })
    .skipEnvironmentVariableNameChecks();

await builder.addProject('api', '../Fake.Api/Fake.Api.csproj', { launchProfileOrOptions: 'https' })
    .publishAsAzureAppServiceWebsite({
        configureSlot: async (_infrastructure, appServiceSlot) => {
            await appServiceSlot.configureSlotSiteConfig({ isAlwaysOn: false });
        }
    })
    .skipEnvironmentVariableNameChecks();

const _environmentName = await environment.getResourceName();
const _websiteName = await website.getResourceName();

await builder.build().run();
