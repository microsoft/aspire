import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var appConfig = builder.addAzureAppConfiguration("appconfig");
        appConfig.withAppConfigurationRoleAssignments(appConfig, new AzureAppConfigurationRole[] { AzureAppConfigurationRole.APP_CONFIGURATION_DATA_OWNER, AzureAppConfigurationRole.APP_CONFIGURATION_DATA_READER });
        appConfig.runAsEmulator((emulator) -> {
                emulator.withDataBindMount(".aace/appconfig");
                emulator.withDataVolume("appconfig-data");
                emulator.withHostPort(8483.0);
            });
        builder.build().run();
    }
