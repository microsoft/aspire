# aspire.py - Capability-based Aspire SDK
# GENERATED CODE - DO NOT EDIT

from __future__ import annotations

import os
import sys
from dataclasses import dataclass
from enum import Enum
from typing import Any, Callable, Dict, List

from transport import AspireClient, Handle, CapabilityError, register_callback, register_handle_wrapper, register_cancellation
from base import AspireDict, AspireList, ReferenceExpression, ref_expr, HandleWrapperBase, ResourceBuilderBase, serialize_value

# ============================================================================
# Enums
# ============================================================================

class ContainerLifetime(str, Enum):
    SESSION = "Session"
    PERSISTENT = "Persistent"

class ImagePullPolicy(str, Enum):
    DEFAULT = "Default"
    ALWAYS = "Always"
    MISSING = "Missing"
    NEVER = "Never"

class DistributedApplicationOperation(str, Enum):
    RUN = "Run"
    PUBLISH = "Publish"

class OtlpProtocol(str, Enum):
    GRPC = "Grpc"
    HTTP_PROTOBUF = "HttpProtobuf"
    HTTP_JSON = "HttpJson"

class ProtocolType(str, Enum):
    IP = "IP"
    I_PV6_HOP_BY_HOP_OPTIONS = "IPv6HopByHopOptions"
    UNSPECIFIED = "Unspecified"
    ICMP = "Icmp"
    IGMP = "Igmp"
    GGP = "Ggp"
    I_PV4 = "IPv4"
    TCP = "Tcp"
    PUP = "Pup"
    UDP = "Udp"
    IDP = "Idp"
    I_PV6 = "IPv6"
    I_PV6_ROUTING_HEADER = "IPv6RoutingHeader"
    I_PV6_FRAGMENT_HEADER = "IPv6FragmentHeader"
    IP_SEC_ENCAPSULATING_SECURITY_PAYLOAD = "IPSecEncapsulatingSecurityPayload"
    IP_SEC_AUTHENTICATION_HEADER = "IPSecAuthenticationHeader"
    ICMP_V6 = "IcmpV6"
    I_PV6_NO_NEXT_HEADER = "IPv6NoNextHeader"
    I_PV6_DESTINATION_OPTIONS = "IPv6DestinationOptions"
    ND = "ND"
    RAW = "Raw"
    IPX = "Ipx"
    SPX = "Spx"
    SPX_II = "SpxII"
    UNKNOWN = "Unknown"

class WaitBehavior(str, Enum):
    WAIT_ON_RESOURCE_UNAVAILABLE = "WaitOnResourceUnavailable"
    STOP_ON_RESOURCE_UNAVAILABLE = "StopOnResourceUnavailable"

class CertificateTrustScope(str, Enum):
    NONE_ = "None"
    APPEND = "Append"
    OVERRIDE = "Override"
    SYSTEM = "System"

class IconVariant(str, Enum):
    REGULAR = "Regular"
    FILLED = "Filled"

class ProbeType(str, Enum):
    STARTUP = "Startup"
    READINESS = "Readiness"
    LIVENESS = "Liveness"

class EndpointProperty(str, Enum):
    URL = "Url"
    HOST = "Host"
    IPV4_HOST = "IPV4Host"
    PORT = "Port"
    SCHEME = "Scheme"
    TARGET_PORT = "TargetPort"
    HOST_AND_PORT = "HostAndPort"
    TLS_ENABLED = "TlsEnabled"

class UrlDisplayLocation(str, Enum):
    SUMMARY_AND_DETAILS = "SummaryAndDetails"
    DETAILS_ONLY = "DetailsOnly"

class TestPersistenceMode(str, Enum):
    NONE_ = "None"
    VOLUME = "Volume"
    BIND = "Bind"

class TestResourceStatus(str, Enum):
    PENDING = "Pending"
    RUNNING = "Running"
    STOPPED = "Stopped"
    FAILED = "Failed"

# ============================================================================
# DTOs
# ============================================================================

@dataclass
class CreateBuilderOptions:
    args: list[str]
    project_directory: str
    app_host_file_path: str
    container_registry_override: str
    disable_dashboard: bool
    dashboard_application_name: str
    allow_unsecured_transport: bool
    enable_resource_logging: bool

    def to_dict(self) -> Dict[str, Any]:
        return {
            "Args": serialize_value(self.args),
            "ProjectDirectory": serialize_value(self.project_directory),
            "AppHostFilePath": serialize_value(self.app_host_file_path),
            "ContainerRegistryOverride": serialize_value(self.container_registry_override),
            "DisableDashboard": serialize_value(self.disable_dashboard),
            "DashboardApplicationName": serialize_value(self.dashboard_application_name),
            "AllowUnsecuredTransport": serialize_value(self.allow_unsecured_transport),
            "EnableResourceLogging": serialize_value(self.enable_resource_logging),
        }

@dataclass
class ResourceEventDto:
    resource_name: str
    resource_id: str
    state: str
    state_style: str
    health_status: str
    exit_code: float

    def to_dict(self) -> Dict[str, Any]:
        return {
            "ResourceName": serialize_value(self.resource_name),
            "ResourceId": serialize_value(self.resource_id),
            "State": serialize_value(self.state),
            "StateStyle": serialize_value(self.state_style),
            "HealthStatus": serialize_value(self.health_status),
            "ExitCode": serialize_value(self.exit_code),
        }

@dataclass
class CommandOptions:
    description: str
    parameter: Any
    confirmation_message: str
    icon_name: str
    icon_variant: IconVariant
    is_highlighted: bool
    update_state: Any

    def to_dict(self) -> Dict[str, Any]:
        return {
            "Description": serialize_value(self.description),
            "Parameter": serialize_value(self.parameter),
            "ConfirmationMessage": serialize_value(self.confirmation_message),
            "IconName": serialize_value(self.icon_name),
            "IconVariant": serialize_value(self.icon_variant),
            "IsHighlighted": serialize_value(self.is_highlighted),
            "UpdateState": serialize_value(self.update_state),
        }

@dataclass
class ExecuteCommandResult:
    success: bool
    canceled: bool
    error_message: str

    def to_dict(self) -> Dict[str, Any]:
        return {
            "Success": serialize_value(self.success),
            "Canceled": serialize_value(self.canceled),
            "ErrorMessage": serialize_value(self.error_message),
        }

@dataclass
class ResourceUrlAnnotation:
    url: str
    display_text: str
    endpoint: EndpointReference
    display_location: UrlDisplayLocation

    def to_dict(self) -> Dict[str, Any]:
        return {
            "Url": serialize_value(self.url),
            "DisplayText": serialize_value(self.display_text),
            "Endpoint": serialize_value(self.endpoint),
            "DisplayLocation": serialize_value(self.display_location),
        }

@dataclass
class TestConfigDto:
    name: str
    port: float
    enabled: bool
    optional_field: str

    def to_dict(self) -> Dict[str, Any]:
        return {
            "Name": serialize_value(self.name),
            "Port": serialize_value(self.port),
            "Enabled": serialize_value(self.enabled),
            "OptionalField": serialize_value(self.optional_field),
        }

@dataclass
class TestNestedDto:
    id: str
    config: TestConfigDto
    tags: AspireList[str]
    counts: AspireDict[str, float]

    def to_dict(self) -> Dict[str, Any]:
        return {
            "Id": serialize_value(self.id),
            "Config": serialize_value(self.config),
            "Tags": serialize_value(self.tags),
            "Counts": serialize_value(self.counts),
        }

@dataclass
class TestDeeplyNestedDto:
    nested_data: AspireDict[str, AspireList[TestConfigDto]]
    metadata_array: list[AspireDict[str, str]]

    def to_dict(self) -> Dict[str, Any]:
        return {
            "NestedData": serialize_value(self.nested_data),
            "MetadataArray": serialize_value(self.metadata_array),
        }

# ============================================================================
# Handle Wrappers
# ============================================================================

