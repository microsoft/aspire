package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder(nil)
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	helmNamespace := builder.AddParameter("helm-namespace")
	helmReleaseName := builder.AddParameter("helm-release-name")
	helmChartVersion := builder.AddParameter("helm-chart-version")

	kubernetes := builder.AddKubernetesEnvironment("kube")
	kubernetes.WithHelm(&aspire.WithHelmOptions{
		Configure: func(helm aspire.HelmChartOptions) {
			helm.WithNamespace("validation-namespace")
			helm.WithReleaseName("validation-release")
			helm.WithChartVersion("1.2.3")
			helm.WithNamespace(helmNamespace)
			helm.WithReleaseName(helmReleaseName)
			helm.WithChartVersion(helmChartVersion)
		},
	})
	kubernetes.WithProperties(func(environment aspire.KubernetesEnvironmentResource) {
		environment.SetHelmChartName("validation-kubernetes")
		_, _ = environment.HelmChartName()
		environment.SetHelmChartVersion("1.2.3")
		_, _ = environment.HelmChartVersion()
		environment.SetHelmChartDescription("Validation Helm Chart")
		_, _ = environment.HelmChartDescription()
		environment.SetDefaultStorageType("pvc")
		_, _ = environment.DefaultStorageType()
		environment.SetDefaultStorageClassName("fast-storage")
		_, _ = environment.DefaultStorageClassName()
		environment.SetDefaultStorageSize("5Gi")
		_, _ = environment.DefaultStorageSize()
		environment.SetDefaultStorageReadWritePolicy("ReadWriteMany")
		_, _ = environment.DefaultStorageReadWritePolicy()
		environment.SetDefaultImagePullPolicy("Always")
		_, _ = environment.DefaultImagePullPolicy()
		environment.SetDefaultServiceType("LoadBalancer")
		_, _ = environment.DefaultServiceType()
	})
	_, _ = kubernetes.HelmChartName()
	_, _ = kubernetes.DefaultStorageClassName()
	_, _ = kubernetes.DefaultServiceType()
	if err = kubernetes.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	gateway := kubernetes.AddGateway("public-gateway")
	gateway.WithHostname("gateway.example.com")
	gateway.WithTls("gateway-tls")

	ingress := kubernetes.AddIngress("public-ingress")
	ingress.WithHostname("ingress.example.com")
	ingress.WithTls("ingress-tls")

	serviceContainer := builder.AddContainer("kube-service", "redis:alpine")
	_ = serviceContainer.PublishAsKubernetesService(func(service aspire.KubernetesResource) {
		_, _ = service.Name()
		serviceParent := service.Parent()
		_, _ = serviceParent.HelmChartName()
	})
	if err = serviceContainer.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
