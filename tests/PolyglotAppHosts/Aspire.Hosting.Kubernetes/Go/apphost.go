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
	helmChartName := builder.AddParameter("helm-chart-name")
	helmChartVersion := builder.AddParameter("helm-chart-version")
	helmChartDescription := builder.AddParameter("helm-chart-description")

	kubernetes := builder.AddKubernetesEnvironment("kube")
	kubernetes.WithHelm(&aspire.WithHelmOptions{
		Configure: func(helm aspire.HelmChartOptions) {
			helm.WithNamespace("validation-namespace")
			helm.WithReleaseName("validation-release")
			helm.WithChartName("validation-kubernetes")
			helm.WithChartVersion("1.2.3")
			helm.WithChartDescription("Validation Helm Chart")
			helm.WithNamespace(helmNamespace)
			helm.WithReleaseName(helmReleaseName)
			helm.WithChartName(helmChartName)
			helm.WithChartVersion(helmChartVersion)
			helm.WithChartDescription(helmChartDescription)
		},
	})
	kubernetes.WithProperties(func(environment aspire.KubernetesEnvironmentResource) {
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
		_ = service.Parent()
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