class AfterResourcesCreatedEvent(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def services(self) -> IServiceProvider:
        """Gets the Services property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/AfterResourcesCreatedEvent.services", args)

    def model(self) -> DistributedApplicationModel:
        """Gets the Model property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/AfterResourcesCreatedEvent.model", args)


class BeforeResourceStartedEvent(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def resource(self) -> IResource:
        """Gets the Resource property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/BeforeResourceStartedEvent.resource", args)

    def services(self) -> IServiceProvider:
        """Gets the Services property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/BeforeResourceStartedEvent.services", args)


class BeforeStartEvent(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def services(self) -> IServiceProvider:
        """Gets the Services property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/BeforeStartEvent.services", args)

    def model(self) -> DistributedApplicationModel:
        """Gets the Model property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/BeforeStartEvent.model", args)


class CSharpAppResource(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def with_container_registry(self, registry: IResource) -> IResource:
        """Configures a resource to use a container registry"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["registry"] = serialize_value(registry)
        return self._client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)

    def with_dockerfile_base_image(self, build_image: str | None = None, runtime_image: str | None = None) -> IResource:
        """Sets the base image for a Dockerfile build"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if build_image is not None:
            args["buildImage"] = serialize_value(build_image)
        if runtime_image is not None:
            args["runtimeImage"] = serialize_value(runtime_image)
        return self._client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)

    def with_mcp_server(self, path: str = "/mcp", endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Configures an MCP server endpoint on the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["path"] = serialize_value(path)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withMcpServer", args)

    def with_otlp_exporter(self) -> IResourceWithEnvironment:
        """Configures OTLP telemetry export"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withOtlpExporter", args)

    def with_otlp_exporter_protocol(self, protocol: OtlpProtocol) -> IResourceWithEnvironment:
        """Configures OTLP telemetry export with specific protocol"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["protocol"] = serialize_value(protocol)
        return self._client.invoke_capability("Aspire.Hosting/withOtlpExporterProtocol", args)

    def with_replicas(self, replicas: float) -> ProjectResource:
        """Sets the number of replicas"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["replicas"] = serialize_value(replicas)
        return self._client.invoke_capability("Aspire.Hosting/withReplicas", args)

    def disable_forwarded_headers(self) -> ProjectResource:
        """Disables forwarded headers for the project"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/disableForwardedHeaders", args)

    def publish_as_docker_file(self, configure: Callable[[ContainerResource], None] | None = None) -> ProjectResource:
        """Publishes a project as a Docker file with optional container configuration"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        configure_id = register_callback(configure) if configure is not None else None
        if configure_id is not None:
            args["configure"] = configure_id
        return self._client.invoke_capability("Aspire.Hosting/publishProjectAsDockerFileWithConfigure", args)

    def with_required_command(self, command: str, help_link: str | None = None) -> IResource:
        """Adds a required command dependency"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["command"] = serialize_value(command)
        if help_link is not None:
            args["helpLink"] = serialize_value(help_link)
        return self._client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)

    def with_environment(self, name: str, value: str) -> IResourceWithEnvironment:
        """Sets an environment variable"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironment", args)

    def with_environment_expression(self, name: str, value: ReferenceExpression) -> IResourceWithEnvironment:
        """Adds an environment variable with a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentExpression", args)

    def with_environment_callback(self, callback: Callable[[EnvironmentCallbackContext], None]) -> IResourceWithEnvironment:
        """Sets environment variables via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentCallback", args)

    def with_environment_callback_async(self, callback: Callable[[EnvironmentCallbackContext], None]) -> IResourceWithEnvironment:
        """Sets environment variables via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentCallbackAsync", args)

    def with_environment_endpoint(self, name: str, endpoint_reference: EndpointReference) -> IResourceWithEnvironment:
        """Sets an environment variable from an endpoint reference"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["endpointReference"] = serialize_value(endpoint_reference)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentEndpoint", args)

    def with_environment_parameter(self, name: str, parameter: ParameterResource) -> IResourceWithEnvironment:
        """Sets an environment variable from a parameter resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["parameter"] = serialize_value(parameter)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentParameter", args)

    def with_environment_connection_string(self, env_var_name: str, resource: IResourceWithConnectionString) -> IResourceWithEnvironment:
        """Sets an environment variable from a connection string resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["envVarName"] = serialize_value(env_var_name)
        args["resource"] = serialize_value(resource)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentConnectionString", args)

    def with_args(self, args: list[str]) -> IResourceWithArgs:
        """Adds arguments"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["args"] = serialize_value(args)
        return self._client.invoke_capability("Aspire.Hosting/withArgs", args)

    def with_args_callback(self, callback: Callable[[CommandLineArgsCallbackContext], None]) -> IResourceWithArgs:
        """Sets command-line arguments via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withArgsCallback", args)

    def with_args_callback_async(self, callback: Callable[[CommandLineArgsCallbackContext], None]) -> IResourceWithArgs:
        """Sets command-line arguments via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withArgsCallbackAsync", args)

    def with_reference(self, source: IResource, connection_name: str | None = None, optional: bool = False, name: str | None = None) -> IResourceWithEnvironment:
        """Adds a reference to another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["source"] = serialize_value(source)
        if connection_name is not None:
            args["connectionName"] = serialize_value(connection_name)
        args["optional"] = serialize_value(optional)
        if name is not None:
            args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/withReference", args)

    def with_reference_uri(self, name: str, uri: str) -> IResourceWithEnvironment:
        """Adds a reference to a URI"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["uri"] = serialize_value(uri)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceUri", args)

    def with_reference_external_service(self, external_service: ExternalServiceResource) -> IResourceWithEnvironment:
        """Adds a reference to an external service"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["externalService"] = serialize_value(external_service)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceExternalService", args)

    def with_reference_endpoint(self, endpoint_reference: EndpointReference) -> IResourceWithEnvironment:
        """Adds a reference to an endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointReference"] = serialize_value(endpoint_reference)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceEndpoint", args)

    def with_endpoint(self, port: float | None = None, target_port: float | None = None, scheme: str | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True, is_external: bool | None = None, protocol: ProtocolType | None = None) -> IResourceWithEndpoints:
        """Adds a network endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if scheme is not None:
            args["scheme"] = serialize_value(scheme)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        if is_external is not None:
            args["isExternal"] = serialize_value(is_external)
        if protocol is not None:
            args["protocol"] = serialize_value(protocol)
        return self._client.invoke_capability("Aspire.Hosting/withEndpoint", args)

    def with_http_endpoint(self, port: float | None = None, target_port: float | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> IResourceWithEndpoints:
        """Adds an HTTP endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        return self._client.invoke_capability("Aspire.Hosting/withHttpEndpoint", args)

    def with_https_endpoint(self, port: float | None = None, target_port: float | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> IResourceWithEndpoints:
        """Adds an HTTPS endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        return self._client.invoke_capability("Aspire.Hosting/withHttpsEndpoint", args)

    def with_external_http_endpoints(self) -> IResourceWithEndpoints:
        """Makes HTTP endpoints externally accessible"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExternalHttpEndpoints", args)

    def get_endpoint(self, name: str) -> EndpointReference:
        """Gets an endpoint reference"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/getEndpoint", args)

    def as_http2_service(self) -> IResourceWithEndpoints:
        """Configures resource for HTTP/2"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/asHttp2Service", args)

    def with_urls_callback(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)

    def with_urls_callback_async(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallbackAsync", args)

    def with_url(self, url: str, display_text: str | None = None) -> IResource:
        """Adds or modifies displayed URLs"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrl", args)

    def with_url_expression(self, url: ReferenceExpression, display_text: str | None = None) -> IResource:
        """Adds a URL using a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrlExpression", args)

    def with_url_for_endpoint(self, endpoint_name: str, callback: Callable[[ResourceUrlAnnotation], None]) -> IResource:
        """Customizes the URL for a specific endpoint via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)

    def with_url_for_endpoint_factory(self, endpoint_name: str, callback: Callable[[EndpointReference], ResourceUrlAnnotation]) -> IResourceWithEndpoints:
        """Adds a URL for a specific endpoint via factory callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpointFactory", args)

    def publish_with_container_files(self, source: IResourceWithContainerFiles, destination_path: str) -> IContainerFilesDestinationResource:
        """Configures the resource to copy container files from the specified source during publishing"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["source"] = serialize_value(source)
        args["destinationPath"] = serialize_value(destination_path)
        return self._client.invoke_capability("Aspire.Hosting/publishWithContainerFiles", args)

    def exclude_from_manifest(self) -> IResource:
        """Excludes the resource from the deployment manifest"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)

    def wait_for(self, dependency: IResource) -> IResourceWithWaitSupport:
        """Waits for another resource to be ready"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting/waitFor", args)

    def wait_for_with_behavior(self, dependency: IResource, wait_behavior: WaitBehavior) -> IResourceWithWaitSupport:
        """Waits for another resource with specific behavior"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["waitBehavior"] = serialize_value(wait_behavior)
        return self._client.invoke_capability("Aspire.Hosting/waitForWithBehavior", args)

    def wait_for_start(self, dependency: IResource) -> IResourceWithWaitSupport:
        """Waits for another resource to start"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting/waitForStart", args)

    def wait_for_start_with_behavior(self, dependency: IResource, wait_behavior: WaitBehavior) -> IResourceWithWaitSupport:
        """Waits for another resource to start with specific behavior"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["waitBehavior"] = serialize_value(wait_behavior)
        return self._client.invoke_capability("Aspire.Hosting/waitForStartWithBehavior", args)

    def with_explicit_start(self) -> IResource:
        """Prevents resource from starting automatically"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExplicitStart", args)

    def wait_for_completion(self, dependency: IResource, exit_code: float = 0) -> IResourceWithWaitSupport:
        """Waits for resource completion"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["exitCode"] = serialize_value(exit_code)
        return self._client.invoke_capability("Aspire.Hosting/waitForCompletion", args)

    def with_health_check(self, key: str) -> IResource:
        """Adds a health check by key"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["key"] = serialize_value(key)
        return self._client.invoke_capability("Aspire.Hosting/withHealthCheck", args)

    def with_http_health_check(self, path: str | None = None, status_code: float | None = None, endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Adds an HTTP health check"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if path is not None:
            args["path"] = serialize_value(path)
        if status_code is not None:
            args["statusCode"] = serialize_value(status_code)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withHttpHealthCheck", args)

    def with_command(self, name: str, display_name: str, execute_command: Callable[[ExecuteCommandContext], ExecuteCommandResult], command_options: CommandOptions | None = None) -> IResource:
        """Adds a resource command"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["displayName"] = serialize_value(display_name)
        execute_command_id = register_callback(execute_command) if execute_command is not None else None
        if execute_command_id is not None:
            args["executeCommand"] = execute_command_id
        if command_options is not None:
            args["commandOptions"] = serialize_value(command_options)
        return self._client.invoke_capability("Aspire.Hosting/withCommand", args)

    def with_developer_certificate_trust(self, trust: bool) -> IResourceWithEnvironment:
        """Configures developer certificate trust"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["trust"] = serialize_value(trust)
        return self._client.invoke_capability("Aspire.Hosting/withDeveloperCertificateTrust", args)

    def with_certificate_trust_scope(self, scope: CertificateTrustScope) -> IResourceWithEnvironment:
        """Sets the certificate trust scope"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["scope"] = serialize_value(scope)
        return self._client.invoke_capability("Aspire.Hosting/withCertificateTrustScope", args)

    def with_https_developer_certificate(self, password: ParameterResource | None = None) -> IResourceWithEnvironment:
        """Configures HTTPS with a developer certificate"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if password is not None:
            args["password"] = serialize_value(password)
        return self._client.invoke_capability("Aspire.Hosting/withHttpsDeveloperCertificate", args)

    def without_https_certificate(self) -> IResourceWithEnvironment:
        """Removes HTTPS certificate configuration"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withoutHttpsCertificate", args)

    def with_parent_relationship(self, parent: IResource) -> IResource:
        """Sets the parent relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["parent"] = serialize_value(parent)
        return self._client.invoke_capability("Aspire.Hosting/withParentRelationship", args)

    def with_child_relationship(self, child: IResource) -> IResource:
        """Sets a child relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["child"] = serialize_value(child)
        return self._client.invoke_capability("Aspire.Hosting/withChildRelationship", args)

    def with_icon_name(self, icon_name: str, icon_variant: IconVariant = None) -> IResource:
        """Sets the icon for the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["iconName"] = serialize_value(icon_name)
        args["iconVariant"] = serialize_value(icon_variant)
        return self._client.invoke_capability("Aspire.Hosting/withIconName", args)

    def with_http_probe(self, probe_type: ProbeType, path: str | None = None, initial_delay_seconds: float | None = None, period_seconds: float | None = None, timeout_seconds: float | None = None, failure_threshold: float | None = None, success_threshold: float | None = None, endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Adds an HTTP health probe to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["probeType"] = serialize_value(probe_type)
        if path is not None:
            args["path"] = serialize_value(path)
        if initial_delay_seconds is not None:
            args["initialDelaySeconds"] = serialize_value(initial_delay_seconds)
        if period_seconds is not None:
            args["periodSeconds"] = serialize_value(period_seconds)
        if timeout_seconds is not None:
            args["timeoutSeconds"] = serialize_value(timeout_seconds)
        if failure_threshold is not None:
            args["failureThreshold"] = serialize_value(failure_threshold)
        if success_threshold is not None:
            args["successThreshold"] = serialize_value(success_threshold)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withHttpProbe", args)

    def exclude_from_mcp(self) -> IResource:
        """Excludes the resource from MCP server exposure"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)

    def with_remote_image_name(self, remote_image_name: str) -> IComputeResource:
        """Sets the remote image name for publishing"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["remoteImageName"] = serialize_value(remote_image_name)
        return self._client.invoke_capability("Aspire.Hosting/withRemoteImageName", args)

    def with_remote_image_tag(self, remote_image_tag: str) -> IComputeResource:
        """Sets the remote image tag for publishing"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["remoteImageTag"] = serialize_value(remote_image_tag)
        return self._client.invoke_capability("Aspire.Hosting/withRemoteImageTag", args)

    def with_pipeline_step_factory(self, step_name: str, callback: Callable[[PipelineStepContext], None], depends_on: list[str] | None = None, required_by: list[str] | None = None, tags: list[str] | None = None, description: str | None = None) -> IResource:
        """Adds a pipeline step to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["stepName"] = serialize_value(step_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        if depends_on is not None:
            args["dependsOn"] = serialize_value(depends_on)
        if required_by is not None:
            args["requiredBy"] = serialize_value(required_by)
        if tags is not None:
            args["tags"] = serialize_value(tags)
        if description is not None:
            args["description"] = serialize_value(description)
        return self._client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)

    def with_pipeline_configuration_async(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via an async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfigurationAsync", args)

    def with_pipeline_configuration(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via a callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)

    def get_resource_name(self) -> str:
        """Gets the resource name"""
        args: Dict[str, Any] = { "resource": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getResourceName", args)

    def on_before_resource_started(self, callback: Callable[[BeforeResourceStartedEvent], None]) -> IResource:
        """Subscribes to the BeforeResourceStarted event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)

    def on_resource_stopped(self, callback: Callable[[ResourceStoppedEvent], None]) -> IResource:
        """Subscribes to the ResourceStopped event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceStopped", args)

    def on_initialize_resource(self, callback: Callable[[InitializeResourceEvent], None]) -> IResource:
        """Subscribes to the InitializeResource event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onInitializeResource", args)

    def on_resource_endpoints_allocated(self, callback: Callable[[ResourceEndpointsAllocatedEvent], None]) -> IResourceWithEndpoints:
        """Subscribes to the ResourceEndpointsAllocated event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceEndpointsAllocated", args)

    def on_resource_ready(self, callback: Callable[[ResourceReadyEvent], None]) -> IResource:
        """Subscribes to the ResourceReady event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceReady", args)

    def with_optional_string(self, value: str | None = None, enabled: bool = True) -> IResource:
        """Adds an optional string parameter"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if value is not None:
            args["value"] = serialize_value(value)
        args["enabled"] = serialize_value(enabled)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalString", args)

    def with_config(self, config: TestConfigDto) -> IResource:
        """Configures the resource with a DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConfig", args)

    def test_with_environment_callback(self, callback: Callable[[TestEnvironmentContext], None]) -> IResourceWithEnvironment:
        """Configures environment with callback (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWithEnvironmentCallback", args)

    def with_created_at(self, created_at: str) -> IResource:
        """Sets the created timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["createdAt"] = serialize_value(created_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCreatedAt", args)

    def with_modified_at(self, modified_at: str) -> IResource:
        """Sets the modified timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["modifiedAt"] = serialize_value(modified_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withModifiedAt", args)

    def with_correlation_id(self, correlation_id: str) -> IResource:
        """Sets the correlation ID"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["correlationId"] = serialize_value(correlation_id)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCorrelationId", args)

    def with_optional_callback(self, callback: Callable[[TestCallbackContext], None] | None = None) -> IResource:
        """Configures with optional callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalCallback", args)

    def with_status(self, status: TestResourceStatus) -> IResource:
        """Sets the resource status"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["status"] = serialize_value(status)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withStatus", args)

    def with_nested_config(self, config: TestNestedDto) -> IResource:
        """Configures with nested DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withNestedConfig", args)

    def with_validator(self, validator: Callable[[TestResourceContext], bool]) -> IResource:
        """Adds validation callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        validator_id = register_callback(validator) if validator is not None else None
        if validator_id is not None:
            args["validator"] = validator_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withValidator", args)

    def test_wait_for(self, dependency: IResource) -> IResource:
        """Waits for another resource (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWaitFor", args)

    def with_dependency(self, dependency: IResourceWithConnectionString) -> IResource:
        """Adds a dependency on another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withDependency", args)

    def with_endpoints(self, endpoints: list[str]) -> IResource:
        """Sets the endpoints"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpoints"] = serialize_value(endpoints)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEndpoints", args)

    def with_environment_variables(self, variables: dict[str, str]) -> IResourceWithEnvironment:
        """Sets environment variables"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["variables"] = serialize_value(variables)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEnvironmentVariables", args)

    def with_cancellable_operation(self, operation: Callable[[CancellationToken], None]) -> IResource:
        """Performs a cancellable operation"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        operation_id = register_callback(operation) if operation is not None else None
        if operation_id is not None:
            args["operation"] = operation_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCancellableOperation", args)


class CancellationToken(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class CommandLineArgsCallbackContext(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    @property
    def args(self) -> AspireList[Any]:
        """Gets the Args property"""
        if not hasattr(self, '_args'):
            self._args = AspireList(
                self._handle,
                self._client,
                "Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.args"
            )
        return self._args

    def cancellation_token(self) -> CancellationToken:
        """Gets the CancellationToken property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.cancellationToken", args)

    def execution_context(self) -> DistributedApplicationExecutionContext:
        """Gets the ExecutionContext property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.executionContext", args)

    def set_execution_context(self, value: DistributedApplicationExecutionContext) -> CommandLineArgsCallbackContext:
        """Sets the ExecutionContext property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.setExecutionContext", args)

    def logger(self) -> ILogger:
        """Gets the Logger property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.logger", args)

    def set_logger(self, value: ILogger) -> CommandLineArgsCallbackContext:
        """Sets the Logger property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.setLogger", args)

    def resource(self) -> IResource:
        """Gets the Resource property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.resource", args)


class ConnectionStringAvailableEvent(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def resource(self) -> IResource:
        """Gets the Resource property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ConnectionStringAvailableEvent.resource", args)

    def services(self) -> IServiceProvider:
        """Gets the Services property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ConnectionStringAvailableEvent.services", args)


class ConnectionStringResource(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def with_container_registry(self, registry: IResource) -> IResource:
        """Configures a resource to use a container registry"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["registry"] = serialize_value(registry)
        return self._client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)

    def with_dockerfile_base_image(self, build_image: str | None = None, runtime_image: str | None = None) -> IResource:
        """Sets the base image for a Dockerfile build"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if build_image is not None:
            args["buildImage"] = serialize_value(build_image)
        if runtime_image is not None:
            args["runtimeImage"] = serialize_value(runtime_image)
        return self._client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)

    def with_required_command(self, command: str, help_link: str | None = None) -> IResource:
        """Adds a required command dependency"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["command"] = serialize_value(command)
        if help_link is not None:
            args["helpLink"] = serialize_value(help_link)
        return self._client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)

    def with_connection_property(self, name: str, value: ReferenceExpression) -> IResourceWithConnectionString:
        """Adds a connection property with a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withConnectionProperty", args)

    def with_connection_property_value(self, name: str, value: str) -> IResourceWithConnectionString:
        """Adds a connection property with a string value"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withConnectionPropertyValue", args)

    def with_urls_callback(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)

    def with_urls_callback_async(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallbackAsync", args)

    def with_url(self, url: str, display_text: str | None = None) -> IResource:
        """Adds or modifies displayed URLs"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrl", args)

    def with_url_expression(self, url: ReferenceExpression, display_text: str | None = None) -> IResource:
        """Adds a URL using a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrlExpression", args)

    def with_url_for_endpoint(self, endpoint_name: str, callback: Callable[[ResourceUrlAnnotation], None]) -> IResource:
        """Customizes the URL for a specific endpoint via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)

    def exclude_from_manifest(self) -> IResource:
        """Excludes the resource from the deployment manifest"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)

    def wait_for(self, dependency: IResource) -> IResourceWithWaitSupport:
        """Waits for another resource to be ready"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting/waitFor", args)

    def wait_for_with_behavior(self, dependency: IResource, wait_behavior: WaitBehavior) -> IResourceWithWaitSupport:
        """Waits for another resource with specific behavior"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["waitBehavior"] = serialize_value(wait_behavior)
        return self._client.invoke_capability("Aspire.Hosting/waitForWithBehavior", args)

    def wait_for_start(self, dependency: IResource) -> IResourceWithWaitSupport:
        """Waits for another resource to start"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting/waitForStart", args)

    def wait_for_start_with_behavior(self, dependency: IResource, wait_behavior: WaitBehavior) -> IResourceWithWaitSupport:
        """Waits for another resource to start with specific behavior"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["waitBehavior"] = serialize_value(wait_behavior)
        return self._client.invoke_capability("Aspire.Hosting/waitForStartWithBehavior", args)

    def with_explicit_start(self) -> IResource:
        """Prevents resource from starting automatically"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExplicitStart", args)

    def wait_for_completion(self, dependency: IResource, exit_code: float = 0) -> IResourceWithWaitSupport:
        """Waits for resource completion"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["exitCode"] = serialize_value(exit_code)
        return self._client.invoke_capability("Aspire.Hosting/waitForCompletion", args)

    def with_health_check(self, key: str) -> IResource:
        """Adds a health check by key"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["key"] = serialize_value(key)
        return self._client.invoke_capability("Aspire.Hosting/withHealthCheck", args)

    def with_command(self, name: str, display_name: str, execute_command: Callable[[ExecuteCommandContext], ExecuteCommandResult], command_options: CommandOptions | None = None) -> IResource:
        """Adds a resource command"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["displayName"] = serialize_value(display_name)
        execute_command_id = register_callback(execute_command) if execute_command is not None else None
        if execute_command_id is not None:
            args["executeCommand"] = execute_command_id
        if command_options is not None:
            args["commandOptions"] = serialize_value(command_options)
        return self._client.invoke_capability("Aspire.Hosting/withCommand", args)

    def with_parent_relationship(self, parent: IResource) -> IResource:
        """Sets the parent relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["parent"] = serialize_value(parent)
        return self._client.invoke_capability("Aspire.Hosting/withParentRelationship", args)

    def with_child_relationship(self, child: IResource) -> IResource:
        """Sets a child relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["child"] = serialize_value(child)
        return self._client.invoke_capability("Aspire.Hosting/withChildRelationship", args)

    def with_icon_name(self, icon_name: str, icon_variant: IconVariant = None) -> IResource:
        """Sets the icon for the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["iconName"] = serialize_value(icon_name)
        args["iconVariant"] = serialize_value(icon_variant)
        return self._client.invoke_capability("Aspire.Hosting/withIconName", args)

    def exclude_from_mcp(self) -> IResource:
        """Excludes the resource from MCP server exposure"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)

    def with_pipeline_step_factory(self, step_name: str, callback: Callable[[PipelineStepContext], None], depends_on: list[str] | None = None, required_by: list[str] | None = None, tags: list[str] | None = None, description: str | None = None) -> IResource:
        """Adds a pipeline step to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["stepName"] = serialize_value(step_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        if depends_on is not None:
            args["dependsOn"] = serialize_value(depends_on)
        if required_by is not None:
            args["requiredBy"] = serialize_value(required_by)
        if tags is not None:
            args["tags"] = serialize_value(tags)
        if description is not None:
            args["description"] = serialize_value(description)
        return self._client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)

    def with_pipeline_configuration_async(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via an async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfigurationAsync", args)

    def with_pipeline_configuration(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via a callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)

    def get_resource_name(self) -> str:
        """Gets the resource name"""
        args: Dict[str, Any] = { "resource": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getResourceName", args)

    def on_before_resource_started(self, callback: Callable[[BeforeResourceStartedEvent], None]) -> IResource:
        """Subscribes to the BeforeResourceStarted event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)

    def on_resource_stopped(self, callback: Callable[[ResourceStoppedEvent], None]) -> IResource:
        """Subscribes to the ResourceStopped event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceStopped", args)

    def on_connection_string_available(self, callback: Callable[[ConnectionStringAvailableEvent], None]) -> IResourceWithConnectionString:
        """Subscribes to the ConnectionStringAvailable event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onConnectionStringAvailable", args)

    def on_initialize_resource(self, callback: Callable[[InitializeResourceEvent], None]) -> IResource:
        """Subscribes to the InitializeResource event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onInitializeResource", args)

    def on_resource_ready(self, callback: Callable[[ResourceReadyEvent], None]) -> IResource:
        """Subscribes to the ResourceReady event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceReady", args)

    def with_optional_string(self, value: str | None = None, enabled: bool = True) -> IResource:
        """Adds an optional string parameter"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if value is not None:
            args["value"] = serialize_value(value)
        args["enabled"] = serialize_value(enabled)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalString", args)

    def with_config(self, config: TestConfigDto) -> IResource:
        """Configures the resource with a DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConfig", args)

    def with_connection_string(self, connection_string: ReferenceExpression) -> IResourceWithConnectionString:
        """Sets the connection string using a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["connectionString"] = serialize_value(connection_string)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConnectionString", args)

    def with_created_at(self, created_at: str) -> IResource:
        """Sets the created timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["createdAt"] = serialize_value(created_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCreatedAt", args)

    def with_modified_at(self, modified_at: str) -> IResource:
        """Sets the modified timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["modifiedAt"] = serialize_value(modified_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withModifiedAt", args)

    def with_correlation_id(self, correlation_id: str) -> IResource:
        """Sets the correlation ID"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["correlationId"] = serialize_value(correlation_id)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCorrelationId", args)

    def with_optional_callback(self, callback: Callable[[TestCallbackContext], None] | None = None) -> IResource:
        """Configures with optional callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalCallback", args)

    def with_status(self, status: TestResourceStatus) -> IResource:
        """Sets the resource status"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["status"] = serialize_value(status)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withStatus", args)

    def with_nested_config(self, config: TestNestedDto) -> IResource:
        """Configures with nested DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withNestedConfig", args)

    def with_validator(self, validator: Callable[[TestResourceContext], bool]) -> IResource:
        """Adds validation callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        validator_id = register_callback(validator) if validator is not None else None
        if validator_id is not None:
            args["validator"] = validator_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withValidator", args)

    def test_wait_for(self, dependency: IResource) -> IResource:
        """Waits for another resource (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWaitFor", args)

    def with_connection_string_direct(self, connection_string: str) -> IResourceWithConnectionString:
        """Sets connection string using direct interface target"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["connectionString"] = serialize_value(connection_string)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConnectionStringDirect", args)

    def with_dependency(self, dependency: IResourceWithConnectionString) -> IResource:
        """Adds a dependency on another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withDependency", args)

    def with_endpoints(self, endpoints: list[str]) -> IResource:
        """Sets the endpoints"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpoints"] = serialize_value(endpoints)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEndpoints", args)

    def with_cancellable_operation(self, operation: Callable[[CancellationToken], None]) -> IResource:
        """Performs a cancellable operation"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        operation_id = register_callback(operation) if operation is not None else None
        if operation_id is not None:
            args["operation"] = operation_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCancellableOperation", args)


class ContainerRegistryResource(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def with_container_registry(self, registry: IResource) -> IResource:
        """Configures a resource to use a container registry"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["registry"] = serialize_value(registry)
        return self._client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)

    def with_dockerfile_base_image(self, build_image: str | None = None, runtime_image: str | None = None) -> IResource:
        """Sets the base image for a Dockerfile build"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if build_image is not None:
            args["buildImage"] = serialize_value(build_image)
        if runtime_image is not None:
            args["runtimeImage"] = serialize_value(runtime_image)
        return self._client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)

    def with_required_command(self, command: str, help_link: str | None = None) -> IResource:
        """Adds a required command dependency"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["command"] = serialize_value(command)
        if help_link is not None:
            args["helpLink"] = serialize_value(help_link)
        return self._client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)

    def with_urls_callback(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)

    def with_urls_callback_async(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallbackAsync", args)

    def with_url(self, url: str, display_text: str | None = None) -> IResource:
        """Adds or modifies displayed URLs"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrl", args)

    def with_url_expression(self, url: ReferenceExpression, display_text: str | None = None) -> IResource:
        """Adds a URL using a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrlExpression", args)

    def with_url_for_endpoint(self, endpoint_name: str, callback: Callable[[ResourceUrlAnnotation], None]) -> IResource:
        """Customizes the URL for a specific endpoint via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)

    def exclude_from_manifest(self) -> IResource:
        """Excludes the resource from the deployment manifest"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)

    def with_explicit_start(self) -> IResource:
        """Prevents resource from starting automatically"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExplicitStart", args)

    def with_health_check(self, key: str) -> IResource:
        """Adds a health check by key"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["key"] = serialize_value(key)
        return self._client.invoke_capability("Aspire.Hosting/withHealthCheck", args)

    def with_command(self, name: str, display_name: str, execute_command: Callable[[ExecuteCommandContext], ExecuteCommandResult], command_options: CommandOptions | None = None) -> IResource:
        """Adds a resource command"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["displayName"] = serialize_value(display_name)
        execute_command_id = register_callback(execute_command) if execute_command is not None else None
        if execute_command_id is not None:
            args["executeCommand"] = execute_command_id
        if command_options is not None:
            args["commandOptions"] = serialize_value(command_options)
        return self._client.invoke_capability("Aspire.Hosting/withCommand", args)

    def with_parent_relationship(self, parent: IResource) -> IResource:
        """Sets the parent relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["parent"] = serialize_value(parent)
        return self._client.invoke_capability("Aspire.Hosting/withParentRelationship", args)

    def with_child_relationship(self, child: IResource) -> IResource:
        """Sets a child relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["child"] = serialize_value(child)
        return self._client.invoke_capability("Aspire.Hosting/withChildRelationship", args)

    def with_icon_name(self, icon_name: str, icon_variant: IconVariant = None) -> IResource:
        """Sets the icon for the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["iconName"] = serialize_value(icon_name)
        args["iconVariant"] = serialize_value(icon_variant)
        return self._client.invoke_capability("Aspire.Hosting/withIconName", args)

    def exclude_from_mcp(self) -> IResource:
        """Excludes the resource from MCP server exposure"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)

    def with_pipeline_step_factory(self, step_name: str, callback: Callable[[PipelineStepContext], None], depends_on: list[str] | None = None, required_by: list[str] | None = None, tags: list[str] | None = None, description: str | None = None) -> IResource:
        """Adds a pipeline step to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["stepName"] = serialize_value(step_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        if depends_on is not None:
            args["dependsOn"] = serialize_value(depends_on)
        if required_by is not None:
            args["requiredBy"] = serialize_value(required_by)
        if tags is not None:
            args["tags"] = serialize_value(tags)
        if description is not None:
            args["description"] = serialize_value(description)
        return self._client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)

    def with_pipeline_configuration_async(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via an async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfigurationAsync", args)

    def with_pipeline_configuration(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via a callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)

    def get_resource_name(self) -> str:
        """Gets the resource name"""
        args: Dict[str, Any] = { "resource": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getResourceName", args)

    def on_before_resource_started(self, callback: Callable[[BeforeResourceStartedEvent], None]) -> IResource:
        """Subscribes to the BeforeResourceStarted event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)

    def on_resource_stopped(self, callback: Callable[[ResourceStoppedEvent], None]) -> IResource:
        """Subscribes to the ResourceStopped event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceStopped", args)

    def on_initialize_resource(self, callback: Callable[[InitializeResourceEvent], None]) -> IResource:
        """Subscribes to the InitializeResource event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onInitializeResource", args)

    def on_resource_ready(self, callback: Callable[[ResourceReadyEvent], None]) -> IResource:
        """Subscribes to the ResourceReady event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceReady", args)

    def with_optional_string(self, value: str | None = None, enabled: bool = True) -> IResource:
        """Adds an optional string parameter"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if value is not None:
            args["value"] = serialize_value(value)
        args["enabled"] = serialize_value(enabled)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalString", args)

    def with_config(self, config: TestConfigDto) -> IResource:
        """Configures the resource with a DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConfig", args)

    def with_created_at(self, created_at: str) -> IResource:
        """Sets the created timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["createdAt"] = serialize_value(created_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCreatedAt", args)

    def with_modified_at(self, modified_at: str) -> IResource:
        """Sets the modified timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["modifiedAt"] = serialize_value(modified_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withModifiedAt", args)

    def with_correlation_id(self, correlation_id: str) -> IResource:
        """Sets the correlation ID"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["correlationId"] = serialize_value(correlation_id)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCorrelationId", args)

    def with_optional_callback(self, callback: Callable[[TestCallbackContext], None] | None = None) -> IResource:
        """Configures with optional callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalCallback", args)

    def with_status(self, status: TestResourceStatus) -> IResource:
        """Sets the resource status"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["status"] = serialize_value(status)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withStatus", args)

    def with_nested_config(self, config: TestNestedDto) -> IResource:
        """Configures with nested DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withNestedConfig", args)

    def with_validator(self, validator: Callable[[TestResourceContext], bool]) -> IResource:
        """Adds validation callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        validator_id = register_callback(validator) if validator is not None else None
        if validator_id is not None:
            args["validator"] = validator_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withValidator", args)

    def test_wait_for(self, dependency: IResource) -> IResource:
        """Waits for another resource (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWaitFor", args)

    def with_dependency(self, dependency: IResourceWithConnectionString) -> IResource:
        """Adds a dependency on another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withDependency", args)

    def with_endpoints(self, endpoints: list[str]) -> IResource:
        """Sets the endpoints"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpoints"] = serialize_value(endpoints)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEndpoints", args)

    def with_cancellable_operation(self, operation: Callable[[CancellationToken], None]) -> IResource:
        """Performs a cancellable operation"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        operation_id = register_callback(operation) if operation is not None else None
        if operation_id is not None:
            args["operation"] = operation_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCancellableOperation", args)


class ContainerResource(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def with_container_registry(self, registry: IResource) -> IResource:
        """Configures a resource to use a container registry"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["registry"] = serialize_value(registry)
        return self._client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)

    def with_bind_mount(self, source: str, target: str, is_read_only: bool = False) -> ContainerResource:
        """Adds a bind mount"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["source"] = serialize_value(source)
        args["target"] = serialize_value(target)
        args["isReadOnly"] = serialize_value(is_read_only)
        return self._client.invoke_capability("Aspire.Hosting/withBindMount", args)

    def with_entrypoint(self, entrypoint: str) -> ContainerResource:
        """Sets the container entrypoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["entrypoint"] = serialize_value(entrypoint)
        return self._client.invoke_capability("Aspire.Hosting/withEntrypoint", args)

    def with_image_tag(self, tag: str) -> ContainerResource:
        """Sets the container image tag"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["tag"] = serialize_value(tag)
        return self._client.invoke_capability("Aspire.Hosting/withImageTag", args)

    def with_image_registry(self, registry: str) -> ContainerResource:
        """Sets the container image registry"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["registry"] = serialize_value(registry)
        return self._client.invoke_capability("Aspire.Hosting/withImageRegistry", args)

    def with_image(self, image: str, tag: str | None = None) -> ContainerResource:
        """Sets the container image"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["image"] = serialize_value(image)
        if tag is not None:
            args["tag"] = serialize_value(tag)
        return self._client.invoke_capability("Aspire.Hosting/withImage", args)

    def with_image_sha256(self, sha256: str) -> ContainerResource:
        """Sets the image SHA256 digest"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["sha256"] = serialize_value(sha256)
        return self._client.invoke_capability("Aspire.Hosting/withImageSHA256", args)

    def with_container_runtime_args(self, args: list[str]) -> ContainerResource:
        """Adds runtime arguments for the container"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["args"] = serialize_value(args)
        return self._client.invoke_capability("Aspire.Hosting/withContainerRuntimeArgs", args)

    def with_lifetime(self, lifetime: ContainerLifetime) -> ContainerResource:
        """Sets the lifetime behavior of the container resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["lifetime"] = serialize_value(lifetime)
        return self._client.invoke_capability("Aspire.Hosting/withLifetime", args)

    def with_image_pull_policy(self, pull_policy: ImagePullPolicy) -> ContainerResource:
        """Sets the container image pull policy"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["pullPolicy"] = serialize_value(pull_policy)
        return self._client.invoke_capability("Aspire.Hosting/withImagePullPolicy", args)

    def publish_as_container(self) -> ContainerResource:
        """Configures the resource to be published as a container"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/publishAsContainer", args)

    def with_dockerfile(self, context_path: str, dockerfile_path: str | None = None, stage: str | None = None) -> ContainerResource:
        """Configures the resource to use a Dockerfile"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["contextPath"] = serialize_value(context_path)
        if dockerfile_path is not None:
            args["dockerfilePath"] = serialize_value(dockerfile_path)
        if stage is not None:
            args["stage"] = serialize_value(stage)
        return self._client.invoke_capability("Aspire.Hosting/withDockerfile", args)

    def with_container_name(self, name: str) -> ContainerResource:
        """Sets the container name"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/withContainerName", args)

    def with_build_arg(self, name: str, value: ParameterResource) -> ContainerResource:
        """Adds a build argument from a parameter resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withBuildArg", args)

    def with_build_secret(self, name: str, value: ParameterResource) -> ContainerResource:
        """Adds a build secret from a parameter resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withBuildSecret", args)

    def with_endpoint_proxy_support(self, proxy_enabled: bool) -> ContainerResource:
        """Configures endpoint proxy support"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["proxyEnabled"] = serialize_value(proxy_enabled)
        return self._client.invoke_capability("Aspire.Hosting/withEndpointProxySupport", args)

    def with_dockerfile_base_image(self, build_image: str | None = None, runtime_image: str | None = None) -> IResource:
        """Sets the base image for a Dockerfile build"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if build_image is not None:
            args["buildImage"] = serialize_value(build_image)
        if runtime_image is not None:
            args["runtimeImage"] = serialize_value(runtime_image)
        return self._client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)

    def with_container_network_alias(self, alias: str) -> ContainerResource:
        """Adds a network alias for the container"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["alias"] = serialize_value(alias)
        return self._client.invoke_capability("Aspire.Hosting/withContainerNetworkAlias", args)

    def with_mcp_server(self, path: str = "/mcp", endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Configures an MCP server endpoint on the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["path"] = serialize_value(path)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withMcpServer", args)

    def with_otlp_exporter(self) -> IResourceWithEnvironment:
        """Configures OTLP telemetry export"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withOtlpExporter", args)

    def with_otlp_exporter_protocol(self, protocol: OtlpProtocol) -> IResourceWithEnvironment:
        """Configures OTLP telemetry export with specific protocol"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["protocol"] = serialize_value(protocol)
        return self._client.invoke_capability("Aspire.Hosting/withOtlpExporterProtocol", args)

    def publish_as_connection_string(self) -> ContainerResource:
        """Publishes the resource as a connection string"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/publishAsConnectionString", args)

    def with_required_command(self, command: str, help_link: str | None = None) -> IResource:
        """Adds a required command dependency"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["command"] = serialize_value(command)
        if help_link is not None:
            args["helpLink"] = serialize_value(help_link)
        return self._client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)

    def with_environment(self, name: str, value: str) -> IResourceWithEnvironment:
        """Sets an environment variable"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironment", args)

    def with_environment_expression(self, name: str, value: ReferenceExpression) -> IResourceWithEnvironment:
        """Adds an environment variable with a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentExpression", args)

    def with_environment_callback(self, callback: Callable[[EnvironmentCallbackContext], None]) -> IResourceWithEnvironment:
        """Sets environment variables via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentCallback", args)

    def with_environment_callback_async(self, callback: Callable[[EnvironmentCallbackContext], None]) -> IResourceWithEnvironment:
        """Sets environment variables via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentCallbackAsync", args)

    def with_environment_endpoint(self, name: str, endpoint_reference: EndpointReference) -> IResourceWithEnvironment:
        """Sets an environment variable from an endpoint reference"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["endpointReference"] = serialize_value(endpoint_reference)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentEndpoint", args)

    def with_environment_parameter(self, name: str, parameter: ParameterResource) -> IResourceWithEnvironment:
        """Sets an environment variable from a parameter resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["parameter"] = serialize_value(parameter)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentParameter", args)

    def with_environment_connection_string(self, env_var_name: str, resource: IResourceWithConnectionString) -> IResourceWithEnvironment:
        """Sets an environment variable from a connection string resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["envVarName"] = serialize_value(env_var_name)
        args["resource"] = serialize_value(resource)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentConnectionString", args)

    def with_args(self, args: list[str]) -> IResourceWithArgs:
        """Adds arguments"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["args"] = serialize_value(args)
        return self._client.invoke_capability("Aspire.Hosting/withArgs", args)

    def with_args_callback(self, callback: Callable[[CommandLineArgsCallbackContext], None]) -> IResourceWithArgs:
        """Sets command-line arguments via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withArgsCallback", args)

    def with_args_callback_async(self, callback: Callable[[CommandLineArgsCallbackContext], None]) -> IResourceWithArgs:
        """Sets command-line arguments via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withArgsCallbackAsync", args)

    def with_reference(self, source: IResource, connection_name: str | None = None, optional: bool = False, name: str | None = None) -> IResourceWithEnvironment:
        """Adds a reference to another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["source"] = serialize_value(source)
        if connection_name is not None:
            args["connectionName"] = serialize_value(connection_name)
        args["optional"] = serialize_value(optional)
        if name is not None:
            args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/withReference", args)

    def with_reference_uri(self, name: str, uri: str) -> IResourceWithEnvironment:
        """Adds a reference to a URI"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["uri"] = serialize_value(uri)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceUri", args)

    def with_reference_external_service(self, external_service: ExternalServiceResource) -> IResourceWithEnvironment:
        """Adds a reference to an external service"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["externalService"] = serialize_value(external_service)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceExternalService", args)

    def with_reference_endpoint(self, endpoint_reference: EndpointReference) -> IResourceWithEnvironment:
        """Adds a reference to an endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointReference"] = serialize_value(endpoint_reference)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceEndpoint", args)

    def with_endpoint(self, port: float | None = None, target_port: float | None = None, scheme: str | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True, is_external: bool | None = None, protocol: ProtocolType | None = None) -> IResourceWithEndpoints:
        """Adds a network endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if scheme is not None:
            args["scheme"] = serialize_value(scheme)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        if is_external is not None:
            args["isExternal"] = serialize_value(is_external)
        if protocol is not None:
            args["protocol"] = serialize_value(protocol)
        return self._client.invoke_capability("Aspire.Hosting/withEndpoint", args)

    def with_http_endpoint(self, port: float | None = None, target_port: float | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> IResourceWithEndpoints:
        """Adds an HTTP endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        return self._client.invoke_capability("Aspire.Hosting/withHttpEndpoint", args)

    def with_https_endpoint(self, port: float | None = None, target_port: float | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> IResourceWithEndpoints:
        """Adds an HTTPS endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        return self._client.invoke_capability("Aspire.Hosting/withHttpsEndpoint", args)

    def with_external_http_endpoints(self) -> IResourceWithEndpoints:
        """Makes HTTP endpoints externally accessible"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExternalHttpEndpoints", args)

    def get_endpoint(self, name: str) -> EndpointReference:
        """Gets an endpoint reference"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/getEndpoint", args)

    def as_http2_service(self) -> IResourceWithEndpoints:
        """Configures resource for HTTP/2"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/asHttp2Service", args)

    def with_urls_callback(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)

    def with_urls_callback_async(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallbackAsync", args)

    def with_url(self, url: str, display_text: str | None = None) -> IResource:
        """Adds or modifies displayed URLs"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrl", args)

    def with_url_expression(self, url: ReferenceExpression, display_text: str | None = None) -> IResource:
        """Adds a URL using a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrlExpression", args)

    def with_url_for_endpoint(self, endpoint_name: str, callback: Callable[[ResourceUrlAnnotation], None]) -> IResource:
        """Customizes the URL for a specific endpoint via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)

    def with_url_for_endpoint_factory(self, endpoint_name: str, callback: Callable[[EndpointReference], ResourceUrlAnnotation]) -> IResourceWithEndpoints:
        """Adds a URL for a specific endpoint via factory callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpointFactory", args)

    def exclude_from_manifest(self) -> IResource:
        """Excludes the resource from the deployment manifest"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)

    def wait_for(self, dependency: IResource) -> IResourceWithWaitSupport:
        """Waits for another resource to be ready"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting/waitFor", args)

    def wait_for_with_behavior(self, dependency: IResource, wait_behavior: WaitBehavior) -> IResourceWithWaitSupport:
        """Waits for another resource with specific behavior"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["waitBehavior"] = serialize_value(wait_behavior)
        return self._client.invoke_capability("Aspire.Hosting/waitForWithBehavior", args)

    def wait_for_start(self, dependency: IResource) -> IResourceWithWaitSupport:
        """Waits for another resource to start"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting/waitForStart", args)

    def wait_for_start_with_behavior(self, dependency: IResource, wait_behavior: WaitBehavior) -> IResourceWithWaitSupport:
        """Waits for another resource to start with specific behavior"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["waitBehavior"] = serialize_value(wait_behavior)
        return self._client.invoke_capability("Aspire.Hosting/waitForStartWithBehavior", args)

    def with_explicit_start(self) -> IResource:
        """Prevents resource from starting automatically"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExplicitStart", args)

    def wait_for_completion(self, dependency: IResource, exit_code: float = 0) -> IResourceWithWaitSupport:
        """Waits for resource completion"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["exitCode"] = serialize_value(exit_code)
        return self._client.invoke_capability("Aspire.Hosting/waitForCompletion", args)

    def with_health_check(self, key: str) -> IResource:
        """Adds a health check by key"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["key"] = serialize_value(key)
        return self._client.invoke_capability("Aspire.Hosting/withHealthCheck", args)

    def with_http_health_check(self, path: str | None = None, status_code: float | None = None, endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Adds an HTTP health check"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if path is not None:
            args["path"] = serialize_value(path)
        if status_code is not None:
            args["statusCode"] = serialize_value(status_code)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withHttpHealthCheck", args)

    def with_command(self, name: str, display_name: str, execute_command: Callable[[ExecuteCommandContext], ExecuteCommandResult], command_options: CommandOptions | None = None) -> IResource:
        """Adds a resource command"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["displayName"] = serialize_value(display_name)
        execute_command_id = register_callback(execute_command) if execute_command is not None else None
        if execute_command_id is not None:
            args["executeCommand"] = execute_command_id
        if command_options is not None:
            args["commandOptions"] = serialize_value(command_options)
        return self._client.invoke_capability("Aspire.Hosting/withCommand", args)

    def with_developer_certificate_trust(self, trust: bool) -> IResourceWithEnvironment:
        """Configures developer certificate trust"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["trust"] = serialize_value(trust)
        return self._client.invoke_capability("Aspire.Hosting/withDeveloperCertificateTrust", args)

    def with_certificate_trust_scope(self, scope: CertificateTrustScope) -> IResourceWithEnvironment:
        """Sets the certificate trust scope"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["scope"] = serialize_value(scope)
        return self._client.invoke_capability("Aspire.Hosting/withCertificateTrustScope", args)

    def with_https_developer_certificate(self, password: ParameterResource | None = None) -> IResourceWithEnvironment:
        """Configures HTTPS with a developer certificate"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if password is not None:
            args["password"] = serialize_value(password)
        return self._client.invoke_capability("Aspire.Hosting/withHttpsDeveloperCertificate", args)

    def without_https_certificate(self) -> IResourceWithEnvironment:
        """Removes HTTPS certificate configuration"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withoutHttpsCertificate", args)

    def with_parent_relationship(self, parent: IResource) -> IResource:
        """Sets the parent relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["parent"] = serialize_value(parent)
        return self._client.invoke_capability("Aspire.Hosting/withParentRelationship", args)

    def with_child_relationship(self, child: IResource) -> IResource:
        """Sets a child relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["child"] = serialize_value(child)
        return self._client.invoke_capability("Aspire.Hosting/withChildRelationship", args)

    def with_icon_name(self, icon_name: str, icon_variant: IconVariant = None) -> IResource:
        """Sets the icon for the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["iconName"] = serialize_value(icon_name)
        args["iconVariant"] = serialize_value(icon_variant)
        return self._client.invoke_capability("Aspire.Hosting/withIconName", args)

    def with_http_probe(self, probe_type: ProbeType, path: str | None = None, initial_delay_seconds: float | None = None, period_seconds: float | None = None, timeout_seconds: float | None = None, failure_threshold: float | None = None, success_threshold: float | None = None, endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Adds an HTTP health probe to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["probeType"] = serialize_value(probe_type)
        if path is not None:
            args["path"] = serialize_value(path)
        if initial_delay_seconds is not None:
            args["initialDelaySeconds"] = serialize_value(initial_delay_seconds)
        if period_seconds is not None:
            args["periodSeconds"] = serialize_value(period_seconds)
        if timeout_seconds is not None:
            args["timeoutSeconds"] = serialize_value(timeout_seconds)
        if failure_threshold is not None:
            args["failureThreshold"] = serialize_value(failure_threshold)
        if success_threshold is not None:
            args["successThreshold"] = serialize_value(success_threshold)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withHttpProbe", args)

    def exclude_from_mcp(self) -> IResource:
        """Excludes the resource from MCP server exposure"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)

    def with_remote_image_name(self, remote_image_name: str) -> IComputeResource:
        """Sets the remote image name for publishing"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["remoteImageName"] = serialize_value(remote_image_name)
        return self._client.invoke_capability("Aspire.Hosting/withRemoteImageName", args)

    def with_remote_image_tag(self, remote_image_tag: str) -> IComputeResource:
        """Sets the remote image tag for publishing"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["remoteImageTag"] = serialize_value(remote_image_tag)
        return self._client.invoke_capability("Aspire.Hosting/withRemoteImageTag", args)

    def with_pipeline_step_factory(self, step_name: str, callback: Callable[[PipelineStepContext], None], depends_on: list[str] | None = None, required_by: list[str] | None = None, tags: list[str] | None = None, description: str | None = None) -> IResource:
        """Adds a pipeline step to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["stepName"] = serialize_value(step_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        if depends_on is not None:
            args["dependsOn"] = serialize_value(depends_on)
        if required_by is not None:
            args["requiredBy"] = serialize_value(required_by)
        if tags is not None:
            args["tags"] = serialize_value(tags)
        if description is not None:
            args["description"] = serialize_value(description)
        return self._client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)

    def with_pipeline_configuration_async(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via an async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfigurationAsync", args)

    def with_pipeline_configuration(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via a callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)

    def with_volume(self, target: str, name: str | None = None, is_read_only: bool = False) -> ContainerResource:
        """Adds a volume"""
        args: Dict[str, Any] = { "resource": serialize_value(self._handle) }
        args["target"] = serialize_value(target)
        if name is not None:
            args["name"] = serialize_value(name)
        args["isReadOnly"] = serialize_value(is_read_only)
        return self._client.invoke_capability("Aspire.Hosting/withVolume", args)

    def get_resource_name(self) -> str:
        """Gets the resource name"""
        args: Dict[str, Any] = { "resource": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getResourceName", args)

    def on_before_resource_started(self, callback: Callable[[BeforeResourceStartedEvent], None]) -> IResource:
        """Subscribes to the BeforeResourceStarted event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)

    def on_resource_stopped(self, callback: Callable[[ResourceStoppedEvent], None]) -> IResource:
        """Subscribes to the ResourceStopped event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceStopped", args)

    def on_initialize_resource(self, callback: Callable[[InitializeResourceEvent], None]) -> IResource:
        """Subscribes to the InitializeResource event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onInitializeResource", args)

    def on_resource_endpoints_allocated(self, callback: Callable[[ResourceEndpointsAllocatedEvent], None]) -> IResourceWithEndpoints:
        """Subscribes to the ResourceEndpointsAllocated event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceEndpointsAllocated", args)

    def on_resource_ready(self, callback: Callable[[ResourceReadyEvent], None]) -> IResource:
        """Subscribes to the ResourceReady event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceReady", args)

    def with_optional_string(self, value: str | None = None, enabled: bool = True) -> IResource:
        """Adds an optional string parameter"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if value is not None:
            args["value"] = serialize_value(value)
        args["enabled"] = serialize_value(enabled)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalString", args)

    def with_config(self, config: TestConfigDto) -> IResource:
        """Configures the resource with a DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConfig", args)

    def test_with_environment_callback(self, callback: Callable[[TestEnvironmentContext], None]) -> IResourceWithEnvironment:
        """Configures environment with callback (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWithEnvironmentCallback", args)

    def with_created_at(self, created_at: str) -> IResource:
        """Sets the created timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["createdAt"] = serialize_value(created_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCreatedAt", args)

    def with_modified_at(self, modified_at: str) -> IResource:
        """Sets the modified timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["modifiedAt"] = serialize_value(modified_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withModifiedAt", args)

    def with_correlation_id(self, correlation_id: str) -> IResource:
        """Sets the correlation ID"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["correlationId"] = serialize_value(correlation_id)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCorrelationId", args)

    def with_optional_callback(self, callback: Callable[[TestCallbackContext], None] | None = None) -> IResource:
        """Configures with optional callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalCallback", args)

    def with_status(self, status: TestResourceStatus) -> IResource:
        """Sets the resource status"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["status"] = serialize_value(status)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withStatus", args)

    def with_nested_config(self, config: TestNestedDto) -> IResource:
        """Configures with nested DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withNestedConfig", args)

    def with_validator(self, validator: Callable[[TestResourceContext], bool]) -> IResource:
        """Adds validation callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        validator_id = register_callback(validator) if validator is not None else None
        if validator_id is not None:
            args["validator"] = validator_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withValidator", args)

    def test_wait_for(self, dependency: IResource) -> IResource:
        """Waits for another resource (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWaitFor", args)

    def with_dependency(self, dependency: IResourceWithConnectionString) -> IResource:
        """Adds a dependency on another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withDependency", args)

    def with_endpoints(self, endpoints: list[str]) -> IResource:
        """Sets the endpoints"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpoints"] = serialize_value(endpoints)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEndpoints", args)

    def with_environment_variables(self, variables: dict[str, str]) -> IResourceWithEnvironment:
        """Sets environment variables"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["variables"] = serialize_value(variables)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEnvironmentVariables", args)

    def with_cancellable_operation(self, operation: Callable[[CancellationToken], None]) -> IResource:
        """Performs a cancellable operation"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        operation_id = register_callback(operation) if operation is not None else None
        if operation_id is not None:
            args["operation"] = operation_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCancellableOperation", args)


class DistributedApplication(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def run(self, cancellation_token: CancellationToken | None = None) -> None:
        """Runs the distributed application"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        cancellation_token_id = register_cancellation(cancellation_token, self._client) if cancellation_token is not None else None
        if cancellation_token_id is not None:
            args["cancellationToken"] = cancellation_token_id
        self._client.invoke_capability("Aspire.Hosting/run", args)
        return None


