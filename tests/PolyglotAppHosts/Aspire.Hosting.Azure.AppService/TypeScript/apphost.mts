import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const deploymentSlot = await builder.addParameter('deploymentSlot');
const existingApplicationInsights = await builder.addAzureApplicationInsights('existingApplicationInsights');

const environment = await builder.addAzureAppServiceEnvironment('appservice-environment')
    .withDashboard()
    .withDashboard({ enable: false })
    .withAzureApplicationInsights()
    .withAzureApplicationInsights({ applicationInsights: existingApplicationInsights })
    .withDeploymentSlot(deploymentSlot)
    .withDeploymentSlot('staging');

const website = await builder.addContainer('frontend', 'nginx')
    .publishAsAzureAppServiceWebsite({
        configure: async (_infrastructure, appService) => {
            await appService.configureSiteConfig({ isAlwaysOn: true });
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
