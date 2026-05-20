import { createBuilder } from './.modules/aspire.mjs';

const builder = await createBuilder();

const helmNamespace = await builder.addParameter('helm-namespace');
const helmReleaseName = await builder.addParameter('helm-release-name');
const helmChartName = await builder.addParameter('helm-chart-name');
const helmChartVersion = await builder.addParameter('helm-chart-version');
const helmChartDescription = await builder.addParameter('helm-chart-description');

const kubernetes = await builder.addKubernetesEnvironment('kube');

await kubernetes.withHelm({
    configure: async (helm) => {
        await helm.withNamespace('validation-namespace');
        await helm.withReleaseName('validation-release');
        await helm.withChartName('validation-kubernetes');
        await helm.withChartVersion('1.2.3');
        await helm.withChartDescription('Validation Helm Chart');
        await helm.withNamespace(helmNamespace);
        await helm.withReleaseName(helmReleaseName);
        await helm.withChartName(helmChartName);
        await helm.withChartVersion(helmChartVersion);
        await helm.withChartDescription(helmChartDescription);
    },
});

await kubernetes.withProperties(async (environment) => {
    await environment.defaultStorageType.set('pvc');
    const _configuredDefaultStorageType: string | null = await environment.defaultStorageType.get();

    await environment.defaultStorageClassName.set('fast-storage');
    const _configuredDefaultStorageClassName: string | null = await environment.defaultStorageClassName.get();

    await environment.defaultStorageSize.set('5Gi');
    const _configuredDefaultStorageSize: string | null = await environment.defaultStorageSize.get();

    await environment.defaultStorageReadWritePolicy.set('ReadWriteMany');
    const _configuredDefaultStorageReadWritePolicy: string | null = await environment.defaultStorageReadWritePolicy.get();

    await environment.defaultImagePullPolicy.set('Always');
    const _configuredDefaultImagePullPolicy: string | null = await environment.defaultImagePullPolicy.get();

    await environment.defaultServiceType.set('LoadBalancer');
    const _configuredDefaultServiceType: string | null = await environment.defaultServiceType.get();
});

const _resolvedDefaultStorageClassName: string | null = await kubernetes.defaultStorageClassName.get();
const _resolvedDefaultServiceType: string | null = await kubernetes.defaultServiceType.get();

const gateway = await kubernetes.addGateway('public-gateway');
await gateway.withHostname('gateway.example.com');
await gateway.withTls('gateway-tls');

const ingress = await kubernetes.addIngress('public-ingress');
await ingress.withHostname('ingress.example.com');
await ingress.withTls('ingress-tls');

// === cert-manager ===
// Validates the typed cert-manager API surface generated for TypeScript:
// addCertManager / addIssuer / withLetsEncrypt* / withAcmeServer / withHttp01Solver /
// gateway.withGatewayTlsIssuer.
const acmeEmail = await builder.addParameter('acme-email');

const certManager = await kubernetes.addCertManager('cert-manager');

const prodIssuer = await certManager.addIssuer('letsencrypt-prod');
await prodIssuer.withLetsEncryptProduction('admin@example.com');
await prodIssuer.withHttp01Solver();

const prodIssuerParam = await certManager.addIssuer('letsencrypt-prod-param');
await prodIssuerParam.withLetsEncryptProductionParam(acmeEmail);
await prodIssuerParam.withHttp01Solver();

const stagingIssuer = await certManager.addIssuer('letsencrypt-staging');
await stagingIssuer.withLetsEncryptStaging('admin@example.com');
await stagingIssuer.withHttp01Solver();

const stagingIssuerParam = await certManager.addIssuer('letsencrypt-staging-param');
await stagingIssuerParam.withLetsEncryptStagingParam(acmeEmail);
await stagingIssuerParam.withHttp01Solver();

const customIssuer = await certManager.addIssuer('custom-acme');
await customIssuer.withAcmeServer('https://acme.example.com/directory', 'admin@example.com');
await customIssuer.withHttp01Solver();

const customIssuerParam = await certManager.addIssuer('custom-acme-param');
await customIssuerParam.withAcmeServerParam('https://acme.example.com/directory', acmeEmail);
await customIssuerParam.withHttp01Solver();

// Wire the staging issuer onto the gateway via the typed cert-manager overload.
await gateway.withGatewayTlsIssuer(stagingIssuer);

const serviceContainer = await builder.addContainer('kube-service', 'redis:alpine');
await serviceContainer.withComputeEnvironment(kubernetes);
await serviceContainer.publishAsKubernetesService(async (service) => {
    const _serviceName: string = await service.name();
    const _serviceParent = await service.parent();

    await service.addManifest('keda.sh/v1alpha1', 'ScaledObject', 'kube-service-scaler', {
        configure: async (manifest) => {
            await manifest.withLabel('example.com/custom', 'true');
            await manifest.withAnnotation('example.com/source', 'typescript');
            await manifest.withField('spec.scaleTargetRef.kind', 'Deployment');
            await manifest.withField('spec.scaleTargetRef.name', 'kube-service');
            await manifest.withField('spec.maxReplicaCount', 3);
        },
    });
});

await builder.build().run();