class DistributedApplicationEventSubscription(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class DistributedApplicationExecutionContext(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def publisher_name(self) -> str:
        """Gets the PublisherName property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/DistributedApplicationExecutionContext.publisherName", args)

    def set_publisher_name(self, value: str) -> DistributedApplicationExecutionContext:
        """Sets the PublisherName property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/DistributedApplicationExecutionContext.setPublisherName", args)

    def operation(self) -> DistributedApplicationOperation:
        """Gets the Operation property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/DistributedApplicationExecutionContext.operation", args)

    def service_provider(self) -> IServiceProvider:
        """Gets the ServiceProvider property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/DistributedApplicationExecutionContext.serviceProvider", args)

    def is_publish_mode(self) -> bool:
        """Gets the IsPublishMode property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/DistributedApplicationExecutionContext.isPublishMode", args)

    def is_run_mode(self) -> bool:
        """Gets the IsRunMode property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/DistributedApplicationExecutionContext.isRunMode", args)


class DistributedApplicationExecutionContextOptions(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class DistributedApplicationModel(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def get_resources(self) -> list[IResource]:
        """Gets resources from the distributed application model"""
        args: Dict[str, Any] = { "model": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getResources", args)

    def find_resource_by_name(self, name: str) -> IResource:
        """Finds a resource by name"""
        args: Dict[str, Any] = { "model": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/findResourceByName", args)


class DistributedApplicationResourceEventSubscription(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class DotnetToolResource(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def with_container_registry(self, registry: IResource) -> IResource:
        """Configures a resource to use a container registry"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["registry"] = serialize_value(registry)
        return self._client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)

    def with_dockerfile_base_image(self, build_image: str | None = None, runtime_image: str | None = None) -> IResource:
        """Sets the base image for a Dockerfile build"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if build_image is not None:
            args["buildImage"] = serialize_value(build_image)
        if runtime_image is not None:
            args["runtimeImage"] = serialize_value(runtime_image)
        return self._client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)

    def with_tool_package(self, package_id: str) -> DotnetToolResource:
        """Sets the tool package ID"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["packageId"] = serialize_value(package_id)
        return self._client.invoke_capability("Aspire.Hosting/withToolPackage", args)

    def with_tool_version(self, version: str) -> DotnetToolResource:
        """Sets the tool version"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["version"] = serialize_value(version)
        return self._client.invoke_capability("Aspire.Hosting/withToolVersion", args)

    def with_tool_prerelease(self) -> DotnetToolResource:
        """Allows prerelease tool versions"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withToolPrerelease", args)

    def with_tool_source(self, source: str) -> DotnetToolResource:
        """Adds a NuGet source for the tool"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["source"] = serialize_value(source)
        return self._client.invoke_capability("Aspire.Hosting/withToolSource", args)

    def with_tool_ignore_existing_feeds(self) -> DotnetToolResource:
        """Ignores existing NuGet feeds"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withToolIgnoreExistingFeeds", args)

    def with_tool_ignore_failed_sources(self) -> DotnetToolResource:
        """Ignores failed NuGet sources"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withToolIgnoreFailedSources", args)

    def publish_as_docker_file(self) -> ExecutableResource:
        """Publishes the executable as a Docker container"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/publishAsDockerFile", args)

    def publish_as_docker_file_with_configure(self, configure: Callable[[ContainerResource], None]) -> ExecutableResource:
        """Publishes an executable as a Docker file with optional container configuration"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        configure_id = register_callback(configure) if configure is not None else None
        if configure_id is not None:
            args["configure"] = configure_id
        return self._client.invoke_capability("Aspire.Hosting/publishAsDockerFileWithConfigure", args)

    def with_executable_command(self, command: str) -> ExecutableResource:
        """Sets the executable command"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["command"] = serialize_value(command)
        return self._client.invoke_capability("Aspire.Hosting/withExecutableCommand", args)

    def with_working_directory(self, working_directory: str) -> ExecutableResource:
        """Sets the executable working directory"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["workingDirectory"] = serialize_value(working_directory)
        return self._client.invoke_capability("Aspire.Hosting/withWorkingDirectory", args)

    def with_mcp_server(self, path: str = "/mcp", endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Configures an MCP server endpoint on the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["path"] = serialize_value(path)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withMcpServer", args)

    def with_otlp_exporter(self) -> IResourceWithEnvironment:
        """Configures OTLP telemetry export"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withOtlpExporter", args)

    def with_otlp_exporter_protocol(self, protocol: OtlpProtocol) -> IResourceWithEnvironment:
        """Configures OTLP telemetry export with specific protocol"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["protocol"] = serialize_value(protocol)
        return self._client.invoke_capability("Aspire.Hosting/withOtlpExporterProtocol", args)

    def with_required_command(self, command: str, help_link: str | None = None) -> IResource:
        """Adds a required command dependency"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["command"] = serialize_value(command)
        if help_link is not None:
            args["helpLink"] = serialize_value(help_link)
        return self._client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)

    def with_environment(self, name: str, value: str) -> IResourceWithEnvironment:
        """Sets an environment variable"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironment", args)

    def with_environment_expression(self, name: str, value: ReferenceExpression) -> IResourceWithEnvironment:
        """Adds an environment variable with a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentExpression", args)

    def with_environment_callback(self, callback: Callable[[EnvironmentCallbackContext], None]) -> IResourceWithEnvironment:
        """Sets environment variables via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentCallback", args)

    def with_environment_callback_async(self, callback: Callable[[EnvironmentCallbackContext], None]) -> IResourceWithEnvironment:
        """Sets environment variables via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentCallbackAsync", args)

    def with_environment_endpoint(self, name: str, endpoint_reference: EndpointReference) -> IResourceWithEnvironment:
        """Sets an environment variable from an endpoint reference"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["endpointReference"] = serialize_value(endpoint_reference)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentEndpoint", args)

    def with_environment_parameter(self, name: str, parameter: ParameterResource) -> IResourceWithEnvironment:
        """Sets an environment variable from a parameter resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["parameter"] = serialize_value(parameter)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentParameter", args)

    def with_environment_connection_string(self, env_var_name: str, resource: IResourceWithConnectionString) -> IResourceWithEnvironment:
        """Sets an environment variable from a connection string resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["envVarName"] = serialize_value(env_var_name)
        args["resource"] = serialize_value(resource)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentConnectionString", args)

    def with_args(self, args: list[str]) -> IResourceWithArgs:
        """Adds arguments"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["args"] = serialize_value(args)
        return self._client.invoke_capability("Aspire.Hosting/withArgs", args)

    def with_args_callback(self, callback: Callable[[CommandLineArgsCallbackContext], None]) -> IResourceWithArgs:
        """Sets command-line arguments via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withArgsCallback", args)

    def with_args_callback_async(self, callback: Callable[[CommandLineArgsCallbackContext], None]) -> IResourceWithArgs:
        """Sets command-line arguments via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withArgsCallbackAsync", args)

    def with_reference(self, source: IResource, connection_name: str | None = None, optional: bool = False, name: str | None = None) -> IResourceWithEnvironment:
        """Adds a reference to another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["source"] = serialize_value(source)
        if connection_name is not None:
            args["connectionName"] = serialize_value(connection_name)
        args["optional"] = serialize_value(optional)
        if name is not None:
            args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/withReference", args)

    def with_reference_uri(self, name: str, uri: str) -> IResourceWithEnvironment:
        """Adds a reference to a URI"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["uri"] = serialize_value(uri)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceUri", args)

    def with_reference_external_service(self, external_service: ExternalServiceResource) -> IResourceWithEnvironment:
        """Adds a reference to an external service"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["externalService"] = serialize_value(external_service)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceExternalService", args)

    def with_reference_endpoint(self, endpoint_reference: EndpointReference) -> IResourceWithEnvironment:
        """Adds a reference to an endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointReference"] = serialize_value(endpoint_reference)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceEndpoint", args)

    def with_endpoint(self, port: float | None = None, target_port: float | None = None, scheme: str | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True, is_external: bool | None = None, protocol: ProtocolType | None = None) -> IResourceWithEndpoints:
        """Adds a network endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if scheme is not None:
            args["scheme"] = serialize_value(scheme)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        if is_external is not None:
            args["isExternal"] = serialize_value(is_external)
        if protocol is not None:
            args["protocol"] = serialize_value(protocol)
        return self._client.invoke_capability("Aspire.Hosting/withEndpoint", args)

    def with_http_endpoint(self, port: float | None = None, target_port: float | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> IResourceWithEndpoints:
        """Adds an HTTP endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        return self._client.invoke_capability("Aspire.Hosting/withHttpEndpoint", args)

    def with_https_endpoint(self, port: float | None = None, target_port: float | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> IResourceWithEndpoints:
        """Adds an HTTPS endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        return self._client.invoke_capability("Aspire.Hosting/withHttpsEndpoint", args)

    def with_external_http_endpoints(self) -> IResourceWithEndpoints:
        """Makes HTTP endpoints externally accessible"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExternalHttpEndpoints", args)

    def get_endpoint(self, name: str) -> EndpointReference:
        """Gets an endpoint reference"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/getEndpoint", args)

    def as_http2_service(self) -> IResourceWithEndpoints:
        """Configures resource for HTTP/2"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/asHttp2Service", args)

    def with_urls_callback(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)

    def with_urls_callback_async(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallbackAsync", args)

    def with_url(self, url: str, display_text: str | None = None) -> IResource:
        """Adds or modifies displayed URLs"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrl", args)

    def with_url_expression(self, url: ReferenceExpression, display_text: str | None = None) -> IResource:
        """Adds a URL using a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrlExpression", args)

    def with_url_for_endpoint(self, endpoint_name: str, callback: Callable[[ResourceUrlAnnotation], None]) -> IResource:
        """Customizes the URL for a specific endpoint via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)

    def with_url_for_endpoint_factory(self, endpoint_name: str, callback: Callable[[EndpointReference], ResourceUrlAnnotation]) -> IResourceWithEndpoints:
        """Adds a URL for a specific endpoint via factory callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpointFactory", args)

    def exclude_from_manifest(self) -> IResource:
        """Excludes the resource from the deployment manifest"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)

    def wait_for(self, dependency: IResource) -> IResourceWithWaitSupport:
        """Waits for another resource to be ready"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting/waitFor", args)

    def wait_for_with_behavior(self, dependency: IResource, wait_behavior: WaitBehavior) -> IResourceWithWaitSupport:
        """Waits for another resource with specific behavior"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["waitBehavior"] = serialize_value(wait_behavior)
        return self._client.invoke_capability("Aspire.Hosting/waitForWithBehavior", args)

    def wait_for_start(self, dependency: IResource) -> IResourceWithWaitSupport:
        """Waits for another resource to start"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting/waitForStart", args)

    def wait_for_start_with_behavior(self, dependency: IResource, wait_behavior: WaitBehavior) -> IResourceWithWaitSupport:
        """Waits for another resource to start with specific behavior"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["waitBehavior"] = serialize_value(wait_behavior)
        return self._client.invoke_capability("Aspire.Hosting/waitForStartWithBehavior", args)

    def with_explicit_start(self) -> IResource:
        """Prevents resource from starting automatically"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExplicitStart", args)

    def wait_for_completion(self, dependency: IResource, exit_code: float = 0) -> IResourceWithWaitSupport:
        """Waits for resource completion"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["exitCode"] = serialize_value(exit_code)
        return self._client.invoke_capability("Aspire.Hosting/waitForCompletion", args)

    def with_health_check(self, key: str) -> IResource:
        """Adds a health check by key"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["key"] = serialize_value(key)
        return self._client.invoke_capability("Aspire.Hosting/withHealthCheck", args)

    def with_http_health_check(self, path: str | None = None, status_code: float | None = None, endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Adds an HTTP health check"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if path is not None:
            args["path"] = serialize_value(path)
        if status_code is not None:
            args["statusCode"] = serialize_value(status_code)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withHttpHealthCheck", args)

    def with_command(self, name: str, display_name: str, execute_command: Callable[[ExecuteCommandContext], ExecuteCommandResult], command_options: CommandOptions | None = None) -> IResource:
        """Adds a resource command"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["displayName"] = serialize_value(display_name)
        execute_command_id = register_callback(execute_command) if execute_command is not None else None
        if execute_command_id is not None:
            args["executeCommand"] = execute_command_id
        if command_options is not None:
            args["commandOptions"] = serialize_value(command_options)
        return self._client.invoke_capability("Aspire.Hosting/withCommand", args)

    def with_developer_certificate_trust(self, trust: bool) -> IResourceWithEnvironment:
        """Configures developer certificate trust"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["trust"] = serialize_value(trust)
        return self._client.invoke_capability("Aspire.Hosting/withDeveloperCertificateTrust", args)

    def with_certificate_trust_scope(self, scope: CertificateTrustScope) -> IResourceWithEnvironment:
        """Sets the certificate trust scope"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["scope"] = serialize_value(scope)
        return self._client.invoke_capability("Aspire.Hosting/withCertificateTrustScope", args)

    def with_https_developer_certificate(self, password: ParameterResource | None = None) -> IResourceWithEnvironment:
        """Configures HTTPS with a developer certificate"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if password is not None:
            args["password"] = serialize_value(password)
        return self._client.invoke_capability("Aspire.Hosting/withHttpsDeveloperCertificate", args)

    def without_https_certificate(self) -> IResourceWithEnvironment:
        """Removes HTTPS certificate configuration"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withoutHttpsCertificate", args)

    def with_parent_relationship(self, parent: IResource) -> IResource:
        """Sets the parent relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["parent"] = serialize_value(parent)
        return self._client.invoke_capability("Aspire.Hosting/withParentRelationship", args)

    def with_child_relationship(self, child: IResource) -> IResource:
        """Sets a child relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["child"] = serialize_value(child)
        return self._client.invoke_capability("Aspire.Hosting/withChildRelationship", args)

    def with_icon_name(self, icon_name: str, icon_variant: IconVariant = None) -> IResource:
        """Sets the icon for the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["iconName"] = serialize_value(icon_name)
        args["iconVariant"] = serialize_value(icon_variant)
        return self._client.invoke_capability("Aspire.Hosting/withIconName", args)

    def with_http_probe(self, probe_type: ProbeType, path: str | None = None, initial_delay_seconds: float | None = None, period_seconds: float | None = None, timeout_seconds: float | None = None, failure_threshold: float | None = None, success_threshold: float | None = None, endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Adds an HTTP health probe to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["probeType"] = serialize_value(probe_type)
        if path is not None:
            args["path"] = serialize_value(path)
        if initial_delay_seconds is not None:
            args["initialDelaySeconds"] = serialize_value(initial_delay_seconds)
        if period_seconds is not None:
            args["periodSeconds"] = serialize_value(period_seconds)
        if timeout_seconds is not None:
            args["timeoutSeconds"] = serialize_value(timeout_seconds)
        if failure_threshold is not None:
            args["failureThreshold"] = serialize_value(failure_threshold)
        if success_threshold is not None:
            args["successThreshold"] = serialize_value(success_threshold)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withHttpProbe", args)

    def exclude_from_mcp(self) -> IResource:
        """Excludes the resource from MCP server exposure"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)

    def with_remote_image_name(self, remote_image_name: str) -> IComputeResource:
        """Sets the remote image name for publishing"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["remoteImageName"] = serialize_value(remote_image_name)
        return self._client.invoke_capability("Aspire.Hosting/withRemoteImageName", args)

    def with_remote_image_tag(self, remote_image_tag: str) -> IComputeResource:
        """Sets the remote image tag for publishing"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["remoteImageTag"] = serialize_value(remote_image_tag)
        return self._client.invoke_capability("Aspire.Hosting/withRemoteImageTag", args)

    def with_pipeline_step_factory(self, step_name: str, callback: Callable[[PipelineStepContext], None], depends_on: list[str] | None = None, required_by: list[str] | None = None, tags: list[str] | None = None, description: str | None = None) -> IResource:
        """Adds a pipeline step to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["stepName"] = serialize_value(step_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        if depends_on is not None:
            args["dependsOn"] = serialize_value(depends_on)
        if required_by is not None:
            args["requiredBy"] = serialize_value(required_by)
        if tags is not None:
            args["tags"] = serialize_value(tags)
        if description is not None:
            args["description"] = serialize_value(description)
        return self._client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)

    def with_pipeline_configuration_async(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via an async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfigurationAsync", args)

    def with_pipeline_configuration(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via a callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)

    def get_resource_name(self) -> str:
        """Gets the resource name"""
        args: Dict[str, Any] = { "resource": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getResourceName", args)

    def on_before_resource_started(self, callback: Callable[[BeforeResourceStartedEvent], None]) -> IResource:
        """Subscribes to the BeforeResourceStarted event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)

    def on_resource_stopped(self, callback: Callable[[ResourceStoppedEvent], None]) -> IResource:
        """Subscribes to the ResourceStopped event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceStopped", args)

    def on_initialize_resource(self, callback: Callable[[InitializeResourceEvent], None]) -> IResource:
        """Subscribes to the InitializeResource event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onInitializeResource", args)

    def on_resource_endpoints_allocated(self, callback: Callable[[ResourceEndpointsAllocatedEvent], None]) -> IResourceWithEndpoints:
        """Subscribes to the ResourceEndpointsAllocated event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceEndpointsAllocated", args)

    def on_resource_ready(self, callback: Callable[[ResourceReadyEvent], None]) -> IResource:
        """Subscribes to the ResourceReady event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceReady", args)

    def with_optional_string(self, value: str | None = None, enabled: bool = True) -> IResource:
        """Adds an optional string parameter"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if value is not None:
            args["value"] = serialize_value(value)
        args["enabled"] = serialize_value(enabled)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalString", args)

    def with_config(self, config: TestConfigDto) -> IResource:
        """Configures the resource with a DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConfig", args)

    def test_with_environment_callback(self, callback: Callable[[TestEnvironmentContext], None]) -> IResourceWithEnvironment:
        """Configures environment with callback (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWithEnvironmentCallback", args)

    def with_created_at(self, created_at: str) -> IResource:
        """Sets the created timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["createdAt"] = serialize_value(created_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCreatedAt", args)

    def with_modified_at(self, modified_at: str) -> IResource:
        """Sets the modified timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["modifiedAt"] = serialize_value(modified_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withModifiedAt", args)

    def with_correlation_id(self, correlation_id: str) -> IResource:
        """Sets the correlation ID"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["correlationId"] = serialize_value(correlation_id)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCorrelationId", args)

    def with_optional_callback(self, callback: Callable[[TestCallbackContext], None] | None = None) -> IResource:
        """Configures with optional callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalCallback", args)

    def with_status(self, status: TestResourceStatus) -> IResource:
        """Sets the resource status"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["status"] = serialize_value(status)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withStatus", args)

    def with_nested_config(self, config: TestNestedDto) -> IResource:
        """Configures with nested DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withNestedConfig", args)

    def with_validator(self, validator: Callable[[TestResourceContext], bool]) -> IResource:
        """Adds validation callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        validator_id = register_callback(validator) if validator is not None else None
        if validator_id is not None:
            args["validator"] = validator_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withValidator", args)

    def test_wait_for(self, dependency: IResource) -> IResource:
        """Waits for another resource (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWaitFor", args)

    def with_dependency(self, dependency: IResourceWithConnectionString) -> IResource:
        """Adds a dependency on another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withDependency", args)

    def with_endpoints(self, endpoints: list[str]) -> IResource:
        """Sets the endpoints"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpoints"] = serialize_value(endpoints)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEndpoints", args)

    def with_environment_variables(self, variables: dict[str, str]) -> IResourceWithEnvironment:
        """Sets environment variables"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["variables"] = serialize_value(variables)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEnvironmentVariables", args)

    def with_cancellable_operation(self, operation: Callable[[CancellationToken], None]) -> IResource:
        """Performs a cancellable operation"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        operation_id = register_callback(operation) if operation is not None else None
        if operation_id is not None:
            args["operation"] = operation_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCancellableOperation", args)


class EndpointReference(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def resource(self) -> IResourceWithEndpoints:
        """Gets the Resource property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.resource", args)

    def endpoint_name(self) -> str:
        """Gets the EndpointName property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.endpointName", args)

    def error_message(self) -> str:
        """Gets the ErrorMessage property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.errorMessage", args)

    def set_error_message(self, value: str) -> EndpointReference:
        """Sets the ErrorMessage property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.setErrorMessage", args)

    def is_allocated(self) -> bool:
        """Gets the IsAllocated property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.isAllocated", args)

    def exists(self) -> bool:
        """Gets the Exists property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.exists", args)

    def is_http(self) -> bool:
        """Gets the IsHttp property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.isHttp", args)

    def is_https(self) -> bool:
        """Gets the IsHttps property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.isHttps", args)

    def tls_enabled(self) -> bool:
        """Gets the TlsEnabled property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.tlsEnabled", args)

    def port(self) -> float:
        """Gets the Port property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.port", args)

    def target_port(self) -> float:
        """Gets the TargetPort property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.targetPort", args)

    def host(self) -> str:
        """Gets the Host property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.host", args)

    def scheme(self) -> str:
        """Gets the Scheme property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.scheme", args)

    def url(self) -> str:
        """Gets the Url property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.url", args)

    def get_value_async(self, cancellation_token: CancellationToken | None = None) -> str:
        """Gets the URL of the endpoint asynchronously"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        cancellation_token_id = register_cancellation(cancellation_token, self._client) if cancellation_token is not None else None
        if cancellation_token_id is not None:
            args["cancellationToken"] = cancellation_token_id
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/getValueAsync", args)

    def get_tls_value(self, enabled_value: ReferenceExpression, disabled_value: ReferenceExpression) -> ReferenceExpression:
        """Gets a conditional expression that resolves to the enabledValue when TLS is enabled on the endpoint, or to the disabledValue otherwise."""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["enabledValue"] = serialize_value(enabled_value)
        args["disabledValue"] = serialize_value(disabled_value)
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.getTlsValue", args)


