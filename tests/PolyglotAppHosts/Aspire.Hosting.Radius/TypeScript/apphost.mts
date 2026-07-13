import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const azureSecret = await builder.addParameter('azure-client-secret', { secret: true });
const awsAccessKeyId = await builder.addParameter('aws-access-key-id', { secret: true });
const awsSecretAccessKey = await builder.addParameter('aws-secret-access-key', { secret: true });

const radius = await builder.addRadiusEnvironment('radius');
await radius.withNamespace('radius-system');
const _namespace: string = await radius.namespace.get();

await radius.withAzureProvider(
    '00000000-0000-0000-0000-000000000000',
    'radius-validation',
    async (azure) => {
        await azure.withServicePrincipal(
            '11111111-1111-1111-1111-111111111111',
            '22222222-2222-2222-2222-222222222222',
            azureSecret);
        await azure.withWorkloadIdentity(
            '11111111-1111-1111-1111-111111111111',
            '22222222-2222-2222-2222-222222222222');
    });

await radius.withAwsProvider(
    '123456789012',
    'us-west-2',
    async (aws) => {
        await aws.withAccessKey(awsAccessKeyId, awsSecretAccessKey);
        await aws.withIrsa('arn:aws:iam::123456789012:role/radius-validation');
    });

await radius.configureRadiusInfrastructure(async (options) => {
    const environments = await options.environments();
    const environment = await environments.get(0);
    await environment
        .withEnvironmentName('custom-radius-environment')
        .withKubernetesNamespace('custom-radius-namespace')
        .withEnvironmentAzureProvider(
            '00000000-0000-0000-0000-000000000000',
            'custom-radius-group')
        .withEnvironmentAwsProvider('123456789012', 'us-east-1')
        .withEnvironmentRecipePack('recipepack');

    const applications = await options.applications();
    const application = await applications.get(0);
    await application
        .withApplicationName('custom-radius-application')
        .withApplicationEnvironment('radius');

    const recipePacks = await options.recipePacks();
    const recipePack = await recipePacks.get(0);
    await recipePack
        .withRecipePackName('custom-radius-recipes')
        .withRecipe(
            'Custom.Resources/widgets',
            'bicep',
            'ghcr.io/example/radius-recipes/widget:1.0');

    const generatedResources = await options.resourceTypeInstances();
    const generatedResource = await generatedResources.get(0);
    await generatedResource
        .withResourceName('custom-generated-resource')
        .withResourceRecipeName('custom-generated-recipe');

    const customResource = await options.addResourceTypeInstance(
        'custom_widget',
        'Custom.Resources/widgets',
        '2025-01-01-preview');
    await customResource
        .withResourceName('custom-widget')
        .withResourceRecipeName('default')
        .withResourceScope('app', 'radius')
        .withStringRecipeParameter('sku', 'small');

    const containers = await options.containers();
    const container = await containers.get(0);
    await container
        .withImage('ghcr.io/example/api:1.0')
        .withContainerScope('app', 'radius')
        .withContainerEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Validation')
        .withContainerPort('http', 8080, 'TCP')
        .withContainerConnection('widget', 'custom_widget');

    const legacyEnvironments = await options.legacyEnvironments();
    const legacyEnvironment = await legacyEnvironments.get(0);
    await legacyEnvironment
        .withLegacyEnvironment('custom-legacy-environment', 'custom-radius-namespace')
        .withLegacyAzureScope('/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/custom-radius-group')
        .withLegacyAwsScope('/planes/aws/aws/accounts/123456789012/regions/us-east-1');

    const legacyApplications = await options.legacyApplications();
    const legacyApplication = await legacyApplications.get(0);
    await legacyApplication
        .withLegacyApplicationName('custom-legacy-application')
        .withLegacyApplicationEnvironment('legacy_env');
});

await builder.addContainer('api', 'nginx:alpine');
const project = await builder.addProject('project', './src/Project');
await project.withContainerImage('ghcr.io/example/project:1.0');
await builder.build().run();
