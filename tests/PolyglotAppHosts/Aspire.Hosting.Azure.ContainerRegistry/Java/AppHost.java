import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var registry = builder.addAzureContainerRegistry("containerregistry")
            .withPurgeTask("0 1 * * *", new WithPurgeTaskOptions().filter("samples:*").ago(7.0).keep(5.0).taskName("purge-samples"));
        var environment = builder.addAzureContainerAppEnvironment("environment");
        environment.withAzureContainerRegistry(registry);
        environment.withRoleAssignments(registry, new AzureContainerRegistryRole[] { AzureContainerRegistryRole.ACR_PULL, AzureContainerRegistryRole.ACR_PUSH });
        var registryFromEnvironment = environment.getAzureContainerRegistry();
        registryFromEnvironment.withPurgeTask("0 2 * * *", new WithPurgeTaskOptions().filter("environment:*").ago(14.0).keep(2.0));
        builder.build().run();
    }