class EndpointReferenceExpression(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def endpoint(self) -> EndpointReference:
        """Gets the Endpoint property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReferenceExpression.endpoint", args)

    def property(self) -> EndpointProperty:
        """Gets the Property property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReferenceExpression.property", args)

    def value_expression(self) -> str:
        """Gets the ValueExpression property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReferenceExpression.valueExpression", args)


class EnvironmentCallbackContext(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    @property
    def environment_variables(self) -> AspireDict[str, str | ReferenceExpression]:
        """Gets the EnvironmentVariables property"""
        if not hasattr(self, '_environment_variables'):
            self._environment_variables = AspireDict(
                self._handle,
                self._client,
                "Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.environmentVariables"
            )
        return self._environment_variables

    def cancellation_token(self) -> CancellationToken:
        """Gets the CancellationToken property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.cancellationToken", args)

    def logger(self) -> ILogger:
        """Gets the Logger property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.logger", args)

    def set_logger(self, value: ILogger) -> EnvironmentCallbackContext:
        """Sets the Logger property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.setLogger", args)

    def resource(self) -> IResource:
        """Gets the Resource property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.resource", args)

    def execution_context(self) -> DistributedApplicationExecutionContext:
        """Gets the ExecutionContext property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.executionContext", args)


class ExecutableResource(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def with_container_registry(self, registry: IResource) -> IResource:
        """Configures a resource to use a container registry"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["registry"] = serialize_value(registry)
        return self._client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)

    def with_dockerfile_base_image(self, build_image: str | None = None, runtime_image: str | None = None) -> IResource:
        """Sets the base image for a Dockerfile build"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if build_image is not None:
            args["buildImage"] = serialize_value(build_image)
        if runtime_image is not None:
            args["runtimeImage"] = serialize_value(runtime_image)
        return self._client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)

    def publish_as_docker_file(self) -> ExecutableResource:
        """Publishes the executable as a Docker container"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/publishAsDockerFile", args)

    def publish_as_docker_file_with_configure(self, configure: Callable[[ContainerResource], None]) -> ExecutableResource:
        """Publishes an executable as a Docker file with optional container configuration"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        configure_id = register_callback(configure) if configure is not None else None
        if configure_id is not None:
            args["configure"] = configure_id
        return self._client.invoke_capability("Aspire.Hosting/publishAsDockerFileWithConfigure", args)

    def with_executable_command(self, command: str) -> ExecutableResource:
        """Sets the executable command"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["command"] = serialize_value(command)
        return self._client.invoke_capability("Aspire.Hosting/withExecutableCommand", args)

    def with_working_directory(self, working_directory: str) -> ExecutableResource:
        """Sets the executable working directory"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["workingDirectory"] = serialize_value(working_directory)
        return self._client.invoke_capability("Aspire.Hosting/withWorkingDirectory", args)

    def with_mcp_server(self, path: str = "/mcp", endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Configures an MCP server endpoint on the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["path"] = serialize_value(path)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withMcpServer", args)

    def with_otlp_exporter(self) -> IResourceWithEnvironment:
        """Configures OTLP telemetry export"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withOtlpExporter", args)

    def with_otlp_exporter_protocol(self, protocol: OtlpProtocol) -> IResourceWithEnvironment:
        """Configures OTLP telemetry export with specific protocol"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["protocol"] = serialize_value(protocol)
        return self._client.invoke_capability("Aspire.Hosting/withOtlpExporterProtocol", args)

    def with_required_command(self, command: str, help_link: str | None = None) -> IResource:
        """Adds a required command dependency"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["command"] = serialize_value(command)
        if help_link is not None:
            args["helpLink"] = serialize_value(help_link)
        return self._client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)

    def with_environment(self, name: str, value: str) -> IResourceWithEnvironment:
        """Sets an environment variable"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironment", args)

    def with_environment_expression(self, name: str, value: ReferenceExpression) -> IResourceWithEnvironment:
        """Adds an environment variable with a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentExpression", args)

    def with_environment_callback(self, callback: Callable[[EnvironmentCallbackContext], None]) -> IResourceWithEnvironment:
        """Sets environment variables via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentCallback", args)

    def with_environment_callback_async(self, callback: Callable[[EnvironmentCallbackContext], None]) -> IResourceWithEnvironment:
        """Sets environment variables via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentCallbackAsync", args)

    def with_environment_endpoint(self, name: str, endpoint_reference: EndpointReference) -> IResourceWithEnvironment:
        """Sets an environment variable from an endpoint reference"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["endpointReference"] = serialize_value(endpoint_reference)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentEndpoint", args)

    def with_environment_parameter(self, name: str, parameter: ParameterResource) -> IResourceWithEnvironment:
        """Sets an environment variable from a parameter resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["parameter"] = serialize_value(parameter)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentParameter", args)

    def with_environment_connection_string(self, env_var_name: str, resource: IResourceWithConnectionString) -> IResourceWithEnvironment:
        """Sets an environment variable from a connection string resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["envVarName"] = serialize_value(env_var_name)
        args["resource"] = serialize_value(resource)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentConnectionString", args)

    def with_args(self, args: list[str]) -> IResourceWithArgs:
        """Adds arguments"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["args"] = serialize_value(args)
        return self._client.invoke_capability("Aspire.Hosting/withArgs", args)

    def with_args_callback(self, callback: Callable[[CommandLineArgsCallbackContext], None]) -> IResourceWithArgs:
        """Sets command-line arguments via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withArgsCallback", args)

    def with_args_callback_async(self, callback: Callable[[CommandLineArgsCallbackContext], None]) -> IResourceWithArgs:
        """Sets command-line arguments via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withArgsCallbackAsync", args)

    def with_reference(self, source: IResource, connection_name: str | None = None, optional: bool = False, name: str | None = None) -> IResourceWithEnvironment:
        """Adds a reference to another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["source"] = serialize_value(source)
        if connection_name is not None:
            args["connectionName"] = serialize_value(connection_name)
        args["optional"] = serialize_value(optional)
        if name is not None:
            args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/withReference", args)

    def with_reference_uri(self, name: str, uri: str) -> IResourceWithEnvironment:
        """Adds a reference to a URI"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["uri"] = serialize_value(uri)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceUri", args)

    def with_reference_external_service(self, external_service: ExternalServiceResource) -> IResourceWithEnvironment:
        """Adds a reference to an external service"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["externalService"] = serialize_value(external_service)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceExternalService", args)

    def with_reference_endpoint(self, endpoint_reference: EndpointReference) -> IResourceWithEnvironment:
        """Adds a reference to an endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointReference"] = serialize_value(endpoint_reference)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceEndpoint", args)

    def with_endpoint(self, port: float | None = None, target_port: float | None = None, scheme: str | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True, is_external: bool | None = None, protocol: ProtocolType | None = None) -> IResourceWithEndpoints:
        """Adds a network endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if scheme is not None:
            args["scheme"] = serialize_value(scheme)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        if is_external is not None:
            args["isExternal"] = serialize_value(is_external)
        if protocol is not None:
            args["protocol"] = serialize_value(protocol)
        return self._client.invoke_capability("Aspire.Hosting/withEndpoint", args)

    def with_http_endpoint(self, port: float | None = None, target_port: float | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> IResourceWithEndpoints:
        """Adds an HTTP endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        return self._client.invoke_capability("Aspire.Hosting/withHttpEndpoint", args)

    def with_https_endpoint(self, port: float | None = None, target_port: float | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> IResourceWithEndpoints:
        """Adds an HTTPS endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        return self._client.invoke_capability("Aspire.Hosting/withHttpsEndpoint", args)

    def with_external_http_endpoints(self) -> IResourceWithEndpoints:
        """Makes HTTP endpoints externally accessible"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExternalHttpEndpoints", args)

    def get_endpoint(self, name: str) -> EndpointReference:
        """Gets an endpoint reference"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/getEndpoint", args)

    def as_http2_service(self) -> IResourceWithEndpoints:
        """Configures resource for HTTP/2"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/asHttp2Service", args)

    def with_urls_callback(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)

    def with_urls_callback_async(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallbackAsync", args)

    def with_url(self, url: str, display_text: str | None = None) -> IResource:
        """Adds or modifies displayed URLs"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrl", args)

    def with_url_expression(self, url: ReferenceExpression, display_text: str | None = None) -> IResource:
        """Adds a URL using a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrlExpression", args)

    def with_url_for_endpoint(self, endpoint_name: str, callback: Callable[[ResourceUrlAnnotation], None]) -> IResource:
        """Customizes the URL for a specific endpoint via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)

    def with_url_for_endpoint_factory(self, endpoint_name: str, callback: Callable[[EndpointReference], ResourceUrlAnnotation]) -> IResourceWithEndpoints:
        """Adds a URL for a specific endpoint via factory callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpointFactory", args)

    def exclude_from_manifest(self) -> IResource:
        """Excludes the resource from the deployment manifest"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)

    def wait_for(self, dependency: IResource) -> IResourceWithWaitSupport:
        """Waits for another resource to be ready"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting/waitFor", args)

    def wait_for_with_behavior(self, dependency: IResource, wait_behavior: WaitBehavior) -> IResourceWithWaitSupport:
        """Waits for another resource with specific behavior"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["waitBehavior"] = serialize_value(wait_behavior)
        return self._client.invoke_capability("Aspire.Hosting/waitForWithBehavior", args)

    def wait_for_start(self, dependency: IResource) -> IResourceWithWaitSupport:
        """Waits for another resource to start"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting/waitForStart", args)

    def wait_for_start_with_behavior(self, dependency: IResource, wait_behavior: WaitBehavior) -> IResourceWithWaitSupport:
        """Waits for another resource to start with specific behavior"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["waitBehavior"] = serialize_value(wait_behavior)
        return self._client.invoke_capability("Aspire.Hosting/waitForStartWithBehavior", args)

    def with_explicit_start(self) -> IResource:
        """Prevents resource from starting automatically"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExplicitStart", args)

    def wait_for_completion(self, dependency: IResource, exit_code: float = 0) -> IResourceWithWaitSupport:
        """Waits for resource completion"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["exitCode"] = serialize_value(exit_code)
        return self._client.invoke_capability("Aspire.Hosting/waitForCompletion", args)

    def with_health_check(self, key: str) -> IResource:
        """Adds a health check by key"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["key"] = serialize_value(key)
        return self._client.invoke_capability("Aspire.Hosting/withHealthCheck", args)

    def with_http_health_check(self, path: str | None = None, status_code: float | None = None, endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Adds an HTTP health check"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if path is not None:
            args["path"] = serialize_value(path)
        if status_code is not None:
            args["statusCode"] = serialize_value(status_code)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withHttpHealthCheck", args)

    def with_command(self, name: str, display_name: str, execute_command: Callable[[ExecuteCommandContext], ExecuteCommandResult], command_options: CommandOptions | None = None) -> IResource:
        """Adds a resource command"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["displayName"] = serialize_value(display_name)
        execute_command_id = register_callback(execute_command) if execute_command is not None else None
        if execute_command_id is not None:
            args["executeCommand"] = execute_command_id
        if command_options is not None:
            args["commandOptions"] = serialize_value(command_options)
        return self._client.invoke_capability("Aspire.Hosting/withCommand", args)

    def with_developer_certificate_trust(self, trust: bool) -> IResourceWithEnvironment:
        """Configures developer certificate trust"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["trust"] = serialize_value(trust)
        return self._client.invoke_capability("Aspire.Hosting/withDeveloperCertificateTrust", args)

    def with_certificate_trust_scope(self, scope: CertificateTrustScope) -> IResourceWithEnvironment:
        """Sets the certificate trust scope"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["scope"] = serialize_value(scope)
        return self._client.invoke_capability("Aspire.Hosting/withCertificateTrustScope", args)

    def with_https_developer_certificate(self, password: ParameterResource | None = None) -> IResourceWithEnvironment:
        """Configures HTTPS with a developer certificate"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if password is not None:
            args["password"] = serialize_value(password)
        return self._client.invoke_capability("Aspire.Hosting/withHttpsDeveloperCertificate", args)

    def without_https_certificate(self) -> IResourceWithEnvironment:
        """Removes HTTPS certificate configuration"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withoutHttpsCertificate", args)

    def with_parent_relationship(self, parent: IResource) -> IResource:
        """Sets the parent relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["parent"] = serialize_value(parent)
        return self._client.invoke_capability("Aspire.Hosting/withParentRelationship", args)

    def with_child_relationship(self, child: IResource) -> IResource:
        """Sets a child relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["child"] = serialize_value(child)
        return self._client.invoke_capability("Aspire.Hosting/withChildRelationship", args)

    def with_icon_name(self, icon_name: str, icon_variant: IconVariant = None) -> IResource:
        """Sets the icon for the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["iconName"] = serialize_value(icon_name)
        args["iconVariant"] = serialize_value(icon_variant)
        return self._client.invoke_capability("Aspire.Hosting/withIconName", args)

    def with_http_probe(self, probe_type: ProbeType, path: str | None = None, initial_delay_seconds: float | None = None, period_seconds: float | None = None, timeout_seconds: float | None = None, failure_threshold: float | None = None, success_threshold: float | None = None, endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Adds an HTTP health probe to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["probeType"] = serialize_value(probe_type)
        if path is not None:
            args["path"] = serialize_value(path)
        if initial_delay_seconds is not None:
            args["initialDelaySeconds"] = serialize_value(initial_delay_seconds)
        if period_seconds is not None:
            args["periodSeconds"] = serialize_value(period_seconds)
        if timeout_seconds is not None:
            args["timeoutSeconds"] = serialize_value(timeout_seconds)
        if failure_threshold is not None:
            args["failureThreshold"] = serialize_value(failure_threshold)
        if success_threshold is not None:
            args["successThreshold"] = serialize_value(success_threshold)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withHttpProbe", args)

    def exclude_from_mcp(self) -> IResource:
        """Excludes the resource from MCP server exposure"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)

    def with_remote_image_name(self, remote_image_name: str) -> IComputeResource:
        """Sets the remote image name for publishing"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["remoteImageName"] = serialize_value(remote_image_name)
        return self._client.invoke_capability("Aspire.Hosting/withRemoteImageName", args)

    def with_remote_image_tag(self, remote_image_tag: str) -> IComputeResource:
        """Sets the remote image tag for publishing"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["remoteImageTag"] = serialize_value(remote_image_tag)
        return self._client.invoke_capability("Aspire.Hosting/withRemoteImageTag", args)

    def with_pipeline_step_factory(self, step_name: str, callback: Callable[[PipelineStepContext], None], depends_on: list[str] | None = None, required_by: list[str] | None = None, tags: list[str] | None = None, description: str | None = None) -> IResource:
        """Adds a pipeline step to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["stepName"] = serialize_value(step_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        if depends_on is not None:
            args["dependsOn"] = serialize_value(depends_on)
        if required_by is not None:
            args["requiredBy"] = serialize_value(required_by)
        if tags is not None:
            args["tags"] = serialize_value(tags)
        if description is not None:
            args["description"] = serialize_value(description)
        return self._client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)

    def with_pipeline_configuration_async(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via an async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfigurationAsync", args)

    def with_pipeline_configuration(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via a callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)

    def get_resource_name(self) -> str:
        """Gets the resource name"""
        args: Dict[str, Any] = { "resource": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getResourceName", args)

    def on_before_resource_started(self, callback: Callable[[BeforeResourceStartedEvent], None]) -> IResource:
        """Subscribes to the BeforeResourceStarted event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)

    def on_resource_stopped(self, callback: Callable[[ResourceStoppedEvent], None]) -> IResource:
        """Subscribes to the ResourceStopped event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceStopped", args)

    def on_initialize_resource(self, callback: Callable[[InitializeResourceEvent], None]) -> IResource:
        """Subscribes to the InitializeResource event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onInitializeResource", args)

    def on_resource_endpoints_allocated(self, callback: Callable[[ResourceEndpointsAllocatedEvent], None]) -> IResourceWithEndpoints:
        """Subscribes to the ResourceEndpointsAllocated event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceEndpointsAllocated", args)

    def on_resource_ready(self, callback: Callable[[ResourceReadyEvent], None]) -> IResource:
        """Subscribes to the ResourceReady event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceReady", args)

    def with_optional_string(self, value: str | None = None, enabled: bool = True) -> IResource:
        """Adds an optional string parameter"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if value is not None:
            args["value"] = serialize_value(value)
        args["enabled"] = serialize_value(enabled)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalString", args)

    def with_config(self, config: TestConfigDto) -> IResource:
        """Configures the resource with a DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConfig", args)

    def test_with_environment_callback(self, callback: Callable[[TestEnvironmentContext], None]) -> IResourceWithEnvironment:
        """Configures environment with callback (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWithEnvironmentCallback", args)

    def with_created_at(self, created_at: str) -> IResource:
        """Sets the created timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["createdAt"] = serialize_value(created_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCreatedAt", args)

    def with_modified_at(self, modified_at: str) -> IResource:
        """Sets the modified timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["modifiedAt"] = serialize_value(modified_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withModifiedAt", args)

    def with_correlation_id(self, correlation_id: str) -> IResource:
        """Sets the correlation ID"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["correlationId"] = serialize_value(correlation_id)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCorrelationId", args)

    def with_optional_callback(self, callback: Callable[[TestCallbackContext], None] | None = None) -> IResource:
        """Configures with optional callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalCallback", args)

    def with_status(self, status: TestResourceStatus) -> IResource:
        """Sets the resource status"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["status"] = serialize_value(status)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withStatus", args)

    def with_nested_config(self, config: TestNestedDto) -> IResource:
        """Configures with nested DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withNestedConfig", args)

    def with_validator(self, validator: Callable[[TestResourceContext], bool]) -> IResource:
        """Adds validation callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        validator_id = register_callback(validator) if validator is not None else None
        if validator_id is not None:
            args["validator"] = validator_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withValidator", args)

    def test_wait_for(self, dependency: IResource) -> IResource:
        """Waits for another resource (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWaitFor", args)

    def with_dependency(self, dependency: IResourceWithConnectionString) -> IResource:
        """Adds a dependency on another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withDependency", args)

    def with_endpoints(self, endpoints: list[str]) -> IResource:
        """Sets the endpoints"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpoints"] = serialize_value(endpoints)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEndpoints", args)

    def with_environment_variables(self, variables: dict[str, str]) -> IResourceWithEnvironment:
        """Sets environment variables"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["variables"] = serialize_value(variables)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEnvironmentVariables", args)

    def with_cancellable_operation(self, operation: Callable[[CancellationToken], None]) -> IResource:
        """Performs a cancellable operation"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        operation_id = register_callback(operation) if operation is not None else None
        if operation_id is not None:
            args["operation"] = operation_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCancellableOperation", args)


class ExecuteCommandContext(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def service_provider(self) -> IServiceProvider:
        """Gets the ServiceProvider property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.serviceProvider", args)

    def set_service_provider(self, value: IServiceProvider) -> ExecuteCommandContext:
        """Sets the ServiceProvider property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.setServiceProvider", args)

    def resource_name(self) -> str:
        """Gets the ResourceName property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.resourceName", args)

    def set_resource_name(self, value: str) -> ExecuteCommandContext:
        """Sets the ResourceName property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.setResourceName", args)

    def cancellation_token(self) -> CancellationToken:
        """Gets the CancellationToken property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.cancellationToken", args)

    def set_cancellation_token(self, value: CancellationToken) -> ExecuteCommandContext:
        """Sets the CancellationToken property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        value_id = register_cancellation(value, self._client) if value is not None else None
        if value_id is not None:
            args["value"] = value_id
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.setCancellationToken", args)


class ExternalServiceResource(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def with_container_registry(self, registry: IResource) -> IResource:
        """Configures a resource to use a container registry"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["registry"] = serialize_value(registry)
        return self._client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)

    def with_dockerfile_base_image(self, build_image: str | None = None, runtime_image: str | None = None) -> IResource:
        """Sets the base image for a Dockerfile build"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if build_image is not None:
            args["buildImage"] = serialize_value(build_image)
        if runtime_image is not None:
            args["runtimeImage"] = serialize_value(runtime_image)
        return self._client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)

    def with_external_service_http_health_check(self, path: str | None = None, status_code: float | None = None) -> ExternalServiceResource:
        """Adds an HTTP health check to an external service"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if path is not None:
            args["path"] = serialize_value(path)
        if status_code is not None:
            args["statusCode"] = serialize_value(status_code)
        return self._client.invoke_capability("Aspire.Hosting/withExternalServiceHttpHealthCheck", args)

    def with_required_command(self, command: str, help_link: str | None = None) -> IResource:
        """Adds a required command dependency"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["command"] = serialize_value(command)
        if help_link is not None:
            args["helpLink"] = serialize_value(help_link)
        return self._client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)

    def with_urls_callback(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)

    def with_urls_callback_async(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallbackAsync", args)

    def with_url(self, url: str, display_text: str | None = None) -> IResource:
        """Adds or modifies displayed URLs"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrl", args)

    def with_url_expression(self, url: ReferenceExpression, display_text: str | None = None) -> IResource:
        """Adds a URL using a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrlExpression", args)

    def with_url_for_endpoint(self, endpoint_name: str, callback: Callable[[ResourceUrlAnnotation], None]) -> IResource:
        """Customizes the URL for a specific endpoint via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)

    def exclude_from_manifest(self) -> IResource:
        """Excludes the resource from the deployment manifest"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)

    def with_explicit_start(self) -> IResource:
        """Prevents resource from starting automatically"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExplicitStart", args)

    def with_health_check(self, key: str) -> IResource:
        """Adds a health check by key"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["key"] = serialize_value(key)
        return self._client.invoke_capability("Aspire.Hosting/withHealthCheck", args)

    def with_command(self, name: str, display_name: str, execute_command: Callable[[ExecuteCommandContext], ExecuteCommandResult], command_options: CommandOptions | None = None) -> IResource:
        """Adds a resource command"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["displayName"] = serialize_value(display_name)
        execute_command_id = register_callback(execute_command) if execute_command is not None else None
        if execute_command_id is not None:
            args["executeCommand"] = execute_command_id
        if command_options is not None:
            args["commandOptions"] = serialize_value(command_options)
        return self._client.invoke_capability("Aspire.Hosting/withCommand", args)

    def with_parent_relationship(self, parent: IResource) -> IResource:
        """Sets the parent relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["parent"] = serialize_value(parent)
        return self._client.invoke_capability("Aspire.Hosting/withParentRelationship", args)

    def with_child_relationship(self, child: IResource) -> IResource:
        """Sets a child relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["child"] = serialize_value(child)
        return self._client.invoke_capability("Aspire.Hosting/withChildRelationship", args)

    def with_icon_name(self, icon_name: str, icon_variant: IconVariant = None) -> IResource:
        """Sets the icon for the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["iconName"] = serialize_value(icon_name)
        args["iconVariant"] = serialize_value(icon_variant)
        return self._client.invoke_capability("Aspire.Hosting/withIconName", args)

    def exclude_from_mcp(self) -> IResource:
        """Excludes the resource from MCP server exposure"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)

    def with_pipeline_step_factory(self, step_name: str, callback: Callable[[PipelineStepContext], None], depends_on: list[str] | None = None, required_by: list[str] | None = None, tags: list[str] | None = None, description: str | None = None) -> IResource:
        """Adds a pipeline step to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["stepName"] = serialize_value(step_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        if depends_on is not None:
            args["dependsOn"] = serialize_value(depends_on)
        if required_by is not None:
            args["requiredBy"] = serialize_value(required_by)
        if tags is not None:
            args["tags"] = serialize_value(tags)
        if description is not None:
            args["description"] = serialize_value(description)
        return self._client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)

    def with_pipeline_configuration_async(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via an async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfigurationAsync", args)

    def with_pipeline_configuration(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via a callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)

    def get_resource_name(self) -> str:
        """Gets the resource name"""
        args: Dict[str, Any] = { "resource": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getResourceName", args)

    def on_before_resource_started(self, callback: Callable[[BeforeResourceStartedEvent], None]) -> IResource:
        """Subscribes to the BeforeResourceStarted event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)

    def on_resource_stopped(self, callback: Callable[[ResourceStoppedEvent], None]) -> IResource:
        """Subscribes to the ResourceStopped event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceStopped", args)

    def on_initialize_resource(self, callback: Callable[[InitializeResourceEvent], None]) -> IResource:
        """Subscribes to the InitializeResource event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onInitializeResource", args)

    def on_resource_ready(self, callback: Callable[[ResourceReadyEvent], None]) -> IResource:
        """Subscribes to the ResourceReady event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceReady", args)

    def with_optional_string(self, value: str | None = None, enabled: bool = True) -> IResource:
        """Adds an optional string parameter"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if value is not None:
            args["value"] = serialize_value(value)
        args["enabled"] = serialize_value(enabled)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalString", args)

    def with_config(self, config: TestConfigDto) -> IResource:
        """Configures the resource with a DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConfig", args)

    def with_created_at(self, created_at: str) -> IResource:
        """Sets the created timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["createdAt"] = serialize_value(created_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCreatedAt", args)

    def with_modified_at(self, modified_at: str) -> IResource:
        """Sets the modified timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["modifiedAt"] = serialize_value(modified_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withModifiedAt", args)

    def with_correlation_id(self, correlation_id: str) -> IResource:
        """Sets the correlation ID"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["correlationId"] = serialize_value(correlation_id)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCorrelationId", args)

    def with_optional_callback(self, callback: Callable[[TestCallbackContext], None] | None = None) -> IResource:
        """Configures with optional callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalCallback", args)

    def with_status(self, status: TestResourceStatus) -> IResource:
        """Sets the resource status"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["status"] = serialize_value(status)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withStatus", args)

    def with_nested_config(self, config: TestNestedDto) -> IResource:
        """Configures with nested DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withNestedConfig", args)

    def with_validator(self, validator: Callable[[TestResourceContext], bool]) -> IResource:
        """Adds validation callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        validator_id = register_callback(validator) if validator is not None else None
        if validator_id is not None:
            args["validator"] = validator_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withValidator", args)

    def test_wait_for(self, dependency: IResource) -> IResource:
        """Waits for another resource (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWaitFor", args)

    def with_dependency(self, dependency: IResourceWithConnectionString) -> IResource:
        """Adds a dependency on another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withDependency", args)

    def with_endpoints(self, endpoints: list[str]) -> IResource:
        """Sets the endpoints"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpoints"] = serialize_value(endpoints)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEndpoints", args)

    def with_cancellable_operation(self, operation: Callable[[CancellationToken], None]) -> IResource:
        """Performs a cancellable operation"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        operation_id = register_callback(operation) if operation is not None else None
        if operation_id is not None:
            args["operation"] = operation_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCancellableOperation", args)


