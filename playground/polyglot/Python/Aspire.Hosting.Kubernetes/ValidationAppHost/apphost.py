# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    kubernetes = builder.add_kubernetes_environment("resource")
    kubernetes.with_properties()
    _resolved_helm_chart_name = kubernetes.helm_chart_name
    _resolved_default_storage_class_name = kubernetes.default_storage_class_name
    _resolved_default_service_type = kubernetes.default_service_type
    service_container = builder.add_container("resource", "image")
    service_container.publish_as_kubernetes_service()
    builder.run()
