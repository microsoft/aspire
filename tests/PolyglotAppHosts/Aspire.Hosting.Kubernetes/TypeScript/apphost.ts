import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const kubernetes = await builder.addKubernetesEnvironment('kube');

await kubernetes.withProperties(async (environment) => {
    await environment.helmChartName.set('validation-kubernetes');
    const _configuredHelmChartName: string = await environment.helmChartName.get();

    await environment.helmChartVersion.set('1.2.3');
    const _configuredHelmChartVersion: string = await environment.helmChartVersion.get();

    await environment.helmChartDescription.set('Validation Helm Chart');
    const _configuredHelmChartDescription: string = await environment.helmChartDescription.get();

    await environment.defaultStorageType.set('pvc');
    const _configuredDefaultStorageType: string = await environment.defaultStorageType.get();

    await environment.defaultStorageClassName.set('fast-storage');
    const _configuredDefaultStorageClassName: string | undefined = await environment.defaultStorageClassName.get();

    await environment.defaultStorageSize.set('5Gi');
    const _configuredDefaultStorageSize: string = await environment.defaultStorageSize.get();

    await environment.defaultStorageReadWritePolicy.set('ReadWriteMany');
    const _configuredDefaultStorageReadWritePolicy: string = await environment.defaultStorageReadWritePolicy.get();

    await environment.defaultImagePullPolicy.set('Always');
    const _configuredDefaultImagePullPolicy: string = await environment.defaultImagePullPolicy.get();

    await environment.defaultServiceType.set('LoadBalancer');
    const _configuredDefaultServiceType: string = await environment.defaultServiceType.get();
});

const _resolvedHelmChartName: string = await kubernetes.helmChartName.get();
const _resolvedDefaultStorageClassName: string | undefined = await kubernetes.defaultStorageClassName.get();
const _resolvedDefaultServiceType: string = await kubernetes.defaultServiceType.get();

const serviceContainer = await builder.addContainer('kube-service', 'redis:alpine');
await serviceContainer.publishAsKubernetesService(async (service) => {
    const _serviceName: string = await service.name.get();
    const serviceParent = await service.parent.get();
    const _serviceParentChartName: string = await serviceParent.helmChartName.get();
});

await builder.build().run();