class IComputeResource(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class IConfiguration(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def get_config_value(self, key: str) -> str:
        """Gets a configuration value by key"""
        args: Dict[str, Any] = { "configuration": serialize_value(self._handle) }
        args["key"] = serialize_value(key)
        return self._client.invoke_capability("Aspire.Hosting/getConfigValue", args)

    def get_connection_string(self, name: str) -> str:
        """Gets a connection string by name"""
        args: Dict[str, Any] = { "configuration": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/getConnectionString", args)

    def get_section(self, key: str) -> IConfigurationSection:
        """Gets a configuration section by key"""
        args: Dict[str, Any] = { "configuration": serialize_value(self._handle) }
        args["key"] = serialize_value(key)
        return self._client.invoke_capability("Aspire.Hosting/getSection", args)

    def get_children(self) -> list[IConfigurationSection]:
        """Gets child configuration sections"""
        args: Dict[str, Any] = { "configuration": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getChildren", args)

    def exists(self, key: str) -> bool:
        """Checks whether a configuration section exists"""
        args: Dict[str, Any] = { "configuration": serialize_value(self._handle) }
        args["key"] = serialize_value(key)
        return self._client.invoke_capability("Aspire.Hosting/exists", args)


class IConfigurationSection(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class IContainerFilesDestinationResource(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class IDistributedApplicationBuilder(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def add_connection_string_expression(self, name: str, connection_string_expression: ReferenceExpression) -> ConnectionStringResource:
        """Adds a connection string with a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["connectionStringExpression"] = serialize_value(connection_string_expression)
        return self._client.invoke_capability("Aspire.Hosting/addConnectionStringExpression", args)

    def add_connection_string_builder(self, name: str, connection_string_builder: Callable[[ReferenceExpressionBuilder], None]) -> ConnectionStringResource:
        """Adds a connection string with a builder callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        connection_string_builder_id = register_callback(connection_string_builder) if connection_string_builder is not None else None
        if connection_string_builder_id is not None:
            args["connectionStringBuilder"] = connection_string_builder_id
        return self._client.invoke_capability("Aspire.Hosting/addConnectionStringBuilder", args)

    def add_container_registry(self, name: str, endpoint: ParameterResource, repository: ParameterResource | None = None) -> ContainerRegistryResource:
        """Adds a container registry resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["endpoint"] = serialize_value(endpoint)
        if repository is not None:
            args["repository"] = serialize_value(repository)
        return self._client.invoke_capability("Aspire.Hosting/addContainerRegistry", args)

    def add_container_registry_from_string(self, name: str, endpoint: str, repository: str | None = None) -> ContainerRegistryResource:
        """Adds a container registry with string endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["endpoint"] = serialize_value(endpoint)
        if repository is not None:
            args["repository"] = serialize_value(repository)
        return self._client.invoke_capability("Aspire.Hosting/addContainerRegistryFromString", args)

    def add_container(self, name: str, image: str) -> ContainerResource:
        """Adds a container resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["image"] = serialize_value(image)
        return self._client.invoke_capability("Aspire.Hosting/addContainer", args)

    def add_dockerfile(self, name: str, context_path: str, dockerfile_path: str | None = None, stage: str | None = None) -> ContainerResource:
        """Adds a container resource built from a Dockerfile"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["contextPath"] = serialize_value(context_path)
        if dockerfile_path is not None:
            args["dockerfilePath"] = serialize_value(dockerfile_path)
        if stage is not None:
            args["stage"] = serialize_value(stage)
        return self._client.invoke_capability("Aspire.Hosting/addDockerfile", args)

    def add_dotnet_tool(self, name: str, package_id: str) -> DotnetToolResource:
        """Adds a .NET tool resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["packageId"] = serialize_value(package_id)
        return self._client.invoke_capability("Aspire.Hosting/addDotnetTool", args)

    def add_executable(self, name: str, command: str, working_directory: str, args: list[str]) -> ExecutableResource:
        """Adds an executable resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["command"] = serialize_value(command)
        args["workingDirectory"] = serialize_value(working_directory)
        args["args"] = serialize_value(args)
        return self._client.invoke_capability("Aspire.Hosting/addExecutable", args)

    def add_external_service(self, name: str, url: str) -> ExternalServiceResource:
        """Adds an external service resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["url"] = serialize_value(url)
        return self._client.invoke_capability("Aspire.Hosting/addExternalService", args)

    def add_external_service_uri(self, name: str, uri: str) -> ExternalServiceResource:
        """Adds an external service with a URI"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["uri"] = serialize_value(uri)
        return self._client.invoke_capability("Aspire.Hosting/addExternalServiceUri", args)

    def add_external_service_parameter(self, name: str, url_parameter: ParameterResource) -> ExternalServiceResource:
        """Adds an external service with a parameter URL"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["urlParameter"] = serialize_value(url_parameter)
        return self._client.invoke_capability("Aspire.Hosting/addExternalServiceParameter", args)

    def app_host_directory(self) -> str:
        """Gets the AppHostDirectory property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/IDistributedApplicationBuilder.appHostDirectory", args)

    def environment(self) -> IHostEnvironment:
        """Gets the Environment property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/IDistributedApplicationBuilder.environment", args)

    def eventing(self) -> IDistributedApplicationEventing:
        """Gets the Eventing property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/IDistributedApplicationBuilder.eventing", args)

    def execution_context(self) -> DistributedApplicationExecutionContext:
        """Gets the ExecutionContext property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/IDistributedApplicationBuilder.executionContext", args)

    def user_secrets_manager(self) -> IUserSecretsManager:
        """Gets the UserSecretsManager property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/IDistributedApplicationBuilder.userSecretsManager", args)

    def build(self) -> DistributedApplication:
        """Builds the distributed application"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/build", args)

    def add_parameter(self, name: str, secret: bool = False) -> ParameterResource:
        """Adds a parameter resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["secret"] = serialize_value(secret)
        return self._client.invoke_capability("Aspire.Hosting/addParameter", args)

    def add_parameter_with_value(self, name: str, value: str, publish_value_as_default: bool = False, secret: bool = False) -> ParameterResource:
        """Adds a parameter with a default value"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        args["publishValueAsDefault"] = serialize_value(publish_value_as_default)
        args["secret"] = serialize_value(secret)
        return self._client.invoke_capability("Aspire.Hosting/addParameterWithValue", args)

    def add_parameter_from_configuration(self, name: str, configuration_key: str, secret: bool = False) -> ParameterResource:
        """Adds a parameter sourced from configuration"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["configurationKey"] = serialize_value(configuration_key)
        args["secret"] = serialize_value(secret)
        return self._client.invoke_capability("Aspire.Hosting/addParameterFromConfiguration", args)

    def add_connection_string(self, name: str, environment_variable_name: str | None = None) -> IResourceWithConnectionString:
        """Adds a connection string resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        if environment_variable_name is not None:
            args["environmentVariableName"] = serialize_value(environment_variable_name)
        return self._client.invoke_capability("Aspire.Hosting/addConnectionString", args)

    def add_project(self, name: str, project_path: str, launch_profile_name: str) -> ProjectResource:
        """Adds a .NET project resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["projectPath"] = serialize_value(project_path)
        args["launchProfileName"] = serialize_value(launch_profile_name)
        return self._client.invoke_capability("Aspire.Hosting/addProject", args)

    def add_project_with_options(self, name: str, project_path: str, configure: Callable[[ProjectResourceOptions], None]) -> ProjectResource:
        """Adds a project resource with configuration options"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["projectPath"] = serialize_value(project_path)
        configure_id = register_callback(configure) if configure is not None else None
        if configure_id is not None:
            args["configure"] = configure_id
        return self._client.invoke_capability("Aspire.Hosting/addProjectWithOptions", args)

    def add_c_sharp_app(self, name: str, path: str) -> ProjectResource:
        """Adds a C# application resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["path"] = serialize_value(path)
        return self._client.invoke_capability("Aspire.Hosting/addCSharpApp", args)

    def add_c_sharp_app_with_options(self, name: str, path: str, configure: Callable[[ProjectResourceOptions], None]) -> CSharpAppResource:
        """Adds a C# application resource with configuration options"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["path"] = serialize_value(path)
        configure_id = register_callback(configure) if configure is not None else None
        if configure_id is not None:
            args["configure"] = configure_id
        return self._client.invoke_capability("Aspire.Hosting/addCSharpAppWithOptions", args)

    def get_configuration(self) -> IConfiguration:
        """Gets the application configuration"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getConfiguration", args)

    def subscribe_before_start(self, callback: Callable[[BeforeStartEvent], None]) -> DistributedApplicationEventSubscription:
        """Subscribes to the BeforeStart event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/subscribeBeforeStart", args)

    def subscribe_after_resources_created(self, callback: Callable[[AfterResourcesCreatedEvent], None]) -> DistributedApplicationEventSubscription:
        """Subscribes to the AfterResourcesCreated event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/subscribeAfterResourcesCreated", args)

    def add_test_redis(self, name: str, port: float | None = None) -> TestRedisResource:
        """Adds a test Redis resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        if port is not None:
            args["port"] = serialize_value(port)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/addTestRedis", args)

    def add_test_vault(self, name: str) -> TestVaultResource:
        """Adds a test vault resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/addTestVault", args)


class IDistributedApplicationEvent(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class IDistributedApplicationEventing(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def unsubscribe(self, subscription: DistributedApplicationEventSubscription) -> None:
        """Invokes the Unsubscribe method"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["subscription"] = serialize_value(subscription)
        self._client.invoke_capability("Aspire.Hosting.Eventing/IDistributedApplicationEventing.unsubscribe", args)
        return None


class IDistributedApplicationResourceEvent(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class IHostEnvironment(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def is_development(self) -> bool:
        """Checks if running in Development environment"""
        args: Dict[str, Any] = { "environment": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/isDevelopment", args)

    def is_production(self) -> bool:
        """Checks if running in Production environment"""
        args: Dict[str, Any] = { "environment": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/isProduction", args)

    def is_staging(self) -> bool:
        """Checks if running in Staging environment"""
        args: Dict[str, Any] = { "environment": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/isStaging", args)

    def is_environment(self, environment_name: str) -> bool:
        """Checks if the environment matches the specified name"""
        args: Dict[str, Any] = { "environment": serialize_value(self._handle) }
        args["environmentName"] = serialize_value(environment_name)
        return self._client.invoke_capability("Aspire.Hosting/isEnvironment", args)


class ILogger(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def log_information(self, message: str) -> None:
        """Logs an information message"""
        args: Dict[str, Any] = { "logger": serialize_value(self._handle) }
        args["message"] = serialize_value(message)
        self._client.invoke_capability("Aspire.Hosting/logInformation", args)
        return None

    def log_warning(self, message: str) -> None:
        """Logs a warning message"""
        args: Dict[str, Any] = { "logger": serialize_value(self._handle) }
        args["message"] = serialize_value(message)
        self._client.invoke_capability("Aspire.Hosting/logWarning", args)
        return None

    def log_error(self, message: str) -> None:
        """Logs an error message"""
        args: Dict[str, Any] = { "logger": serialize_value(self._handle) }
        args["message"] = serialize_value(message)
        self._client.invoke_capability("Aspire.Hosting/logError", args)
        return None

    def log_debug(self, message: str) -> None:
        """Logs a debug message"""
        args: Dict[str, Any] = { "logger": serialize_value(self._handle) }
        args["message"] = serialize_value(message)
        self._client.invoke_capability("Aspire.Hosting/logDebug", args)
        return None

    def log(self, level: str, message: str) -> None:
        """Logs a message with specified level"""
        args: Dict[str, Any] = { "logger": serialize_value(self._handle) }
        args["level"] = serialize_value(level)
        args["message"] = serialize_value(message)
        self._client.invoke_capability("Aspire.Hosting/log", args)
        return None


class ILoggerFactory(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def create_logger(self, category_name: str) -> ILogger:
        """Creates a logger for a category"""
        args: Dict[str, Any] = { "loggerFactory": serialize_value(self._handle) }
        args["categoryName"] = serialize_value(category_name)
        return self._client.invoke_capability("Aspire.Hosting/createLogger", args)


class IReportingStep(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def create_task(self, status_text: str, cancellation_token: CancellationToken | None = None) -> IReportingTask:
        """Creates a reporting task with plain-text status text"""
        args: Dict[str, Any] = { "reportingStep": serialize_value(self._handle) }
        args["statusText"] = serialize_value(status_text)
        cancellation_token_id = register_cancellation(cancellation_token, self._client) if cancellation_token is not None else None
        if cancellation_token_id is not None:
            args["cancellationToken"] = cancellation_token_id
        return self._client.invoke_capability("Aspire.Hosting/createTask", args)

    def create_markdown_task(self, markdown_string: str, cancellation_token: CancellationToken | None = None) -> IReportingTask:
        """Creates a reporting task with Markdown-formatted status text"""
        args: Dict[str, Any] = { "reportingStep": serialize_value(self._handle) }
        args["markdownString"] = serialize_value(markdown_string)
        cancellation_token_id = register_cancellation(cancellation_token, self._client) if cancellation_token is not None else None
        if cancellation_token_id is not None:
            args["cancellationToken"] = cancellation_token_id
        return self._client.invoke_capability("Aspire.Hosting/createMarkdownTask", args)

    def log_step(self, level: str, message: str) -> None:
        """Logs a plain-text message for the reporting step"""
        args: Dict[str, Any] = { "reportingStep": serialize_value(self._handle) }
        args["level"] = serialize_value(level)
        args["message"] = serialize_value(message)
        self._client.invoke_capability("Aspire.Hosting/logStep", args)
        return None

    def log_step_markdown(self, level: str, markdown_string: str) -> None:
        """Logs a Markdown-formatted message for the reporting step"""
        args: Dict[str, Any] = { "reportingStep": serialize_value(self._handle) }
        args["level"] = serialize_value(level)
        args["markdownString"] = serialize_value(markdown_string)
        self._client.invoke_capability("Aspire.Hosting/logStepMarkdown", args)
        return None

    def complete_step(self, completion_text: str, completion_state: str = "completed", cancellation_token: CancellationToken | None = None) -> None:
        """Completes the reporting step with plain-text completion text"""
        args: Dict[str, Any] = { "reportingStep": serialize_value(self._handle) }
        args["completionText"] = serialize_value(completion_text)
        args["completionState"] = serialize_value(completion_state)
        cancellation_token_id = register_cancellation(cancellation_token, self._client) if cancellation_token is not None else None
        if cancellation_token_id is not None:
            args["cancellationToken"] = cancellation_token_id
        self._client.invoke_capability("Aspire.Hosting/completeStep", args)
        return None

    def complete_step_markdown(self, markdown_string: str, completion_state: str = "completed", cancellation_token: CancellationToken | None = None) -> None:
        """Completes the reporting step with Markdown-formatted completion text"""
        args: Dict[str, Any] = { "reportingStep": serialize_value(self._handle) }
        args["markdownString"] = serialize_value(markdown_string)
        args["completionState"] = serialize_value(completion_state)
        cancellation_token_id = register_cancellation(cancellation_token, self._client) if cancellation_token is not None else None
        if cancellation_token_id is not None:
            args["cancellationToken"] = cancellation_token_id
        self._client.invoke_capability("Aspire.Hosting/completeStepMarkdown", args)
        return None


class IReportingTask(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def update_task(self, status_text: str, cancellation_token: CancellationToken | None = None) -> None:
        """Updates the reporting task with plain-text status text"""
        args: Dict[str, Any] = { "reportingTask": serialize_value(self._handle) }
        args["statusText"] = serialize_value(status_text)
        cancellation_token_id = register_cancellation(cancellation_token, self._client) if cancellation_token is not None else None
        if cancellation_token_id is not None:
            args["cancellationToken"] = cancellation_token_id
        self._client.invoke_capability("Aspire.Hosting/updateTask", args)
        return None

    def update_task_markdown(self, markdown_string: str, cancellation_token: CancellationToken | None = None) -> None:
        """Updates the reporting task with Markdown-formatted status text"""
        args: Dict[str, Any] = { "reportingTask": serialize_value(self._handle) }
        args["markdownString"] = serialize_value(markdown_string)
        cancellation_token_id = register_cancellation(cancellation_token, self._client) if cancellation_token is not None else None
        if cancellation_token_id is not None:
            args["cancellationToken"] = cancellation_token_id
        self._client.invoke_capability("Aspire.Hosting/updateTaskMarkdown", args)
        return None

    def complete_task(self, completion_message: str | None = None, completion_state: str = "completed", cancellation_token: CancellationToken | None = None) -> None:
        """Completes the reporting task with plain-text completion text"""
        args: Dict[str, Any] = { "reportingTask": serialize_value(self._handle) }
        if completion_message is not None:
            args["completionMessage"] = serialize_value(completion_message)
        args["completionState"] = serialize_value(completion_state)
        cancellation_token_id = register_cancellation(cancellation_token, self._client) if cancellation_token is not None else None
        if cancellation_token_id is not None:
            args["cancellationToken"] = cancellation_token_id
        self._client.invoke_capability("Aspire.Hosting/completeTask", args)
        return None

    def complete_task_markdown(self, markdown_string: str, completion_state: str = "completed", cancellation_token: CancellationToken | None = None) -> None:
        """Completes the reporting task with Markdown-formatted completion text"""
        args: Dict[str, Any] = { "reportingTask": serialize_value(self._handle) }
        args["markdownString"] = serialize_value(markdown_string)
        args["completionState"] = serialize_value(completion_state)
        cancellation_token_id = register_cancellation(cancellation_token, self._client) if cancellation_token is not None else None
        if cancellation_token_id is not None:
            args["cancellationToken"] = cancellation_token_id
        self._client.invoke_capability("Aspire.Hosting/completeTaskMarkdown", args)
        return None


class IResource(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class IResourceWithArgs(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class IResourceWithConnectionString(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class IResourceWithContainerFiles(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def with_container_files_source(self, source_path: str) -> IResourceWithContainerFiles:
        """Sets the source directory for container files"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["sourcePath"] = serialize_value(source_path)
        return self._client.invoke_capability("Aspire.Hosting/withContainerFilesSource", args)

    def clear_container_files_sources(self) -> IResourceWithContainerFiles:
        """Clears all container file sources"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/clearContainerFilesSources", args)


class IResourceWithEndpoints(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class IResourceWithEnvironment(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class IResourceWithParent(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class IResourceWithWaitSupport(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class IServiceProvider(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def get_eventing(self) -> IDistributedApplicationEventing:
        """Gets the distributed application eventing service from the service provider"""
        args: Dict[str, Any] = { "serviceProvider": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getEventing", args)

    def get_logger_factory(self) -> ILoggerFactory:
        """Gets the logger factory from the service provider"""
        args: Dict[str, Any] = { "serviceProvider": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getLoggerFactory", args)

    def get_resource_logger_service(self) -> ResourceLoggerService:
        """Gets the resource logger service from the service provider"""
        args: Dict[str, Any] = { "serviceProvider": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getResourceLoggerService", args)

    def get_distributed_application_model(self) -> DistributedApplicationModel:
        """Gets the distributed application model from the service provider"""
        args: Dict[str, Any] = { "serviceProvider": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getDistributedApplicationModel", args)

    def get_resource_notification_service(self) -> ResourceNotificationService:
        """Gets the resource notification service from the service provider"""
        args: Dict[str, Any] = { "serviceProvider": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getResourceNotificationService", args)

    def get_user_secrets_manager(self) -> IUserSecretsManager:
        """Gets the user secrets manager from the service provider"""
        args: Dict[str, Any] = { "serviceProvider": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getUserSecretsManager", args)


class ITestVaultResource(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class IUserSecretsManager(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def is_available(self) -> bool:
        """Gets the IsAvailable property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/IUserSecretsManager.isAvailable", args)

    def file_path(self) -> str:
        """Gets the FilePath property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/IUserSecretsManager.filePath", args)

    def try_set_secret(self, name: str, value: str) -> bool:
        """Attempts to set a user secret value"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/IUserSecretsManager.trySetSecret", args)

    def save_state_json(self, json: str, cancellation_token: CancellationToken | None = None) -> None:
        """Saves state to user secrets from a JSON string"""
        args: Dict[str, Any] = { "userSecretsManager": serialize_value(self._handle) }
        args["json"] = serialize_value(json)
        cancellation_token_id = register_cancellation(cancellation_token, self._client) if cancellation_token is not None else None
        if cancellation_token_id is not None:
            args["cancellationToken"] = cancellation_token_id
        self._client.invoke_capability("Aspire.Hosting/saveStateJson", args)
        return None

    def get_or_set_secret(self, resource_builder: IResource, name: str, value: str) -> None:
        """Gets a secret value if it exists, or sets it to the provided value if it does not"""
        args: Dict[str, Any] = { "userSecretsManager": serialize_value(self._handle) }
        args["resourceBuilder"] = serialize_value(resource_builder)
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        self._client.invoke_capability("Aspire.Hosting/getOrSetSecret", args)
        return None


class InitializeResourceEvent(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def resource(self) -> IResource:
        """Gets the Resource property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/InitializeResourceEvent.resource", args)

    def eventing(self) -> IDistributedApplicationEventing:
        """Gets the Eventing property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/InitializeResourceEvent.eventing", args)

    def logger(self) -> ILogger:
        """Gets the Logger property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/InitializeResourceEvent.logger", args)

    def notifications(self) -> ResourceNotificationService:
        """Gets the Notifications property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/InitializeResourceEvent.notifications", args)

    def services(self) -> IServiceProvider:
        """Gets the Services property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/InitializeResourceEvent.services", args)


class ParameterResource(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def with_container_registry(self, registry: IResource) -> IResource:
        """Configures a resource to use a container registry"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["registry"] = serialize_value(registry)
        return self._client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)

    def with_dockerfile_base_image(self, build_image: str | None = None, runtime_image: str | None = None) -> IResource:
        """Sets the base image for a Dockerfile build"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if build_image is not None:
            args["buildImage"] = serialize_value(build_image)
        if runtime_image is not None:
            args["runtimeImage"] = serialize_value(runtime_image)
        return self._client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)

    def with_description(self, description: str, enable_markdown: bool = False) -> ParameterResource:
        """Sets a parameter description"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["description"] = serialize_value(description)
        args["enableMarkdown"] = serialize_value(enable_markdown)
        return self._client.invoke_capability("Aspire.Hosting/withDescription", args)

    def with_required_command(self, command: str, help_link: str | None = None) -> IResource:
        """Adds a required command dependency"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["command"] = serialize_value(command)
        if help_link is not None:
            args["helpLink"] = serialize_value(help_link)
        return self._client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)

    def with_urls_callback(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)

    def with_urls_callback_async(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallbackAsync", args)

    def with_url(self, url: str, display_text: str | None = None) -> IResource:
        """Adds or modifies displayed URLs"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrl", args)

    def with_url_expression(self, url: ReferenceExpression, display_text: str | None = None) -> IResource:
        """Adds a URL using a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrlExpression", args)

    def with_url_for_endpoint(self, endpoint_name: str, callback: Callable[[ResourceUrlAnnotation], None]) -> IResource:
        """Customizes the URL for a specific endpoint via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)

    def exclude_from_manifest(self) -> IResource:
        """Excludes the resource from the deployment manifest"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)

    def with_explicit_start(self) -> IResource:
        """Prevents resource from starting automatically"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExplicitStart", args)

    def with_health_check(self, key: str) -> IResource:
        """Adds a health check by key"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["key"] = serialize_value(key)
        return self._client.invoke_capability("Aspire.Hosting/withHealthCheck", args)

    def with_command(self, name: str, display_name: str, execute_command: Callable[[ExecuteCommandContext], ExecuteCommandResult], command_options: CommandOptions | None = None) -> IResource:
        """Adds a resource command"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["displayName"] = serialize_value(display_name)
        execute_command_id = register_callback(execute_command) if execute_command is not None else None
        if execute_command_id is not None:
            args["executeCommand"] = execute_command_id
        if command_options is not None:
            args["commandOptions"] = serialize_value(command_options)
        return self._client.invoke_capability("Aspire.Hosting/withCommand", args)

    def with_parent_relationship(self, parent: IResource) -> IResource:
        """Sets the parent relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["parent"] = serialize_value(parent)
        return self._client.invoke_capability("Aspire.Hosting/withParentRelationship", args)

    def with_child_relationship(self, child: IResource) -> IResource:
        """Sets a child relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["child"] = serialize_value(child)
        return self._client.invoke_capability("Aspire.Hosting/withChildRelationship", args)

    def with_icon_name(self, icon_name: str, icon_variant: IconVariant = None) -> IResource:
        """Sets the icon for the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["iconName"] = serialize_value(icon_name)
        args["iconVariant"] = serialize_value(icon_variant)
        return self._client.invoke_capability("Aspire.Hosting/withIconName", args)

    def exclude_from_mcp(self) -> IResource:
        """Excludes the resource from MCP server exposure"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)

    def with_pipeline_step_factory(self, step_name: str, callback: Callable[[PipelineStepContext], None], depends_on: list[str] | None = None, required_by: list[str] | None = None, tags: list[str] | None = None, description: str | None = None) -> IResource:
        """Adds a pipeline step to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["stepName"] = serialize_value(step_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        if depends_on is not None:
            args["dependsOn"] = serialize_value(depends_on)
        if required_by is not None:
            args["requiredBy"] = serialize_value(required_by)
        if tags is not None:
            args["tags"] = serialize_value(tags)
        if description is not None:
            args["description"] = serialize_value(description)
        return self._client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)

    def with_pipeline_configuration_async(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via an async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfigurationAsync", args)

    def with_pipeline_configuration(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via a callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)

    def get_resource_name(self) -> str:
        """Gets the resource name"""
        args: Dict[str, Any] = { "resource": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getResourceName", args)

    def on_before_resource_started(self, callback: Callable[[BeforeResourceStartedEvent], None]) -> IResource:
        """Subscribes to the BeforeResourceStarted event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)

    def on_resource_stopped(self, callback: Callable[[ResourceStoppedEvent], None]) -> IResource:
        """Subscribes to the ResourceStopped event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceStopped", args)

    def on_initialize_resource(self, callback: Callable[[InitializeResourceEvent], None]) -> IResource:
        """Subscribes to the InitializeResource event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onInitializeResource", args)

    def on_resource_ready(self, callback: Callable[[ResourceReadyEvent], None]) -> IResource:
        """Subscribes to the ResourceReady event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceReady", args)

    def with_optional_string(self, value: str | None = None, enabled: bool = True) -> IResource:
        """Adds an optional string parameter"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if value is not None:
            args["value"] = serialize_value(value)
        args["enabled"] = serialize_value(enabled)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalString", args)

    def with_config(self, config: TestConfigDto) -> IResource:
        """Configures the resource with a DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConfig", args)

    def with_created_at(self, created_at: str) -> IResource:
        """Sets the created timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["createdAt"] = serialize_value(created_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCreatedAt", args)

    def with_modified_at(self, modified_at: str) -> IResource:
        """Sets the modified timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["modifiedAt"] = serialize_value(modified_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withModifiedAt", args)

    def with_correlation_id(self, correlation_id: str) -> IResource:
        """Sets the correlation ID"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["correlationId"] = serialize_value(correlation_id)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCorrelationId", args)

    def with_optional_callback(self, callback: Callable[[TestCallbackContext], None] | None = None) -> IResource:
        """Configures with optional callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalCallback", args)

    def with_status(self, status: TestResourceStatus) -> IResource:
        """Sets the resource status"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["status"] = serialize_value(status)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withStatus", args)

    def with_nested_config(self, config: TestNestedDto) -> IResource:
        """Configures with nested DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withNestedConfig", args)

    def with_validator(self, validator: Callable[[TestResourceContext], bool]) -> IResource:
        """Adds validation callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        validator_id = register_callback(validator) if validator is not None else None
        if validator_id is not None:
            args["validator"] = validator_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withValidator", args)

    def test_wait_for(self, dependency: IResource) -> IResource:
        """Waits for another resource (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWaitFor", args)

    def with_dependency(self, dependency: IResourceWithConnectionString) -> IResource:
        """Adds a dependency on another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withDependency", args)

    def with_endpoints(self, endpoints: list[str]) -> IResource:
        """Sets the endpoints"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpoints"] = serialize_value(endpoints)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEndpoints", args)

    def with_cancellable_operation(self, operation: Callable[[CancellationToken], None]) -> IResource:
        """Performs a cancellable operation"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        operation_id = register_callback(operation) if operation is not None else None
        if operation_id is not None:
            args["operation"] = operation_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCancellableOperation", args)


class PipelineConfigurationContext(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def services(self) -> IServiceProvider:
        """Gets the Services property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.services", args)

    def set_services(self, value: IServiceProvider) -> PipelineConfigurationContext:
        """Sets the Services property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.setServices", args)

    def steps(self) -> list[PipelineStep]:
        """Gets the Steps property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.steps", args)

    def set_steps(self, value: list[PipelineStep]) -> PipelineConfigurationContext:
        """Sets the Steps property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.setSteps", args)

    def model(self) -> DistributedApplicationModel:
        """Gets the Model property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.model", args)

    def set_model(self, value: DistributedApplicationModel) -> PipelineConfigurationContext:
        """Sets the Model property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.setModel", args)

    def get_steps_by_tag(self, tag: str) -> list[PipelineStep]:
        """Gets pipeline steps with the specified tag"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["tag"] = serialize_value(tag)
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/getStepsByTag", args)


class PipelineContext(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def model(self) -> DistributedApplicationModel:
        """Gets the Model property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineContext.model", args)

    def execution_context(self) -> DistributedApplicationExecutionContext:
        """Gets the ExecutionContext property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineContext.executionContext", args)

    def services(self) -> IServiceProvider:
        """Gets the Services property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineContext.services", args)

    def logger(self) -> ILogger:
        """Gets the Logger property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineContext.logger", args)

    def cancellation_token(self) -> CancellationToken:
        """Gets the CancellationToken property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineContext.cancellationToken", args)

    def set_cancellation_token(self, value: CancellationToken) -> PipelineContext:
        """Sets the CancellationToken property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        value_id = register_cancellation(value, self._client) if value is not None else None
        if value_id is not None:
            args["value"] = value_id
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineContext.setCancellationToken", args)

    def summary(self) -> PipelineSummary:
        """Gets the Summary property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineContext.summary", args)


class PipelineStep(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def name(self) -> str:
        """Gets the Name property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStep.name", args)

    def set_name(self, value: str) -> PipelineStep:
        """Sets the Name property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStep.setName", args)

    def description(self) -> str:
        """Gets the Description property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStep.description", args)

    def set_description(self, value: str) -> PipelineStep:
        """Sets the Description property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStep.setDescription", args)

    @property
    def depends_on_steps(self) -> AspireList[str]:
        """Gets the DependsOnSteps property"""
        if not hasattr(self, '_depends_on_steps'):
            self._depends_on_steps = AspireList(
                self._handle,
                self._client,
                "Aspire.Hosting.Pipelines/PipelineStep.dependsOnSteps"
            )
        return self._depends_on_steps

    def set_depends_on_steps(self, value: AspireList[str]) -> PipelineStep:
        """Sets the DependsOnSteps property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStep.setDependsOnSteps", args)

    @property
    def required_by_steps(self) -> AspireList[str]:
        """Gets the RequiredBySteps property"""
        if not hasattr(self, '_required_by_steps'):
            self._required_by_steps = AspireList(
                self._handle,
                self._client,
                "Aspire.Hosting.Pipelines/PipelineStep.requiredBySteps"
            )
        return self._required_by_steps

    def set_required_by_steps(self, value: AspireList[str]) -> PipelineStep:
        """Sets the RequiredBySteps property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStep.setRequiredBySteps", args)

    @property
    def tags(self) -> AspireList[str]:
        """Gets the Tags property"""
        if not hasattr(self, '_tags'):
            self._tags = AspireList(
                self._handle,
                self._client,
                "Aspire.Hosting.Pipelines/PipelineStep.tags"
            )
        return self._tags

    def set_tags(self, value: AspireList[str]) -> PipelineStep:
        """Sets the Tags property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStep.setTags", args)

    def resource(self) -> IResource:
        """Gets the Resource property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStep.resource", args)

    def set_resource(self, value: IResource) -> PipelineStep:
        """Sets the Resource property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStep.setResource", args)

    def depends_on(self, step_name: str) -> None:
        """Adds a dependency on another step by name"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["stepName"] = serialize_value(step_name)
        self._client.invoke_capability("Aspire.Hosting.Pipelines/dependsOn", args)
        return None

    def required_by(self, step_name: str) -> None:
        """Specifies that another step requires this step by name"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["stepName"] = serialize_value(step_name)
        self._client.invoke_capability("Aspire.Hosting.Pipelines/requiredBy", args)
        return None


class PipelineStepContext(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def pipeline_context(self) -> PipelineContext:
        """Gets the PipelineContext property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.pipelineContext", args)

    def set_pipeline_context(self, value: PipelineContext) -> PipelineStepContext:
        """Sets the PipelineContext property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.setPipelineContext", args)

    def reporting_step(self) -> IReportingStep:
        """Gets the ReportingStep property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.reportingStep", args)

    def set_reporting_step(self, value: IReportingStep) -> PipelineStepContext:
        """Sets the ReportingStep property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.setReportingStep", args)

    def model(self) -> DistributedApplicationModel:
        """Gets the Model property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.model", args)

    def execution_context(self) -> DistributedApplicationExecutionContext:
        """Gets the ExecutionContext property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.executionContext", args)

    def services(self) -> IServiceProvider:
        """Gets the Services property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.services", args)

    def logger(self) -> ILogger:
        """Gets the Logger property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.logger", args)

    def cancellation_token(self) -> CancellationToken:
        """Gets the CancellationToken property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.cancellationToken", args)

    def summary(self) -> PipelineSummary:
        """Gets the Summary property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.summary", args)


class PipelineStepFactoryContext(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def pipeline_context(self) -> PipelineContext:
        """Gets the PipelineContext property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepFactoryContext.pipelineContext", args)

    def set_pipeline_context(self, value: PipelineContext) -> PipelineStepFactoryContext:
        """Sets the PipelineContext property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepFactoryContext.setPipelineContext", args)

    def resource(self) -> IResource:
        """Gets the Resource property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepFactoryContext.resource", args)

    def set_resource(self, value: IResource) -> PipelineStepFactoryContext:
        """Sets the Resource property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepFactoryContext.setResource", args)


class PipelineSummary(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def add(self, key: str, value: str) -> None:
        """Invokes the Add method"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["key"] = serialize_value(key)
        args["value"] = serialize_value(value)
        self._client.invoke_capability("Aspire.Hosting.Pipelines/PipelineSummary.add", args)
        return None

    def add_markdown(self, key: str, markdown_string: str) -> None:
        """Adds a Markdown-formatted value to the pipeline summary"""
        args: Dict[str, Any] = { "summary": serialize_value(self._handle) }
        args["key"] = serialize_value(key)
        args["markdownString"] = serialize_value(markdown_string)
        self._client.invoke_capability("Aspire.Hosting/addMarkdown", args)
        return None


class ProjectResource(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def with_container_registry(self, registry: IResource) -> IResource:
        """Configures a resource to use a container registry"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["registry"] = serialize_value(registry)
        return self._client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)

    def with_dockerfile_base_image(self, build_image: str | None = None, runtime_image: str | None = None) -> IResource:
        """Sets the base image for a Dockerfile build"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if build_image is not None:
            args["buildImage"] = serialize_value(build_image)
        if runtime_image is not None:
            args["runtimeImage"] = serialize_value(runtime_image)
        return self._client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)

    def with_mcp_server(self, path: str = "/mcp", endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Configures an MCP server endpoint on the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["path"] = serialize_value(path)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withMcpServer", args)

    def with_otlp_exporter(self) -> IResourceWithEnvironment:
        """Configures OTLP telemetry export"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withOtlpExporter", args)

    def with_otlp_exporter_protocol(self, protocol: OtlpProtocol) -> IResourceWithEnvironment:
        """Configures OTLP telemetry export with specific protocol"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["protocol"] = serialize_value(protocol)
        return self._client.invoke_capability("Aspire.Hosting/withOtlpExporterProtocol", args)

    def with_replicas(self, replicas: float) -> ProjectResource:
        """Sets the number of replicas"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["replicas"] = serialize_value(replicas)
        return self._client.invoke_capability("Aspire.Hosting/withReplicas", args)

    def disable_forwarded_headers(self) -> ProjectResource:
        """Disables forwarded headers for the project"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/disableForwardedHeaders", args)

    def publish_as_docker_file(self, configure: Callable[[ContainerResource], None] | None = None) -> ProjectResource:
        """Publishes a project as a Docker file with optional container configuration"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        configure_id = register_callback(configure) if configure is not None else None
        if configure_id is not None:
            args["configure"] = configure_id
        return self._client.invoke_capability("Aspire.Hosting/publishProjectAsDockerFileWithConfigure", args)

    def with_required_command(self, command: str, help_link: str | None = None) -> IResource:
        """Adds a required command dependency"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["command"] = serialize_value(command)
        if help_link is not None:
            args["helpLink"] = serialize_value(help_link)
        return self._client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)

    def with_environment(self, name: str, value: str) -> IResourceWithEnvironment:
        """Sets an environment variable"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironment", args)

    def with_environment_expression(self, name: str, value: ReferenceExpression) -> IResourceWithEnvironment:
        """Adds an environment variable with a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentExpression", args)

    def with_environment_callback(self, callback: Callable[[EnvironmentCallbackContext], None]) -> IResourceWithEnvironment:
        """Sets environment variables via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentCallback", args)

    def with_environment_callback_async(self, callback: Callable[[EnvironmentCallbackContext], None]) -> IResourceWithEnvironment:
        """Sets environment variables via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentCallbackAsync", args)

    def with_environment_endpoint(self, name: str, endpoint_reference: EndpointReference) -> IResourceWithEnvironment:
        """Sets an environment variable from an endpoint reference"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["endpointReference"] = serialize_value(endpoint_reference)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentEndpoint", args)

    def with_environment_parameter(self, name: str, parameter: ParameterResource) -> IResourceWithEnvironment:
        """Sets an environment variable from a parameter resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["parameter"] = serialize_value(parameter)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentParameter", args)

    def with_environment_connection_string(self, env_var_name: str, resource: IResourceWithConnectionString) -> IResourceWithEnvironment:
        """Sets an environment variable from a connection string resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["envVarName"] = serialize_value(env_var_name)
        args["resource"] = serialize_value(resource)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentConnectionString", args)

    def with_args(self, args: list[str]) -> IResourceWithArgs:
        """Adds arguments"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["args"] = serialize_value(args)
        return self._client.invoke_capability("Aspire.Hosting/withArgs", args)

    def with_args_callback(self, callback: Callable[[CommandLineArgsCallbackContext], None]) -> IResourceWithArgs:
        """Sets command-line arguments via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withArgsCallback", args)

    def with_args_callback_async(self, callback: Callable[[CommandLineArgsCallbackContext], None]) -> IResourceWithArgs:
        """Sets command-line arguments via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withArgsCallbackAsync", args)

    def with_reference(self, source: IResource, connection_name: str | None = None, optional: bool = False, name: str | None = None) -> IResourceWithEnvironment:
        """Adds a reference to another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["source"] = serialize_value(source)
        if connection_name is not None:
            args["connectionName"] = serialize_value(connection_name)
        args["optional"] = serialize_value(optional)
        if name is not None:
            args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/withReference", args)

    def with_reference_uri(self, name: str, uri: str) -> IResourceWithEnvironment:
        """Adds a reference to a URI"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["uri"] = serialize_value(uri)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceUri", args)

    def with_reference_external_service(self, external_service: ExternalServiceResource) -> IResourceWithEnvironment:
        """Adds a reference to an external service"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["externalService"] = serialize_value(external_service)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceExternalService", args)

    def with_reference_endpoint(self, endpoint_reference: EndpointReference) -> IResourceWithEnvironment:
        """Adds a reference to an endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointReference"] = serialize_value(endpoint_reference)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceEndpoint", args)

    def with_endpoint(self, port: float | None = None, target_port: float | None = None, scheme: str | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True, is_external: bool | None = None, protocol: ProtocolType | None = None) -> IResourceWithEndpoints:
        """Adds a network endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if scheme is not None:
            args["scheme"] = serialize_value(scheme)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        if is_external is not None:
            args["isExternal"] = serialize_value(is_external)
        if protocol is not None:
            args["protocol"] = serialize_value(protocol)
        return self._client.invoke_capability("Aspire.Hosting/withEndpoint", args)

    def with_http_endpoint(self, port: float | None = None, target_port: float | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> IResourceWithEndpoints:
        """Adds an HTTP endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        return self._client.invoke_capability("Aspire.Hosting/withHttpEndpoint", args)

    def with_https_endpoint(self, port: float | None = None, target_port: float | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> IResourceWithEndpoints:
        """Adds an HTTPS endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        return self._client.invoke_capability("Aspire.Hosting/withHttpsEndpoint", args)

    def with_external_http_endpoints(self) -> IResourceWithEndpoints:
        """Makes HTTP endpoints externally accessible"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExternalHttpEndpoints", args)

    def get_endpoint(self, name: str) -> EndpointReference:
        """Gets an endpoint reference"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/getEndpoint", args)

    def as_http2_service(self) -> IResourceWithEndpoints:
        """Configures resource for HTTP/2"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/asHttp2Service", args)

    def with_urls_callback(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)

    def with_urls_callback_async(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallbackAsync", args)

    def with_url(self, url: str, display_text: str | None = None) -> IResource:
        """Adds or modifies displayed URLs"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrl", args)

    def with_url_expression(self, url: ReferenceExpression, display_text: str | None = None) -> IResource:
        """Adds a URL using a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrlExpression", args)

    def with_url_for_endpoint(self, endpoint_name: str, callback: Callable[[ResourceUrlAnnotation], None]) -> IResource:
        """Customizes the URL for a specific endpoint via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)

    def with_url_for_endpoint_factory(self, endpoint_name: str, callback: Callable[[EndpointReference], ResourceUrlAnnotation]) -> IResourceWithEndpoints:
        """Adds a URL for a specific endpoint via factory callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpointFactory", args)

    def publish_with_container_files(self, source: IResourceWithContainerFiles, destination_path: str) -> IContainerFilesDestinationResource:
        """Configures the resource to copy container files from the specified source during publishing"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["source"] = serialize_value(source)
        args["destinationPath"] = serialize_value(destination_path)
        return self._client.invoke_capability("Aspire.Hosting/publishWithContainerFiles", args)

    def exclude_from_manifest(self) -> IResource:
        """Excludes the resource from the deployment manifest"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)

    def wait_for(self, dependency: IResource) -> IResourceWithWaitSupport:
        """Waits for another resource to be ready"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting/waitFor", args)

    def wait_for_with_behavior(self, dependency: IResource, wait_behavior: WaitBehavior) -> IResourceWithWaitSupport:
        """Waits for another resource with specific behavior"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["waitBehavior"] = serialize_value(wait_behavior)
        return self._client.invoke_capability("Aspire.Hosting/waitForWithBehavior", args)

    def wait_for_start(self, dependency: IResource) -> IResourceWithWaitSupport:
        """Waits for another resource to start"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting/waitForStart", args)

    def wait_for_start_with_behavior(self, dependency: IResource, wait_behavior: WaitBehavior) -> IResourceWithWaitSupport:
        """Waits for another resource to start with specific behavior"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["waitBehavior"] = serialize_value(wait_behavior)
        return self._client.invoke_capability("Aspire.Hosting/waitForStartWithBehavior", args)

    def with_explicit_start(self) -> IResource:
        """Prevents resource from starting automatically"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExplicitStart", args)

    def wait_for_completion(self, dependency: IResource, exit_code: float = 0) -> IResourceWithWaitSupport:
        """Waits for resource completion"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["exitCode"] = serialize_value(exit_code)
        return self._client.invoke_capability("Aspire.Hosting/waitForCompletion", args)

    def with_health_check(self, key: str) -> IResource:
        """Adds a health check by key"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["key"] = serialize_value(key)
        return self._client.invoke_capability("Aspire.Hosting/withHealthCheck", args)

    def with_http_health_check(self, path: str | None = None, status_code: float | None = None, endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Adds an HTTP health check"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if path is not None:
            args["path"] = serialize_value(path)
        if status_code is not None:
            args["statusCode"] = serialize_value(status_code)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withHttpHealthCheck", args)

    def with_command(self, name: str, display_name: str, execute_command: Callable[[ExecuteCommandContext], ExecuteCommandResult], command_options: CommandOptions | None = None) -> IResource:
        """Adds a resource command"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["displayName"] = serialize_value(display_name)
        execute_command_id = register_callback(execute_command) if execute_command is not None else None
        if execute_command_id is not None:
            args["executeCommand"] = execute_command_id
        if command_options is not None:
            args["commandOptions"] = serialize_value(command_options)
        return self._client.invoke_capability("Aspire.Hosting/withCommand", args)

    def with_developer_certificate_trust(self, trust: bool) -> IResourceWithEnvironment:
        """Configures developer certificate trust"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["trust"] = serialize_value(trust)
        return self._client.invoke_capability("Aspire.Hosting/withDeveloperCertificateTrust", args)

    def with_certificate_trust_scope(self, scope: CertificateTrustScope) -> IResourceWithEnvironment:
        """Sets the certificate trust scope"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["scope"] = serialize_value(scope)
        return self._client.invoke_capability("Aspire.Hosting/withCertificateTrustScope", args)

    def with_https_developer_certificate(self, password: ParameterResource | None = None) -> IResourceWithEnvironment:
        """Configures HTTPS with a developer certificate"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if password is not None:
            args["password"] = serialize_value(password)
        return self._client.invoke_capability("Aspire.Hosting/withHttpsDeveloperCertificate", args)

    def without_https_certificate(self) -> IResourceWithEnvironment:
        """Removes HTTPS certificate configuration"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withoutHttpsCertificate", args)

    def with_parent_relationship(self, parent: IResource) -> IResource:
        """Sets the parent relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["parent"] = serialize_value(parent)
        return self._client.invoke_capability("Aspire.Hosting/withParentRelationship", args)

    def with_child_relationship(self, child: IResource) -> IResource:
        """Sets a child relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["child"] = serialize_value(child)
        return self._client.invoke_capability("Aspire.Hosting/withChildRelationship", args)

    def with_icon_name(self, icon_name: str, icon_variant: IconVariant = None) -> IResource:
        """Sets the icon for the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["iconName"] = serialize_value(icon_name)
        args["iconVariant"] = serialize_value(icon_variant)
        return self._client.invoke_capability("Aspire.Hosting/withIconName", args)

    def with_http_probe(self, probe_type: ProbeType, path: str | None = None, initial_delay_seconds: float | None = None, period_seconds: float | None = None, timeout_seconds: float | None = None, failure_threshold: float | None = None, success_threshold: float | None = None, endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Adds an HTTP health probe to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["probeType"] = serialize_value(probe_type)
        if path is not None:
            args["path"] = serialize_value(path)
        if initial_delay_seconds is not None:
            args["initialDelaySeconds"] = serialize_value(initial_delay_seconds)
        if period_seconds is not None:
            args["periodSeconds"] = serialize_value(period_seconds)
        if timeout_seconds is not None:
            args["timeoutSeconds"] = serialize_value(timeout_seconds)
        if failure_threshold is not None:
            args["failureThreshold"] = serialize_value(failure_threshold)
        if success_threshold is not None:
            args["successThreshold"] = serialize_value(success_threshold)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withHttpProbe", args)

    def exclude_from_mcp(self) -> IResource:
        """Excludes the resource from MCP server exposure"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)

    def with_remote_image_name(self, remote_image_name: str) -> IComputeResource:
        """Sets the remote image name for publishing"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["remoteImageName"] = serialize_value(remote_image_name)
        return self._client.invoke_capability("Aspire.Hosting/withRemoteImageName", args)

    def with_remote_image_tag(self, remote_image_tag: str) -> IComputeResource:
        """Sets the remote image tag for publishing"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["remoteImageTag"] = serialize_value(remote_image_tag)
        return self._client.invoke_capability("Aspire.Hosting/withRemoteImageTag", args)

    def with_pipeline_step_factory(self, step_name: str, callback: Callable[[PipelineStepContext], None], depends_on: list[str] | None = None, required_by: list[str] | None = None, tags: list[str] | None = None, description: str | None = None) -> IResource:
        """Adds a pipeline step to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["stepName"] = serialize_value(step_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        if depends_on is not None:
            args["dependsOn"] = serialize_value(depends_on)
        if required_by is not None:
            args["requiredBy"] = serialize_value(required_by)
        if tags is not None:
            args["tags"] = serialize_value(tags)
        if description is not None:
            args["description"] = serialize_value(description)
        return self._client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)

    def with_pipeline_configuration_async(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via an async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfigurationAsync", args)

    def with_pipeline_configuration(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via a callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)

    def get_resource_name(self) -> str:
        """Gets the resource name"""
        args: Dict[str, Any] = { "resource": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getResourceName", args)

    def on_before_resource_started(self, callback: Callable[[BeforeResourceStartedEvent], None]) -> IResource:
        """Subscribes to the BeforeResourceStarted event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)

    def on_resource_stopped(self, callback: Callable[[ResourceStoppedEvent], None]) -> IResource:
        """Subscribes to the ResourceStopped event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceStopped", args)

    def on_initialize_resource(self, callback: Callable[[InitializeResourceEvent], None]) -> IResource:
        """Subscribes to the InitializeResource event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onInitializeResource", args)

    def on_resource_endpoints_allocated(self, callback: Callable[[ResourceEndpointsAllocatedEvent], None]) -> IResourceWithEndpoints:
        """Subscribes to the ResourceEndpointsAllocated event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceEndpointsAllocated", args)

    def on_resource_ready(self, callback: Callable[[ResourceReadyEvent], None]) -> IResource:
        """Subscribes to the ResourceReady event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceReady", args)

    def with_optional_string(self, value: str | None = None, enabled: bool = True) -> IResource:
        """Adds an optional string parameter"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if value is not None:
            args["value"] = serialize_value(value)
        args["enabled"] = serialize_value(enabled)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalString", args)

    def with_config(self, config: TestConfigDto) -> IResource:
        """Configures the resource with a DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConfig", args)

    def test_with_environment_callback(self, callback: Callable[[TestEnvironmentContext], None]) -> IResourceWithEnvironment:
        """Configures environment with callback (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWithEnvironmentCallback", args)

    def with_created_at(self, created_at: str) -> IResource:
        """Sets the created timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["createdAt"] = serialize_value(created_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCreatedAt", args)

    def with_modified_at(self, modified_at: str) -> IResource:
        """Sets the modified timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["modifiedAt"] = serialize_value(modified_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withModifiedAt", args)

    def with_correlation_id(self, correlation_id: str) -> IResource:
        """Sets the correlation ID"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["correlationId"] = serialize_value(correlation_id)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCorrelationId", args)

    def with_optional_callback(self, callback: Callable[[TestCallbackContext], None] | None = None) -> IResource:
        """Configures with optional callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalCallback", args)

    def with_status(self, status: TestResourceStatus) -> IResource:
        """Sets the resource status"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["status"] = serialize_value(status)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withStatus", args)

    def with_nested_config(self, config: TestNestedDto) -> IResource:
        """Configures with nested DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withNestedConfig", args)

    def with_validator(self, validator: Callable[[TestResourceContext], bool]) -> IResource:
        """Adds validation callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        validator_id = register_callback(validator) if validator is not None else None
        if validator_id is not None:
            args["validator"] = validator_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withValidator", args)

    def test_wait_for(self, dependency: IResource) -> IResource:
        """Waits for another resource (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWaitFor", args)

    def with_dependency(self, dependency: IResourceWithConnectionString) -> IResource:
        """Adds a dependency on another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withDependency", args)

    def with_endpoints(self, endpoints: list[str]) -> IResource:
        """Sets the endpoints"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpoints"] = serialize_value(endpoints)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEndpoints", args)

    def with_environment_variables(self, variables: dict[str, str]) -> IResourceWithEnvironment:
        """Sets environment variables"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["variables"] = serialize_value(variables)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEnvironmentVariables", args)

    def with_cancellable_operation(self, operation: Callable[[CancellationToken], None]) -> IResource:
        """Performs a cancellable operation"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        operation_id = register_callback(operation) if operation is not None else None
        if operation_id is not None:
            args["operation"] = operation_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCancellableOperation", args)


class ProjectResourceOptions(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def launch_profile_name(self) -> str:
        """Gets the LaunchProfileName property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/ProjectResourceOptions.launchProfileName", args)

    def set_launch_profile_name(self, value: str) -> ProjectResourceOptions:
        """Sets the LaunchProfileName property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/ProjectResourceOptions.setLaunchProfileName", args)

    def exclude_launch_profile(self) -> bool:
        """Gets the ExcludeLaunchProfile property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/ProjectResourceOptions.excludeLaunchProfile", args)

    def set_exclude_launch_profile(self, value: bool) -> ProjectResourceOptions:
        """Sets the ExcludeLaunchProfile property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/ProjectResourceOptions.setExcludeLaunchProfile", args)

    def exclude_kestrel_endpoints(self) -> bool:
        """Gets the ExcludeKestrelEndpoints property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/ProjectResourceOptions.excludeKestrelEndpoints", args)

    def set_exclude_kestrel_endpoints(self, value: bool) -> ProjectResourceOptions:
        """Sets the ExcludeKestrelEndpoints property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/ProjectResourceOptions.setExcludeKestrelEndpoints", args)


class ReferenceExpression(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def get_value(self, cancellation_token: CancellationToken) -> str:
        """Gets the resolved string value of the reference expression asynchronously"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        cancellation_token_id = register_cancellation(cancellation_token, self._client) if cancellation_token is not None else None
        if cancellation_token_id is not None:
            args["cancellationToken"] = cancellation_token_id
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/getValue", args)


