# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    helm_namespace = builder.add_parameter("helm-namespace")
    helm_release_name = builder.add_parameter("helm-release-name")
    helm_chart_version = builder.add_parameter("helm-chart-version")
    kubernetes = builder.add_kubernetes_environment("resource")

    def configure_helm(helm):
        helm.with_namespace("validation-namespace")
        helm.with_release_name("validation-release")
        helm.with_chart_version("1.2.3")
        helm.with_namespace(helm_namespace)
        helm.with_release_name(helm_release_name)
        helm.with_chart_version(helm_chart_version)

    kubernetes.with_helm(configure_helm)
    kubernetes.with_properties()
    _resolved_helm_chart_name = kubernetes.helm_chart_name
    _resolved_default_storage_class_name = kubernetes.default_storage_class_name
    _resolved_default_service_type = kubernetes.default_service_type
    gateway = kubernetes.add_gateway("public-gateway")
    gateway.with_hostname("gateway.example.com")
    gateway.with_tls("gateway-tls")
    ingress = kubernetes.add_ingress("public-ingress")
    ingress.with_hostname("ingress.example.com")
    ingress.with_tls("ingress-tls")
    service_container = builder.add_container("resource", "image")
    service_container.publish_as_kubernetes_service()
    builder.run()
