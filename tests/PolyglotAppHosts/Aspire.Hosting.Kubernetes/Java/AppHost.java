import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var helmNamespace = builder.addParameter("helm-namespace");
        var helmReleaseName = builder.addParameter("helm-release-name");
        var helmChartVersion = builder.addParameter("helm-chart-version");
        var kubernetes = builder.addKubernetesEnvironment("kube");
        kubernetes.withHelm((helm) -> {
            helm.withNamespace("validation-namespace");
            helm.withReleaseName("validation-release");
            helm.withChartVersion("1.2.3");
            helm.withNamespace(helmNamespace);
            helm.withReleaseName(helmReleaseName);
            helm.withChartVersion(helmChartVersion);
        });
        kubernetes.withProperties((environment) -> {
            environment.setHelmChartName("validation-kubernetes");
            var _configuredHelmChartName = environment.helmChartName();
            environment.setHelmChartVersion("1.2.3");
            var _configuredHelmChartVersion = environment.helmChartVersion();
            environment.setHelmChartDescription("Validation Helm Chart");
            var _configuredHelmChartDescription = environment.helmChartDescription();
            environment.setDefaultStorageType("pvc");
            var _configuredDefaultStorageType = environment.defaultStorageType();
            environment.setDefaultStorageClassName("fast-storage");
            var _configuredDefaultStorageClassName = environment.defaultStorageClassName();
            environment.setDefaultStorageSize("5Gi");
            var _configuredDefaultStorageSize = environment.defaultStorageSize();
            environment.setDefaultStorageReadWritePolicy("ReadWriteMany");
            var _configuredDefaultStorageReadWritePolicy = environment.defaultStorageReadWritePolicy();
            environment.setDefaultImagePullPolicy("Always");
            var _configuredDefaultImagePullPolicy = environment.defaultImagePullPolicy();
            environment.setDefaultServiceType("LoadBalancer");
            var _configuredDefaultServiceType = environment.defaultServiceType();
        });
        var _resolvedHelmChartName = kubernetes.helmChartName();
        var _resolvedDefaultStorageClassName = kubernetes.defaultStorageClassName();
        var _resolvedDefaultServiceType = kubernetes.defaultServiceType();
        var gateway = kubernetes.addGateway("public-gateway");
        gateway.withHostname("gateway.example.com");
        gateway.withTls("gateway-tls");
        var ingress = kubernetes.addIngress("public-ingress");
        ingress.withHostname("ingress.example.com");
        ingress.withTls("ingress-tls");
        var serviceContainer = builder.addContainer("kube-service", "redis:alpine");
        serviceContainer.publishAsKubernetesService((service) -> {
            var _serviceName = service.name();
            var serviceParent = service.parent();
            var _serviceParentChartName = serviceParent.helmChartName();
        });
        builder.build().run();
    }