class ReferenceExpressionBuilder(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def is_empty(self) -> bool:
        """Gets the IsEmpty property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ReferenceExpressionBuilder.isEmpty", args)

    def append_literal(self, value: str) -> None:
        """Appends a literal string to the reference expression"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        self._client.invoke_capability("Aspire.Hosting.ApplicationModel/appendLiteral", args)
        return None

    def append_formatted(self, value: str, format: str | None = None) -> None:
        """Appends a formatted string value to the reference expression"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        if format is not None:
            args["format"] = serialize_value(format)
        self._client.invoke_capability("Aspire.Hosting.ApplicationModel/appendFormatted", args)
        return None

    def append_value_provider(self, value_provider: Any, format: str | None = None) -> None:
        """Appends a value provider to the reference expression"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["valueProvider"] = serialize_value(value_provider)
        if format is not None:
            args["format"] = serialize_value(format)
        self._client.invoke_capability("Aspire.Hosting.ApplicationModel/appendValueProvider", args)
        return None

    def build(self) -> ReferenceExpression:
        """Builds the reference expression"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/build", args)


class ResourceEndpointsAllocatedEvent(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def resource(self) -> IResource:
        """Gets the Resource property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceEndpointsAllocatedEvent.resource", args)

    def services(self) -> IServiceProvider:
        """Gets the Services property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceEndpointsAllocatedEvent.services", args)


class ResourceLoggerService(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def complete_log(self, resource: IResource) -> None:
        """Completes the log stream for a resource"""
        args: Dict[str, Any] = { "loggerService": serialize_value(self._handle) }
        args["resource"] = serialize_value(resource)
        self._client.invoke_capability("Aspire.Hosting/completeLog", args)
        return None

    def complete_log_by_name(self, resource_name: str) -> None:
        """Completes the log stream by resource name"""
        args: Dict[str, Any] = { "loggerService": serialize_value(self._handle) }
        args["resourceName"] = serialize_value(resource_name)
        self._client.invoke_capability("Aspire.Hosting/completeLogByName", args)
        return None


class ResourceNotificationService(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def wait_for_resource_state(self, resource_name: str, target_state: str | None = None) -> None:
        """Waits for a resource to reach a specified state"""
        args: Dict[str, Any] = { "notificationService": serialize_value(self._handle) }
        args["resourceName"] = serialize_value(resource_name)
        if target_state is not None:
            args["targetState"] = serialize_value(target_state)
        self._client.invoke_capability("Aspire.Hosting/waitForResourceState", args)
        return None

    def wait_for_resource_states(self, resource_name: str, target_states: list[str]) -> str:
        """Waits for a resource to reach one of the specified states"""
        args: Dict[str, Any] = { "notificationService": serialize_value(self._handle) }
        args["resourceName"] = serialize_value(resource_name)
        args["targetStates"] = serialize_value(target_states)
        return self._client.invoke_capability("Aspire.Hosting/waitForResourceStates", args)

    def wait_for_resource_healthy(self, resource_name: str) -> ResourceEventDto:
        """Waits for a resource to become healthy"""
        args: Dict[str, Any] = { "notificationService": serialize_value(self._handle) }
        args["resourceName"] = serialize_value(resource_name)
        return self._client.invoke_capability("Aspire.Hosting/waitForResourceHealthy", args)

    def wait_for_dependencies(self, resource: IResource) -> None:
        """Waits for all dependencies of a resource to be ready"""
        args: Dict[str, Any] = { "notificationService": serialize_value(self._handle) }
        args["resource"] = serialize_value(resource)
        self._client.invoke_capability("Aspire.Hosting/waitForDependencies", args)
        return None

    def try_get_resource_state(self, resource_name: str) -> ResourceEventDto:
        """Tries to get the current state of a resource"""
        args: Dict[str, Any] = { "notificationService": serialize_value(self._handle) }
        args["resourceName"] = serialize_value(resource_name)
        return self._client.invoke_capability("Aspire.Hosting/tryGetResourceState", args)

    def publish_resource_update(self, resource: IResource, state: str | None = None, state_style: str | None = None) -> None:
        """Publishes an update for a resource's state"""
        args: Dict[str, Any] = { "notificationService": serialize_value(self._handle) }
        args["resource"] = serialize_value(resource)
        if state is not None:
            args["state"] = serialize_value(state)
        if state_style is not None:
            args["stateStyle"] = serialize_value(state_style)
        self._client.invoke_capability("Aspire.Hosting/publishResourceUpdate", args)
        return None


class ResourceReadyEvent(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def resource(self) -> IResource:
        """Gets the Resource property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceReadyEvent.resource", args)

    def services(self) -> IServiceProvider:
        """Gets the Services property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceReadyEvent.services", args)


class ResourceStoppedEvent(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def resource(self) -> IResource:
        """Gets the Resource property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceStoppedEvent.resource", args)

    def services(self) -> IServiceProvider:
        """Gets the Services property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceStoppedEvent.services", args)


class ResourceUrlsCallbackContext(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def resource(self) -> IResource:
        """Gets the Resource property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.resource", args)

    @property
    def urls(self) -> AspireList[ResourceUrlAnnotation]:
        """Gets the Urls property"""
        if not hasattr(self, '_urls'):
            self._urls = AspireList(
                self._handle,
                self._client,
                "Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.urls"
            )
        return self._urls

    def cancellation_token(self) -> CancellationToken:
        """Gets the CancellationToken property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.cancellationToken", args)

    def logger(self) -> ILogger:
        """Gets the Logger property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.logger", args)

    def set_logger(self, value: ILogger) -> ResourceUrlsCallbackContext:
        """Sets the Logger property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.setLogger", args)

    def execution_context(self) -> DistributedApplicationExecutionContext:
        """Gets the ExecutionContext property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.executionContext", args)


class TestCallbackContext(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def name(self) -> str:
        """Gets the Name property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.name", args)

    def set_name(self, value: str) -> TestCallbackContext:
        """Sets the Name property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setName", args)

    def value(self) -> float:
        """Gets the Value property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.value", args)

    def set_value(self, value: float) -> TestCallbackContext:
        """Sets the Value property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setValue", args)

    def cancellation_token(self) -> CancellationToken:
        """Gets the CancellationToken property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.cancellationToken", args)

    def set_cancellation_token(self, value: CancellationToken) -> TestCallbackContext:
        """Sets the CancellationToken property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        value_id = register_cancellation(value, self._client) if value is not None else None
        if value_id is not None:
            args["value"] = value_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setCancellationToken", args)


class TestCollectionContext(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    @property
    def items(self) -> AspireList[str]:
        """Gets the Items property"""
        if not hasattr(self, '_items'):
            self._items = AspireList(
                self._handle,
                self._client,
                "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.items"
            )
        return self._items

    @property
    def metadata(self) -> AspireDict[str, str]:
        """Gets the Metadata property"""
        if not hasattr(self, '_metadata'):
            self._metadata = AspireDict(
                self._handle,
                self._client,
                "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.metadata"
            )
        return self._metadata


class TestDatabaseResource(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def with_container_registry(self, registry: IResource) -> IResource:
        """Configures a resource to use a container registry"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["registry"] = serialize_value(registry)
        return self._client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)

    def with_bind_mount(self, source: str, target: str, is_read_only: bool = False) -> ContainerResource:
        """Adds a bind mount"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["source"] = serialize_value(source)
        args["target"] = serialize_value(target)
        args["isReadOnly"] = serialize_value(is_read_only)
        return self._client.invoke_capability("Aspire.Hosting/withBindMount", args)

    def with_entrypoint(self, entrypoint: str) -> ContainerResource:
        """Sets the container entrypoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["entrypoint"] = serialize_value(entrypoint)
        return self._client.invoke_capability("Aspire.Hosting/withEntrypoint", args)

    def with_image_tag(self, tag: str) -> ContainerResource:
        """Sets the container image tag"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["tag"] = serialize_value(tag)
        return self._client.invoke_capability("Aspire.Hosting/withImageTag", args)

    def with_image_registry(self, registry: str) -> ContainerResource:
        """Sets the container image registry"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["registry"] = serialize_value(registry)
        return self._client.invoke_capability("Aspire.Hosting/withImageRegistry", args)

    def with_image(self, image: str, tag: str | None = None) -> ContainerResource:
        """Sets the container image"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["image"] = serialize_value(image)
        if tag is not None:
            args["tag"] = serialize_value(tag)
        return self._client.invoke_capability("Aspire.Hosting/withImage", args)

    def with_image_sha256(self, sha256: str) -> ContainerResource:
        """Sets the image SHA256 digest"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["sha256"] = serialize_value(sha256)
        return self._client.invoke_capability("Aspire.Hosting/withImageSHA256", args)

    def with_container_runtime_args(self, args: list[str]) -> ContainerResource:
        """Adds runtime arguments for the container"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["args"] = serialize_value(args)
        return self._client.invoke_capability("Aspire.Hosting/withContainerRuntimeArgs", args)

    def with_lifetime(self, lifetime: ContainerLifetime) -> ContainerResource:
        """Sets the lifetime behavior of the container resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["lifetime"] = serialize_value(lifetime)
        return self._client.invoke_capability("Aspire.Hosting/withLifetime", args)

    def with_image_pull_policy(self, pull_policy: ImagePullPolicy) -> ContainerResource:
        """Sets the container image pull policy"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["pullPolicy"] = serialize_value(pull_policy)
        return self._client.invoke_capability("Aspire.Hosting/withImagePullPolicy", args)

    def publish_as_container(self) -> ContainerResource:
        """Configures the resource to be published as a container"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/publishAsContainer", args)

    def with_dockerfile(self, context_path: str, dockerfile_path: str | None = None, stage: str | None = None) -> ContainerResource:
        """Configures the resource to use a Dockerfile"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["contextPath"] = serialize_value(context_path)
        if dockerfile_path is not None:
            args["dockerfilePath"] = serialize_value(dockerfile_path)
        if stage is not None:
            args["stage"] = serialize_value(stage)
        return self._client.invoke_capability("Aspire.Hosting/withDockerfile", args)

    def with_container_name(self, name: str) -> ContainerResource:
        """Sets the container name"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/withContainerName", args)

    def with_build_arg(self, name: str, value: ParameterResource) -> ContainerResource:
        """Adds a build argument from a parameter resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withBuildArg", args)

    def with_build_secret(self, name: str, value: ParameterResource) -> ContainerResource:
        """Adds a build secret from a parameter resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withBuildSecret", args)

    def with_endpoint_proxy_support(self, proxy_enabled: bool) -> ContainerResource:
        """Configures endpoint proxy support"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["proxyEnabled"] = serialize_value(proxy_enabled)
        return self._client.invoke_capability("Aspire.Hosting/withEndpointProxySupport", args)

    def with_dockerfile_base_image(self, build_image: str | None = None, runtime_image: str | None = None) -> IResource:
        """Sets the base image for a Dockerfile build"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if build_image is not None:
            args["buildImage"] = serialize_value(build_image)
        if runtime_image is not None:
            args["runtimeImage"] = serialize_value(runtime_image)
        return self._client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)

    def with_container_network_alias(self, alias: str) -> ContainerResource:
        """Adds a network alias for the container"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["alias"] = serialize_value(alias)
        return self._client.invoke_capability("Aspire.Hosting/withContainerNetworkAlias", args)

    def with_mcp_server(self, path: str = "/mcp", endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Configures an MCP server endpoint on the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["path"] = serialize_value(path)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withMcpServer", args)

    def with_otlp_exporter(self) -> IResourceWithEnvironment:
        """Configures OTLP telemetry export"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withOtlpExporter", args)

    def with_otlp_exporter_protocol(self, protocol: OtlpProtocol) -> IResourceWithEnvironment:
        """Configures OTLP telemetry export with specific protocol"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["protocol"] = serialize_value(protocol)
        return self._client.invoke_capability("Aspire.Hosting/withOtlpExporterProtocol", args)

    def publish_as_connection_string(self) -> ContainerResource:
        """Publishes the resource as a connection string"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/publishAsConnectionString", args)

    def with_required_command(self, command: str, help_link: str | None = None) -> IResource:
        """Adds a required command dependency"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["command"] = serialize_value(command)
        if help_link is not None:
            args["helpLink"] = serialize_value(help_link)
        return self._client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)

    def with_environment(self, name: str, value: str) -> IResourceWithEnvironment:
        """Sets an environment variable"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironment", args)

    def with_environment_expression(self, name: str, value: ReferenceExpression) -> IResourceWithEnvironment:
        """Adds an environment variable with a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentExpression", args)

    def with_environment_callback(self, callback: Callable[[EnvironmentCallbackContext], None]) -> IResourceWithEnvironment:
        """Sets environment variables via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentCallback", args)

    def with_environment_callback_async(self, callback: Callable[[EnvironmentCallbackContext], None]) -> IResourceWithEnvironment:
        """Sets environment variables via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentCallbackAsync", args)

    def with_environment_endpoint(self, name: str, endpoint_reference: EndpointReference) -> IResourceWithEnvironment:
        """Sets an environment variable from an endpoint reference"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["endpointReference"] = serialize_value(endpoint_reference)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentEndpoint", args)

    def with_environment_parameter(self, name: str, parameter: ParameterResource) -> IResourceWithEnvironment:
        """Sets an environment variable from a parameter resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["parameter"] = serialize_value(parameter)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentParameter", args)

    def with_environment_connection_string(self, env_var_name: str, resource: IResourceWithConnectionString) -> IResourceWithEnvironment:
        """Sets an environment variable from a connection string resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["envVarName"] = serialize_value(env_var_name)
        args["resource"] = serialize_value(resource)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentConnectionString", args)

    def with_args(self, args: list[str]) -> IResourceWithArgs:
        """Adds arguments"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["args"] = serialize_value(args)
        return self._client.invoke_capability("Aspire.Hosting/withArgs", args)

    def with_args_callback(self, callback: Callable[[CommandLineArgsCallbackContext], None]) -> IResourceWithArgs:
        """Sets command-line arguments via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withArgsCallback", args)

    def with_args_callback_async(self, callback: Callable[[CommandLineArgsCallbackContext], None]) -> IResourceWithArgs:
        """Sets command-line arguments via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withArgsCallbackAsync", args)

    def with_reference(self, source: IResource, connection_name: str | None = None, optional: bool = False, name: str | None = None) -> IResourceWithEnvironment:
        """Adds a reference to another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["source"] = serialize_value(source)
        if connection_name is not None:
            args["connectionName"] = serialize_value(connection_name)
        args["optional"] = serialize_value(optional)
        if name is not None:
            args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/withReference", args)

    def with_reference_uri(self, name: str, uri: str) -> IResourceWithEnvironment:
        """Adds a reference to a URI"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["uri"] = serialize_value(uri)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceUri", args)

    def with_reference_external_service(self, external_service: ExternalServiceResource) -> IResourceWithEnvironment:
        """Adds a reference to an external service"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["externalService"] = serialize_value(external_service)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceExternalService", args)

    def with_reference_endpoint(self, endpoint_reference: EndpointReference) -> IResourceWithEnvironment:
        """Adds a reference to an endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointReference"] = serialize_value(endpoint_reference)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceEndpoint", args)

    def with_endpoint(self, port: float | None = None, target_port: float | None = None, scheme: str | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True, is_external: bool | None = None, protocol: ProtocolType | None = None) -> IResourceWithEndpoints:
        """Adds a network endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if scheme is not None:
            args["scheme"] = serialize_value(scheme)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        if is_external is not None:
            args["isExternal"] = serialize_value(is_external)
        if protocol is not None:
            args["protocol"] = serialize_value(protocol)
        return self._client.invoke_capability("Aspire.Hosting/withEndpoint", args)

    def with_http_endpoint(self, port: float | None = None, target_port: float | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> IResourceWithEndpoints:
        """Adds an HTTP endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        return self._client.invoke_capability("Aspire.Hosting/withHttpEndpoint", args)

    def with_https_endpoint(self, port: float | None = None, target_port: float | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> IResourceWithEndpoints:
        """Adds an HTTPS endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        return self._client.invoke_capability("Aspire.Hosting/withHttpsEndpoint", args)

    def with_external_http_endpoints(self) -> IResourceWithEndpoints:
        """Makes HTTP endpoints externally accessible"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExternalHttpEndpoints", args)

    def get_endpoint(self, name: str) -> EndpointReference:
        """Gets an endpoint reference"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/getEndpoint", args)

    def as_http2_service(self) -> IResourceWithEndpoints:
        """Configures resource for HTTP/2"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/asHttp2Service", args)

    def with_urls_callback(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)

    def with_urls_callback_async(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallbackAsync", args)

    def with_url(self, url: str, display_text: str | None = None) -> IResource:
        """Adds or modifies displayed URLs"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrl", args)

    def with_url_expression(self, url: ReferenceExpression, display_text: str | None = None) -> IResource:
        """Adds a URL using a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrlExpression", args)

    def with_url_for_endpoint(self, endpoint_name: str, callback: Callable[[ResourceUrlAnnotation], None]) -> IResource:
        """Customizes the URL for a specific endpoint via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)

    def with_url_for_endpoint_factory(self, endpoint_name: str, callback: Callable[[EndpointReference], ResourceUrlAnnotation]) -> IResourceWithEndpoints:
        """Adds a URL for a specific endpoint via factory callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpointFactory", args)

    def exclude_from_manifest(self) -> IResource:
        """Excludes the resource from the deployment manifest"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)

    def wait_for(self, dependency: IResource) -> IResourceWithWaitSupport:
        """Waits for another resource to be ready"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting/waitFor", args)

    def wait_for_with_behavior(self, dependency: IResource, wait_behavior: WaitBehavior) -> IResourceWithWaitSupport:
        """Waits for another resource with specific behavior"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["waitBehavior"] = serialize_value(wait_behavior)
        return self._client.invoke_capability("Aspire.Hosting/waitForWithBehavior", args)

    def wait_for_start(self, dependency: IResource) -> IResourceWithWaitSupport:
        """Waits for another resource to start"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting/waitForStart", args)

    def wait_for_start_with_behavior(self, dependency: IResource, wait_behavior: WaitBehavior) -> IResourceWithWaitSupport:
        """Waits for another resource to start with specific behavior"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["waitBehavior"] = serialize_value(wait_behavior)
        return self._client.invoke_capability("Aspire.Hosting/waitForStartWithBehavior", args)

    def with_explicit_start(self) -> IResource:
        """Prevents resource from starting automatically"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExplicitStart", args)

    def wait_for_completion(self, dependency: IResource, exit_code: float = 0) -> IResourceWithWaitSupport:
        """Waits for resource completion"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["exitCode"] = serialize_value(exit_code)
        return self._client.invoke_capability("Aspire.Hosting/waitForCompletion", args)

    def with_health_check(self, key: str) -> IResource:
        """Adds a health check by key"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["key"] = serialize_value(key)
        return self._client.invoke_capability("Aspire.Hosting/withHealthCheck", args)

    def with_http_health_check(self, path: str | None = None, status_code: float | None = None, endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Adds an HTTP health check"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if path is not None:
            args["path"] = serialize_value(path)
        if status_code is not None:
            args["statusCode"] = serialize_value(status_code)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withHttpHealthCheck", args)

    def with_command(self, name: str, display_name: str, execute_command: Callable[[ExecuteCommandContext], ExecuteCommandResult], command_options: CommandOptions | None = None) -> IResource:
        """Adds a resource command"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["displayName"] = serialize_value(display_name)
        execute_command_id = register_callback(execute_command) if execute_command is not None else None
        if execute_command_id is not None:
            args["executeCommand"] = execute_command_id
        if command_options is not None:
            args["commandOptions"] = serialize_value(command_options)
        return self._client.invoke_capability("Aspire.Hosting/withCommand", args)

    def with_developer_certificate_trust(self, trust: bool) -> IResourceWithEnvironment:
        """Configures developer certificate trust"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["trust"] = serialize_value(trust)
        return self._client.invoke_capability("Aspire.Hosting/withDeveloperCertificateTrust", args)

    def with_certificate_trust_scope(self, scope: CertificateTrustScope) -> IResourceWithEnvironment:
        """Sets the certificate trust scope"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["scope"] = serialize_value(scope)
        return self._client.invoke_capability("Aspire.Hosting/withCertificateTrustScope", args)

    def with_https_developer_certificate(self, password: ParameterResource | None = None) -> IResourceWithEnvironment:
        """Configures HTTPS with a developer certificate"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if password is not None:
            args["password"] = serialize_value(password)
        return self._client.invoke_capability("Aspire.Hosting/withHttpsDeveloperCertificate", args)

    def without_https_certificate(self) -> IResourceWithEnvironment:
        """Removes HTTPS certificate configuration"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withoutHttpsCertificate", args)

    def with_parent_relationship(self, parent: IResource) -> IResource:
        """Sets the parent relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["parent"] = serialize_value(parent)
        return self._client.invoke_capability("Aspire.Hosting/withParentRelationship", args)

    def with_child_relationship(self, child: IResource) -> IResource:
        """Sets a child relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["child"] = serialize_value(child)
        return self._client.invoke_capability("Aspire.Hosting/withChildRelationship", args)

    def with_icon_name(self, icon_name: str, icon_variant: IconVariant = None) -> IResource:
        """Sets the icon for the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["iconName"] = serialize_value(icon_name)
        args["iconVariant"] = serialize_value(icon_variant)
        return self._client.invoke_capability("Aspire.Hosting/withIconName", args)

    def with_http_probe(self, probe_type: ProbeType, path: str | None = None, initial_delay_seconds: float | None = None, period_seconds: float | None = None, timeout_seconds: float | None = None, failure_threshold: float | None = None, success_threshold: float | None = None, endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Adds an HTTP health probe to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["probeType"] = serialize_value(probe_type)
        if path is not None:
            args["path"] = serialize_value(path)
        if initial_delay_seconds is not None:
            args["initialDelaySeconds"] = serialize_value(initial_delay_seconds)
        if period_seconds is not None:
            args["periodSeconds"] = serialize_value(period_seconds)
        if timeout_seconds is not None:
            args["timeoutSeconds"] = serialize_value(timeout_seconds)
        if failure_threshold is not None:
            args["failureThreshold"] = serialize_value(failure_threshold)
        if success_threshold is not None:
            args["successThreshold"] = serialize_value(success_threshold)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withHttpProbe", args)

    def exclude_from_mcp(self) -> IResource:
        """Excludes the resource from MCP server exposure"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)

    def with_remote_image_name(self, remote_image_name: str) -> IComputeResource:
        """Sets the remote image name for publishing"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["remoteImageName"] = serialize_value(remote_image_name)
        return self._client.invoke_capability("Aspire.Hosting/withRemoteImageName", args)

    def with_remote_image_tag(self, remote_image_tag: str) -> IComputeResource:
        """Sets the remote image tag for publishing"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["remoteImageTag"] = serialize_value(remote_image_tag)
        return self._client.invoke_capability("Aspire.Hosting/withRemoteImageTag", args)

    def with_pipeline_step_factory(self, step_name: str, callback: Callable[[PipelineStepContext], None], depends_on: list[str] | None = None, required_by: list[str] | None = None, tags: list[str] | None = None, description: str | None = None) -> IResource:
        """Adds a pipeline step to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["stepName"] = serialize_value(step_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        if depends_on is not None:
            args["dependsOn"] = serialize_value(depends_on)
        if required_by is not None:
            args["requiredBy"] = serialize_value(required_by)
        if tags is not None:
            args["tags"] = serialize_value(tags)
        if description is not None:
            args["description"] = serialize_value(description)
        return self._client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)

    def with_pipeline_configuration_async(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via an async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfigurationAsync", args)

    def with_pipeline_configuration(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via a callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)

    def with_volume(self, target: str, name: str | None = None, is_read_only: bool = False) -> ContainerResource:
        """Adds a volume"""
        args: Dict[str, Any] = { "resource": serialize_value(self._handle) }
        args["target"] = serialize_value(target)
        if name is not None:
            args["name"] = serialize_value(name)
        args["isReadOnly"] = serialize_value(is_read_only)
        return self._client.invoke_capability("Aspire.Hosting/withVolume", args)

    def get_resource_name(self) -> str:
        """Gets the resource name"""
        args: Dict[str, Any] = { "resource": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getResourceName", args)

    def on_before_resource_started(self, callback: Callable[[BeforeResourceStartedEvent], None]) -> IResource:
        """Subscribes to the BeforeResourceStarted event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)

    def on_resource_stopped(self, callback: Callable[[ResourceStoppedEvent], None]) -> IResource:
        """Subscribes to the ResourceStopped event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceStopped", args)

    def on_initialize_resource(self, callback: Callable[[InitializeResourceEvent], None]) -> IResource:
        """Subscribes to the InitializeResource event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onInitializeResource", args)

    def on_resource_endpoints_allocated(self, callback: Callable[[ResourceEndpointsAllocatedEvent], None]) -> IResourceWithEndpoints:
        """Subscribes to the ResourceEndpointsAllocated event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceEndpointsAllocated", args)

    def on_resource_ready(self, callback: Callable[[ResourceReadyEvent], None]) -> IResource:
        """Subscribes to the ResourceReady event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceReady", args)

    def with_optional_string(self, value: str | None = None, enabled: bool = True) -> IResource:
        """Adds an optional string parameter"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if value is not None:
            args["value"] = serialize_value(value)
        args["enabled"] = serialize_value(enabled)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalString", args)

    def with_config(self, config: TestConfigDto) -> IResource:
        """Configures the resource with a DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConfig", args)

    def test_with_environment_callback(self, callback: Callable[[TestEnvironmentContext], None]) -> IResourceWithEnvironment:
        """Configures environment with callback (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWithEnvironmentCallback", args)

    def with_created_at(self, created_at: str) -> IResource:
        """Sets the created timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["createdAt"] = serialize_value(created_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCreatedAt", args)

    def with_modified_at(self, modified_at: str) -> IResource:
        """Sets the modified timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["modifiedAt"] = serialize_value(modified_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withModifiedAt", args)

    def with_correlation_id(self, correlation_id: str) -> IResource:
        """Sets the correlation ID"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["correlationId"] = serialize_value(correlation_id)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCorrelationId", args)

    def with_optional_callback(self, callback: Callable[[TestCallbackContext], None] | None = None) -> IResource:
        """Configures with optional callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalCallback", args)

    def with_status(self, status: TestResourceStatus) -> IResource:
        """Sets the resource status"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["status"] = serialize_value(status)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withStatus", args)

    def with_nested_config(self, config: TestNestedDto) -> IResource:
        """Configures with nested DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withNestedConfig", args)

    def with_validator(self, validator: Callable[[TestResourceContext], bool]) -> IResource:
        """Adds validation callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        validator_id = register_callback(validator) if validator is not None else None
        if validator_id is not None:
            args["validator"] = validator_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withValidator", args)

    def test_wait_for(self, dependency: IResource) -> IResource:
        """Waits for another resource (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWaitFor", args)

    def with_dependency(self, dependency: IResourceWithConnectionString) -> IResource:
        """Adds a dependency on another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withDependency", args)

    def with_endpoints(self, endpoints: list[str]) -> IResource:
        """Sets the endpoints"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpoints"] = serialize_value(endpoints)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEndpoints", args)

    def with_environment_variables(self, variables: dict[str, str]) -> IResourceWithEnvironment:
        """Sets environment variables"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["variables"] = serialize_value(variables)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEnvironmentVariables", args)

    def with_cancellable_operation(self, operation: Callable[[CancellationToken], None]) -> IResource:
        """Performs a cancellable operation"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        operation_id = register_callback(operation) if operation is not None else None
        if operation_id is not None:
            args["operation"] = operation_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCancellableOperation", args)


class TestEnvironmentContext(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def name(self) -> str:
        """Gets the Name property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.name", args)

    def set_name(self, value: str) -> TestEnvironmentContext:
        """Sets the Name property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setName", args)

    def description(self) -> str:
        """Gets the Description property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.description", args)

    def set_description(self, value: str) -> TestEnvironmentContext:
        """Sets the Description property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setDescription", args)

    def priority(self) -> float:
        """Gets the Priority property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.priority", args)

    def set_priority(self, value: float) -> TestEnvironmentContext:
        """Sets the Priority property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setPriority", args)


class TestRedisResource(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def with_container_registry(self, registry: IResource) -> IResource:
        """Configures a resource to use a container registry"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["registry"] = serialize_value(registry)
        return self._client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)

    def with_bind_mount(self, source: str, target: str, is_read_only: bool = False) -> ContainerResource:
        """Adds a bind mount"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["source"] = serialize_value(source)
        args["target"] = serialize_value(target)
        args["isReadOnly"] = serialize_value(is_read_only)
        return self._client.invoke_capability("Aspire.Hosting/withBindMount", args)

    def with_entrypoint(self, entrypoint: str) -> ContainerResource:
        """Sets the container entrypoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["entrypoint"] = serialize_value(entrypoint)
        return self._client.invoke_capability("Aspire.Hosting/withEntrypoint", args)

    def with_image_tag(self, tag: str) -> ContainerResource:
        """Sets the container image tag"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["tag"] = serialize_value(tag)
        return self._client.invoke_capability("Aspire.Hosting/withImageTag", args)

    def with_image_registry(self, registry: str) -> ContainerResource:
        """Sets the container image registry"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["registry"] = serialize_value(registry)
        return self._client.invoke_capability("Aspire.Hosting/withImageRegistry", args)

    def with_image(self, image: str, tag: str | None = None) -> ContainerResource:
        """Sets the container image"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["image"] = serialize_value(image)
        if tag is not None:
            args["tag"] = serialize_value(tag)
        return self._client.invoke_capability("Aspire.Hosting/withImage", args)

    def with_image_sha256(self, sha256: str) -> ContainerResource:
        """Sets the image SHA256 digest"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["sha256"] = serialize_value(sha256)
        return self._client.invoke_capability("Aspire.Hosting/withImageSHA256", args)

    def with_container_runtime_args(self, args: list[str]) -> ContainerResource:
        """Adds runtime arguments for the container"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["args"] = serialize_value(args)
        return self._client.invoke_capability("Aspire.Hosting/withContainerRuntimeArgs", args)

    def with_lifetime(self, lifetime: ContainerLifetime) -> ContainerResource:
        """Sets the lifetime behavior of the container resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["lifetime"] = serialize_value(lifetime)
        return self._client.invoke_capability("Aspire.Hosting/withLifetime", args)

    def with_image_pull_policy(self, pull_policy: ImagePullPolicy) -> ContainerResource:
        """Sets the container image pull policy"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["pullPolicy"] = serialize_value(pull_policy)
        return self._client.invoke_capability("Aspire.Hosting/withImagePullPolicy", args)

    def publish_as_container(self) -> ContainerResource:
        """Configures the resource to be published as a container"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/publishAsContainer", args)

    def with_dockerfile(self, context_path: str, dockerfile_path: str | None = None, stage: str | None = None) -> ContainerResource:
        """Configures the resource to use a Dockerfile"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["contextPath"] = serialize_value(context_path)
        if dockerfile_path is not None:
            args["dockerfilePath"] = serialize_value(dockerfile_path)
        if stage is not None:
            args["stage"] = serialize_value(stage)
        return self._client.invoke_capability("Aspire.Hosting/withDockerfile", args)

    def with_container_name(self, name: str) -> ContainerResource:
        """Sets the container name"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/withContainerName", args)

    def with_build_arg(self, name: str, value: ParameterResource) -> ContainerResource:
        """Adds a build argument from a parameter resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withBuildArg", args)

    def with_build_secret(self, name: str, value: ParameterResource) -> ContainerResource:
        """Adds a build secret from a parameter resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withBuildSecret", args)

    def with_endpoint_proxy_support(self, proxy_enabled: bool) -> ContainerResource:
        """Configures endpoint proxy support"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["proxyEnabled"] = serialize_value(proxy_enabled)
        return self._client.invoke_capability("Aspire.Hosting/withEndpointProxySupport", args)

    def with_dockerfile_base_image(self, build_image: str | None = None, runtime_image: str | None = None) -> IResource:
        """Sets the base image for a Dockerfile build"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if build_image is not None:
            args["buildImage"] = serialize_value(build_image)
        if runtime_image is not None:
            args["runtimeImage"] = serialize_value(runtime_image)
        return self._client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)

    def with_container_network_alias(self, alias: str) -> ContainerResource:
        """Adds a network alias for the container"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["alias"] = serialize_value(alias)
        return self._client.invoke_capability("Aspire.Hosting/withContainerNetworkAlias", args)

    def with_mcp_server(self, path: str = "/mcp", endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Configures an MCP server endpoint on the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["path"] = serialize_value(path)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withMcpServer", args)

    def with_otlp_exporter(self) -> IResourceWithEnvironment:
        """Configures OTLP telemetry export"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withOtlpExporter", args)

    def with_otlp_exporter_protocol(self, protocol: OtlpProtocol) -> IResourceWithEnvironment:
        """Configures OTLP telemetry export with specific protocol"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["protocol"] = serialize_value(protocol)
        return self._client.invoke_capability("Aspire.Hosting/withOtlpExporterProtocol", args)

    def publish_as_connection_string(self) -> ContainerResource:
        """Publishes the resource as a connection string"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/publishAsConnectionString", args)

    def with_required_command(self, command: str, help_link: str | None = None) -> IResource:
        """Adds a required command dependency"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["command"] = serialize_value(command)
        if help_link is not None:
            args["helpLink"] = serialize_value(help_link)
        return self._client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)

    def with_environment(self, name: str, value: str) -> IResourceWithEnvironment:
        """Sets an environment variable"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironment", args)

    def with_environment_expression(self, name: str, value: ReferenceExpression) -> IResourceWithEnvironment:
        """Adds an environment variable with a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentExpression", args)

    def with_environment_callback(self, callback: Callable[[EnvironmentCallbackContext], None]) -> IResourceWithEnvironment:
        """Sets environment variables via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentCallback", args)

    def with_environment_callback_async(self, callback: Callable[[EnvironmentCallbackContext], None]) -> IResourceWithEnvironment:
        """Sets environment variables via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentCallbackAsync", args)

    def with_environment_endpoint(self, name: str, endpoint_reference: EndpointReference) -> IResourceWithEnvironment:
        """Sets an environment variable from an endpoint reference"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["endpointReference"] = serialize_value(endpoint_reference)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentEndpoint", args)

    def with_environment_parameter(self, name: str, parameter: ParameterResource) -> IResourceWithEnvironment:
        """Sets an environment variable from a parameter resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["parameter"] = serialize_value(parameter)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentParameter", args)

    def with_environment_connection_string(self, env_var_name: str, resource: IResourceWithConnectionString) -> IResourceWithEnvironment:
        """Sets an environment variable from a connection string resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["envVarName"] = serialize_value(env_var_name)
        args["resource"] = serialize_value(resource)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentConnectionString", args)

    def with_connection_property(self, name: str, value: ReferenceExpression) -> IResourceWithConnectionString:
        """Adds a connection property with a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withConnectionProperty", args)

    def with_connection_property_value(self, name: str, value: str) -> IResourceWithConnectionString:
        """Adds a connection property with a string value"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withConnectionPropertyValue", args)

    def with_args(self, args: list[str]) -> IResourceWithArgs:
        """Adds arguments"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["args"] = serialize_value(args)
        return self._client.invoke_capability("Aspire.Hosting/withArgs", args)

    def with_args_callback(self, callback: Callable[[CommandLineArgsCallbackContext], None]) -> IResourceWithArgs:
        """Sets command-line arguments via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withArgsCallback", args)

    def with_args_callback_async(self, callback: Callable[[CommandLineArgsCallbackContext], None]) -> IResourceWithArgs:
        """Sets command-line arguments via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withArgsCallbackAsync", args)

    def with_reference(self, source: IResource, connection_name: str | None = None, optional: bool = False, name: str | None = None) -> IResourceWithEnvironment:
        """Adds a reference to another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["source"] = serialize_value(source)
        if connection_name is not None:
            args["connectionName"] = serialize_value(connection_name)
        args["optional"] = serialize_value(optional)
        if name is not None:
            args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/withReference", args)

    def with_reference_uri(self, name: str, uri: str) -> IResourceWithEnvironment:
        """Adds a reference to a URI"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["uri"] = serialize_value(uri)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceUri", args)

    def with_reference_external_service(self, external_service: ExternalServiceResource) -> IResourceWithEnvironment:
        """Adds a reference to an external service"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["externalService"] = serialize_value(external_service)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceExternalService", args)

    def with_reference_endpoint(self, endpoint_reference: EndpointReference) -> IResourceWithEnvironment:
        """Adds a reference to an endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointReference"] = serialize_value(endpoint_reference)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceEndpoint", args)

    def with_endpoint(self, port: float | None = None, target_port: float | None = None, scheme: str | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True, is_external: bool | None = None, protocol: ProtocolType | None = None) -> IResourceWithEndpoints:
        """Adds a network endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if scheme is not None:
            args["scheme"] = serialize_value(scheme)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        if is_external is not None:
            args["isExternal"] = serialize_value(is_external)
        if protocol is not None:
            args["protocol"] = serialize_value(protocol)
        return self._client.invoke_capability("Aspire.Hosting/withEndpoint", args)

    def with_http_endpoint(self, port: float | None = None, target_port: float | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> IResourceWithEndpoints:
        """Adds an HTTP endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        return self._client.invoke_capability("Aspire.Hosting/withHttpEndpoint", args)

    def with_https_endpoint(self, port: float | None = None, target_port: float | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> IResourceWithEndpoints:
        """Adds an HTTPS endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        return self._client.invoke_capability("Aspire.Hosting/withHttpsEndpoint", args)

    def with_external_http_endpoints(self) -> IResourceWithEndpoints:
        """Makes HTTP endpoints externally accessible"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExternalHttpEndpoints", args)

    def get_endpoint(self, name: str) -> EndpointReference:
        """Gets an endpoint reference"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/getEndpoint", args)

    def as_http2_service(self) -> IResourceWithEndpoints:
        """Configures resource for HTTP/2"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/asHttp2Service", args)

    def with_urls_callback(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)

    def with_urls_callback_async(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallbackAsync", args)

    def with_url(self, url: str, display_text: str | None = None) -> IResource:
        """Adds or modifies displayed URLs"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrl", args)

    def with_url_expression(self, url: ReferenceExpression, display_text: str | None = None) -> IResource:
        """Adds a URL using a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrlExpression", args)

    def with_url_for_endpoint(self, endpoint_name: str, callback: Callable[[ResourceUrlAnnotation], None]) -> IResource:
        """Customizes the URL for a specific endpoint via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)

    def with_url_for_endpoint_factory(self, endpoint_name: str, callback: Callable[[EndpointReference], ResourceUrlAnnotation]) -> IResourceWithEndpoints:
        """Adds a URL for a specific endpoint via factory callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpointFactory", args)

    def exclude_from_manifest(self) -> IResource:
        """Excludes the resource from the deployment manifest"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)

    def wait_for(self, dependency: IResource) -> IResourceWithWaitSupport:
        """Waits for another resource to be ready"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting/waitFor", args)

    def wait_for_with_behavior(self, dependency: IResource, wait_behavior: WaitBehavior) -> IResourceWithWaitSupport:
        """Waits for another resource with specific behavior"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["waitBehavior"] = serialize_value(wait_behavior)
        return self._client.invoke_capability("Aspire.Hosting/waitForWithBehavior", args)

    def wait_for_start(self, dependency: IResource) -> IResourceWithWaitSupport:
        """Waits for another resource to start"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting/waitForStart", args)

    def wait_for_start_with_behavior(self, dependency: IResource, wait_behavior: WaitBehavior) -> IResourceWithWaitSupport:
        """Waits for another resource to start with specific behavior"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["waitBehavior"] = serialize_value(wait_behavior)
        return self._client.invoke_capability("Aspire.Hosting/waitForStartWithBehavior", args)

    def with_explicit_start(self) -> IResource:
        """Prevents resource from starting automatically"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExplicitStart", args)

    def wait_for_completion(self, dependency: IResource, exit_code: float = 0) -> IResourceWithWaitSupport:
        """Waits for resource completion"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["exitCode"] = serialize_value(exit_code)
        return self._client.invoke_capability("Aspire.Hosting/waitForCompletion", args)

    def with_health_check(self, key: str) -> IResource:
        """Adds a health check by key"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["key"] = serialize_value(key)
        return self._client.invoke_capability("Aspire.Hosting/withHealthCheck", args)

    def with_http_health_check(self, path: str | None = None, status_code: float | None = None, endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Adds an HTTP health check"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if path is not None:
            args["path"] = serialize_value(path)
        if status_code is not None:
            args["statusCode"] = serialize_value(status_code)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withHttpHealthCheck", args)

    def with_command(self, name: str, display_name: str, execute_command: Callable[[ExecuteCommandContext], ExecuteCommandResult], command_options: CommandOptions | None = None) -> IResource:
        """Adds a resource command"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["displayName"] = serialize_value(display_name)
        execute_command_id = register_callback(execute_command) if execute_command is not None else None
        if execute_command_id is not None:
            args["executeCommand"] = execute_command_id
        if command_options is not None:
            args["commandOptions"] = serialize_value(command_options)
        return self._client.invoke_capability("Aspire.Hosting/withCommand", args)

    def with_developer_certificate_trust(self, trust: bool) -> IResourceWithEnvironment:
        """Configures developer certificate trust"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["trust"] = serialize_value(trust)
        return self._client.invoke_capability("Aspire.Hosting/withDeveloperCertificateTrust", args)

    def with_certificate_trust_scope(self, scope: CertificateTrustScope) -> IResourceWithEnvironment:
        """Sets the certificate trust scope"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["scope"] = serialize_value(scope)
        return self._client.invoke_capability("Aspire.Hosting/withCertificateTrustScope", args)

    def with_https_developer_certificate(self, password: ParameterResource | None = None) -> IResourceWithEnvironment:
        """Configures HTTPS with a developer certificate"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if password is not None:
            args["password"] = serialize_value(password)
        return self._client.invoke_capability("Aspire.Hosting/withHttpsDeveloperCertificate", args)

    def without_https_certificate(self) -> IResourceWithEnvironment:
        """Removes HTTPS certificate configuration"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withoutHttpsCertificate", args)

    def with_parent_relationship(self, parent: IResource) -> IResource:
        """Sets the parent relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["parent"] = serialize_value(parent)
        return self._client.invoke_capability("Aspire.Hosting/withParentRelationship", args)

    def with_child_relationship(self, child: IResource) -> IResource:
        """Sets a child relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["child"] = serialize_value(child)
        return self._client.invoke_capability("Aspire.Hosting/withChildRelationship", args)

    def with_icon_name(self, icon_name: str, icon_variant: IconVariant = None) -> IResource:
        """Sets the icon for the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["iconName"] = serialize_value(icon_name)
        args["iconVariant"] = serialize_value(icon_variant)
        return self._client.invoke_capability("Aspire.Hosting/withIconName", args)

    def with_http_probe(self, probe_type: ProbeType, path: str | None = None, initial_delay_seconds: float | None = None, period_seconds: float | None = None, timeout_seconds: float | None = None, failure_threshold: float | None = None, success_threshold: float | None = None, endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Adds an HTTP health probe to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["probeType"] = serialize_value(probe_type)
        if path is not None:
            args["path"] = serialize_value(path)
        if initial_delay_seconds is not None:
            args["initialDelaySeconds"] = serialize_value(initial_delay_seconds)
        if period_seconds is not None:
            args["periodSeconds"] = serialize_value(period_seconds)
        if timeout_seconds is not None:
            args["timeoutSeconds"] = serialize_value(timeout_seconds)
        if failure_threshold is not None:
            args["failureThreshold"] = serialize_value(failure_threshold)
        if success_threshold is not None:
            args["successThreshold"] = serialize_value(success_threshold)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withHttpProbe", args)

    def exclude_from_mcp(self) -> IResource:
        """Excludes the resource from MCP server exposure"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)

    def with_remote_image_name(self, remote_image_name: str) -> IComputeResource:
        """Sets the remote image name for publishing"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["remoteImageName"] = serialize_value(remote_image_name)
        return self._client.invoke_capability("Aspire.Hosting/withRemoteImageName", args)

    def with_remote_image_tag(self, remote_image_tag: str) -> IComputeResource:
        """Sets the remote image tag for publishing"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["remoteImageTag"] = serialize_value(remote_image_tag)
        return self._client.invoke_capability("Aspire.Hosting/withRemoteImageTag", args)

    def with_pipeline_step_factory(self, step_name: str, callback: Callable[[PipelineStepContext], None], depends_on: list[str] | None = None, required_by: list[str] | None = None, tags: list[str] | None = None, description: str | None = None) -> IResource:
        """Adds a pipeline step to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["stepName"] = serialize_value(step_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        if depends_on is not None:
            args["dependsOn"] = serialize_value(depends_on)
        if required_by is not None:
            args["requiredBy"] = serialize_value(required_by)
        if tags is not None:
            args["tags"] = serialize_value(tags)
        if description is not None:
            args["description"] = serialize_value(description)
        return self._client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)

    def with_pipeline_configuration_async(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via an async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfigurationAsync", args)

    def with_pipeline_configuration(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via a callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)

    def with_volume(self, target: str, name: str | None = None, is_read_only: bool = False) -> ContainerResource:
        """Adds a volume"""
        args: Dict[str, Any] = { "resource": serialize_value(self._handle) }
        args["target"] = serialize_value(target)
        if name is not None:
            args["name"] = serialize_value(name)
        args["isReadOnly"] = serialize_value(is_read_only)
        return self._client.invoke_capability("Aspire.Hosting/withVolume", args)

    def get_resource_name(self) -> str:
        """Gets the resource name"""
        args: Dict[str, Any] = { "resource": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getResourceName", args)

    def on_before_resource_started(self, callback: Callable[[BeforeResourceStartedEvent], None]) -> IResource:
        """Subscribes to the BeforeResourceStarted event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)

    def on_resource_stopped(self, callback: Callable[[ResourceStoppedEvent], None]) -> IResource:
        """Subscribes to the ResourceStopped event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceStopped", args)

    def on_connection_string_available(self, callback: Callable[[ConnectionStringAvailableEvent], None]) -> IResourceWithConnectionString:
        """Subscribes to the ConnectionStringAvailable event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onConnectionStringAvailable", args)

    def on_initialize_resource(self, callback: Callable[[InitializeResourceEvent], None]) -> IResource:
        """Subscribes to the InitializeResource event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onInitializeResource", args)

    def on_resource_endpoints_allocated(self, callback: Callable[[ResourceEndpointsAllocatedEvent], None]) -> IResourceWithEndpoints:
        """Subscribes to the ResourceEndpointsAllocated event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceEndpointsAllocated", args)

    def on_resource_ready(self, callback: Callable[[ResourceReadyEvent], None]) -> IResource:
        """Subscribes to the ResourceReady event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceReady", args)

    def add_test_child_database(self, name: str, database_name: str | None = None) -> TestDatabaseResource:
        """Adds a child database to a test Redis resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        if database_name is not None:
            args["databaseName"] = serialize_value(database_name)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/addTestChildDatabase", args)

    def with_persistence(self, mode: TestPersistenceMode = None) -> TestRedisResource:
        """Configures the Redis resource with persistence"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["mode"] = serialize_value(mode)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withPersistence", args)

    def with_optional_string(self, value: str | None = None, enabled: bool = True) -> IResource:
        """Adds an optional string parameter"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if value is not None:
            args["value"] = serialize_value(value)
        args["enabled"] = serialize_value(enabled)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalString", args)

    def with_config(self, config: TestConfigDto) -> IResource:
        """Configures the resource with a DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConfig", args)

    @property
    def get_tags(self) -> AspireList[str]:
        """Gets the tags for the resource"""
        if not hasattr(self, '_get_tags'):
            self._get_tags = AspireList(
                self._handle,
                self._client,
                "Aspire.Hosting.CodeGeneration.Python.Tests/getTags"
            )
        return self._get_tags

    @property
    def get_metadata(self) -> AspireDict[str, str]:
        """Gets the metadata for the resource"""
        if not hasattr(self, '_get_metadata'):
            self._get_metadata = AspireDict(
                self._handle,
                self._client,
                "Aspire.Hosting.CodeGeneration.Python.Tests/getMetadata"
            )
        return self._get_metadata

    def with_connection_string(self, connection_string: ReferenceExpression) -> IResourceWithConnectionString:
        """Sets the connection string using a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["connectionString"] = serialize_value(connection_string)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConnectionString", args)

    def test_with_environment_callback(self, callback: Callable[[TestEnvironmentContext], None]) -> IResourceWithEnvironment:
        """Configures environment with callback (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWithEnvironmentCallback", args)

    def with_created_at(self, created_at: str) -> IResource:
        """Sets the created timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["createdAt"] = serialize_value(created_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCreatedAt", args)

    def with_modified_at(self, modified_at: str) -> IResource:
        """Sets the modified timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["modifiedAt"] = serialize_value(modified_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withModifiedAt", args)

    def with_correlation_id(self, correlation_id: str) -> IResource:
        """Sets the correlation ID"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["correlationId"] = serialize_value(correlation_id)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCorrelationId", args)

    def with_optional_callback(self, callback: Callable[[TestCallbackContext], None] | None = None) -> IResource:
        """Configures with optional callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalCallback", args)

    def with_status(self, status: TestResourceStatus) -> IResource:
        """Sets the resource status"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["status"] = serialize_value(status)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withStatus", args)

    def with_nested_config(self, config: TestNestedDto) -> IResource:
        """Configures with nested DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withNestedConfig", args)

    def with_validator(self, validator: Callable[[TestResourceContext], bool]) -> IResource:
        """Adds validation callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        validator_id = register_callback(validator) if validator is not None else None
        if validator_id is not None:
            args["validator"] = validator_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withValidator", args)

    def test_wait_for(self, dependency: IResource) -> IResource:
        """Waits for another resource (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWaitFor", args)

    def get_endpoints(self) -> list[str]:
        """Gets the endpoints"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/getEndpoints", args)

    def with_connection_string_direct(self, connection_string: str) -> IResourceWithConnectionString:
        """Sets connection string using direct interface target"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["connectionString"] = serialize_value(connection_string)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConnectionStringDirect", args)

    def with_redis_specific(self, option: str) -> TestRedisResource:
        """Redis-specific configuration"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["option"] = serialize_value(option)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withRedisSpecific", args)

    def with_dependency(self, dependency: IResourceWithConnectionString) -> IResource:
        """Adds a dependency on another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withDependency", args)

    def with_endpoints(self, endpoints: list[str]) -> IResource:
        """Sets the endpoints"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpoints"] = serialize_value(endpoints)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEndpoints", args)

    def with_environment_variables(self, variables: dict[str, str]) -> IResourceWithEnvironment:
        """Sets environment variables"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["variables"] = serialize_value(variables)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEnvironmentVariables", args)

    def get_status_async(self, cancellation_token: CancellationToken | None = None) -> str:
        """Gets the status of the resource asynchronously"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        cancellation_token_id = register_cancellation(cancellation_token, self._client) if cancellation_token is not None else None
        if cancellation_token_id is not None:
            args["cancellationToken"] = cancellation_token_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/getStatusAsync", args)

    def with_cancellable_operation(self, operation: Callable[[CancellationToken], None]) -> IResource:
        """Performs a cancellable operation"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        operation_id = register_callback(operation) if operation is not None else None
        if operation_id is not None:
            args["operation"] = operation_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCancellableOperation", args)

    def wait_for_ready_async(self, timeout: float, cancellation_token: CancellationToken | None = None) -> bool:
        """Waits for the resource to be ready"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["timeout"] = serialize_value(timeout)
        cancellation_token_id = register_cancellation(cancellation_token, self._client) if cancellation_token is not None else None
        if cancellation_token_id is not None:
            args["cancellationToken"] = cancellation_token_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/waitForReadyAsync", args)

    def with_multi_param_handle_callback(self, callback: Callable[[TestCallbackContext, TestEnvironmentContext], None]) -> TestRedisResource:
        """Tests multi-param callback destructuring"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withMultiParamHandleCallback", args)

    def with_data_volume(self, name: str | None = None, is_read_only: bool = False) -> TestRedisResource:
        """Adds a data volume with persistence"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if name is not None:
            args["name"] = serialize_value(name)
        args["isReadOnly"] = serialize_value(is_read_only)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withDataVolume", args)


