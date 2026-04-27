import { AzureAppConfigurationRole, createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const appConfig = await builder.addAzureAppConfiguration("appconfig");
await appConfig.withRoleAssignments(appConfig, [AzureAppConfigurationRole.AppConfigurationDataOwner, AzureAppConfigurationRole.AppConfigurationDataReader]);
await appConfig.runAsEmulator({
    configureEmulator: async (emulator) => {
        await emulator.withDataBindMount({ path: ".aace/appconfig" });
        await emulator.withDataVolume({ name: "appconfig-data" });
        await emulator.withHostPort({ port: 8483 });
    }
});

await builder.build().run();