class TestResourceContext(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def name(self) -> str:
        """Gets the Name property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.name", args)

    def set_name(self, value: str) -> TestResourceContext:
        """Sets the Name property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setName", args)

    def value(self) -> float:
        """Gets the Value property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.value", args)

    def set_value(self, value: float) -> TestResourceContext:
        """Sets the Value property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setValue", args)

    def get_value_async(self) -> str:
        """Invokes the GetValueAsync method"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.getValueAsync", args)

    def set_value_async(self, value: str) -> None:
        """Invokes the SetValueAsync method"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setValueAsync", args)
        return None

    def validate_async(self) -> bool:
        """Invokes the ValidateAsync method"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.validateAsync", args)


class TestVaultResource(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def with_container_registry(self, registry: IResource) -> IResource:
        """Configures a resource to use a container registry"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["registry"] = serialize_value(registry)
        return self._client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)

    def with_bind_mount(self, source: str, target: str, is_read_only: bool = False) -> ContainerResource:
        """Adds a bind mount"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["source"] = serialize_value(source)
        args["target"] = serialize_value(target)
        args["isReadOnly"] = serialize_value(is_read_only)
        return self._client.invoke_capability("Aspire.Hosting/withBindMount", args)

    def with_entrypoint(self, entrypoint: str) -> ContainerResource:
        """Sets the container entrypoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["entrypoint"] = serialize_value(entrypoint)
        return self._client.invoke_capability("Aspire.Hosting/withEntrypoint", args)

    def with_image_tag(self, tag: str) -> ContainerResource:
        """Sets the container image tag"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["tag"] = serialize_value(tag)
        return self._client.invoke_capability("Aspire.Hosting/withImageTag", args)

    def with_image_registry(self, registry: str) -> ContainerResource:
        """Sets the container image registry"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["registry"] = serialize_value(registry)
        return self._client.invoke_capability("Aspire.Hosting/withImageRegistry", args)

    def with_image(self, image: str, tag: str | None = None) -> ContainerResource:
        """Sets the container image"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["image"] = serialize_value(image)
        if tag is not None:
            args["tag"] = serialize_value(tag)
        return self._client.invoke_capability("Aspire.Hosting/withImage", args)

    def with_image_sha256(self, sha256: str) -> ContainerResource:
        """Sets the image SHA256 digest"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["sha256"] = serialize_value(sha256)
        return self._client.invoke_capability("Aspire.Hosting/withImageSHA256", args)

    def with_container_runtime_args(self, args: list[str]) -> ContainerResource:
        """Adds runtime arguments for the container"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["args"] = serialize_value(args)
        return self._client.invoke_capability("Aspire.Hosting/withContainerRuntimeArgs", args)

    def with_lifetime(self, lifetime: ContainerLifetime) -> ContainerResource:
        """Sets the lifetime behavior of the container resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["lifetime"] = serialize_value(lifetime)
        return self._client.invoke_capability("Aspire.Hosting/withLifetime", args)

    def with_image_pull_policy(self, pull_policy: ImagePullPolicy) -> ContainerResource:
        """Sets the container image pull policy"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["pullPolicy"] = serialize_value(pull_policy)
        return self._client.invoke_capability("Aspire.Hosting/withImagePullPolicy", args)

    def publish_as_container(self) -> ContainerResource:
        """Configures the resource to be published as a container"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/publishAsContainer", args)

    def with_dockerfile(self, context_path: str, dockerfile_path: str | None = None, stage: str | None = None) -> ContainerResource:
        """Configures the resource to use a Dockerfile"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["contextPath"] = serialize_value(context_path)
        if dockerfile_path is not None:
            args["dockerfilePath"] = serialize_value(dockerfile_path)
        if stage is not None:
            args["stage"] = serialize_value(stage)
        return self._client.invoke_capability("Aspire.Hosting/withDockerfile", args)

    def with_container_name(self, name: str) -> ContainerResource:
        """Sets the container name"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/withContainerName", args)

    def with_build_arg(self, name: str, value: ParameterResource) -> ContainerResource:
        """Adds a build argument from a parameter resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withBuildArg", args)

    def with_build_secret(self, name: str, value: ParameterResource) -> ContainerResource:
        """Adds a build secret from a parameter resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withBuildSecret", args)

    def with_endpoint_proxy_support(self, proxy_enabled: bool) -> ContainerResource:
        """Configures endpoint proxy support"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["proxyEnabled"] = serialize_value(proxy_enabled)
        return self._client.invoke_capability("Aspire.Hosting/withEndpointProxySupport", args)

    def with_dockerfile_base_image(self, build_image: str | None = None, runtime_image: str | None = None) -> IResource:
        """Sets the base image for a Dockerfile build"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if build_image is not None:
            args["buildImage"] = serialize_value(build_image)
        if runtime_image is not None:
            args["runtimeImage"] = serialize_value(runtime_image)
        return self._client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)

    def with_container_network_alias(self, alias: str) -> ContainerResource:
        """Adds a network alias for the container"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["alias"] = serialize_value(alias)
        return self._client.invoke_capability("Aspire.Hosting/withContainerNetworkAlias", args)

    def with_mcp_server(self, path: str = "/mcp", endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Configures an MCP server endpoint on the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["path"] = serialize_value(path)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withMcpServer", args)

    def with_otlp_exporter(self) -> IResourceWithEnvironment:
        """Configures OTLP telemetry export"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withOtlpExporter", args)

    def with_otlp_exporter_protocol(self, protocol: OtlpProtocol) -> IResourceWithEnvironment:
        """Configures OTLP telemetry export with specific protocol"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["protocol"] = serialize_value(protocol)
        return self._client.invoke_capability("Aspire.Hosting/withOtlpExporterProtocol", args)

    def publish_as_connection_string(self) -> ContainerResource:
        """Publishes the resource as a connection string"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/publishAsConnectionString", args)

    def with_required_command(self, command: str, help_link: str | None = None) -> IResource:
        """Adds a required command dependency"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["command"] = serialize_value(command)
        if help_link is not None:
            args["helpLink"] = serialize_value(help_link)
        return self._client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)

    def with_environment(self, name: str, value: str) -> IResourceWithEnvironment:
        """Sets an environment variable"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironment", args)

    def with_environment_expression(self, name: str, value: ReferenceExpression) -> IResourceWithEnvironment:
        """Adds an environment variable with a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentExpression", args)

    def with_environment_callback(self, callback: Callable[[EnvironmentCallbackContext], None]) -> IResourceWithEnvironment:
        """Sets environment variables via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentCallback", args)

    def with_environment_callback_async(self, callback: Callable[[EnvironmentCallbackContext], None]) -> IResourceWithEnvironment:
        """Sets environment variables via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentCallbackAsync", args)

    def with_environment_endpoint(self, name: str, endpoint_reference: EndpointReference) -> IResourceWithEnvironment:
        """Sets an environment variable from an endpoint reference"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["endpointReference"] = serialize_value(endpoint_reference)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentEndpoint", args)

    def with_environment_parameter(self, name: str, parameter: ParameterResource) -> IResourceWithEnvironment:
        """Sets an environment variable from a parameter resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["parameter"] = serialize_value(parameter)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentParameter", args)

    def with_environment_connection_string(self, env_var_name: str, resource: IResourceWithConnectionString) -> IResourceWithEnvironment:
        """Sets an environment variable from a connection string resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["envVarName"] = serialize_value(env_var_name)
        args["resource"] = serialize_value(resource)
        return self._client.invoke_capability("Aspire.Hosting/withEnvironmentConnectionString", args)

    def with_args(self, args: list[str]) -> IResourceWithArgs:
        """Adds arguments"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["args"] = serialize_value(args)
        return self._client.invoke_capability("Aspire.Hosting/withArgs", args)

    def with_args_callback(self, callback: Callable[[CommandLineArgsCallbackContext], None]) -> IResourceWithArgs:
        """Sets command-line arguments via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withArgsCallback", args)

    def with_args_callback_async(self, callback: Callable[[CommandLineArgsCallbackContext], None]) -> IResourceWithArgs:
        """Sets command-line arguments via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withArgsCallbackAsync", args)

    def with_reference(self, source: IResource, connection_name: str | None = None, optional: bool = False, name: str | None = None) -> IResourceWithEnvironment:
        """Adds a reference to another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["source"] = serialize_value(source)
        if connection_name is not None:
            args["connectionName"] = serialize_value(connection_name)
        args["optional"] = serialize_value(optional)
        if name is not None:
            args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/withReference", args)

    def with_reference_uri(self, name: str, uri: str) -> IResourceWithEnvironment:
        """Adds a reference to a URI"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["uri"] = serialize_value(uri)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceUri", args)

    def with_reference_external_service(self, external_service: ExternalServiceResource) -> IResourceWithEnvironment:
        """Adds a reference to an external service"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["externalService"] = serialize_value(external_service)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceExternalService", args)

    def with_reference_endpoint(self, endpoint_reference: EndpointReference) -> IResourceWithEnvironment:
        """Adds a reference to an endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointReference"] = serialize_value(endpoint_reference)
        return self._client.invoke_capability("Aspire.Hosting/withReferenceEndpoint", args)

    def with_endpoint(self, port: float | None = None, target_port: float | None = None, scheme: str | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True, is_external: bool | None = None, protocol: ProtocolType | None = None) -> IResourceWithEndpoints:
        """Adds a network endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if scheme is not None:
            args["scheme"] = serialize_value(scheme)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        if is_external is not None:
            args["isExternal"] = serialize_value(is_external)
        if protocol is not None:
            args["protocol"] = serialize_value(protocol)
        return self._client.invoke_capability("Aspire.Hosting/withEndpoint", args)

    def with_http_endpoint(self, port: float | None = None, target_port: float | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> IResourceWithEndpoints:
        """Adds an HTTP endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        return self._client.invoke_capability("Aspire.Hosting/withHttpEndpoint", args)

    def with_https_endpoint(self, port: float | None = None, target_port: float | None = None, name: str | None = None, env: str | None = None, is_proxied: bool = True) -> IResourceWithEndpoints:
        """Adds an HTTPS endpoint"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if port is not None:
            args["port"] = serialize_value(port)
        if target_port is not None:
            args["targetPort"] = serialize_value(target_port)
        if name is not None:
            args["name"] = serialize_value(name)
        if env is not None:
            args["env"] = serialize_value(env)
        args["isProxied"] = serialize_value(is_proxied)
        return self._client.invoke_capability("Aspire.Hosting/withHttpsEndpoint", args)

    def with_external_http_endpoints(self) -> IResourceWithEndpoints:
        """Makes HTTP endpoints externally accessible"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExternalHttpEndpoints", args)

    def get_endpoint(self, name: str) -> EndpointReference:
        """Gets an endpoint reference"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting/getEndpoint", args)

    def as_http2_service(self) -> IResourceWithEndpoints:
        """Configures resource for HTTP/2"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/asHttp2Service", args)

    def with_urls_callback(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)

    def with_urls_callback_async(self, callback: Callable[[ResourceUrlsCallbackContext], None]) -> IResource:
        """Customizes displayed URLs via async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlsCallbackAsync", args)

    def with_url(self, url: str, display_text: str | None = None) -> IResource:
        """Adds or modifies displayed URLs"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrl", args)

    def with_url_expression(self, url: ReferenceExpression, display_text: str | None = None) -> IResource:
        """Adds a URL using a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["url"] = serialize_value(url)
        if display_text is not None:
            args["displayText"] = serialize_value(display_text)
        return self._client.invoke_capability("Aspire.Hosting/withUrlExpression", args)

    def with_url_for_endpoint(self, endpoint_name: str, callback: Callable[[ResourceUrlAnnotation], None]) -> IResource:
        """Customizes the URL for a specific endpoint via callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)

    def with_url_for_endpoint_factory(self, endpoint_name: str, callback: Callable[[EndpointReference], ResourceUrlAnnotation]) -> IResourceWithEndpoints:
        """Adds a URL for a specific endpoint via factory callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpointName"] = serialize_value(endpoint_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withUrlForEndpointFactory", args)

    def exclude_from_manifest(self) -> IResource:
        """Excludes the resource from the deployment manifest"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)

    def wait_for(self, dependency: IResource) -> IResourceWithWaitSupport:
        """Waits for another resource to be ready"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting/waitFor", args)

    def wait_for_with_behavior(self, dependency: IResource, wait_behavior: WaitBehavior) -> IResourceWithWaitSupport:
        """Waits for another resource with specific behavior"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["waitBehavior"] = serialize_value(wait_behavior)
        return self._client.invoke_capability("Aspire.Hosting/waitForWithBehavior", args)

    def wait_for_start(self, dependency: IResource) -> IResourceWithWaitSupport:
        """Waits for another resource to start"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting/waitForStart", args)

    def wait_for_start_with_behavior(self, dependency: IResource, wait_behavior: WaitBehavior) -> IResourceWithWaitSupport:
        """Waits for another resource to start with specific behavior"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["waitBehavior"] = serialize_value(wait_behavior)
        return self._client.invoke_capability("Aspire.Hosting/waitForStartWithBehavior", args)

    def with_explicit_start(self) -> IResource:
        """Prevents resource from starting automatically"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withExplicitStart", args)

    def wait_for_completion(self, dependency: IResource, exit_code: float = 0) -> IResourceWithWaitSupport:
        """Waits for resource completion"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        args["exitCode"] = serialize_value(exit_code)
        return self._client.invoke_capability("Aspire.Hosting/waitForCompletion", args)

    def with_health_check(self, key: str) -> IResource:
        """Adds a health check by key"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["key"] = serialize_value(key)
        return self._client.invoke_capability("Aspire.Hosting/withHealthCheck", args)

    def with_http_health_check(self, path: str | None = None, status_code: float | None = None, endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Adds an HTTP health check"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if path is not None:
            args["path"] = serialize_value(path)
        if status_code is not None:
            args["statusCode"] = serialize_value(status_code)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withHttpHealthCheck", args)

    def with_command(self, name: str, display_name: str, execute_command: Callable[[ExecuteCommandContext], ExecuteCommandResult], command_options: CommandOptions | None = None) -> IResource:
        """Adds a resource command"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        args["displayName"] = serialize_value(display_name)
        execute_command_id = register_callback(execute_command) if execute_command is not None else None
        if execute_command_id is not None:
            args["executeCommand"] = execute_command_id
        if command_options is not None:
            args["commandOptions"] = serialize_value(command_options)
        return self._client.invoke_capability("Aspire.Hosting/withCommand", args)

    def with_developer_certificate_trust(self, trust: bool) -> IResourceWithEnvironment:
        """Configures developer certificate trust"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["trust"] = serialize_value(trust)
        return self._client.invoke_capability("Aspire.Hosting/withDeveloperCertificateTrust", args)

    def with_certificate_trust_scope(self, scope: CertificateTrustScope) -> IResourceWithEnvironment:
        """Sets the certificate trust scope"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["scope"] = serialize_value(scope)
        return self._client.invoke_capability("Aspire.Hosting/withCertificateTrustScope", args)

    def with_https_developer_certificate(self, password: ParameterResource | None = None) -> IResourceWithEnvironment:
        """Configures HTTPS with a developer certificate"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if password is not None:
            args["password"] = serialize_value(password)
        return self._client.invoke_capability("Aspire.Hosting/withHttpsDeveloperCertificate", args)

    def without_https_certificate(self) -> IResourceWithEnvironment:
        """Removes HTTPS certificate configuration"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/withoutHttpsCertificate", args)

    def with_parent_relationship(self, parent: IResource) -> IResource:
        """Sets the parent relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["parent"] = serialize_value(parent)
        return self._client.invoke_capability("Aspire.Hosting/withParentRelationship", args)

    def with_child_relationship(self, child: IResource) -> IResource:
        """Sets a child relationship"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["child"] = serialize_value(child)
        return self._client.invoke_capability("Aspire.Hosting/withChildRelationship", args)

    def with_icon_name(self, icon_name: str, icon_variant: IconVariant = None) -> IResource:
        """Sets the icon for the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["iconName"] = serialize_value(icon_name)
        args["iconVariant"] = serialize_value(icon_variant)
        return self._client.invoke_capability("Aspire.Hosting/withIconName", args)

    def with_http_probe(self, probe_type: ProbeType, path: str | None = None, initial_delay_seconds: float | None = None, period_seconds: float | None = None, timeout_seconds: float | None = None, failure_threshold: float | None = None, success_threshold: float | None = None, endpoint_name: str | None = None) -> IResourceWithEndpoints:
        """Adds an HTTP health probe to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["probeType"] = serialize_value(probe_type)
        if path is not None:
            args["path"] = serialize_value(path)
        if initial_delay_seconds is not None:
            args["initialDelaySeconds"] = serialize_value(initial_delay_seconds)
        if period_seconds is not None:
            args["periodSeconds"] = serialize_value(period_seconds)
        if timeout_seconds is not None:
            args["timeoutSeconds"] = serialize_value(timeout_seconds)
        if failure_threshold is not None:
            args["failureThreshold"] = serialize_value(failure_threshold)
        if success_threshold is not None:
            args["successThreshold"] = serialize_value(success_threshold)
        if endpoint_name is not None:
            args["endpointName"] = serialize_value(endpoint_name)
        return self._client.invoke_capability("Aspire.Hosting/withHttpProbe", args)

    def exclude_from_mcp(self) -> IResource:
        """Excludes the resource from MCP server exposure"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)

    def with_remote_image_name(self, remote_image_name: str) -> IComputeResource:
        """Sets the remote image name for publishing"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["remoteImageName"] = serialize_value(remote_image_name)
        return self._client.invoke_capability("Aspire.Hosting/withRemoteImageName", args)

    def with_remote_image_tag(self, remote_image_tag: str) -> IComputeResource:
        """Sets the remote image tag for publishing"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["remoteImageTag"] = serialize_value(remote_image_tag)
        return self._client.invoke_capability("Aspire.Hosting/withRemoteImageTag", args)

    def with_pipeline_step_factory(self, step_name: str, callback: Callable[[PipelineStepContext], None], depends_on: list[str] | None = None, required_by: list[str] | None = None, tags: list[str] | None = None, description: str | None = None) -> IResource:
        """Adds a pipeline step to the resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["stepName"] = serialize_value(step_name)
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        if depends_on is not None:
            args["dependsOn"] = serialize_value(depends_on)
        if required_by is not None:
            args["requiredBy"] = serialize_value(required_by)
        if tags is not None:
            args["tags"] = serialize_value(tags)
        if description is not None:
            args["description"] = serialize_value(description)
        return self._client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)

    def with_pipeline_configuration_async(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via an async callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfigurationAsync", args)

    def with_pipeline_configuration(self, callback: Callable[[PipelineConfigurationContext], None]) -> IResource:
        """Configures pipeline step dependencies via a callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)

    def with_volume(self, target: str, name: str | None = None, is_read_only: bool = False) -> ContainerResource:
        """Adds a volume"""
        args: Dict[str, Any] = { "resource": serialize_value(self._handle) }
        args["target"] = serialize_value(target)
        if name is not None:
            args["name"] = serialize_value(name)
        args["isReadOnly"] = serialize_value(is_read_only)
        return self._client.invoke_capability("Aspire.Hosting/withVolume", args)

    def get_resource_name(self) -> str:
        """Gets the resource name"""
        args: Dict[str, Any] = { "resource": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting/getResourceName", args)

    def on_before_resource_started(self, callback: Callable[[BeforeResourceStartedEvent], None]) -> IResource:
        """Subscribes to the BeforeResourceStarted event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)

    def on_resource_stopped(self, callback: Callable[[ResourceStoppedEvent], None]) -> IResource:
        """Subscribes to the ResourceStopped event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceStopped", args)

    def on_initialize_resource(self, callback: Callable[[InitializeResourceEvent], None]) -> IResource:
        """Subscribes to the InitializeResource event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onInitializeResource", args)

    def on_resource_endpoints_allocated(self, callback: Callable[[ResourceEndpointsAllocatedEvent], None]) -> IResourceWithEndpoints:
        """Subscribes to the ResourceEndpointsAllocated event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceEndpointsAllocated", args)

    def on_resource_ready(self, callback: Callable[[ResourceReadyEvent], None]) -> IResource:
        """Subscribes to the ResourceReady event"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting/onResourceReady", args)

    def with_optional_string(self, value: str | None = None, enabled: bool = True) -> IResource:
        """Adds an optional string parameter"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if value is not None:
            args["value"] = serialize_value(value)
        args["enabled"] = serialize_value(enabled)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalString", args)

    def with_config(self, config: TestConfigDto) -> IResource:
        """Configures the resource with a DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConfig", args)

    def test_with_environment_callback(self, callback: Callable[[TestEnvironmentContext], None]) -> IResourceWithEnvironment:
        """Configures environment with callback (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWithEnvironmentCallback", args)

    def with_created_at(self, created_at: str) -> IResource:
        """Sets the created timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["createdAt"] = serialize_value(created_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCreatedAt", args)

    def with_modified_at(self, modified_at: str) -> IResource:
        """Sets the modified timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["modifiedAt"] = serialize_value(modified_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withModifiedAt", args)

    def with_correlation_id(self, correlation_id: str) -> IResource:
        """Sets the correlation ID"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["correlationId"] = serialize_value(correlation_id)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCorrelationId", args)

    def with_optional_callback(self, callback: Callable[[TestCallbackContext], None] | None = None) -> IResource:
        """Configures with optional callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalCallback", args)

    def with_status(self, status: TestResourceStatus) -> IResource:
        """Sets the resource status"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["status"] = serialize_value(status)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withStatus", args)

    def with_nested_config(self, config: TestNestedDto) -> IResource:
        """Configures with nested DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withNestedConfig", args)

    def with_validator(self, validator: Callable[[TestResourceContext], bool]) -> IResource:
        """Adds validation callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        validator_id = register_callback(validator) if validator is not None else None
        if validator_id is not None:
            args["validator"] = validator_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withValidator", args)

    def test_wait_for(self, dependency: IResource) -> IResource:
        """Waits for another resource (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWaitFor", args)

    def with_dependency(self, dependency: IResourceWithConnectionString) -> IResource:
        """Adds a dependency on another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withDependency", args)

    def with_endpoints(self, endpoints: list[str]) -> IResource:
        """Sets the endpoints"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpoints"] = serialize_value(endpoints)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEndpoints", args)

    def with_environment_variables(self, variables: dict[str, str]) -> IResourceWithEnvironment:
        """Sets environment variables"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["variables"] = serialize_value(variables)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEnvironmentVariables", args)

    def with_cancellable_operation(self, operation: Callable[[CancellationToken], None]) -> IResource:
        """Performs a cancellable operation"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        operation_id = register_callback(operation) if operation is not None else None
        if operation_id is not None:
            args["operation"] = operation_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCancellableOperation", args)

    def with_vault_direct(self, option: str) -> ITestVaultResource:
        """Configures vault using direct interface target"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["option"] = serialize_value(option)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withVaultDirect", args)


class UpdateCommandStateContext(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def service_provider(self) -> IServiceProvider:
        """Gets the ServiceProvider property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/UpdateCommandStateContext.serviceProvider", args)

    def set_service_provider(self, value: IServiceProvider) -> UpdateCommandStateContext:
        """Sets the ServiceProvider property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.ApplicationModel/UpdateCommandStateContext.setServiceProvider", args)


# ============================================================================
# Handle wrapper registrations
# ============================================================================

register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder", lambda handle, client: IDistributedApplicationBuilder(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.DistributedApplication", lambda handle, client: DistributedApplication(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointReference", lambda handle, client: EndpointReference(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpression", lambda handle, client: ReferenceExpression(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResource", lambda handle, client: IResource(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithEnvironment", lambda handle, client: IResourceWithEnvironment(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithEndpoints", lambda handle, client: IResourceWithEndpoints(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithArgs", lambda handle, client: IResourceWithArgs(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithConnectionString", lambda handle, client: IResourceWithConnectionString(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithWaitSupport", lambda handle, client: IResourceWithWaitSupport(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithParent", lambda handle, client: IResourceWithParent(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerResource", lambda handle, client: ContainerResource(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ExecutableResource", lambda handle, client: ExecutableResource(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ProjectResource", lambda handle, client: ProjectResource(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ParameterResource", lambda handle, client: ParameterResource(handle, client))
register_handle_wrapper("System.ComponentModel/System.IServiceProvider", lambda handle, client: IServiceProvider(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceNotificationService", lambda handle, client: ResourceNotificationService(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceLoggerService", lambda handle, client: ResourceLoggerService(handle, client))
register_handle_wrapper("Microsoft.Extensions.Configuration.Abstractions/Microsoft.Extensions.Configuration.IConfiguration", lambda handle, client: IConfiguration(handle, client))
register_handle_wrapper("Microsoft.Extensions.Configuration.Abstractions/Microsoft.Extensions.Configuration.IConfigurationSection", lambda handle, client: IConfigurationSection(handle, client))
register_handle_wrapper("Microsoft.Extensions.Hosting.Abstractions/Microsoft.Extensions.Hosting.IHostEnvironment", lambda handle, client: IHostEnvironment(handle, client))
register_handle_wrapper("Microsoft.Extensions.Logging.Abstractions/Microsoft.Extensions.Logging.ILogger", lambda handle, client: ILogger(handle, client))
register_handle_wrapper("Microsoft.Extensions.Logging.Abstractions/Microsoft.Extensions.Logging.ILoggerFactory", lambda handle, client: ILoggerFactory(handle, client))
register_handle_wrapper("System.Private.CoreLib/System.Threading.CancellationToken", lambda handle, client: CancellationToken(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.IReportingStep", lambda handle, client: IReportingStep(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.IReportingTask", lambda handle, client: IReportingTask(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Eventing.DistributedApplicationEventSubscription", lambda handle, client: DistributedApplicationEventSubscription(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.DistributedApplicationExecutionContext", lambda handle, client: DistributedApplicationExecutionContext(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.DistributedApplicationExecutionContextOptions", lambda handle, client: DistributedApplicationExecutionContextOptions(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ProjectResourceOptions", lambda handle, client: ProjectResourceOptions(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.IUserSecretsManager", lambda handle, client: IUserSecretsManager(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineConfigurationContext", lambda handle, client: PipelineConfigurationContext(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineContext", lambda handle, client: PipelineContext(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineStep", lambda handle, client: PipelineStep(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineStepContext", lambda handle, client: PipelineStepContext(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineStepFactoryContext", lambda handle, client: PipelineStepFactoryContext(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineSummary", lambda handle, client: PipelineSummary(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Eventing.DistributedApplicationResourceEventSubscription", lambda handle, client: DistributedApplicationResourceEventSubscription(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Eventing.IDistributedApplicationEvent", lambda handle, client: IDistributedApplicationEvent(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Eventing.IDistributedApplicationResourceEvent", lambda handle, client: IDistributedApplicationResourceEvent(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.Eventing.IDistributedApplicationEventing", lambda handle, client: IDistributedApplicationEventing(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.AfterResourcesCreatedEvent", lambda handle, client: AfterResourcesCreatedEvent(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.BeforeResourceStartedEvent", lambda handle, client: BeforeResourceStartedEvent(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.BeforeStartEvent", lambda handle, client: BeforeStartEvent(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.CommandLineArgsCallbackContext", lambda handle, client: CommandLineArgsCallbackContext(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ConnectionStringAvailableEvent", lambda handle, client: ConnectionStringAvailableEvent(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.DistributedApplicationModel", lambda handle, client: DistributedApplicationModel(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointReferenceExpression", lambda handle, client: EndpointReferenceExpression(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EnvironmentCallbackContext", lambda handle, client: EnvironmentCallbackContext(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.InitializeResourceEvent", lambda handle, client: InitializeResourceEvent(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpressionBuilder", lambda handle, client: ReferenceExpressionBuilder(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.UpdateCommandStateContext", lambda handle, client: UpdateCommandStateContext(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ExecuteCommandContext", lambda handle, client: ExecuteCommandContext(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceEndpointsAllocatedEvent", lambda handle, client: ResourceEndpointsAllocatedEvent(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceReadyEvent", lambda handle, client: ResourceReadyEvent(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceStoppedEvent", lambda handle, client: ResourceStoppedEvent(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceUrlsCallbackContext", lambda handle, client: ResourceUrlsCallbackContext(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ConnectionStringResource", lambda handle, client: ConnectionStringResource(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerRegistryResource", lambda handle, client: ContainerRegistryResource(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.DotnetToolResource", lambda handle, client: DotnetToolResource(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ExternalServiceResource", lambda handle, client: ExternalServiceResource(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.CSharpAppResource", lambda handle, client: CSharpAppResource(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.IResourceWithContainerFiles", lambda handle, client: IResourceWithContainerFiles(handle, client))
register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCallbackContext", lambda handle, client: TestCallbackContext(handle, client))
register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestResourceContext", lambda handle, client: TestResourceContext(handle, client))
register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestEnvironmentContext", lambda handle, client: TestEnvironmentContext(handle, client))
register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCollectionContext", lambda handle, client: TestCollectionContext(handle, client))
register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestRedisResource", lambda handle, client: TestRedisResource(handle, client))
register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestDatabaseResource", lambda handle, client: TestDatabaseResource(handle, client))
register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestVaultResource", lambda handle, client: TestVaultResource(handle, client))
register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.ITestVaultResource", lambda handle, client: ITestVaultResource(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IContainerFilesDestinationResource", lambda handle, client: IContainerFilesDestinationResource(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IComputeResource", lambda handle, client: IComputeResource(handle, client))
register_handle_wrapper("Aspire.Hosting/List<string>", lambda handle, client: AspireList(handle, client))
register_handle_wrapper("Aspire.Hosting/Dict<string,any>", lambda handle, client: AspireDict(handle, client))
register_handle_wrapper("Aspire.Hosting/List<any>", lambda handle, client: AspireList(handle, client))
register_handle_wrapper("Aspire.Hosting/Dict<string,string|Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpression>", lambda handle, client: AspireDict(handle, client))
register_handle_wrapper("Aspire.Hosting/List<Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceUrlAnnotation>", lambda handle, client: AspireList(handle, client))
register_handle_wrapper("Aspire.Hosting/Dict<string,string>", lambda handle, client: AspireDict(handle, client))

# ============================================================================
# Connection Helpers
# ============================================================================

def connect() -> AspireClient:
    socket_path = os.environ.get("REMOTE_APP_HOST_SOCKET_PATH")
    if not socket_path:
        raise RuntimeError("REMOTE_APP_HOST_SOCKET_PATH environment variable not set. Run this application using `aspire run`.")
    client = AspireClient(socket_path)
    client.connect()
    client.on_disconnect(lambda: sys.exit(1))
    return client

def create_builder(options: Any | None = None) -> IDistributedApplicationBuilder:
    client = connect()
    resolved_options: Dict[str, Any] = {}
    if options is not None:
        if hasattr(options, "to_dict"):
            resolved_options.update(options.to_dict())
        elif isinstance(options, dict):
            resolved_options.update(options)
    resolved_options.setdefault("Args", sys.argv[1:])
    resolved_options.setdefault("ProjectDirectory", os.environ.get("ASPIRE_PROJECT_DIRECTORY", os.getcwd()))
    result = client.invoke_capability("Aspire.Hosting/createBuilderWithOptions", {"options": resolved_options})
    return result

# Re-export commonly used types
CapabilityError = CapabilityError
Handle = Handle
ReferenceExpression = ReferenceExpression
ref_expr = ref_expr

