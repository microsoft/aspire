// aspire.go - Capability-based Aspire SDK
// GENERATED CODE - DO NOT EDIT

package aspire

import (
	"fmt"
	"os"
)

// ============================================================================
// Enums
// ============================================================================

// ContainerLifetime represents ContainerLifetime.
type ContainerLifetime string

const (
	ContainerLifetimeSession ContainerLifetime = "Session"
	ContainerLifetimePersistent ContainerLifetime = "Persistent"
)

// ImagePullPolicy represents ImagePullPolicy.
type ImagePullPolicy string

const (
	ImagePullPolicyDefault ImagePullPolicy = "Default"
	ImagePullPolicyAlways ImagePullPolicy = "Always"
	ImagePullPolicyMissing ImagePullPolicy = "Missing"
	ImagePullPolicyNever ImagePullPolicy = "Never"
)

// DistributedApplicationOperation represents DistributedApplicationOperation.
type DistributedApplicationOperation string

const (
	DistributedApplicationOperationRun DistributedApplicationOperation = "Run"
	DistributedApplicationOperationPublish DistributedApplicationOperation = "Publish"
)

// OtlpProtocol represents OtlpProtocol.
type OtlpProtocol string

const (
	OtlpProtocolGrpc OtlpProtocol = "Grpc"
	OtlpProtocolHttpProtobuf OtlpProtocol = "HttpProtobuf"
	OtlpProtocolHttpJson OtlpProtocol = "HttpJson"
)

// ProtocolType represents ProtocolType.
type ProtocolType string

const (
	ProtocolTypeIP ProtocolType = "IP"
	ProtocolTypeIPv6HopByHopOptions ProtocolType = "IPv6HopByHopOptions"
	ProtocolTypeUnspecified ProtocolType = "Unspecified"
	ProtocolTypeIcmp ProtocolType = "Icmp"
	ProtocolTypeIgmp ProtocolType = "Igmp"
	ProtocolTypeGgp ProtocolType = "Ggp"
	ProtocolTypeIPv4 ProtocolType = "IPv4"
	ProtocolTypeTcp ProtocolType = "Tcp"
	ProtocolTypePup ProtocolType = "Pup"
	ProtocolTypeUdp ProtocolType = "Udp"
	ProtocolTypeIdp ProtocolType = "Idp"
	ProtocolTypeIPv6 ProtocolType = "IPv6"
	ProtocolTypeIPv6RoutingHeader ProtocolType = "IPv6RoutingHeader"
	ProtocolTypeIPv6FragmentHeader ProtocolType = "IPv6FragmentHeader"
	ProtocolTypeIPSecEncapsulatingSecurityPayload ProtocolType = "IPSecEncapsulatingSecurityPayload"
	ProtocolTypeIPSecAuthenticationHeader ProtocolType = "IPSecAuthenticationHeader"
	ProtocolTypeIcmpV6 ProtocolType = "IcmpV6"
	ProtocolTypeIPv6NoNextHeader ProtocolType = "IPv6NoNextHeader"
	ProtocolTypeIPv6DestinationOptions ProtocolType = "IPv6DestinationOptions"
	ProtocolTypeND ProtocolType = "ND"
	ProtocolTypeRaw ProtocolType = "Raw"
	ProtocolTypeIpx ProtocolType = "Ipx"
	ProtocolTypeSpx ProtocolType = "Spx"
	ProtocolTypeSpxII ProtocolType = "SpxII"
	ProtocolTypeUnknown ProtocolType = "Unknown"
)

// WaitBehavior represents WaitBehavior.
type WaitBehavior string

const (
	WaitBehaviorWaitOnResourceUnavailable WaitBehavior = "WaitOnResourceUnavailable"
	WaitBehaviorStopOnResourceUnavailable WaitBehavior = "StopOnResourceUnavailable"
)

// CertificateTrustScope represents CertificateTrustScope.
type CertificateTrustScope string

const (
	CertificateTrustScopeNone CertificateTrustScope = "None"
	CertificateTrustScopeAppend CertificateTrustScope = "Append"
	CertificateTrustScopeOverride CertificateTrustScope = "Override"
	CertificateTrustScopeSystem CertificateTrustScope = "System"
)

// IconVariant represents IconVariant.
type IconVariant string

const (
	IconVariantRegular IconVariant = "Regular"
	IconVariantFilled IconVariant = "Filled"
)

// ProbeType represents ProbeType.
type ProbeType string

const (
	ProbeTypeStartup ProbeType = "Startup"
	ProbeTypeReadiness ProbeType = "Readiness"
	ProbeTypeLiveness ProbeType = "Liveness"
)

// EndpointProperty represents EndpointProperty.
type EndpointProperty string

const (
	EndpointPropertyUrl EndpointProperty = "Url"
	EndpointPropertyHost EndpointProperty = "Host"
	EndpointPropertyIPV4Host EndpointProperty = "IPV4Host"
	EndpointPropertyPort EndpointProperty = "Port"
	EndpointPropertyScheme EndpointProperty = "Scheme"
	EndpointPropertyTargetPort EndpointProperty = "TargetPort"
	EndpointPropertyHostAndPort EndpointProperty = "HostAndPort"
	EndpointPropertyTlsEnabled EndpointProperty = "TlsEnabled"
)

// UrlDisplayLocation represents UrlDisplayLocation.
type UrlDisplayLocation string

const (
	UrlDisplayLocationSummaryAndDetails UrlDisplayLocation = "SummaryAndDetails"
	UrlDisplayLocationDetailsOnly UrlDisplayLocation = "DetailsOnly"
)

// TestPersistenceMode represents TestPersistenceMode.
type TestPersistenceMode string

const (
	TestPersistenceModeNone TestPersistenceMode = "None"
	TestPersistenceModeVolume TestPersistenceMode = "Volume"
	TestPersistenceModeBind TestPersistenceMode = "Bind"
)

// TestResourceStatus represents TestResourceStatus.
type TestResourceStatus string

const (
	TestResourceStatusPending TestResourceStatus = "Pending"
	TestResourceStatusRunning TestResourceStatus = "Running"
	TestResourceStatusStopped TestResourceStatus = "Stopped"
	TestResourceStatusFailed TestResourceStatus = "Failed"
)

// ============================================================================
// DTOs
// ============================================================================

// CreateBuilderOptions represents CreateBuilderOptions.
type CreateBuilderOptions struct {
	Args []string `json:"Args,omitempty"`
	ProjectDirectory string `json:"ProjectDirectory,omitempty"`
	AppHostFilePath string `json:"AppHostFilePath,omitempty"`
	ContainerRegistryOverride string `json:"ContainerRegistryOverride,omitempty"`
	DisableDashboard bool `json:"DisableDashboard,omitempty"`
	DashboardApplicationName string `json:"DashboardApplicationName,omitempty"`
	AllowUnsecuredTransport bool `json:"AllowUnsecuredTransport,omitempty"`
	EnableResourceLogging bool `json:"EnableResourceLogging,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *CreateBuilderOptions) ToMap() map[string]any {
	return map[string]any{
		"Args": SerializeValue(d.Args),
		"ProjectDirectory": SerializeValue(d.ProjectDirectory),
		"AppHostFilePath": SerializeValue(d.AppHostFilePath),
		"ContainerRegistryOverride": SerializeValue(d.ContainerRegistryOverride),
		"DisableDashboard": SerializeValue(d.DisableDashboard),
		"DashboardApplicationName": SerializeValue(d.DashboardApplicationName),
		"AllowUnsecuredTransport": SerializeValue(d.AllowUnsecuredTransport),
		"EnableResourceLogging": SerializeValue(d.EnableResourceLogging),
	}
}

// ResourceEventDto represents ResourceEventDto.
type ResourceEventDto struct {
	ResourceName string `json:"ResourceName,omitempty"`
	ResourceId string `json:"ResourceId,omitempty"`
	State string `json:"State,omitempty"`
	StateStyle string `json:"StateStyle,omitempty"`
	HealthStatus string `json:"HealthStatus,omitempty"`
	ExitCode float64 `json:"ExitCode,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *ResourceEventDto) ToMap() map[string]any {
	return map[string]any{
		"ResourceName": SerializeValue(d.ResourceName),
		"ResourceId": SerializeValue(d.ResourceId),
		"State": SerializeValue(d.State),
		"StateStyle": SerializeValue(d.StateStyle),
		"HealthStatus": SerializeValue(d.HealthStatus),
		"ExitCode": SerializeValue(d.ExitCode),
	}
}

// CommandOptions represents CommandOptions.
type CommandOptions struct {
	Description string `json:"Description,omitempty"`
	Parameter any `json:"Parameter,omitempty"`
	ConfirmationMessage string `json:"ConfirmationMessage,omitempty"`
	IconName string `json:"IconName,omitempty"`
	IconVariant IconVariant `json:"IconVariant,omitempty"`
	IsHighlighted bool `json:"IsHighlighted,omitempty"`
	UpdateState any `json:"UpdateState,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *CommandOptions) ToMap() map[string]any {
	return map[string]any{
		"Description": SerializeValue(d.Description),
		"Parameter": SerializeValue(d.Parameter),
		"ConfirmationMessage": SerializeValue(d.ConfirmationMessage),
		"IconName": SerializeValue(d.IconName),
		"IconVariant": SerializeValue(d.IconVariant),
		"IsHighlighted": SerializeValue(d.IsHighlighted),
		"UpdateState": SerializeValue(d.UpdateState),
	}
}

// ExecuteCommandResult represents ExecuteCommandResult.
type ExecuteCommandResult struct {
	Success bool `json:"Success,omitempty"`
	Canceled bool `json:"Canceled,omitempty"`
	ErrorMessage string `json:"ErrorMessage,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *ExecuteCommandResult) ToMap() map[string]any {
	return map[string]any{
		"Success": SerializeValue(d.Success),
		"Canceled": SerializeValue(d.Canceled),
		"ErrorMessage": SerializeValue(d.ErrorMessage),
	}
}

// ResourceUrlAnnotation represents ResourceUrlAnnotation.
type ResourceUrlAnnotation struct {
	Url string `json:"Url,omitempty"`
	DisplayText string `json:"DisplayText,omitempty"`
	Endpoint *EndpointReference `json:"Endpoint,omitempty"`
	DisplayLocation UrlDisplayLocation `json:"DisplayLocation,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *ResourceUrlAnnotation) ToMap() map[string]any {
	return map[string]any{
		"Url": SerializeValue(d.Url),
		"DisplayText": SerializeValue(d.DisplayText),
		"Endpoint": SerializeValue(d.Endpoint),
		"DisplayLocation": SerializeValue(d.DisplayLocation),
	}
}

// TestConfigDto represents TestConfigDto.
type TestConfigDto struct {
	Name string `json:"Name,omitempty"`
	Port float64 `json:"Port,omitempty"`
	Enabled bool `json:"Enabled,omitempty"`
	OptionalField string `json:"OptionalField,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *TestConfigDto) ToMap() map[string]any {
	return map[string]any{
		"Name": SerializeValue(d.Name),
		"Port": SerializeValue(d.Port),
		"Enabled": SerializeValue(d.Enabled),
		"OptionalField": SerializeValue(d.OptionalField),
	}
}

// TestNestedDto represents TestNestedDto.
type TestNestedDto struct {
	Id string `json:"Id,omitempty"`
	Config *TestConfigDto `json:"Config,omitempty"`
	Tags *AspireList[string] `json:"Tags,omitempty"`
	Counts *AspireDict[string, float64] `json:"Counts,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *TestNestedDto) ToMap() map[string]any {
	return map[string]any{
		"Id": SerializeValue(d.Id),
		"Config": SerializeValue(d.Config),
		"Tags": SerializeValue(d.Tags),
		"Counts": SerializeValue(d.Counts),
	}
}

// TestDeeplyNestedDto represents TestDeeplyNestedDto.
type TestDeeplyNestedDto struct {
	NestedData *AspireDict[string, *AspireList[*TestConfigDto]] `json:"NestedData,omitempty"`
	MetadataArray []*AspireDict[string, string] `json:"MetadataArray,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *TestDeeplyNestedDto) ToMap() map[string]any {
	return map[string]any{
		"NestedData": SerializeValue(d.NestedData),
		"MetadataArray": SerializeValue(d.MetadataArray),
	}
}

// ============================================================================
// Handle Wrappers
// ============================================================================

// AfterResourcesCreatedEvent wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.AfterResourcesCreatedEvent.
type AfterResourcesCreatedEvent struct {
	HandleWrapperBase
}

// NewAfterResourcesCreatedEvent creates a new AfterResourcesCreatedEvent.
func NewAfterResourcesCreatedEvent(handle *Handle, client *AspireClient) *AfterResourcesCreatedEvent {
	return &AfterResourcesCreatedEvent{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Services gets the Services property
func (s *AfterResourcesCreatedEvent) Services() (*IServiceProvider, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/AfterResourcesCreatedEvent.services", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IServiceProvider), nil
}

// Model gets the Model property
func (s *AfterResourcesCreatedEvent) Model() (*DistributedApplicationModel, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/AfterResourcesCreatedEvent.model", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationModel), nil
}

// BeforeResourceStartedEvent wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.BeforeResourceStartedEvent.
type BeforeResourceStartedEvent struct {
	HandleWrapperBase
}

// NewBeforeResourceStartedEvent creates a new BeforeResourceStartedEvent.
func NewBeforeResourceStartedEvent(handle *Handle, client *AspireClient) *BeforeResourceStartedEvent {
	return &BeforeResourceStartedEvent{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Resource gets the Resource property
func (s *BeforeResourceStartedEvent) Resource() (*IResource, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/BeforeResourceStartedEvent.resource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// Services gets the Services property
func (s *BeforeResourceStartedEvent) Services() (*IServiceProvider, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/BeforeResourceStartedEvent.services", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IServiceProvider), nil
}

// BeforeStartEvent wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.BeforeStartEvent.
type BeforeStartEvent struct {
	HandleWrapperBase
}

// NewBeforeStartEvent creates a new BeforeStartEvent.
func NewBeforeStartEvent(handle *Handle, client *AspireClient) *BeforeStartEvent {
	return &BeforeStartEvent{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Services gets the Services property
func (s *BeforeStartEvent) Services() (*IServiceProvider, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/BeforeStartEvent.services", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IServiceProvider), nil
}

// Model gets the Model property
func (s *BeforeStartEvent) Model() (*DistributedApplicationModel, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/BeforeStartEvent.model", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationModel), nil
}

// CSharpAppResource wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.CSharpAppResource.
type CSharpAppResource struct {
	ResourceBuilderBase
}

// NewCSharpAppResource creates a new CSharpAppResource.
func NewCSharpAppResource(handle *Handle, client *AspireClient) *CSharpAppResource {
	return &CSharpAppResource{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// WithContainerRegistry configures a resource to use a container registry
func (s *CSharpAppResource) WithContainerRegistry(registry *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["registry"] = SerializeValue(registry)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerRegistry", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *CSharpAppResource) WithDockerfileBaseImage(buildImage *string, runtimeImage *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if buildImage != nil {
		reqArgs["buildImage"] = SerializeValue(buildImage)
	}
	if runtimeImage != nil {
		reqArgs["runtimeImage"] = SerializeValue(runtimeImage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfileBaseImage", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMcpServer configures an MCP server endpoint on the resource
func (s *CSharpAppResource) WithMcpServer(path *string, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withMcpServer", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithOtlpExporter configures OTLP telemetry export
func (s *CSharpAppResource) WithOtlpExporter() (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporter", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithOtlpExporterProtocol configures OTLP telemetry export with specific protocol
func (s *CSharpAppResource) WithOtlpExporterProtocol(protocol OtlpProtocol) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["protocol"] = SerializeValue(protocol)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporterProtocol", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReplicas sets the number of replicas
func (s *CSharpAppResource) WithReplicas(replicas float64) (*ProjectResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["replicas"] = SerializeValue(replicas)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReplicas", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ProjectResource), nil
}

// DisableForwardedHeaders disables forwarded headers for the project
func (s *CSharpAppResource) DisableForwardedHeaders() (*ProjectResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/disableForwardedHeaders", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ProjectResource), nil
}

// PublishAsDockerFile publishes a project as a Docker file with optional container configuration
func (s *CSharpAppResource) PublishAsDockerFile(configure func(...any) any) (*ProjectResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if configure != nil {
		reqArgs["configure"] = RegisterCallback(configure)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishProjectAsDockerFileWithConfigure", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ProjectResource), nil
}

// WithRequiredCommand adds a required command dependency
func (s *CSharpAppResource) WithRequiredCommand(command string, helpLink *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["command"] = SerializeValue(command)
	if helpLink != nil {
		reqArgs["helpLink"] = SerializeValue(helpLink)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRequiredCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEnvironment sets an environment variable
func (s *CSharpAppResource) WithEnvironment(name string, value string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentExpression adds an environment variable with a reference expression
func (s *CSharpAppResource) WithEnvironmentExpression(name string, value *ReferenceExpression) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentCallback sets environment variables via callback
func (s *CSharpAppResource) WithEnvironmentCallback(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentCallbackAsync sets environment variables via async callback
func (s *CSharpAppResource) WithEnvironmentCallbackAsync(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentEndpoint sets an environment variable from an endpoint reference
func (s *CSharpAppResource) WithEnvironmentEndpoint(name string, endpointReference *EndpointReference) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["endpointReference"] = SerializeValue(endpointReference)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentParameter sets an environment variable from a parameter resource
func (s *CSharpAppResource) WithEnvironmentParameter(name string, parameter *ParameterResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["parameter"] = SerializeValue(parameter)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentParameter", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentConnectionString sets an environment variable from a connection string resource
func (s *CSharpAppResource) WithEnvironmentConnectionString(envVarName string, resource *IResourceWithConnectionString) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["envVarName"] = SerializeValue(envVarName)
	reqArgs["resource"] = SerializeValue(resource)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentConnectionString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithArgs adds arguments
func (s *CSharpAppResource) WithArgs(args []string) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["args"] = SerializeValue(args)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithArgsCallback sets command-line arguments via callback
func (s *CSharpAppResource) WithArgsCallback(callback func(...any) any) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithArgsCallbackAsync sets command-line arguments via async callback
func (s *CSharpAppResource) WithArgsCallbackAsync(callback func(...any) any) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithReference adds a reference to another resource
func (s *CSharpAppResource) WithReference(source *IResource, connectionName *string, optional *bool, name *string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["source"] = SerializeValue(source)
	if connectionName != nil {
		reqArgs["connectionName"] = SerializeValue(connectionName)
	}
	if optional != nil {
		reqArgs["optional"] = SerializeValue(optional)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReference", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceUri adds a reference to a URI
func (s *CSharpAppResource) WithReferenceUri(name string, uri string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["uri"] = SerializeValue(uri)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceUri", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceExternalService adds a reference to an external service
func (s *CSharpAppResource) WithReferenceExternalService(externalService *ExternalServiceResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["externalService"] = SerializeValue(externalService)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceExternalService", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceEndpoint adds a reference to an endpoint
func (s *CSharpAppResource) WithReferenceEndpoint(endpointReference *EndpointReference) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointReference"] = SerializeValue(endpointReference)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEndpoint adds a network endpoint
func (s *CSharpAppResource) WithEndpoint(port *float64, targetPort *float64, scheme *string, name *string, env *string, isProxied *bool, isExternal *bool, protocol *ProtocolType) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if scheme != nil {
		reqArgs["scheme"] = SerializeValue(scheme)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	if isExternal != nil {
		reqArgs["isExternal"] = SerializeValue(isExternal)
	}
	if protocol != nil {
		reqArgs["protocol"] = SerializeValue(protocol)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpEndpoint adds an HTTP endpoint
func (s *CSharpAppResource) WithHttpEndpoint(port *float64, targetPort *float64, name *string, env *string, isProxied *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpsEndpoint adds an HTTPS endpoint
func (s *CSharpAppResource) WithHttpsEndpoint(port *float64, targetPort *float64, name *string, env *string, isProxied *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithExternalHttpEndpoints makes HTTP endpoints externally accessible
func (s *CSharpAppResource) WithExternalHttpEndpoints() (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExternalHttpEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// GetEndpoint gets an endpoint reference
func (s *CSharpAppResource) GetEndpoint(name string) (*EndpointReference, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointReference), nil
}

// AsHttp2Service configures resource for HTTP/2
func (s *CSharpAppResource) AsHttp2Service() (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/asHttp2Service", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithUrlsCallback customizes displayed URLs via callback
func (s *CSharpAppResource) WithUrlsCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlsCallbackAsync customizes displayed URLs via async callback
func (s *CSharpAppResource) WithUrlsCallbackAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *CSharpAppResource) WithUrl(url string, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrl", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlExpression adds a URL using a reference expression
func (s *CSharpAppResource) WithUrlExpression(url *ReferenceExpression, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *CSharpAppResource) WithUrlForEndpoint(endpointName string, callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpointFactory adds a URL for a specific endpoint via factory callback
func (s *CSharpAppResource) WithUrlForEndpointFactory(endpointName string, callback func(...any) any) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpointFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// PublishWithContainerFiles configures the resource to copy container files from the specified source during publishing
func (s *CSharpAppResource) PublishWithContainerFiles(source *IResourceWithContainerFiles, destinationPath string) (*IContainerFilesDestinationResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["source"] = SerializeValue(source)
	reqArgs["destinationPath"] = SerializeValue(destinationPath)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishWithContainerFiles", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IContainerFilesDestinationResource), nil
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *CSharpAppResource) ExcludeFromManifest() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromManifest", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WaitFor waits for another resource to be ready
func (s *CSharpAppResource) WaitFor(dependency *IResource) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForWithBehavior waits for another resource with specific behavior
func (s *CSharpAppResource) WaitForWithBehavior(dependency *IResource, waitBehavior WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForWithBehavior", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStart waits for another resource to start
func (s *CSharpAppResource) WaitForStart(dependency *IResource) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStartWithBehavior waits for another resource to start with specific behavior
func (s *CSharpAppResource) WaitForStartWithBehavior(dependency *IResource, waitBehavior WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStartWithBehavior", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WithExplicitStart prevents resource from starting automatically
func (s *CSharpAppResource) WithExplicitStart() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExplicitStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WaitForCompletion waits for resource completion
func (s *CSharpAppResource) WaitForCompletion(dependency *IResource, exitCode *float64) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if exitCode != nil {
		reqArgs["exitCode"] = SerializeValue(exitCode)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForCompletion", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WithHealthCheck adds a health check by key
func (s *CSharpAppResource) WithHealthCheck(key string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["key"] = SerializeValue(key)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithHttpHealthCheck adds an HTTP health check
func (s *CSharpAppResource) WithHttpHealthCheck(path *string, statusCode *float64, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if statusCode != nil {
		reqArgs["statusCode"] = SerializeValue(statusCode)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithCommand adds a resource command
func (s *CSharpAppResource) WithCommand(name string, displayName string, executeCommand func(...any) any, commandOptions *CommandOptions) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["displayName"] = SerializeValue(displayName)
	if executeCommand != nil {
		reqArgs["executeCommand"] = RegisterCallback(executeCommand)
	}
	if commandOptions != nil {
		reqArgs["commandOptions"] = SerializeValue(commandOptions)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDeveloperCertificateTrust configures developer certificate trust
func (s *CSharpAppResource) WithDeveloperCertificateTrust(trust bool) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["trust"] = SerializeValue(trust)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDeveloperCertificateTrust", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCertificateTrustScope sets the certificate trust scope
func (s *CSharpAppResource) WithCertificateTrustScope(scope CertificateTrustScope) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["scope"] = SerializeValue(scope)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCertificateTrustScope", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithHttpsDeveloperCertificate configures HTTPS with a developer certificate
func (s *CSharpAppResource) WithHttpsDeveloperCertificate(password *ParameterResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if password != nil {
		reqArgs["password"] = SerializeValue(password)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsDeveloperCertificate", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithoutHttpsCertificate removes HTTPS certificate configuration
func (s *CSharpAppResource) WithoutHttpsCertificate() (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withoutHttpsCertificate", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithParentRelationship sets the parent relationship
func (s *CSharpAppResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParentRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithChildRelationship sets a child relationship
func (s *CSharpAppResource) WithChildRelationship(child *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["child"] = SerializeValue(child)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withChildRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithIconName sets the icon for the resource
func (s *CSharpAppResource) WithIconName(iconName string, iconVariant *IconVariant) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["iconName"] = SerializeValue(iconName)
	if iconVariant != nil {
		reqArgs["iconVariant"] = SerializeValue(iconVariant)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withIconName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithHttpProbe adds an HTTP health probe to the resource
func (s *CSharpAppResource) WithHttpProbe(probeType ProbeType, path *string, initialDelaySeconds *float64, periodSeconds *float64, timeoutSeconds *float64, failureThreshold *float64, successThreshold *float64, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["probeType"] = SerializeValue(probeType)
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if initialDelaySeconds != nil {
		reqArgs["initialDelaySeconds"] = SerializeValue(initialDelaySeconds)
	}
	if periodSeconds != nil {
		reqArgs["periodSeconds"] = SerializeValue(periodSeconds)
	}
	if timeoutSeconds != nil {
		reqArgs["timeoutSeconds"] = SerializeValue(timeoutSeconds)
	}
	if failureThreshold != nil {
		reqArgs["failureThreshold"] = SerializeValue(failureThreshold)
	}
	if successThreshold != nil {
		reqArgs["successThreshold"] = SerializeValue(successThreshold)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpProbe", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *CSharpAppResource) ExcludeFromMcp() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromMcp", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithRemoteImageName sets the remote image name for publishing
func (s *CSharpAppResource) WithRemoteImageName(remoteImageName string) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["remoteImageName"] = SerializeValue(remoteImageName)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRemoteImageName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
}

// WithRemoteImageTag sets the remote image tag for publishing
func (s *CSharpAppResource) WithRemoteImageTag(remoteImageTag string) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["remoteImageTag"] = SerializeValue(remoteImageTag)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRemoteImageTag", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *CSharpAppResource) WithPipelineStepFactory(stepName string, callback func(...any) any, dependsOn []string, requiredBy []string, tags []string, description *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["stepName"] = SerializeValue(stepName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if dependsOn != nil {
		reqArgs["dependsOn"] = SerializeValue(dependsOn)
	}
	if requiredBy != nil {
		reqArgs["requiredBy"] = SerializeValue(requiredBy)
	}
	if tags != nil {
		reqArgs["tags"] = SerializeValue(tags)
	}
	if description != nil {
		reqArgs["description"] = SerializeValue(description)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineStepFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfigurationAsync configures pipeline step dependencies via an async callback
func (s *CSharpAppResource) WithPipelineConfigurationAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfigurationAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *CSharpAppResource) WithPipelineConfiguration(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// GetResourceName gets the resource name
func (s *CSharpAppResource) GetResourceName() (*string, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *CSharpAppResource) OnBeforeResourceStarted(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onBeforeResourceStarted", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *CSharpAppResource) OnResourceStopped(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceStopped", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *CSharpAppResource) OnInitializeResource(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onInitializeResource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceEndpointsAllocated subscribes to the ResourceEndpointsAllocated event
func (s *CSharpAppResource) OnResourceEndpointsAllocated(callback func(...any) any) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceEndpointsAllocated", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// OnResourceReady subscribes to the ResourceReady event
func (s *CSharpAppResource) OnResourceReady(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceReady", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalString adds an optional string parameter
func (s *CSharpAppResource) WithOptionalString(value *string, enabled *bool) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if value != nil {
		reqArgs["value"] = SerializeValue(value)
	}
	if enabled != nil {
		reqArgs["enabled"] = SerializeValue(enabled)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithConfig configures the resource with a DTO
func (s *CSharpAppResource) WithConfig(config *TestConfigDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestWithEnvironmentCallback configures environment with callback (test version)
func (s *CSharpAppResource) TestWithEnvironmentCallback(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWithEnvironmentCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCreatedAt sets the created timestamp
func (s *CSharpAppResource) WithCreatedAt(createdAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["createdAt"] = SerializeValue(createdAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCreatedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithModifiedAt sets the modified timestamp
func (s *CSharpAppResource) WithModifiedAt(modifiedAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["modifiedAt"] = SerializeValue(modifiedAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withModifiedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCorrelationId sets the correlation ID
func (s *CSharpAppResource) WithCorrelationId(correlationId string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["correlationId"] = SerializeValue(correlationId)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCorrelationId", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalCallback configures with optional callback
func (s *CSharpAppResource) WithOptionalCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithStatus sets the resource status
func (s *CSharpAppResource) WithStatus(status TestResourceStatus) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["status"] = SerializeValue(status)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withStatus", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithNestedConfig configures with nested DTO
func (s *CSharpAppResource) WithNestedConfig(config *TestNestedDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withNestedConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithValidator adds validation callback
func (s *CSharpAppResource) WithValidator(validator func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if validator != nil {
		reqArgs["validator"] = RegisterCallback(validator)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withValidator", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestWaitFor waits for another resource (test version)
func (s *CSharpAppResource) TestWaitFor(dependency *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWaitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDependency adds a dependency on another resource
func (s *CSharpAppResource) WithDependency(dependency *IResourceWithConnectionString) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withDependency", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEndpoints sets the endpoints
func (s *CSharpAppResource) WithEndpoints(endpoints []string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpoints"] = SerializeValue(endpoints)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEnvironmentVariables sets environment variables
func (s *CSharpAppResource) WithEnvironmentVariables(variables map[string]string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["variables"] = SerializeValue(variables)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEnvironmentVariables", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCancellableOperation performs a cancellable operation
func (s *CSharpAppResource) WithCancellableOperation(operation func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if operation != nil {
		reqArgs["operation"] = RegisterCallback(operation)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCancellableOperation", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// CommandLineArgsCallbackContext wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.CommandLineArgsCallbackContext.
type CommandLineArgsCallbackContext struct {
	HandleWrapperBase
	args *AspireList[any]
}

// NewCommandLineArgsCallbackContext creates a new CommandLineArgsCallbackContext.
func NewCommandLineArgsCallbackContext(handle *Handle, client *AspireClient) *CommandLineArgsCallbackContext {
	return &CommandLineArgsCallbackContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Args gets the Args property
func (s *CommandLineArgsCallbackContext) Args() *AspireList[any] {
	if s.args == nil {
		s.args = NewAspireListWithGetter[any](s.Handle(), s.Client(), "Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.args")
	}
	return s.args
}

// CancellationToken gets the CancellationToken property
func (s *CommandLineArgsCallbackContext) CancellationToken() (*CancellationToken, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.cancellationToken", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*CancellationToken), nil
}

// ExecutionContext gets the ExecutionContext property
func (s *CommandLineArgsCallbackContext) ExecutionContext() (*DistributedApplicationExecutionContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.executionContext", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationExecutionContext), nil
}

// SetExecutionContext sets the ExecutionContext property
func (s *CommandLineArgsCallbackContext) SetExecutionContext(value *DistributedApplicationExecutionContext) (*CommandLineArgsCallbackContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.setExecutionContext", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*CommandLineArgsCallbackContext), nil
}

// Logger gets the Logger property
func (s *CommandLineArgsCallbackContext) Logger() (*ILogger, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.logger", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ILogger), nil
}

// SetLogger sets the Logger property
func (s *CommandLineArgsCallbackContext) SetLogger(value *ILogger) (*CommandLineArgsCallbackContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.setLogger", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*CommandLineArgsCallbackContext), nil
}

// Resource gets the Resource property
func (s *CommandLineArgsCallbackContext) Resource() (*IResource, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.resource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// ConnectionStringAvailableEvent wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ConnectionStringAvailableEvent.
type ConnectionStringAvailableEvent struct {
	HandleWrapperBase
}

// NewConnectionStringAvailableEvent creates a new ConnectionStringAvailableEvent.
func NewConnectionStringAvailableEvent(handle *Handle, client *AspireClient) *ConnectionStringAvailableEvent {
	return &ConnectionStringAvailableEvent{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Resource gets the Resource property
func (s *ConnectionStringAvailableEvent) Resource() (*IResource, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ConnectionStringAvailableEvent.resource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// Services gets the Services property
func (s *ConnectionStringAvailableEvent) Services() (*IServiceProvider, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ConnectionStringAvailableEvent.services", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IServiceProvider), nil
}

// ConnectionStringResource wraps a handle for Aspire.Hosting/Aspire.Hosting.ConnectionStringResource.
type ConnectionStringResource struct {
	ResourceBuilderBase
}

// NewConnectionStringResource creates a new ConnectionStringResource.
func NewConnectionStringResource(handle *Handle, client *AspireClient) *ConnectionStringResource {
	return &ConnectionStringResource{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// WithContainerRegistry configures a resource to use a container registry
func (s *ConnectionStringResource) WithContainerRegistry(registry *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["registry"] = SerializeValue(registry)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerRegistry", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *ConnectionStringResource) WithDockerfileBaseImage(buildImage *string, runtimeImage *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if buildImage != nil {
		reqArgs["buildImage"] = SerializeValue(buildImage)
	}
	if runtimeImage != nil {
		reqArgs["runtimeImage"] = SerializeValue(runtimeImage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfileBaseImage", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithRequiredCommand adds a required command dependency
func (s *ConnectionStringResource) WithRequiredCommand(command string, helpLink *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["command"] = SerializeValue(command)
	if helpLink != nil {
		reqArgs["helpLink"] = SerializeValue(helpLink)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRequiredCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithConnectionProperty adds a connection property with a reference expression
func (s *ConnectionStringResource) WithConnectionProperty(name string, value *ReferenceExpression) (*IResourceWithConnectionString, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withConnectionProperty", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithConnectionString), nil
}

// WithConnectionPropertyValue adds a connection property with a string value
func (s *ConnectionStringResource) WithConnectionPropertyValue(name string, value string) (*IResourceWithConnectionString, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withConnectionPropertyValue", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithConnectionString), nil
}

// WithUrlsCallback customizes displayed URLs via callback
func (s *ConnectionStringResource) WithUrlsCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlsCallbackAsync customizes displayed URLs via async callback
func (s *ConnectionStringResource) WithUrlsCallbackAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *ConnectionStringResource) WithUrl(url string, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrl", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlExpression adds a URL using a reference expression
func (s *ConnectionStringResource) WithUrlExpression(url *ReferenceExpression, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *ConnectionStringResource) WithUrlForEndpoint(endpointName string, callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *ConnectionStringResource) ExcludeFromManifest() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromManifest", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WaitFor waits for another resource to be ready
func (s *ConnectionStringResource) WaitFor(dependency *IResource) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForWithBehavior waits for another resource with specific behavior
func (s *ConnectionStringResource) WaitForWithBehavior(dependency *IResource, waitBehavior WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForWithBehavior", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStart waits for another resource to start
func (s *ConnectionStringResource) WaitForStart(dependency *IResource) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStartWithBehavior waits for another resource to start with specific behavior
func (s *ConnectionStringResource) WaitForStartWithBehavior(dependency *IResource, waitBehavior WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStartWithBehavior", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WithExplicitStart prevents resource from starting automatically
func (s *ConnectionStringResource) WithExplicitStart() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExplicitStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WaitForCompletion waits for resource completion
func (s *ConnectionStringResource) WaitForCompletion(dependency *IResource, exitCode *float64) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if exitCode != nil {
		reqArgs["exitCode"] = SerializeValue(exitCode)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForCompletion", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WithHealthCheck adds a health check by key
func (s *ConnectionStringResource) WithHealthCheck(key string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["key"] = SerializeValue(key)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCommand adds a resource command
func (s *ConnectionStringResource) WithCommand(name string, displayName string, executeCommand func(...any) any, commandOptions *CommandOptions) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["displayName"] = SerializeValue(displayName)
	if executeCommand != nil {
		reqArgs["executeCommand"] = RegisterCallback(executeCommand)
	}
	if commandOptions != nil {
		reqArgs["commandOptions"] = SerializeValue(commandOptions)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithParentRelationship sets the parent relationship
func (s *ConnectionStringResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParentRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithChildRelationship sets a child relationship
func (s *ConnectionStringResource) WithChildRelationship(child *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["child"] = SerializeValue(child)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withChildRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithIconName sets the icon for the resource
func (s *ConnectionStringResource) WithIconName(iconName string, iconVariant *IconVariant) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["iconName"] = SerializeValue(iconName)
	if iconVariant != nil {
		reqArgs["iconVariant"] = SerializeValue(iconVariant)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withIconName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *ConnectionStringResource) ExcludeFromMcp() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromMcp", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *ConnectionStringResource) WithPipelineStepFactory(stepName string, callback func(...any) any, dependsOn []string, requiredBy []string, tags []string, description *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["stepName"] = SerializeValue(stepName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if dependsOn != nil {
		reqArgs["dependsOn"] = SerializeValue(dependsOn)
	}
	if requiredBy != nil {
		reqArgs["requiredBy"] = SerializeValue(requiredBy)
	}
	if tags != nil {
		reqArgs["tags"] = SerializeValue(tags)
	}
	if description != nil {
		reqArgs["description"] = SerializeValue(description)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineStepFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfigurationAsync configures pipeline step dependencies via an async callback
func (s *ConnectionStringResource) WithPipelineConfigurationAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfigurationAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *ConnectionStringResource) WithPipelineConfiguration(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// GetResourceName gets the resource name
func (s *ConnectionStringResource) GetResourceName() (*string, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *ConnectionStringResource) OnBeforeResourceStarted(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onBeforeResourceStarted", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *ConnectionStringResource) OnResourceStopped(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceStopped", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnConnectionStringAvailable subscribes to the ConnectionStringAvailable event
func (s *ConnectionStringResource) OnConnectionStringAvailable(callback func(...any) any) (*IResourceWithConnectionString, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onConnectionStringAvailable", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithConnectionString), nil
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *ConnectionStringResource) OnInitializeResource(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onInitializeResource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceReady subscribes to the ResourceReady event
func (s *ConnectionStringResource) OnResourceReady(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceReady", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalString adds an optional string parameter
func (s *ConnectionStringResource) WithOptionalString(value *string, enabled *bool) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if value != nil {
		reqArgs["value"] = SerializeValue(value)
	}
	if enabled != nil {
		reqArgs["enabled"] = SerializeValue(enabled)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithConfig configures the resource with a DTO
func (s *ConnectionStringResource) WithConfig(config *TestConfigDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithConnectionString sets the connection string using a reference expression
func (s *ConnectionStringResource) WithConnectionString(connectionString *ReferenceExpression) (*IResourceWithConnectionString, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["connectionString"] = SerializeValue(connectionString)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withConnectionString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithConnectionString), nil
}

// WithCreatedAt sets the created timestamp
func (s *ConnectionStringResource) WithCreatedAt(createdAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["createdAt"] = SerializeValue(createdAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCreatedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithModifiedAt sets the modified timestamp
func (s *ConnectionStringResource) WithModifiedAt(modifiedAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["modifiedAt"] = SerializeValue(modifiedAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withModifiedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCorrelationId sets the correlation ID
func (s *ConnectionStringResource) WithCorrelationId(correlationId string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["correlationId"] = SerializeValue(correlationId)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCorrelationId", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalCallback configures with optional callback
func (s *ConnectionStringResource) WithOptionalCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithStatus sets the resource status
func (s *ConnectionStringResource) WithStatus(status TestResourceStatus) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["status"] = SerializeValue(status)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withStatus", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithNestedConfig configures with nested DTO
func (s *ConnectionStringResource) WithNestedConfig(config *TestNestedDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withNestedConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithValidator adds validation callback
func (s *ConnectionStringResource) WithValidator(validator func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if validator != nil {
		reqArgs["validator"] = RegisterCallback(validator)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withValidator", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestWaitFor waits for another resource (test version)
func (s *ConnectionStringResource) TestWaitFor(dependency *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWaitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithConnectionStringDirect sets connection string using direct interface target
func (s *ConnectionStringResource) WithConnectionStringDirect(connectionString string) (*IResourceWithConnectionString, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["connectionString"] = SerializeValue(connectionString)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withConnectionStringDirect", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithConnectionString), nil
}

// WithDependency adds a dependency on another resource
func (s *ConnectionStringResource) WithDependency(dependency *IResourceWithConnectionString) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withDependency", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEndpoints sets the endpoints
func (s *ConnectionStringResource) WithEndpoints(endpoints []string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpoints"] = SerializeValue(endpoints)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCancellableOperation performs a cancellable operation
func (s *ConnectionStringResource) WithCancellableOperation(operation func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if operation != nil {
		reqArgs["operation"] = RegisterCallback(operation)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCancellableOperation", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// ContainerRegistryResource wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerRegistryResource.
type ContainerRegistryResource struct {
	ResourceBuilderBase
}

// NewContainerRegistryResource creates a new ContainerRegistryResource.
func NewContainerRegistryResource(handle *Handle, client *AspireClient) *ContainerRegistryResource {
	return &ContainerRegistryResource{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// WithContainerRegistry configures a resource to use a container registry
func (s *ContainerRegistryResource) WithContainerRegistry(registry *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["registry"] = SerializeValue(registry)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerRegistry", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *ContainerRegistryResource) WithDockerfileBaseImage(buildImage *string, runtimeImage *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if buildImage != nil {
		reqArgs["buildImage"] = SerializeValue(buildImage)
	}
	if runtimeImage != nil {
		reqArgs["runtimeImage"] = SerializeValue(runtimeImage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfileBaseImage", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithRequiredCommand adds a required command dependency
func (s *ContainerRegistryResource) WithRequiredCommand(command string, helpLink *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["command"] = SerializeValue(command)
	if helpLink != nil {
		reqArgs["helpLink"] = SerializeValue(helpLink)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRequiredCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlsCallback customizes displayed URLs via callback
func (s *ContainerRegistryResource) WithUrlsCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlsCallbackAsync customizes displayed URLs via async callback
func (s *ContainerRegistryResource) WithUrlsCallbackAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *ContainerRegistryResource) WithUrl(url string, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrl", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlExpression adds a URL using a reference expression
func (s *ContainerRegistryResource) WithUrlExpression(url *ReferenceExpression, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *ContainerRegistryResource) WithUrlForEndpoint(endpointName string, callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *ContainerRegistryResource) ExcludeFromManifest() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromManifest", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithExplicitStart prevents resource from starting automatically
func (s *ContainerRegistryResource) WithExplicitStart() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExplicitStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithHealthCheck adds a health check by key
func (s *ContainerRegistryResource) WithHealthCheck(key string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["key"] = SerializeValue(key)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCommand adds a resource command
func (s *ContainerRegistryResource) WithCommand(name string, displayName string, executeCommand func(...any) any, commandOptions *CommandOptions) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["displayName"] = SerializeValue(displayName)
	if executeCommand != nil {
		reqArgs["executeCommand"] = RegisterCallback(executeCommand)
	}
	if commandOptions != nil {
		reqArgs["commandOptions"] = SerializeValue(commandOptions)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithParentRelationship sets the parent relationship
func (s *ContainerRegistryResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParentRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithChildRelationship sets a child relationship
func (s *ContainerRegistryResource) WithChildRelationship(child *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["child"] = SerializeValue(child)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withChildRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithIconName sets the icon for the resource
func (s *ContainerRegistryResource) WithIconName(iconName string, iconVariant *IconVariant) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["iconName"] = SerializeValue(iconName)
	if iconVariant != nil {
		reqArgs["iconVariant"] = SerializeValue(iconVariant)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withIconName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *ContainerRegistryResource) ExcludeFromMcp() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromMcp", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *ContainerRegistryResource) WithPipelineStepFactory(stepName string, callback func(...any) any, dependsOn []string, requiredBy []string, tags []string, description *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["stepName"] = SerializeValue(stepName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if dependsOn != nil {
		reqArgs["dependsOn"] = SerializeValue(dependsOn)
	}
	if requiredBy != nil {
		reqArgs["requiredBy"] = SerializeValue(requiredBy)
	}
	if tags != nil {
		reqArgs["tags"] = SerializeValue(tags)
	}
	if description != nil {
		reqArgs["description"] = SerializeValue(description)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineStepFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfigurationAsync configures pipeline step dependencies via an async callback
func (s *ContainerRegistryResource) WithPipelineConfigurationAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfigurationAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *ContainerRegistryResource) WithPipelineConfiguration(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// GetResourceName gets the resource name
func (s *ContainerRegistryResource) GetResourceName() (*string, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *ContainerRegistryResource) OnBeforeResourceStarted(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onBeforeResourceStarted", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *ContainerRegistryResource) OnResourceStopped(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceStopped", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *ContainerRegistryResource) OnInitializeResource(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onInitializeResource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceReady subscribes to the ResourceReady event
func (s *ContainerRegistryResource) OnResourceReady(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceReady", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalString adds an optional string parameter
func (s *ContainerRegistryResource) WithOptionalString(value *string, enabled *bool) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if value != nil {
		reqArgs["value"] = SerializeValue(value)
	}
	if enabled != nil {
		reqArgs["enabled"] = SerializeValue(enabled)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithConfig configures the resource with a DTO
func (s *ContainerRegistryResource) WithConfig(config *TestConfigDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCreatedAt sets the created timestamp
func (s *ContainerRegistryResource) WithCreatedAt(createdAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["createdAt"] = SerializeValue(createdAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCreatedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithModifiedAt sets the modified timestamp
func (s *ContainerRegistryResource) WithModifiedAt(modifiedAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["modifiedAt"] = SerializeValue(modifiedAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withModifiedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCorrelationId sets the correlation ID
func (s *ContainerRegistryResource) WithCorrelationId(correlationId string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["correlationId"] = SerializeValue(correlationId)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCorrelationId", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalCallback configures with optional callback
func (s *ContainerRegistryResource) WithOptionalCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithStatus sets the resource status
func (s *ContainerRegistryResource) WithStatus(status TestResourceStatus) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["status"] = SerializeValue(status)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withStatus", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithNestedConfig configures with nested DTO
func (s *ContainerRegistryResource) WithNestedConfig(config *TestNestedDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withNestedConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithValidator adds validation callback
func (s *ContainerRegistryResource) WithValidator(validator func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if validator != nil {
		reqArgs["validator"] = RegisterCallback(validator)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withValidator", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestWaitFor waits for another resource (test version)
func (s *ContainerRegistryResource) TestWaitFor(dependency *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWaitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDependency adds a dependency on another resource
func (s *ContainerRegistryResource) WithDependency(dependency *IResourceWithConnectionString) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withDependency", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEndpoints sets the endpoints
func (s *ContainerRegistryResource) WithEndpoints(endpoints []string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpoints"] = SerializeValue(endpoints)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCancellableOperation performs a cancellable operation
func (s *ContainerRegistryResource) WithCancellableOperation(operation func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if operation != nil {
		reqArgs["operation"] = RegisterCallback(operation)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCancellableOperation", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// ContainerResource wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerResource.
type ContainerResource struct {
	ResourceBuilderBase
}

// NewContainerResource creates a new ContainerResource.
func NewContainerResource(handle *Handle, client *AspireClient) *ContainerResource {
	return &ContainerResource{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// WithContainerRegistry configures a resource to use a container registry
func (s *ContainerResource) WithContainerRegistry(registry *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["registry"] = SerializeValue(registry)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerRegistry", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithBindMount adds a bind mount
func (s *ContainerResource) WithBindMount(source string, target string, isReadOnly *bool) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["source"] = SerializeValue(source)
	reqArgs["target"] = SerializeValue(target)
	if isReadOnly != nil {
		reqArgs["isReadOnly"] = SerializeValue(isReadOnly)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBindMount", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithEntrypoint sets the container entrypoint
func (s *ContainerResource) WithEntrypoint(entrypoint string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["entrypoint"] = SerializeValue(entrypoint)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEntrypoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImageTag sets the container image tag
func (s *ContainerResource) WithImageTag(tag string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["tag"] = SerializeValue(tag)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImageTag", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImageRegistry sets the container image registry
func (s *ContainerResource) WithImageRegistry(registry string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["registry"] = SerializeValue(registry)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImageRegistry", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImage sets the container image
func (s *ContainerResource) WithImage(image string, tag *string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["image"] = SerializeValue(image)
	if tag != nil {
		reqArgs["tag"] = SerializeValue(tag)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImage", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImageSHA256 sets the image SHA256 digest
func (s *ContainerResource) WithImageSHA256(sha256 string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["sha256"] = SerializeValue(sha256)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImageSHA256", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithContainerRuntimeArgs adds runtime arguments for the container
func (s *ContainerResource) WithContainerRuntimeArgs(args []string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["args"] = SerializeValue(args)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerRuntimeArgs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithLifetime sets the lifetime behavior of the container resource
func (s *ContainerResource) WithLifetime(lifetime ContainerLifetime) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["lifetime"] = SerializeValue(lifetime)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withLifetime", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImagePullPolicy sets the container image pull policy
func (s *ContainerResource) WithImagePullPolicy(pullPolicy ImagePullPolicy) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["pullPolicy"] = SerializeValue(pullPolicy)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImagePullPolicy", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// PublishAsContainer configures the resource to be published as a container
func (s *ContainerResource) PublishAsContainer() (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishAsContainer", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithDockerfile configures the resource to use a Dockerfile
func (s *ContainerResource) WithDockerfile(contextPath string, dockerfilePath *string, stage *string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["contextPath"] = SerializeValue(contextPath)
	if dockerfilePath != nil {
		reqArgs["dockerfilePath"] = SerializeValue(dockerfilePath)
	}
	if stage != nil {
		reqArgs["stage"] = SerializeValue(stage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfile", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithContainerName sets the container name
func (s *ContainerResource) WithContainerName(name string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithBuildArg adds a build argument from a parameter resource
func (s *ContainerResource) WithBuildArg(name string, value *ParameterResource) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuildArg", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithBuildSecret adds a build secret from a parameter resource
func (s *ContainerResource) WithBuildSecret(name string, value *ParameterResource) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuildSecret", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithEndpointProxySupport configures endpoint proxy support
func (s *ContainerResource) WithEndpointProxySupport(proxyEnabled bool) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["proxyEnabled"] = SerializeValue(proxyEnabled)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpointProxySupport", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *ContainerResource) WithDockerfileBaseImage(buildImage *string, runtimeImage *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if buildImage != nil {
		reqArgs["buildImage"] = SerializeValue(buildImage)
	}
	if runtimeImage != nil {
		reqArgs["runtimeImage"] = SerializeValue(runtimeImage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfileBaseImage", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithContainerNetworkAlias adds a network alias for the container
func (s *ContainerResource) WithContainerNetworkAlias(alias string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["alias"] = SerializeValue(alias)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerNetworkAlias", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithMcpServer configures an MCP server endpoint on the resource
func (s *ContainerResource) WithMcpServer(path *string, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withMcpServer", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithOtlpExporter configures OTLP telemetry export
func (s *ContainerResource) WithOtlpExporter() (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporter", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithOtlpExporterProtocol configures OTLP telemetry export with specific protocol
func (s *ContainerResource) WithOtlpExporterProtocol(protocol OtlpProtocol) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["protocol"] = SerializeValue(protocol)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporterProtocol", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// PublishAsConnectionString publishes the resource as a connection string
func (s *ContainerResource) PublishAsConnectionString() (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishAsConnectionString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithRequiredCommand adds a required command dependency
func (s *ContainerResource) WithRequiredCommand(command string, helpLink *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["command"] = SerializeValue(command)
	if helpLink != nil {
		reqArgs["helpLink"] = SerializeValue(helpLink)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRequiredCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEnvironment sets an environment variable
func (s *ContainerResource) WithEnvironment(name string, value string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentExpression adds an environment variable with a reference expression
func (s *ContainerResource) WithEnvironmentExpression(name string, value *ReferenceExpression) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentCallback sets environment variables via callback
func (s *ContainerResource) WithEnvironmentCallback(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentCallbackAsync sets environment variables via async callback
func (s *ContainerResource) WithEnvironmentCallbackAsync(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentEndpoint sets an environment variable from an endpoint reference
func (s *ContainerResource) WithEnvironmentEndpoint(name string, endpointReference *EndpointReference) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["endpointReference"] = SerializeValue(endpointReference)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentParameter sets an environment variable from a parameter resource
func (s *ContainerResource) WithEnvironmentParameter(name string, parameter *ParameterResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["parameter"] = SerializeValue(parameter)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentParameter", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentConnectionString sets an environment variable from a connection string resource
func (s *ContainerResource) WithEnvironmentConnectionString(envVarName string, resource *IResourceWithConnectionString) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["envVarName"] = SerializeValue(envVarName)
	reqArgs["resource"] = SerializeValue(resource)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentConnectionString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithArgs adds arguments
func (s *ContainerResource) WithArgs(args []string) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["args"] = SerializeValue(args)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithArgsCallback sets command-line arguments via callback
func (s *ContainerResource) WithArgsCallback(callback func(...any) any) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithArgsCallbackAsync sets command-line arguments via async callback
func (s *ContainerResource) WithArgsCallbackAsync(callback func(...any) any) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithReference adds a reference to another resource
func (s *ContainerResource) WithReference(source *IResource, connectionName *string, optional *bool, name *string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["source"] = SerializeValue(source)
	if connectionName != nil {
		reqArgs["connectionName"] = SerializeValue(connectionName)
	}
	if optional != nil {
		reqArgs["optional"] = SerializeValue(optional)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReference", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceUri adds a reference to a URI
func (s *ContainerResource) WithReferenceUri(name string, uri string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["uri"] = SerializeValue(uri)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceUri", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceExternalService adds a reference to an external service
func (s *ContainerResource) WithReferenceExternalService(externalService *ExternalServiceResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["externalService"] = SerializeValue(externalService)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceExternalService", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceEndpoint adds a reference to an endpoint
func (s *ContainerResource) WithReferenceEndpoint(endpointReference *EndpointReference) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointReference"] = SerializeValue(endpointReference)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEndpoint adds a network endpoint
func (s *ContainerResource) WithEndpoint(port *float64, targetPort *float64, scheme *string, name *string, env *string, isProxied *bool, isExternal *bool, protocol *ProtocolType) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if scheme != nil {
		reqArgs["scheme"] = SerializeValue(scheme)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	if isExternal != nil {
		reqArgs["isExternal"] = SerializeValue(isExternal)
	}
	if protocol != nil {
		reqArgs["protocol"] = SerializeValue(protocol)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpEndpoint adds an HTTP endpoint
func (s *ContainerResource) WithHttpEndpoint(port *float64, targetPort *float64, name *string, env *string, isProxied *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpsEndpoint adds an HTTPS endpoint
func (s *ContainerResource) WithHttpsEndpoint(port *float64, targetPort *float64, name *string, env *string, isProxied *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithExternalHttpEndpoints makes HTTP endpoints externally accessible
func (s *ContainerResource) WithExternalHttpEndpoints() (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExternalHttpEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// GetEndpoint gets an endpoint reference
func (s *ContainerResource) GetEndpoint(name string) (*EndpointReference, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointReference), nil
}

// AsHttp2Service configures resource for HTTP/2
func (s *ContainerResource) AsHttp2Service() (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/asHttp2Service", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithUrlsCallback customizes displayed URLs via callback
func (s *ContainerResource) WithUrlsCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlsCallbackAsync customizes displayed URLs via async callback
func (s *ContainerResource) WithUrlsCallbackAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *ContainerResource) WithUrl(url string, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrl", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlExpression adds a URL using a reference expression
func (s *ContainerResource) WithUrlExpression(url *ReferenceExpression, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *ContainerResource) WithUrlForEndpoint(endpointName string, callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpointFactory adds a URL for a specific endpoint via factory callback
func (s *ContainerResource) WithUrlForEndpointFactory(endpointName string, callback func(...any) any) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpointFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *ContainerResource) ExcludeFromManifest() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromManifest", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WaitFor waits for another resource to be ready
func (s *ContainerResource) WaitFor(dependency *IResource) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForWithBehavior waits for another resource with specific behavior
func (s *ContainerResource) WaitForWithBehavior(dependency *IResource, waitBehavior WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForWithBehavior", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStart waits for another resource to start
func (s *ContainerResource) WaitForStart(dependency *IResource) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStartWithBehavior waits for another resource to start with specific behavior
func (s *ContainerResource) WaitForStartWithBehavior(dependency *IResource, waitBehavior WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStartWithBehavior", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WithExplicitStart prevents resource from starting automatically
func (s *ContainerResource) WithExplicitStart() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExplicitStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WaitForCompletion waits for resource completion
func (s *ContainerResource) WaitForCompletion(dependency *IResource, exitCode *float64) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if exitCode != nil {
		reqArgs["exitCode"] = SerializeValue(exitCode)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForCompletion", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WithHealthCheck adds a health check by key
func (s *ContainerResource) WithHealthCheck(key string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["key"] = SerializeValue(key)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithHttpHealthCheck adds an HTTP health check
func (s *ContainerResource) WithHttpHealthCheck(path *string, statusCode *float64, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if statusCode != nil {
		reqArgs["statusCode"] = SerializeValue(statusCode)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithCommand adds a resource command
func (s *ContainerResource) WithCommand(name string, displayName string, executeCommand func(...any) any, commandOptions *CommandOptions) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["displayName"] = SerializeValue(displayName)
	if executeCommand != nil {
		reqArgs["executeCommand"] = RegisterCallback(executeCommand)
	}
	if commandOptions != nil {
		reqArgs["commandOptions"] = SerializeValue(commandOptions)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDeveloperCertificateTrust configures developer certificate trust
func (s *ContainerResource) WithDeveloperCertificateTrust(trust bool) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["trust"] = SerializeValue(trust)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDeveloperCertificateTrust", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCertificateTrustScope sets the certificate trust scope
func (s *ContainerResource) WithCertificateTrustScope(scope CertificateTrustScope) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["scope"] = SerializeValue(scope)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCertificateTrustScope", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithHttpsDeveloperCertificate configures HTTPS with a developer certificate
func (s *ContainerResource) WithHttpsDeveloperCertificate(password *ParameterResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if password != nil {
		reqArgs["password"] = SerializeValue(password)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsDeveloperCertificate", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithoutHttpsCertificate removes HTTPS certificate configuration
func (s *ContainerResource) WithoutHttpsCertificate() (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withoutHttpsCertificate", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithParentRelationship sets the parent relationship
func (s *ContainerResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParentRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithChildRelationship sets a child relationship
func (s *ContainerResource) WithChildRelationship(child *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["child"] = SerializeValue(child)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withChildRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithIconName sets the icon for the resource
func (s *ContainerResource) WithIconName(iconName string, iconVariant *IconVariant) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["iconName"] = SerializeValue(iconName)
	if iconVariant != nil {
		reqArgs["iconVariant"] = SerializeValue(iconVariant)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withIconName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithHttpProbe adds an HTTP health probe to the resource
func (s *ContainerResource) WithHttpProbe(probeType ProbeType, path *string, initialDelaySeconds *float64, periodSeconds *float64, timeoutSeconds *float64, failureThreshold *float64, successThreshold *float64, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["probeType"] = SerializeValue(probeType)
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if initialDelaySeconds != nil {
		reqArgs["initialDelaySeconds"] = SerializeValue(initialDelaySeconds)
	}
	if periodSeconds != nil {
		reqArgs["periodSeconds"] = SerializeValue(periodSeconds)
	}
	if timeoutSeconds != nil {
		reqArgs["timeoutSeconds"] = SerializeValue(timeoutSeconds)
	}
	if failureThreshold != nil {
		reqArgs["failureThreshold"] = SerializeValue(failureThreshold)
	}
	if successThreshold != nil {
		reqArgs["successThreshold"] = SerializeValue(successThreshold)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpProbe", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *ContainerResource) ExcludeFromMcp() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromMcp", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithRemoteImageName sets the remote image name for publishing
func (s *ContainerResource) WithRemoteImageName(remoteImageName string) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["remoteImageName"] = SerializeValue(remoteImageName)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRemoteImageName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
}

// WithRemoteImageTag sets the remote image tag for publishing
func (s *ContainerResource) WithRemoteImageTag(remoteImageTag string) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["remoteImageTag"] = SerializeValue(remoteImageTag)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRemoteImageTag", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *ContainerResource) WithPipelineStepFactory(stepName string, callback func(...any) any, dependsOn []string, requiredBy []string, tags []string, description *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["stepName"] = SerializeValue(stepName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if dependsOn != nil {
		reqArgs["dependsOn"] = SerializeValue(dependsOn)
	}
	if requiredBy != nil {
		reqArgs["requiredBy"] = SerializeValue(requiredBy)
	}
	if tags != nil {
		reqArgs["tags"] = SerializeValue(tags)
	}
	if description != nil {
		reqArgs["description"] = SerializeValue(description)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineStepFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfigurationAsync configures pipeline step dependencies via an async callback
func (s *ContainerResource) WithPipelineConfigurationAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfigurationAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *ContainerResource) WithPipelineConfiguration(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithVolume adds a volume
func (s *ContainerResource) WithVolume(target string, name *string, isReadOnly *bool) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	reqArgs["target"] = SerializeValue(target)
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if isReadOnly != nil {
		reqArgs["isReadOnly"] = SerializeValue(isReadOnly)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withVolume", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// GetResourceName gets the resource name
func (s *ContainerResource) GetResourceName() (*string, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *ContainerResource) OnBeforeResourceStarted(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onBeforeResourceStarted", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *ContainerResource) OnResourceStopped(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceStopped", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *ContainerResource) OnInitializeResource(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onInitializeResource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceEndpointsAllocated subscribes to the ResourceEndpointsAllocated event
func (s *ContainerResource) OnResourceEndpointsAllocated(callback func(...any) any) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceEndpointsAllocated", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// OnResourceReady subscribes to the ResourceReady event
func (s *ContainerResource) OnResourceReady(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceReady", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalString adds an optional string parameter
func (s *ContainerResource) WithOptionalString(value *string, enabled *bool) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if value != nil {
		reqArgs["value"] = SerializeValue(value)
	}
	if enabled != nil {
		reqArgs["enabled"] = SerializeValue(enabled)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithConfig configures the resource with a DTO
func (s *ContainerResource) WithConfig(config *TestConfigDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestWithEnvironmentCallback configures environment with callback (test version)
func (s *ContainerResource) TestWithEnvironmentCallback(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWithEnvironmentCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCreatedAt sets the created timestamp
func (s *ContainerResource) WithCreatedAt(createdAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["createdAt"] = SerializeValue(createdAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCreatedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithModifiedAt sets the modified timestamp
func (s *ContainerResource) WithModifiedAt(modifiedAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["modifiedAt"] = SerializeValue(modifiedAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withModifiedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCorrelationId sets the correlation ID
func (s *ContainerResource) WithCorrelationId(correlationId string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["correlationId"] = SerializeValue(correlationId)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCorrelationId", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalCallback configures with optional callback
func (s *ContainerResource) WithOptionalCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithStatus sets the resource status
func (s *ContainerResource) WithStatus(status TestResourceStatus) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["status"] = SerializeValue(status)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withStatus", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithNestedConfig configures with nested DTO
func (s *ContainerResource) WithNestedConfig(config *TestNestedDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withNestedConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithValidator adds validation callback
func (s *ContainerResource) WithValidator(validator func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if validator != nil {
		reqArgs["validator"] = RegisterCallback(validator)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withValidator", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestWaitFor waits for another resource (test version)
func (s *ContainerResource) TestWaitFor(dependency *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWaitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDependency adds a dependency on another resource
func (s *ContainerResource) WithDependency(dependency *IResourceWithConnectionString) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withDependency", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEndpoints sets the endpoints
func (s *ContainerResource) WithEndpoints(endpoints []string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpoints"] = SerializeValue(endpoints)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEnvironmentVariables sets environment variables
func (s *ContainerResource) WithEnvironmentVariables(variables map[string]string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["variables"] = SerializeValue(variables)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEnvironmentVariables", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCancellableOperation performs a cancellable operation
func (s *ContainerResource) WithCancellableOperation(operation func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if operation != nil {
		reqArgs["operation"] = RegisterCallback(operation)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCancellableOperation", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// DistributedApplication wraps a handle for Aspire.Hosting/Aspire.Hosting.DistributedApplication.
type DistributedApplication struct {
	HandleWrapperBase
}

// NewDistributedApplication creates a new DistributedApplication.
func NewDistributedApplication(handle *Handle, client *AspireClient) *DistributedApplication {
	return &DistributedApplication{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Run runs the distributed application
func (s *DistributedApplication) Run(cancellationToken *CancellationToken) error {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	if cancellationToken != nil {
		reqArgs["cancellationToken"] = RegisterCancellation(cancellationToken, s.Client())
	}
	_, err := s.Client().InvokeCapability("Aspire.Hosting/run", reqArgs)
	return err
}

// DistributedApplicationEventSubscription wraps a handle for Aspire.Hosting/Aspire.Hosting.Eventing.DistributedApplicationEventSubscription.
type DistributedApplicationEventSubscription struct {
	HandleWrapperBase
}

// NewDistributedApplicationEventSubscription creates a new DistributedApplicationEventSubscription.
func NewDistributedApplicationEventSubscription(handle *Handle, client *AspireClient) *DistributedApplicationEventSubscription {
	return &DistributedApplicationEventSubscription{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// DistributedApplicationExecutionContext wraps a handle for Aspire.Hosting/Aspire.Hosting.DistributedApplicationExecutionContext.
type DistributedApplicationExecutionContext struct {
	HandleWrapperBase
}

// NewDistributedApplicationExecutionContext creates a new DistributedApplicationExecutionContext.
func NewDistributedApplicationExecutionContext(handle *Handle, client *AspireClient) *DistributedApplicationExecutionContext {
	return &DistributedApplicationExecutionContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// PublisherName gets the PublisherName property
func (s *DistributedApplicationExecutionContext) PublisherName() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/DistributedApplicationExecutionContext.publisherName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// SetPublisherName sets the PublisherName property
func (s *DistributedApplicationExecutionContext) SetPublisherName(value string) (*DistributedApplicationExecutionContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/DistributedApplicationExecutionContext.setPublisherName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationExecutionContext), nil
}

// Operation gets the Operation property
func (s *DistributedApplicationExecutionContext) Operation() (*DistributedApplicationOperation, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/DistributedApplicationExecutionContext.operation", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationOperation), nil
}

// ServiceProvider gets the ServiceProvider property
func (s *DistributedApplicationExecutionContext) ServiceProvider() (*IServiceProvider, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/DistributedApplicationExecutionContext.serviceProvider", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IServiceProvider), nil
}

// IsPublishMode gets the IsPublishMode property
func (s *DistributedApplicationExecutionContext) IsPublishMode() (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/DistributedApplicationExecutionContext.isPublishMode", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// IsRunMode gets the IsRunMode property
func (s *DistributedApplicationExecutionContext) IsRunMode() (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/DistributedApplicationExecutionContext.isRunMode", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// DistributedApplicationExecutionContextOptions wraps a handle for Aspire.Hosting/Aspire.Hosting.DistributedApplicationExecutionContextOptions.
type DistributedApplicationExecutionContextOptions struct {
	HandleWrapperBase
}

// NewDistributedApplicationExecutionContextOptions creates a new DistributedApplicationExecutionContextOptions.
func NewDistributedApplicationExecutionContextOptions(handle *Handle, client *AspireClient) *DistributedApplicationExecutionContextOptions {
	return &DistributedApplicationExecutionContextOptions{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// DistributedApplicationModel wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.DistributedApplicationModel.
type DistributedApplicationModel struct {
	HandleWrapperBase
}

// NewDistributedApplicationModel creates a new DistributedApplicationModel.
func NewDistributedApplicationModel(handle *Handle, client *AspireClient) *DistributedApplicationModel {
	return &DistributedApplicationModel{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// GetResources gets resources from the distributed application model
func (s *DistributedApplicationModel) GetResources() (*[]*IResource, error) {
	reqArgs := map[string]any{
		"model": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getResources", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*[]*IResource), nil
}

// FindResourceByName finds a resource by name
func (s *DistributedApplicationModel) FindResourceByName(name string) (*IResource, error) {
	reqArgs := map[string]any{
		"model": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/findResourceByName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// DistributedApplicationResourceEventSubscription wraps a handle for Aspire.Hosting/Aspire.Hosting.Eventing.DistributedApplicationResourceEventSubscription.
type DistributedApplicationResourceEventSubscription struct {
	HandleWrapperBase
}

// NewDistributedApplicationResourceEventSubscription creates a new DistributedApplicationResourceEventSubscription.
func NewDistributedApplicationResourceEventSubscription(handle *Handle, client *AspireClient) *DistributedApplicationResourceEventSubscription {
	return &DistributedApplicationResourceEventSubscription{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// DotnetToolResource wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.DotnetToolResource.
type DotnetToolResource struct {
	ResourceBuilderBase
}

// NewDotnetToolResource creates a new DotnetToolResource.
func NewDotnetToolResource(handle *Handle, client *AspireClient) *DotnetToolResource {
	return &DotnetToolResource{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// WithContainerRegistry configures a resource to use a container registry
func (s *DotnetToolResource) WithContainerRegistry(registry *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["registry"] = SerializeValue(registry)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerRegistry", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *DotnetToolResource) WithDockerfileBaseImage(buildImage *string, runtimeImage *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if buildImage != nil {
		reqArgs["buildImage"] = SerializeValue(buildImage)
	}
	if runtimeImage != nil {
		reqArgs["runtimeImage"] = SerializeValue(runtimeImage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfileBaseImage", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithToolPackage sets the tool package ID
func (s *DotnetToolResource) WithToolPackage(packageId string) (*DotnetToolResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["packageId"] = SerializeValue(packageId)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withToolPackage", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DotnetToolResource), nil
}

// WithToolVersion sets the tool version
func (s *DotnetToolResource) WithToolVersion(version string) (*DotnetToolResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["version"] = SerializeValue(version)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withToolVersion", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DotnetToolResource), nil
}

// WithToolPrerelease allows prerelease tool versions
func (s *DotnetToolResource) WithToolPrerelease() (*DotnetToolResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withToolPrerelease", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DotnetToolResource), nil
}

// WithToolSource adds a NuGet source for the tool
func (s *DotnetToolResource) WithToolSource(source string) (*DotnetToolResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["source"] = SerializeValue(source)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withToolSource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DotnetToolResource), nil
}

// WithToolIgnoreExistingFeeds ignores existing NuGet feeds
func (s *DotnetToolResource) WithToolIgnoreExistingFeeds() (*DotnetToolResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withToolIgnoreExistingFeeds", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DotnetToolResource), nil
}

// WithToolIgnoreFailedSources ignores failed NuGet sources
func (s *DotnetToolResource) WithToolIgnoreFailedSources() (*DotnetToolResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withToolIgnoreFailedSources", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DotnetToolResource), nil
}

// PublishAsDockerFile publishes the executable as a Docker container
func (s *DotnetToolResource) PublishAsDockerFile() (*ExecutableResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishAsDockerFile", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ExecutableResource), nil
}

// PublishAsDockerFileWithConfigure publishes an executable as a Docker file with optional container configuration
func (s *DotnetToolResource) PublishAsDockerFileWithConfigure(configure func(...any) any) (*ExecutableResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if configure != nil {
		reqArgs["configure"] = RegisterCallback(configure)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishAsDockerFileWithConfigure", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ExecutableResource), nil
}

// WithExecutableCommand sets the executable command
func (s *DotnetToolResource) WithExecutableCommand(command string) (*ExecutableResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["command"] = SerializeValue(command)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExecutableCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ExecutableResource), nil
}

// WithWorkingDirectory sets the executable working directory
func (s *DotnetToolResource) WithWorkingDirectory(workingDirectory string) (*ExecutableResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["workingDirectory"] = SerializeValue(workingDirectory)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withWorkingDirectory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ExecutableResource), nil
}

// WithMcpServer configures an MCP server endpoint on the resource
func (s *DotnetToolResource) WithMcpServer(path *string, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withMcpServer", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithOtlpExporter configures OTLP telemetry export
func (s *DotnetToolResource) WithOtlpExporter() (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporter", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithOtlpExporterProtocol configures OTLP telemetry export with specific protocol
func (s *DotnetToolResource) WithOtlpExporterProtocol(protocol OtlpProtocol) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["protocol"] = SerializeValue(protocol)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporterProtocol", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithRequiredCommand adds a required command dependency
func (s *DotnetToolResource) WithRequiredCommand(command string, helpLink *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["command"] = SerializeValue(command)
	if helpLink != nil {
		reqArgs["helpLink"] = SerializeValue(helpLink)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRequiredCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEnvironment sets an environment variable
func (s *DotnetToolResource) WithEnvironment(name string, value string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentExpression adds an environment variable with a reference expression
func (s *DotnetToolResource) WithEnvironmentExpression(name string, value *ReferenceExpression) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentCallback sets environment variables via callback
func (s *DotnetToolResource) WithEnvironmentCallback(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentCallbackAsync sets environment variables via async callback
func (s *DotnetToolResource) WithEnvironmentCallbackAsync(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentEndpoint sets an environment variable from an endpoint reference
func (s *DotnetToolResource) WithEnvironmentEndpoint(name string, endpointReference *EndpointReference) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["endpointReference"] = SerializeValue(endpointReference)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentParameter sets an environment variable from a parameter resource
func (s *DotnetToolResource) WithEnvironmentParameter(name string, parameter *ParameterResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["parameter"] = SerializeValue(parameter)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentParameter", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentConnectionString sets an environment variable from a connection string resource
func (s *DotnetToolResource) WithEnvironmentConnectionString(envVarName string, resource *IResourceWithConnectionString) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["envVarName"] = SerializeValue(envVarName)
	reqArgs["resource"] = SerializeValue(resource)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentConnectionString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithArgs adds arguments
func (s *DotnetToolResource) WithArgs(args []string) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["args"] = SerializeValue(args)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithArgsCallback sets command-line arguments via callback
func (s *DotnetToolResource) WithArgsCallback(callback func(...any) any) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithArgsCallbackAsync sets command-line arguments via async callback
func (s *DotnetToolResource) WithArgsCallbackAsync(callback func(...any) any) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithReference adds a reference to another resource
func (s *DotnetToolResource) WithReference(source *IResource, connectionName *string, optional *bool, name *string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["source"] = SerializeValue(source)
	if connectionName != nil {
		reqArgs["connectionName"] = SerializeValue(connectionName)
	}
	if optional != nil {
		reqArgs["optional"] = SerializeValue(optional)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReference", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceUri adds a reference to a URI
func (s *DotnetToolResource) WithReferenceUri(name string, uri string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["uri"] = SerializeValue(uri)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceUri", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceExternalService adds a reference to an external service
func (s *DotnetToolResource) WithReferenceExternalService(externalService *ExternalServiceResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["externalService"] = SerializeValue(externalService)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceExternalService", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceEndpoint adds a reference to an endpoint
func (s *DotnetToolResource) WithReferenceEndpoint(endpointReference *EndpointReference) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointReference"] = SerializeValue(endpointReference)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEndpoint adds a network endpoint
func (s *DotnetToolResource) WithEndpoint(port *float64, targetPort *float64, scheme *string, name *string, env *string, isProxied *bool, isExternal *bool, protocol *ProtocolType) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if scheme != nil {
		reqArgs["scheme"] = SerializeValue(scheme)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	if isExternal != nil {
		reqArgs["isExternal"] = SerializeValue(isExternal)
	}
	if protocol != nil {
		reqArgs["protocol"] = SerializeValue(protocol)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpEndpoint adds an HTTP endpoint
func (s *DotnetToolResource) WithHttpEndpoint(port *float64, targetPort *float64, name *string, env *string, isProxied *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpsEndpoint adds an HTTPS endpoint
func (s *DotnetToolResource) WithHttpsEndpoint(port *float64, targetPort *float64, name *string, env *string, isProxied *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithExternalHttpEndpoints makes HTTP endpoints externally accessible
func (s *DotnetToolResource) WithExternalHttpEndpoints() (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExternalHttpEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// GetEndpoint gets an endpoint reference
func (s *DotnetToolResource) GetEndpoint(name string) (*EndpointReference, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointReference), nil
}

// AsHttp2Service configures resource for HTTP/2
func (s *DotnetToolResource) AsHttp2Service() (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/asHttp2Service", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithUrlsCallback customizes displayed URLs via callback
func (s *DotnetToolResource) WithUrlsCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlsCallbackAsync customizes displayed URLs via async callback
func (s *DotnetToolResource) WithUrlsCallbackAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *DotnetToolResource) WithUrl(url string, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrl", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlExpression adds a URL using a reference expression
func (s *DotnetToolResource) WithUrlExpression(url *ReferenceExpression, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *DotnetToolResource) WithUrlForEndpoint(endpointName string, callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpointFactory adds a URL for a specific endpoint via factory callback
func (s *DotnetToolResource) WithUrlForEndpointFactory(endpointName string, callback func(...any) any) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpointFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *DotnetToolResource) ExcludeFromManifest() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromManifest", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WaitFor waits for another resource to be ready
func (s *DotnetToolResource) WaitFor(dependency *IResource) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForWithBehavior waits for another resource with specific behavior
func (s *DotnetToolResource) WaitForWithBehavior(dependency *IResource, waitBehavior WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForWithBehavior", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStart waits for another resource to start
func (s *DotnetToolResource) WaitForStart(dependency *IResource) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStartWithBehavior waits for another resource to start with specific behavior
func (s *DotnetToolResource) WaitForStartWithBehavior(dependency *IResource, waitBehavior WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStartWithBehavior", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WithExplicitStart prevents resource from starting automatically
func (s *DotnetToolResource) WithExplicitStart() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExplicitStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WaitForCompletion waits for resource completion
func (s *DotnetToolResource) WaitForCompletion(dependency *IResource, exitCode *float64) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if exitCode != nil {
		reqArgs["exitCode"] = SerializeValue(exitCode)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForCompletion", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WithHealthCheck adds a health check by key
func (s *DotnetToolResource) WithHealthCheck(key string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["key"] = SerializeValue(key)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithHttpHealthCheck adds an HTTP health check
func (s *DotnetToolResource) WithHttpHealthCheck(path *string, statusCode *float64, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if statusCode != nil {
		reqArgs["statusCode"] = SerializeValue(statusCode)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithCommand adds a resource command
func (s *DotnetToolResource) WithCommand(name string, displayName string, executeCommand func(...any) any, commandOptions *CommandOptions) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["displayName"] = SerializeValue(displayName)
	if executeCommand != nil {
		reqArgs["executeCommand"] = RegisterCallback(executeCommand)
	}
	if commandOptions != nil {
		reqArgs["commandOptions"] = SerializeValue(commandOptions)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDeveloperCertificateTrust configures developer certificate trust
func (s *DotnetToolResource) WithDeveloperCertificateTrust(trust bool) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["trust"] = SerializeValue(trust)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDeveloperCertificateTrust", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCertificateTrustScope sets the certificate trust scope
func (s *DotnetToolResource) WithCertificateTrustScope(scope CertificateTrustScope) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["scope"] = SerializeValue(scope)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCertificateTrustScope", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithHttpsDeveloperCertificate configures HTTPS with a developer certificate
func (s *DotnetToolResource) WithHttpsDeveloperCertificate(password *ParameterResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if password != nil {
		reqArgs["password"] = SerializeValue(password)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsDeveloperCertificate", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithoutHttpsCertificate removes HTTPS certificate configuration
func (s *DotnetToolResource) WithoutHttpsCertificate() (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withoutHttpsCertificate", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithParentRelationship sets the parent relationship
func (s *DotnetToolResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParentRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithChildRelationship sets a child relationship
func (s *DotnetToolResource) WithChildRelationship(child *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["child"] = SerializeValue(child)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withChildRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithIconName sets the icon for the resource
func (s *DotnetToolResource) WithIconName(iconName string, iconVariant *IconVariant) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["iconName"] = SerializeValue(iconName)
	if iconVariant != nil {
		reqArgs["iconVariant"] = SerializeValue(iconVariant)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withIconName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithHttpProbe adds an HTTP health probe to the resource
func (s *DotnetToolResource) WithHttpProbe(probeType ProbeType, path *string, initialDelaySeconds *float64, periodSeconds *float64, timeoutSeconds *float64, failureThreshold *float64, successThreshold *float64, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["probeType"] = SerializeValue(probeType)
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if initialDelaySeconds != nil {
		reqArgs["initialDelaySeconds"] = SerializeValue(initialDelaySeconds)
	}
	if periodSeconds != nil {
		reqArgs["periodSeconds"] = SerializeValue(periodSeconds)
	}
	if timeoutSeconds != nil {
		reqArgs["timeoutSeconds"] = SerializeValue(timeoutSeconds)
	}
	if failureThreshold != nil {
		reqArgs["failureThreshold"] = SerializeValue(failureThreshold)
	}
	if successThreshold != nil {
		reqArgs["successThreshold"] = SerializeValue(successThreshold)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpProbe", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *DotnetToolResource) ExcludeFromMcp() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromMcp", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithRemoteImageName sets the remote image name for publishing
func (s *DotnetToolResource) WithRemoteImageName(remoteImageName string) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["remoteImageName"] = SerializeValue(remoteImageName)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRemoteImageName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
}

// WithRemoteImageTag sets the remote image tag for publishing
func (s *DotnetToolResource) WithRemoteImageTag(remoteImageTag string) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["remoteImageTag"] = SerializeValue(remoteImageTag)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRemoteImageTag", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *DotnetToolResource) WithPipelineStepFactory(stepName string, callback func(...any) any, dependsOn []string, requiredBy []string, tags []string, description *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["stepName"] = SerializeValue(stepName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if dependsOn != nil {
		reqArgs["dependsOn"] = SerializeValue(dependsOn)
	}
	if requiredBy != nil {
		reqArgs["requiredBy"] = SerializeValue(requiredBy)
	}
	if tags != nil {
		reqArgs["tags"] = SerializeValue(tags)
	}
	if description != nil {
		reqArgs["description"] = SerializeValue(description)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineStepFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfigurationAsync configures pipeline step dependencies via an async callback
func (s *DotnetToolResource) WithPipelineConfigurationAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfigurationAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *DotnetToolResource) WithPipelineConfiguration(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// GetResourceName gets the resource name
func (s *DotnetToolResource) GetResourceName() (*string, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *DotnetToolResource) OnBeforeResourceStarted(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onBeforeResourceStarted", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *DotnetToolResource) OnResourceStopped(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceStopped", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *DotnetToolResource) OnInitializeResource(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onInitializeResource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceEndpointsAllocated subscribes to the ResourceEndpointsAllocated event
func (s *DotnetToolResource) OnResourceEndpointsAllocated(callback func(...any) any) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceEndpointsAllocated", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// OnResourceReady subscribes to the ResourceReady event
func (s *DotnetToolResource) OnResourceReady(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceReady", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalString adds an optional string parameter
func (s *DotnetToolResource) WithOptionalString(value *string, enabled *bool) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if value != nil {
		reqArgs["value"] = SerializeValue(value)
	}
	if enabled != nil {
		reqArgs["enabled"] = SerializeValue(enabled)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithConfig configures the resource with a DTO
func (s *DotnetToolResource) WithConfig(config *TestConfigDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestWithEnvironmentCallback configures environment with callback (test version)
func (s *DotnetToolResource) TestWithEnvironmentCallback(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWithEnvironmentCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCreatedAt sets the created timestamp
func (s *DotnetToolResource) WithCreatedAt(createdAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["createdAt"] = SerializeValue(createdAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCreatedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithModifiedAt sets the modified timestamp
func (s *DotnetToolResource) WithModifiedAt(modifiedAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["modifiedAt"] = SerializeValue(modifiedAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withModifiedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCorrelationId sets the correlation ID
func (s *DotnetToolResource) WithCorrelationId(correlationId string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["correlationId"] = SerializeValue(correlationId)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCorrelationId", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalCallback configures with optional callback
func (s *DotnetToolResource) WithOptionalCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithStatus sets the resource status
func (s *DotnetToolResource) WithStatus(status TestResourceStatus) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["status"] = SerializeValue(status)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withStatus", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithNestedConfig configures with nested DTO
func (s *DotnetToolResource) WithNestedConfig(config *TestNestedDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withNestedConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithValidator adds validation callback
func (s *DotnetToolResource) WithValidator(validator func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if validator != nil {
		reqArgs["validator"] = RegisterCallback(validator)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withValidator", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestWaitFor waits for another resource (test version)
func (s *DotnetToolResource) TestWaitFor(dependency *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWaitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDependency adds a dependency on another resource
func (s *DotnetToolResource) WithDependency(dependency *IResourceWithConnectionString) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withDependency", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEndpoints sets the endpoints
func (s *DotnetToolResource) WithEndpoints(endpoints []string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpoints"] = SerializeValue(endpoints)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEnvironmentVariables sets environment variables
func (s *DotnetToolResource) WithEnvironmentVariables(variables map[string]string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["variables"] = SerializeValue(variables)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEnvironmentVariables", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCancellableOperation performs a cancellable operation
func (s *DotnetToolResource) WithCancellableOperation(operation func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if operation != nil {
		reqArgs["operation"] = RegisterCallback(operation)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCancellableOperation", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// EndpointReference wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointReference.
type EndpointReference struct {
	HandleWrapperBase
}

// NewEndpointReference creates a new EndpointReference.
func NewEndpointReference(handle *Handle, client *AspireClient) *EndpointReference {
	return &EndpointReference{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Resource gets the Resource property
func (s *EndpointReference) Resource() (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReference.resource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// EndpointName gets the EndpointName property
func (s *EndpointReference) EndpointName() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReference.endpointName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// ErrorMessage gets the ErrorMessage property
func (s *EndpointReference) ErrorMessage() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReference.errorMessage", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// SetErrorMessage sets the ErrorMessage property
func (s *EndpointReference) SetErrorMessage(value string) (*EndpointReference, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReference.setErrorMessage", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointReference), nil
}

// IsAllocated gets the IsAllocated property
func (s *EndpointReference) IsAllocated() (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReference.isAllocated", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// Exists gets the Exists property
func (s *EndpointReference) Exists() (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReference.exists", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// IsHttp gets the IsHttp property
func (s *EndpointReference) IsHttp() (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReference.isHttp", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// IsHttps gets the IsHttps property
func (s *EndpointReference) IsHttps() (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReference.isHttps", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// TlsEnabled gets the TlsEnabled property
func (s *EndpointReference) TlsEnabled() (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReference.tlsEnabled", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// Port gets the Port property
func (s *EndpointReference) Port() (*float64, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReference.port", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*float64), nil
}

// TargetPort gets the TargetPort property
func (s *EndpointReference) TargetPort() (*float64, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReference.targetPort", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*float64), nil
}

// Host gets the Host property
func (s *EndpointReference) Host() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReference.host", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// Scheme gets the Scheme property
func (s *EndpointReference) Scheme() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReference.scheme", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// Url gets the Url property
func (s *EndpointReference) Url() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReference.url", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// GetValueAsync gets the URL of the endpoint asynchronously
func (s *EndpointReference) GetValueAsync(cancellationToken *CancellationToken) (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	if cancellationToken != nil {
		reqArgs["cancellationToken"] = RegisterCancellation(cancellationToken, s.Client())
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/getValueAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// GetTlsValue gets a conditional expression that resolves to the enabledValue when TLS is enabled on the endpoint, or to the disabledValue otherwise.
func (s *EndpointReference) GetTlsValue(enabledValue *ReferenceExpression, disabledValue *ReferenceExpression) (*ReferenceExpression, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["enabledValue"] = SerializeValue(enabledValue)
	reqArgs["disabledValue"] = SerializeValue(disabledValue)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReference.getTlsValue", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ReferenceExpression), nil
}

// EndpointReferenceExpression wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointReferenceExpression.
type EndpointReferenceExpression struct {
	HandleWrapperBase
}

// NewEndpointReferenceExpression creates a new EndpointReferenceExpression.
func NewEndpointReferenceExpression(handle *Handle, client *AspireClient) *EndpointReferenceExpression {
	return &EndpointReferenceExpression{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Endpoint gets the Endpoint property
func (s *EndpointReferenceExpression) Endpoint() (*EndpointReference, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReferenceExpression.endpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointReference), nil
}

// Property gets the Property property
func (s *EndpointReferenceExpression) Property() (*EndpointProperty, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReferenceExpression.property", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointProperty), nil
}

// ValueExpression gets the ValueExpression property
func (s *EndpointReferenceExpression) ValueExpression() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReferenceExpression.valueExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// EnvironmentCallbackContext wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.EnvironmentCallbackContext.
type EnvironmentCallbackContext struct {
	HandleWrapperBase
	environmentVariables *AspireDict[string, any]
}

// NewEnvironmentCallbackContext creates a new EnvironmentCallbackContext.
func NewEnvironmentCallbackContext(handle *Handle, client *AspireClient) *EnvironmentCallbackContext {
	return &EnvironmentCallbackContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// EnvironmentVariables gets the EnvironmentVariables property
func (s *EnvironmentCallbackContext) EnvironmentVariables() *AspireDict[string, any] {
	if s.environmentVariables == nil {
		s.environmentVariables = NewAspireDictWithGetter[string, any](s.Handle(), s.Client(), "Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.environmentVariables")
	}
	return s.environmentVariables
}

// CancellationToken gets the CancellationToken property
func (s *EnvironmentCallbackContext) CancellationToken() (*CancellationToken, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.cancellationToken", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*CancellationToken), nil
}

// Logger gets the Logger property
func (s *EnvironmentCallbackContext) Logger() (*ILogger, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.logger", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ILogger), nil
}

// SetLogger sets the Logger property
func (s *EnvironmentCallbackContext) SetLogger(value *ILogger) (*EnvironmentCallbackContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.setLogger", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EnvironmentCallbackContext), nil
}

// Resource gets the Resource property
func (s *EnvironmentCallbackContext) Resource() (*IResource, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.resource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// ExecutionContext gets the ExecutionContext property
func (s *EnvironmentCallbackContext) ExecutionContext() (*DistributedApplicationExecutionContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.executionContext", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationExecutionContext), nil
}

// ExecutableResource wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ExecutableResource.
type ExecutableResource struct {
	ResourceBuilderBase
}

// NewExecutableResource creates a new ExecutableResource.
func NewExecutableResource(handle *Handle, client *AspireClient) *ExecutableResource {
	return &ExecutableResource{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// WithContainerRegistry configures a resource to use a container registry
func (s *ExecutableResource) WithContainerRegistry(registry *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["registry"] = SerializeValue(registry)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerRegistry", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *ExecutableResource) WithDockerfileBaseImage(buildImage *string, runtimeImage *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if buildImage != nil {
		reqArgs["buildImage"] = SerializeValue(buildImage)
	}
	if runtimeImage != nil {
		reqArgs["runtimeImage"] = SerializeValue(runtimeImage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfileBaseImage", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// PublishAsDockerFile publishes the executable as a Docker container
func (s *ExecutableResource) PublishAsDockerFile() (*ExecutableResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishAsDockerFile", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ExecutableResource), nil
}

// PublishAsDockerFileWithConfigure publishes an executable as a Docker file with optional container configuration
func (s *ExecutableResource) PublishAsDockerFileWithConfigure(configure func(...any) any) (*ExecutableResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if configure != nil {
		reqArgs["configure"] = RegisterCallback(configure)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishAsDockerFileWithConfigure", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ExecutableResource), nil
}

// WithExecutableCommand sets the executable command
func (s *ExecutableResource) WithExecutableCommand(command string) (*ExecutableResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["command"] = SerializeValue(command)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExecutableCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ExecutableResource), nil
}

// WithWorkingDirectory sets the executable working directory
func (s *ExecutableResource) WithWorkingDirectory(workingDirectory string) (*ExecutableResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["workingDirectory"] = SerializeValue(workingDirectory)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withWorkingDirectory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ExecutableResource), nil
}

// WithMcpServer configures an MCP server endpoint on the resource
func (s *ExecutableResource) WithMcpServer(path *string, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withMcpServer", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithOtlpExporter configures OTLP telemetry export
func (s *ExecutableResource) WithOtlpExporter() (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporter", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithOtlpExporterProtocol configures OTLP telemetry export with specific protocol
func (s *ExecutableResource) WithOtlpExporterProtocol(protocol OtlpProtocol) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["protocol"] = SerializeValue(protocol)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporterProtocol", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithRequiredCommand adds a required command dependency
func (s *ExecutableResource) WithRequiredCommand(command string, helpLink *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["command"] = SerializeValue(command)
	if helpLink != nil {
		reqArgs["helpLink"] = SerializeValue(helpLink)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRequiredCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEnvironment sets an environment variable
func (s *ExecutableResource) WithEnvironment(name string, value string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentExpression adds an environment variable with a reference expression
func (s *ExecutableResource) WithEnvironmentExpression(name string, value *ReferenceExpression) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentCallback sets environment variables via callback
func (s *ExecutableResource) WithEnvironmentCallback(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentCallbackAsync sets environment variables via async callback
func (s *ExecutableResource) WithEnvironmentCallbackAsync(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentEndpoint sets an environment variable from an endpoint reference
func (s *ExecutableResource) WithEnvironmentEndpoint(name string, endpointReference *EndpointReference) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["endpointReference"] = SerializeValue(endpointReference)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentParameter sets an environment variable from a parameter resource
func (s *ExecutableResource) WithEnvironmentParameter(name string, parameter *ParameterResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["parameter"] = SerializeValue(parameter)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentParameter", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentConnectionString sets an environment variable from a connection string resource
func (s *ExecutableResource) WithEnvironmentConnectionString(envVarName string, resource *IResourceWithConnectionString) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["envVarName"] = SerializeValue(envVarName)
	reqArgs["resource"] = SerializeValue(resource)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentConnectionString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithArgs adds arguments
func (s *ExecutableResource) WithArgs(args []string) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["args"] = SerializeValue(args)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithArgsCallback sets command-line arguments via callback
func (s *ExecutableResource) WithArgsCallback(callback func(...any) any) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithArgsCallbackAsync sets command-line arguments via async callback
func (s *ExecutableResource) WithArgsCallbackAsync(callback func(...any) any) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithReference adds a reference to another resource
func (s *ExecutableResource) WithReference(source *IResource, connectionName *string, optional *bool, name *string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["source"] = SerializeValue(source)
	if connectionName != nil {
		reqArgs["connectionName"] = SerializeValue(connectionName)
	}
	if optional != nil {
		reqArgs["optional"] = SerializeValue(optional)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReference", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceUri adds a reference to a URI
func (s *ExecutableResource) WithReferenceUri(name string, uri string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["uri"] = SerializeValue(uri)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceUri", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceExternalService adds a reference to an external service
func (s *ExecutableResource) WithReferenceExternalService(externalService *ExternalServiceResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["externalService"] = SerializeValue(externalService)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceExternalService", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceEndpoint adds a reference to an endpoint
func (s *ExecutableResource) WithReferenceEndpoint(endpointReference *EndpointReference) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointReference"] = SerializeValue(endpointReference)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEndpoint adds a network endpoint
func (s *ExecutableResource) WithEndpoint(port *float64, targetPort *float64, scheme *string, name *string, env *string, isProxied *bool, isExternal *bool, protocol *ProtocolType) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if scheme != nil {
		reqArgs["scheme"] = SerializeValue(scheme)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	if isExternal != nil {
		reqArgs["isExternal"] = SerializeValue(isExternal)
	}
	if protocol != nil {
		reqArgs["protocol"] = SerializeValue(protocol)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpEndpoint adds an HTTP endpoint
func (s *ExecutableResource) WithHttpEndpoint(port *float64, targetPort *float64, name *string, env *string, isProxied *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpsEndpoint adds an HTTPS endpoint
func (s *ExecutableResource) WithHttpsEndpoint(port *float64, targetPort *float64, name *string, env *string, isProxied *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithExternalHttpEndpoints makes HTTP endpoints externally accessible
func (s *ExecutableResource) WithExternalHttpEndpoints() (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExternalHttpEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// GetEndpoint gets an endpoint reference
func (s *ExecutableResource) GetEndpoint(name string) (*EndpointReference, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointReference), nil
}

// AsHttp2Service configures resource for HTTP/2
func (s *ExecutableResource) AsHttp2Service() (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/asHttp2Service", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithUrlsCallback customizes displayed URLs via callback
func (s *ExecutableResource) WithUrlsCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlsCallbackAsync customizes displayed URLs via async callback
func (s *ExecutableResource) WithUrlsCallbackAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *ExecutableResource) WithUrl(url string, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrl", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlExpression adds a URL using a reference expression
func (s *ExecutableResource) WithUrlExpression(url *ReferenceExpression, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *ExecutableResource) WithUrlForEndpoint(endpointName string, callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpointFactory adds a URL for a specific endpoint via factory callback
func (s *ExecutableResource) WithUrlForEndpointFactory(endpointName string, callback func(...any) any) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpointFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *ExecutableResource) ExcludeFromManifest() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromManifest", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WaitFor waits for another resource to be ready
func (s *ExecutableResource) WaitFor(dependency *IResource) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForWithBehavior waits for another resource with specific behavior
func (s *ExecutableResource) WaitForWithBehavior(dependency *IResource, waitBehavior WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForWithBehavior", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStart waits for another resource to start
func (s *ExecutableResource) WaitForStart(dependency *IResource) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStartWithBehavior waits for another resource to start with specific behavior
func (s *ExecutableResource) WaitForStartWithBehavior(dependency *IResource, waitBehavior WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStartWithBehavior", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WithExplicitStart prevents resource from starting automatically
func (s *ExecutableResource) WithExplicitStart() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExplicitStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WaitForCompletion waits for resource completion
func (s *ExecutableResource) WaitForCompletion(dependency *IResource, exitCode *float64) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if exitCode != nil {
		reqArgs["exitCode"] = SerializeValue(exitCode)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForCompletion", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WithHealthCheck adds a health check by key
func (s *ExecutableResource) WithHealthCheck(key string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["key"] = SerializeValue(key)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithHttpHealthCheck adds an HTTP health check
func (s *ExecutableResource) WithHttpHealthCheck(path *string, statusCode *float64, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if statusCode != nil {
		reqArgs["statusCode"] = SerializeValue(statusCode)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithCommand adds a resource command
func (s *ExecutableResource) WithCommand(name string, displayName string, executeCommand func(...any) any, commandOptions *CommandOptions) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["displayName"] = SerializeValue(displayName)
	if executeCommand != nil {
		reqArgs["executeCommand"] = RegisterCallback(executeCommand)
	}
	if commandOptions != nil {
		reqArgs["commandOptions"] = SerializeValue(commandOptions)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDeveloperCertificateTrust configures developer certificate trust
func (s *ExecutableResource) WithDeveloperCertificateTrust(trust bool) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["trust"] = SerializeValue(trust)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDeveloperCertificateTrust", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCertificateTrustScope sets the certificate trust scope
func (s *ExecutableResource) WithCertificateTrustScope(scope CertificateTrustScope) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["scope"] = SerializeValue(scope)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCertificateTrustScope", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithHttpsDeveloperCertificate configures HTTPS with a developer certificate
func (s *ExecutableResource) WithHttpsDeveloperCertificate(password *ParameterResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if password != nil {
		reqArgs["password"] = SerializeValue(password)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsDeveloperCertificate", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithoutHttpsCertificate removes HTTPS certificate configuration
func (s *ExecutableResource) WithoutHttpsCertificate() (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withoutHttpsCertificate", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithParentRelationship sets the parent relationship
func (s *ExecutableResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParentRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithChildRelationship sets a child relationship
func (s *ExecutableResource) WithChildRelationship(child *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["child"] = SerializeValue(child)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withChildRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithIconName sets the icon for the resource
func (s *ExecutableResource) WithIconName(iconName string, iconVariant *IconVariant) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["iconName"] = SerializeValue(iconName)
	if iconVariant != nil {
		reqArgs["iconVariant"] = SerializeValue(iconVariant)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withIconName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithHttpProbe adds an HTTP health probe to the resource
func (s *ExecutableResource) WithHttpProbe(probeType ProbeType, path *string, initialDelaySeconds *float64, periodSeconds *float64, timeoutSeconds *float64, failureThreshold *float64, successThreshold *float64, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["probeType"] = SerializeValue(probeType)
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if initialDelaySeconds != nil {
		reqArgs["initialDelaySeconds"] = SerializeValue(initialDelaySeconds)
	}
	if periodSeconds != nil {
		reqArgs["periodSeconds"] = SerializeValue(periodSeconds)
	}
	if timeoutSeconds != nil {
		reqArgs["timeoutSeconds"] = SerializeValue(timeoutSeconds)
	}
	if failureThreshold != nil {
		reqArgs["failureThreshold"] = SerializeValue(failureThreshold)
	}
	if successThreshold != nil {
		reqArgs["successThreshold"] = SerializeValue(successThreshold)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpProbe", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *ExecutableResource) ExcludeFromMcp() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromMcp", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithRemoteImageName sets the remote image name for publishing
func (s *ExecutableResource) WithRemoteImageName(remoteImageName string) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["remoteImageName"] = SerializeValue(remoteImageName)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRemoteImageName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
}

// WithRemoteImageTag sets the remote image tag for publishing
func (s *ExecutableResource) WithRemoteImageTag(remoteImageTag string) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["remoteImageTag"] = SerializeValue(remoteImageTag)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRemoteImageTag", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *ExecutableResource) WithPipelineStepFactory(stepName string, callback func(...any) any, dependsOn []string, requiredBy []string, tags []string, description *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["stepName"] = SerializeValue(stepName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if dependsOn != nil {
		reqArgs["dependsOn"] = SerializeValue(dependsOn)
	}
	if requiredBy != nil {
		reqArgs["requiredBy"] = SerializeValue(requiredBy)
	}
	if tags != nil {
		reqArgs["tags"] = SerializeValue(tags)
	}
	if description != nil {
		reqArgs["description"] = SerializeValue(description)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineStepFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfigurationAsync configures pipeline step dependencies via an async callback
func (s *ExecutableResource) WithPipelineConfigurationAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfigurationAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *ExecutableResource) WithPipelineConfiguration(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// GetResourceName gets the resource name
func (s *ExecutableResource) GetResourceName() (*string, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *ExecutableResource) OnBeforeResourceStarted(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onBeforeResourceStarted", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *ExecutableResource) OnResourceStopped(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceStopped", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *ExecutableResource) OnInitializeResource(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onInitializeResource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceEndpointsAllocated subscribes to the ResourceEndpointsAllocated event
func (s *ExecutableResource) OnResourceEndpointsAllocated(callback func(...any) any) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceEndpointsAllocated", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// OnResourceReady subscribes to the ResourceReady event
func (s *ExecutableResource) OnResourceReady(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceReady", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalString adds an optional string parameter
func (s *ExecutableResource) WithOptionalString(value *string, enabled *bool) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if value != nil {
		reqArgs["value"] = SerializeValue(value)
	}
	if enabled != nil {
		reqArgs["enabled"] = SerializeValue(enabled)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithConfig configures the resource with a DTO
func (s *ExecutableResource) WithConfig(config *TestConfigDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestWithEnvironmentCallback configures environment with callback (test version)
func (s *ExecutableResource) TestWithEnvironmentCallback(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWithEnvironmentCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCreatedAt sets the created timestamp
func (s *ExecutableResource) WithCreatedAt(createdAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["createdAt"] = SerializeValue(createdAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCreatedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithModifiedAt sets the modified timestamp
func (s *ExecutableResource) WithModifiedAt(modifiedAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["modifiedAt"] = SerializeValue(modifiedAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withModifiedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCorrelationId sets the correlation ID
func (s *ExecutableResource) WithCorrelationId(correlationId string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["correlationId"] = SerializeValue(correlationId)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCorrelationId", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalCallback configures with optional callback
func (s *ExecutableResource) WithOptionalCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithStatus sets the resource status
func (s *ExecutableResource) WithStatus(status TestResourceStatus) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["status"] = SerializeValue(status)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withStatus", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithNestedConfig configures with nested DTO
func (s *ExecutableResource) WithNestedConfig(config *TestNestedDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withNestedConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithValidator adds validation callback
func (s *ExecutableResource) WithValidator(validator func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if validator != nil {
		reqArgs["validator"] = RegisterCallback(validator)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withValidator", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestWaitFor waits for another resource (test version)
func (s *ExecutableResource) TestWaitFor(dependency *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWaitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDependency adds a dependency on another resource
func (s *ExecutableResource) WithDependency(dependency *IResourceWithConnectionString) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withDependency", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEndpoints sets the endpoints
func (s *ExecutableResource) WithEndpoints(endpoints []string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpoints"] = SerializeValue(endpoints)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEnvironmentVariables sets environment variables
func (s *ExecutableResource) WithEnvironmentVariables(variables map[string]string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["variables"] = SerializeValue(variables)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEnvironmentVariables", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCancellableOperation performs a cancellable operation
func (s *ExecutableResource) WithCancellableOperation(operation func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if operation != nil {
		reqArgs["operation"] = RegisterCallback(operation)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCancellableOperation", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// ExecuteCommandContext wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ExecuteCommandContext.
type ExecuteCommandContext struct {
	HandleWrapperBase
}

// NewExecuteCommandContext creates a new ExecuteCommandContext.
func NewExecuteCommandContext(handle *Handle, client *AspireClient) *ExecuteCommandContext {
	return &ExecuteCommandContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// ServiceProvider gets the ServiceProvider property
func (s *ExecuteCommandContext) ServiceProvider() (*IServiceProvider, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.serviceProvider", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IServiceProvider), nil
}

// SetServiceProvider sets the ServiceProvider property
func (s *ExecuteCommandContext) SetServiceProvider(value *IServiceProvider) (*ExecuteCommandContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.setServiceProvider", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ExecuteCommandContext), nil
}

// ResourceName gets the ResourceName property
func (s *ExecuteCommandContext) ResourceName() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.resourceName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// SetResourceName sets the ResourceName property
func (s *ExecuteCommandContext) SetResourceName(value string) (*ExecuteCommandContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.setResourceName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ExecuteCommandContext), nil
}

// CancellationToken gets the CancellationToken property
func (s *ExecuteCommandContext) CancellationToken() (*CancellationToken, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.cancellationToken", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*CancellationToken), nil
}

// SetCancellationToken sets the CancellationToken property
func (s *ExecuteCommandContext) SetCancellationToken(value *CancellationToken) (*ExecuteCommandContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	if value != nil {
		reqArgs["value"] = RegisterCancellation(value, s.Client())
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.setCancellationToken", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ExecuteCommandContext), nil
}

// ExternalServiceResource wraps a handle for Aspire.Hosting/Aspire.Hosting.ExternalServiceResource.
type ExternalServiceResource struct {
	ResourceBuilderBase
}

// NewExternalServiceResource creates a new ExternalServiceResource.
func NewExternalServiceResource(handle *Handle, client *AspireClient) *ExternalServiceResource {
	return &ExternalServiceResource{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// WithContainerRegistry configures a resource to use a container registry
func (s *ExternalServiceResource) WithContainerRegistry(registry *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["registry"] = SerializeValue(registry)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerRegistry", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *ExternalServiceResource) WithDockerfileBaseImage(buildImage *string, runtimeImage *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if buildImage != nil {
		reqArgs["buildImage"] = SerializeValue(buildImage)
	}
	if runtimeImage != nil {
		reqArgs["runtimeImage"] = SerializeValue(runtimeImage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfileBaseImage", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithExternalServiceHttpHealthCheck adds an HTTP health check to an external service
func (s *ExternalServiceResource) WithExternalServiceHttpHealthCheck(path *string, statusCode *float64) (*ExternalServiceResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if statusCode != nil {
		reqArgs["statusCode"] = SerializeValue(statusCode)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExternalServiceHttpHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ExternalServiceResource), nil
}

// WithRequiredCommand adds a required command dependency
func (s *ExternalServiceResource) WithRequiredCommand(command string, helpLink *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["command"] = SerializeValue(command)
	if helpLink != nil {
		reqArgs["helpLink"] = SerializeValue(helpLink)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRequiredCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlsCallback customizes displayed URLs via callback
func (s *ExternalServiceResource) WithUrlsCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlsCallbackAsync customizes displayed URLs via async callback
func (s *ExternalServiceResource) WithUrlsCallbackAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *ExternalServiceResource) WithUrl(url string, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrl", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlExpression adds a URL using a reference expression
func (s *ExternalServiceResource) WithUrlExpression(url *ReferenceExpression, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *ExternalServiceResource) WithUrlForEndpoint(endpointName string, callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *ExternalServiceResource) ExcludeFromManifest() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromManifest", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithExplicitStart prevents resource from starting automatically
func (s *ExternalServiceResource) WithExplicitStart() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExplicitStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithHealthCheck adds a health check by key
func (s *ExternalServiceResource) WithHealthCheck(key string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["key"] = SerializeValue(key)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCommand adds a resource command
func (s *ExternalServiceResource) WithCommand(name string, displayName string, executeCommand func(...any) any, commandOptions *CommandOptions) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["displayName"] = SerializeValue(displayName)
	if executeCommand != nil {
		reqArgs["executeCommand"] = RegisterCallback(executeCommand)
	}
	if commandOptions != nil {
		reqArgs["commandOptions"] = SerializeValue(commandOptions)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithParentRelationship sets the parent relationship
func (s *ExternalServiceResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParentRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithChildRelationship sets a child relationship
func (s *ExternalServiceResource) WithChildRelationship(child *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["child"] = SerializeValue(child)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withChildRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithIconName sets the icon for the resource
func (s *ExternalServiceResource) WithIconName(iconName string, iconVariant *IconVariant) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["iconName"] = SerializeValue(iconName)
	if iconVariant != nil {
		reqArgs["iconVariant"] = SerializeValue(iconVariant)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withIconName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *ExternalServiceResource) ExcludeFromMcp() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromMcp", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *ExternalServiceResource) WithPipelineStepFactory(stepName string, callback func(...any) any, dependsOn []string, requiredBy []string, tags []string, description *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["stepName"] = SerializeValue(stepName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if dependsOn != nil {
		reqArgs["dependsOn"] = SerializeValue(dependsOn)
	}
	if requiredBy != nil {
		reqArgs["requiredBy"] = SerializeValue(requiredBy)
	}
	if tags != nil {
		reqArgs["tags"] = SerializeValue(tags)
	}
	if description != nil {
		reqArgs["description"] = SerializeValue(description)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineStepFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfigurationAsync configures pipeline step dependencies via an async callback
func (s *ExternalServiceResource) WithPipelineConfigurationAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfigurationAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *ExternalServiceResource) WithPipelineConfiguration(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// GetResourceName gets the resource name
func (s *ExternalServiceResource) GetResourceName() (*string, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *ExternalServiceResource) OnBeforeResourceStarted(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onBeforeResourceStarted", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *ExternalServiceResource) OnResourceStopped(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceStopped", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *ExternalServiceResource) OnInitializeResource(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onInitializeResource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceReady subscribes to the ResourceReady event
func (s *ExternalServiceResource) OnResourceReady(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceReady", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalString adds an optional string parameter
func (s *ExternalServiceResource) WithOptionalString(value *string, enabled *bool) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if value != nil {
		reqArgs["value"] = SerializeValue(value)
	}
	if enabled != nil {
		reqArgs["enabled"] = SerializeValue(enabled)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithConfig configures the resource with a DTO
func (s *ExternalServiceResource) WithConfig(config *TestConfigDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCreatedAt sets the created timestamp
func (s *ExternalServiceResource) WithCreatedAt(createdAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["createdAt"] = SerializeValue(createdAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCreatedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithModifiedAt sets the modified timestamp
func (s *ExternalServiceResource) WithModifiedAt(modifiedAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["modifiedAt"] = SerializeValue(modifiedAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withModifiedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCorrelationId sets the correlation ID
func (s *ExternalServiceResource) WithCorrelationId(correlationId string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["correlationId"] = SerializeValue(correlationId)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCorrelationId", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalCallback configures with optional callback
func (s *ExternalServiceResource) WithOptionalCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithStatus sets the resource status
func (s *ExternalServiceResource) WithStatus(status TestResourceStatus) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["status"] = SerializeValue(status)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withStatus", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithNestedConfig configures with nested DTO
func (s *ExternalServiceResource) WithNestedConfig(config *TestNestedDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withNestedConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithValidator adds validation callback
func (s *ExternalServiceResource) WithValidator(validator func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if validator != nil {
		reqArgs["validator"] = RegisterCallback(validator)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withValidator", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestWaitFor waits for another resource (test version)
func (s *ExternalServiceResource) TestWaitFor(dependency *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWaitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDependency adds a dependency on another resource
func (s *ExternalServiceResource) WithDependency(dependency *IResourceWithConnectionString) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withDependency", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEndpoints sets the endpoints
func (s *ExternalServiceResource) WithEndpoints(endpoints []string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpoints"] = SerializeValue(endpoints)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCancellableOperation performs a cancellable operation
func (s *ExternalServiceResource) WithCancellableOperation(operation func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if operation != nil {
		reqArgs["operation"] = RegisterCallback(operation)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCancellableOperation", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// IComputeResource wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IComputeResource.
type IComputeResource struct {
	HandleWrapperBase
}

// NewIComputeResource creates a new IComputeResource.
func NewIComputeResource(handle *Handle, client *AspireClient) *IComputeResource {
	return &IComputeResource{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// IConfiguration wraps a handle for Microsoft.Extensions.Configuration.Abstractions/Microsoft.Extensions.Configuration.IConfiguration.
type IConfiguration struct {
	HandleWrapperBase
}

// NewIConfiguration creates a new IConfiguration.
func NewIConfiguration(handle *Handle, client *AspireClient) *IConfiguration {
	return &IConfiguration{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// GetConfigValue gets a configuration value by key
func (s *IConfiguration) GetConfigValue(key string) (*string, error) {
	reqArgs := map[string]any{
		"configuration": SerializeValue(s.Handle()),
	}
	reqArgs["key"] = SerializeValue(key)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getConfigValue", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// GetConnectionString gets a connection string by name
func (s *IConfiguration) GetConnectionString(name string) (*string, error) {
	reqArgs := map[string]any{
		"configuration": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getConnectionString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// GetSection gets a configuration section by key
func (s *IConfiguration) GetSection(key string) (*IConfigurationSection, error) {
	reqArgs := map[string]any{
		"configuration": SerializeValue(s.Handle()),
	}
	reqArgs["key"] = SerializeValue(key)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getSection", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IConfigurationSection), nil
}

// GetChildren gets child configuration sections
func (s *IConfiguration) GetChildren() (*[]*IConfigurationSection, error) {
	reqArgs := map[string]any{
		"configuration": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getChildren", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*[]*IConfigurationSection), nil
}

// Exists checks whether a configuration section exists
func (s *IConfiguration) Exists(key string) (*bool, error) {
	reqArgs := map[string]any{
		"configuration": SerializeValue(s.Handle()),
	}
	reqArgs["key"] = SerializeValue(key)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/exists", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// IConfigurationSection wraps a handle for Microsoft.Extensions.Configuration.Abstractions/Microsoft.Extensions.Configuration.IConfigurationSection.
type IConfigurationSection struct {
	HandleWrapperBase
}

// NewIConfigurationSection creates a new IConfigurationSection.
func NewIConfigurationSection(handle *Handle, client *AspireClient) *IConfigurationSection {
	return &IConfigurationSection{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// IContainerFilesDestinationResource wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IContainerFilesDestinationResource.
type IContainerFilesDestinationResource struct {
	HandleWrapperBase
}

// NewIContainerFilesDestinationResource creates a new IContainerFilesDestinationResource.
func NewIContainerFilesDestinationResource(handle *Handle, client *AspireClient) *IContainerFilesDestinationResource {
	return &IContainerFilesDestinationResource{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// IDistributedApplicationBuilder wraps a handle for Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder.
type IDistributedApplicationBuilder struct {
	HandleWrapperBase
}

// NewIDistributedApplicationBuilder creates a new IDistributedApplicationBuilder.
func NewIDistributedApplicationBuilder(handle *Handle, client *AspireClient) *IDistributedApplicationBuilder {
	return &IDistributedApplicationBuilder{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// AddConnectionStringExpression adds a connection string with a reference expression
func (s *IDistributedApplicationBuilder) AddConnectionStringExpression(name string, connectionStringExpression *ReferenceExpression) (*ConnectionStringResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["connectionStringExpression"] = SerializeValue(connectionStringExpression)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addConnectionStringExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ConnectionStringResource), nil
}

// AddConnectionStringBuilder adds a connection string with a builder callback
func (s *IDistributedApplicationBuilder) AddConnectionStringBuilder(name string, connectionStringBuilder func(...any) any) (*ConnectionStringResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	if connectionStringBuilder != nil {
		reqArgs["connectionStringBuilder"] = RegisterCallback(connectionStringBuilder)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addConnectionStringBuilder", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ConnectionStringResource), nil
}

// AddContainerRegistry adds a container registry resource
func (s *IDistributedApplicationBuilder) AddContainerRegistry(name string, endpoint *ParameterResource, repository *ParameterResource) (*ContainerRegistryResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["endpoint"] = SerializeValue(endpoint)
	if repository != nil {
		reqArgs["repository"] = SerializeValue(repository)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addContainerRegistry", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerRegistryResource), nil
}

// AddContainerRegistryFromString adds a container registry with string endpoint
func (s *IDistributedApplicationBuilder) AddContainerRegistryFromString(name string, endpoint string, repository *string) (*ContainerRegistryResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["endpoint"] = SerializeValue(endpoint)
	if repository != nil {
		reqArgs["repository"] = SerializeValue(repository)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addContainerRegistryFromString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerRegistryResource), nil
}

// AddContainer adds a container resource
func (s *IDistributedApplicationBuilder) AddContainer(name string, image string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["image"] = SerializeValue(image)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addContainer", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// AddDockerfile adds a container resource built from a Dockerfile
func (s *IDistributedApplicationBuilder) AddDockerfile(name string, contextPath string, dockerfilePath *string, stage *string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["contextPath"] = SerializeValue(contextPath)
	if dockerfilePath != nil {
		reqArgs["dockerfilePath"] = SerializeValue(dockerfilePath)
	}
	if stage != nil {
		reqArgs["stage"] = SerializeValue(stage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addDockerfile", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// AddDotnetTool adds a .NET tool resource
func (s *IDistributedApplicationBuilder) AddDotnetTool(name string, packageId string) (*DotnetToolResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["packageId"] = SerializeValue(packageId)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addDotnetTool", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DotnetToolResource), nil
}

// AddExecutable adds an executable resource
func (s *IDistributedApplicationBuilder) AddExecutable(name string, command string, workingDirectory string, args []string) (*ExecutableResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["command"] = SerializeValue(command)
	reqArgs["workingDirectory"] = SerializeValue(workingDirectory)
	reqArgs["args"] = SerializeValue(args)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addExecutable", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ExecutableResource), nil
}

// AddExternalService adds an external service resource
func (s *IDistributedApplicationBuilder) AddExternalService(name string, url string) (*ExternalServiceResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["url"] = SerializeValue(url)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addExternalService", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ExternalServiceResource), nil
}

// AddExternalServiceUri adds an external service with a URI
func (s *IDistributedApplicationBuilder) AddExternalServiceUri(name string, uri string) (*ExternalServiceResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["uri"] = SerializeValue(uri)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addExternalServiceUri", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ExternalServiceResource), nil
}

// AddExternalServiceParameter adds an external service with a parameter URL
func (s *IDistributedApplicationBuilder) AddExternalServiceParameter(name string, urlParameter *ParameterResource) (*ExternalServiceResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["urlParameter"] = SerializeValue(urlParameter)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addExternalServiceParameter", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ExternalServiceResource), nil
}

// AppHostDirectory gets the AppHostDirectory property
func (s *IDistributedApplicationBuilder) AppHostDirectory() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/IDistributedApplicationBuilder.appHostDirectory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// Environment gets the Environment property
func (s *IDistributedApplicationBuilder) Environment() (*IHostEnvironment, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/IDistributedApplicationBuilder.environment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IHostEnvironment), nil
}

// Eventing gets the Eventing property
func (s *IDistributedApplicationBuilder) Eventing() (*IDistributedApplicationEventing, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/IDistributedApplicationBuilder.eventing", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IDistributedApplicationEventing), nil
}

// ExecutionContext gets the ExecutionContext property
func (s *IDistributedApplicationBuilder) ExecutionContext() (*DistributedApplicationExecutionContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/IDistributedApplicationBuilder.executionContext", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationExecutionContext), nil
}

// UserSecretsManager gets the UserSecretsManager property
func (s *IDistributedApplicationBuilder) UserSecretsManager() (*IUserSecretsManager, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/IDistributedApplicationBuilder.userSecretsManager", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IUserSecretsManager), nil
}

// Build builds the distributed application
func (s *IDistributedApplicationBuilder) Build() (*DistributedApplication, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/build", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplication), nil
}

// AddParameter adds a parameter resource
func (s *IDistributedApplicationBuilder) AddParameter(name string, secret *bool) (*ParameterResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	if secret != nil {
		reqArgs["secret"] = SerializeValue(secret)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addParameter", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ParameterResource), nil
}

// AddParameterWithValue adds a parameter with a default value
func (s *IDistributedApplicationBuilder) AddParameterWithValue(name string, value string, publishValueAsDefault *bool, secret *bool) (*ParameterResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	if publishValueAsDefault != nil {
		reqArgs["publishValueAsDefault"] = SerializeValue(publishValueAsDefault)
	}
	if secret != nil {
		reqArgs["secret"] = SerializeValue(secret)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addParameterWithValue", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ParameterResource), nil
}

// AddParameterFromConfiguration adds a parameter sourced from configuration
func (s *IDistributedApplicationBuilder) AddParameterFromConfiguration(name string, configurationKey string, secret *bool) (*ParameterResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["configurationKey"] = SerializeValue(configurationKey)
	if secret != nil {
		reqArgs["secret"] = SerializeValue(secret)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addParameterFromConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ParameterResource), nil
}

// AddConnectionString adds a connection string resource
func (s *IDistributedApplicationBuilder) AddConnectionString(name string, environmentVariableName *string) (*IResourceWithConnectionString, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	if environmentVariableName != nil {
		reqArgs["environmentVariableName"] = SerializeValue(environmentVariableName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addConnectionString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithConnectionString), nil
}

// AddProject adds a .NET project resource
func (s *IDistributedApplicationBuilder) AddProject(name string, projectPath string, launchProfileName string) (*ProjectResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["projectPath"] = SerializeValue(projectPath)
	reqArgs["launchProfileName"] = SerializeValue(launchProfileName)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addProject", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ProjectResource), nil
}

// AddProjectWithOptions adds a project resource with configuration options
func (s *IDistributedApplicationBuilder) AddProjectWithOptions(name string, projectPath string, configure func(...any) any) (*ProjectResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["projectPath"] = SerializeValue(projectPath)
	if configure != nil {
		reqArgs["configure"] = RegisterCallback(configure)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addProjectWithOptions", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ProjectResource), nil
}

// AddCSharpApp adds a C# application resource
func (s *IDistributedApplicationBuilder) AddCSharpApp(name string, path string) (*ProjectResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["path"] = SerializeValue(path)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addCSharpApp", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ProjectResource), nil
}

// AddCSharpAppWithOptions adds a C# application resource with configuration options
func (s *IDistributedApplicationBuilder) AddCSharpAppWithOptions(name string, path string, configure func(...any) any) (*CSharpAppResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["path"] = SerializeValue(path)
	if configure != nil {
		reqArgs["configure"] = RegisterCallback(configure)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addCSharpAppWithOptions", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*CSharpAppResource), nil
}

// GetConfiguration gets the application configuration
func (s *IDistributedApplicationBuilder) GetConfiguration() (*IConfiguration, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IConfiguration), nil
}

// SubscribeBeforeStart subscribes to the BeforeStart event
func (s *IDistributedApplicationBuilder) SubscribeBeforeStart(callback func(...any) any) (*DistributedApplicationEventSubscription, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/subscribeBeforeStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationEventSubscription), nil
}

// SubscribeAfterResourcesCreated subscribes to the AfterResourcesCreated event
func (s *IDistributedApplicationBuilder) SubscribeAfterResourcesCreated(callback func(...any) any) (*DistributedApplicationEventSubscription, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/subscribeAfterResourcesCreated", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationEventSubscription), nil
}

// AddTestRedis adds a test Redis resource
func (s *IDistributedApplicationBuilder) AddTestRedis(name string, port *float64) (*TestRedisResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/addTestRedis", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*TestRedisResource), nil
}

// AddTestVault adds a test vault resource
func (s *IDistributedApplicationBuilder) AddTestVault(name string) (*TestVaultResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/addTestVault", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*TestVaultResource), nil
}

// IDistributedApplicationEvent wraps a handle for Aspire.Hosting/Aspire.Hosting.Eventing.IDistributedApplicationEvent.
type IDistributedApplicationEvent struct {
	HandleWrapperBase
}

// NewIDistributedApplicationEvent creates a new IDistributedApplicationEvent.
func NewIDistributedApplicationEvent(handle *Handle, client *AspireClient) *IDistributedApplicationEvent {
	return &IDistributedApplicationEvent{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// IDistributedApplicationEventing wraps a handle for Aspire.Hosting/Aspire.Hosting.Eventing.IDistributedApplicationEventing.
type IDistributedApplicationEventing struct {
	HandleWrapperBase
}

// NewIDistributedApplicationEventing creates a new IDistributedApplicationEventing.
func NewIDistributedApplicationEventing(handle *Handle, client *AspireClient) *IDistributedApplicationEventing {
	return &IDistributedApplicationEventing{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Unsubscribe invokes the Unsubscribe method
func (s *IDistributedApplicationEventing) Unsubscribe(subscription *DistributedApplicationEventSubscription) error {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["subscription"] = SerializeValue(subscription)
	_, err := s.Client().InvokeCapability("Aspire.Hosting.Eventing/IDistributedApplicationEventing.unsubscribe", reqArgs)
	return err
}

// IDistributedApplicationResourceEvent wraps a handle for Aspire.Hosting/Aspire.Hosting.Eventing.IDistributedApplicationResourceEvent.
type IDistributedApplicationResourceEvent struct {
	HandleWrapperBase
}

// NewIDistributedApplicationResourceEvent creates a new IDistributedApplicationResourceEvent.
func NewIDistributedApplicationResourceEvent(handle *Handle, client *AspireClient) *IDistributedApplicationResourceEvent {
	return &IDistributedApplicationResourceEvent{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// IHostEnvironment wraps a handle for Microsoft.Extensions.Hosting.Abstractions/Microsoft.Extensions.Hosting.IHostEnvironment.
type IHostEnvironment struct {
	HandleWrapperBase
}

// NewIHostEnvironment creates a new IHostEnvironment.
func NewIHostEnvironment(handle *Handle, client *AspireClient) *IHostEnvironment {
	return &IHostEnvironment{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// IsDevelopment checks if running in Development environment
func (s *IHostEnvironment) IsDevelopment() (*bool, error) {
	reqArgs := map[string]any{
		"environment": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/isDevelopment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// IsProduction checks if running in Production environment
func (s *IHostEnvironment) IsProduction() (*bool, error) {
	reqArgs := map[string]any{
		"environment": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/isProduction", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// IsStaging checks if running in Staging environment
func (s *IHostEnvironment) IsStaging() (*bool, error) {
	reqArgs := map[string]any{
		"environment": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/isStaging", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// IsEnvironment checks if the environment matches the specified name
func (s *IHostEnvironment) IsEnvironment(environmentName string) (*bool, error) {
	reqArgs := map[string]any{
		"environment": SerializeValue(s.Handle()),
	}
	reqArgs["environmentName"] = SerializeValue(environmentName)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/isEnvironment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// ILogger wraps a handle for Microsoft.Extensions.Logging.Abstractions/Microsoft.Extensions.Logging.ILogger.
type ILogger struct {
	HandleWrapperBase
}

// NewILogger creates a new ILogger.
func NewILogger(handle *Handle, client *AspireClient) *ILogger {
	return &ILogger{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// LogInformation logs an information message
func (s *ILogger) LogInformation(message string) error {
	reqArgs := map[string]any{
		"logger": SerializeValue(s.Handle()),
	}
	reqArgs["message"] = SerializeValue(message)
	_, err := s.Client().InvokeCapability("Aspire.Hosting/logInformation", reqArgs)
	return err
}

// LogWarning logs a warning message
func (s *ILogger) LogWarning(message string) error {
	reqArgs := map[string]any{
		"logger": SerializeValue(s.Handle()),
	}
	reqArgs["message"] = SerializeValue(message)
	_, err := s.Client().InvokeCapability("Aspire.Hosting/logWarning", reqArgs)
	return err
}

// LogError logs an error message
func (s *ILogger) LogError(message string) error {
	reqArgs := map[string]any{
		"logger": SerializeValue(s.Handle()),
	}
	reqArgs["message"] = SerializeValue(message)
	_, err := s.Client().InvokeCapability("Aspire.Hosting/logError", reqArgs)
	return err
}

// LogDebug logs a debug message
func (s *ILogger) LogDebug(message string) error {
	reqArgs := map[string]any{
		"logger": SerializeValue(s.Handle()),
	}
	reqArgs["message"] = SerializeValue(message)
	_, err := s.Client().InvokeCapability("Aspire.Hosting/logDebug", reqArgs)
	return err
}

// Log logs a message with specified level
func (s *ILogger) Log(level string, message string) error {
	reqArgs := map[string]any{
		"logger": SerializeValue(s.Handle()),
	}
	reqArgs["level"] = SerializeValue(level)
	reqArgs["message"] = SerializeValue(message)
	_, err := s.Client().InvokeCapability("Aspire.Hosting/log", reqArgs)
	return err
}

// ILoggerFactory wraps a handle for Microsoft.Extensions.Logging.Abstractions/Microsoft.Extensions.Logging.ILoggerFactory.
type ILoggerFactory struct {
	HandleWrapperBase
}

// NewILoggerFactory creates a new ILoggerFactory.
func NewILoggerFactory(handle *Handle, client *AspireClient) *ILoggerFactory {
	return &ILoggerFactory{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// CreateLogger creates a logger for a category
func (s *ILoggerFactory) CreateLogger(categoryName string) (*ILogger, error) {
	reqArgs := map[string]any{
		"loggerFactory": SerializeValue(s.Handle()),
	}
	reqArgs["categoryName"] = SerializeValue(categoryName)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/createLogger", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ILogger), nil
}

// IReportingStep wraps a handle for Aspire.Hosting/Aspire.Hosting.Pipelines.IReportingStep.
type IReportingStep struct {
	HandleWrapperBase
}

// NewIReportingStep creates a new IReportingStep.
func NewIReportingStep(handle *Handle, client *AspireClient) *IReportingStep {
	return &IReportingStep{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// CreateTask creates a reporting task with plain-text status text
func (s *IReportingStep) CreateTask(statusText string, cancellationToken *CancellationToken) (*IReportingTask, error) {
	reqArgs := map[string]any{
		"reportingStep": SerializeValue(s.Handle()),
	}
	reqArgs["statusText"] = SerializeValue(statusText)
	if cancellationToken != nil {
		reqArgs["cancellationToken"] = RegisterCancellation(cancellationToken, s.Client())
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/createTask", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IReportingTask), nil
}

// CreateMarkdownTask creates a reporting task with Markdown-formatted status text
func (s *IReportingStep) CreateMarkdownTask(markdownString string, cancellationToken *CancellationToken) (*IReportingTask, error) {
	reqArgs := map[string]any{
		"reportingStep": SerializeValue(s.Handle()),
	}
	reqArgs["markdownString"] = SerializeValue(markdownString)
	if cancellationToken != nil {
		reqArgs["cancellationToken"] = RegisterCancellation(cancellationToken, s.Client())
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/createMarkdownTask", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IReportingTask), nil
}

// LogStep logs a plain-text message for the reporting step
func (s *IReportingStep) LogStep(level string, message string) error {
	reqArgs := map[string]any{
		"reportingStep": SerializeValue(s.Handle()),
	}
	reqArgs["level"] = SerializeValue(level)
	reqArgs["message"] = SerializeValue(message)
	_, err := s.Client().InvokeCapability("Aspire.Hosting/logStep", reqArgs)
	return err
}

// LogStepMarkdown logs a Markdown-formatted message for the reporting step
func (s *IReportingStep) LogStepMarkdown(level string, markdownString string) error {
	reqArgs := map[string]any{
		"reportingStep": SerializeValue(s.Handle()),
	}
	reqArgs["level"] = SerializeValue(level)
	reqArgs["markdownString"] = SerializeValue(markdownString)
	_, err := s.Client().InvokeCapability("Aspire.Hosting/logStepMarkdown", reqArgs)
	return err
}

// CompleteStep completes the reporting step with plain-text completion text
func (s *IReportingStep) CompleteStep(completionText string, completionState *string, cancellationToken *CancellationToken) error {
	reqArgs := map[string]any{
		"reportingStep": SerializeValue(s.Handle()),
	}
	reqArgs["completionText"] = SerializeValue(completionText)
	if completionState != nil {
		reqArgs["completionState"] = SerializeValue(completionState)
	}
	if cancellationToken != nil {
		reqArgs["cancellationToken"] = RegisterCancellation(cancellationToken, s.Client())
	}
	_, err := s.Client().InvokeCapability("Aspire.Hosting/completeStep", reqArgs)
	return err
}

// CompleteStepMarkdown completes the reporting step with Markdown-formatted completion text
func (s *IReportingStep) CompleteStepMarkdown(markdownString string, completionState *string, cancellationToken *CancellationToken) error {
	reqArgs := map[string]any{
		"reportingStep": SerializeValue(s.Handle()),
	}
	reqArgs["markdownString"] = SerializeValue(markdownString)
	if completionState != nil {
		reqArgs["completionState"] = SerializeValue(completionState)
	}
	if cancellationToken != nil {
		reqArgs["cancellationToken"] = RegisterCancellation(cancellationToken, s.Client())
	}
	_, err := s.Client().InvokeCapability("Aspire.Hosting/completeStepMarkdown", reqArgs)
	return err
}

// IReportingTask wraps a handle for Aspire.Hosting/Aspire.Hosting.Pipelines.IReportingTask.
type IReportingTask struct {
	HandleWrapperBase
}

// NewIReportingTask creates a new IReportingTask.
func NewIReportingTask(handle *Handle, client *AspireClient) *IReportingTask {
	return &IReportingTask{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// UpdateTask updates the reporting task with plain-text status text
func (s *IReportingTask) UpdateTask(statusText string, cancellationToken *CancellationToken) error {
	reqArgs := map[string]any{
		"reportingTask": SerializeValue(s.Handle()),
	}
	reqArgs["statusText"] = SerializeValue(statusText)
	if cancellationToken != nil {
		reqArgs["cancellationToken"] = RegisterCancellation(cancellationToken, s.Client())
	}
	_, err := s.Client().InvokeCapability("Aspire.Hosting/updateTask", reqArgs)
	return err
}

// UpdateTaskMarkdown updates the reporting task with Markdown-formatted status text
func (s *IReportingTask) UpdateTaskMarkdown(markdownString string, cancellationToken *CancellationToken) error {
	reqArgs := map[string]any{
		"reportingTask": SerializeValue(s.Handle()),
	}
	reqArgs["markdownString"] = SerializeValue(markdownString)
	if cancellationToken != nil {
		reqArgs["cancellationToken"] = RegisterCancellation(cancellationToken, s.Client())
	}
	_, err := s.Client().InvokeCapability("Aspire.Hosting/updateTaskMarkdown", reqArgs)
	return err
}

// CompleteTask completes the reporting task with plain-text completion text
func (s *IReportingTask) CompleteTask(completionMessage *string, completionState *string, cancellationToken *CancellationToken) error {
	reqArgs := map[string]any{
		"reportingTask": SerializeValue(s.Handle()),
	}
	if completionMessage != nil {
		reqArgs["completionMessage"] = SerializeValue(completionMessage)
	}
	if completionState != nil {
		reqArgs["completionState"] = SerializeValue(completionState)
	}
	if cancellationToken != nil {
		reqArgs["cancellationToken"] = RegisterCancellation(cancellationToken, s.Client())
	}
	_, err := s.Client().InvokeCapability("Aspire.Hosting/completeTask", reqArgs)
	return err
}

// CompleteTaskMarkdown completes the reporting task with Markdown-formatted completion text
func (s *IReportingTask) CompleteTaskMarkdown(markdownString string, completionState *string, cancellationToken *CancellationToken) error {
	reqArgs := map[string]any{
		"reportingTask": SerializeValue(s.Handle()),
	}
	reqArgs["markdownString"] = SerializeValue(markdownString)
	if completionState != nil {
		reqArgs["completionState"] = SerializeValue(completionState)
	}
	if cancellationToken != nil {
		reqArgs["cancellationToken"] = RegisterCancellation(cancellationToken, s.Client())
	}
	_, err := s.Client().InvokeCapability("Aspire.Hosting/completeTaskMarkdown", reqArgs)
	return err
}

// IResource wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResource.
type IResource struct {
	ResourceBuilderBase
}

// NewIResource creates a new IResource.
func NewIResource(handle *Handle, client *AspireClient) *IResource {
	return &IResource{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// IResourceWithArgs wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithArgs.
type IResourceWithArgs struct {
	ResourceBuilderBase
}

// NewIResourceWithArgs creates a new IResourceWithArgs.
func NewIResourceWithArgs(handle *Handle, client *AspireClient) *IResourceWithArgs {
	return &IResourceWithArgs{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// IResourceWithConnectionString wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithConnectionString.
type IResourceWithConnectionString struct {
	ResourceBuilderBase
}

// NewIResourceWithConnectionString creates a new IResourceWithConnectionString.
func NewIResourceWithConnectionString(handle *Handle, client *AspireClient) *IResourceWithConnectionString {
	return &IResourceWithConnectionString{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// IResourceWithContainerFiles wraps a handle for Aspire.Hosting/Aspire.Hosting.IResourceWithContainerFiles.
type IResourceWithContainerFiles struct {
	ResourceBuilderBase
}

// NewIResourceWithContainerFiles creates a new IResourceWithContainerFiles.
func NewIResourceWithContainerFiles(handle *Handle, client *AspireClient) *IResourceWithContainerFiles {
	return &IResourceWithContainerFiles{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// WithContainerFilesSource sets the source directory for container files
func (s *IResourceWithContainerFiles) WithContainerFilesSource(sourcePath string) (*IResourceWithContainerFiles, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["sourcePath"] = SerializeValue(sourcePath)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerFilesSource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithContainerFiles), nil
}

// ClearContainerFilesSources clears all container file sources
func (s *IResourceWithContainerFiles) ClearContainerFilesSources() (*IResourceWithContainerFiles, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/clearContainerFilesSources", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithContainerFiles), nil
}

// IResourceWithEndpoints wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithEndpoints.
type IResourceWithEndpoints struct {
	ResourceBuilderBase
}

// NewIResourceWithEndpoints creates a new IResourceWithEndpoints.
func NewIResourceWithEndpoints(handle *Handle, client *AspireClient) *IResourceWithEndpoints {
	return &IResourceWithEndpoints{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// IResourceWithEnvironment wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithEnvironment.
type IResourceWithEnvironment struct {
	ResourceBuilderBase
}

// NewIResourceWithEnvironment creates a new IResourceWithEnvironment.
func NewIResourceWithEnvironment(handle *Handle, client *AspireClient) *IResourceWithEnvironment {
	return &IResourceWithEnvironment{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// IResourceWithParent wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithParent.
type IResourceWithParent struct {
	ResourceBuilderBase
}

// NewIResourceWithParent creates a new IResourceWithParent.
func NewIResourceWithParent(handle *Handle, client *AspireClient) *IResourceWithParent {
	return &IResourceWithParent{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// IResourceWithWaitSupport wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithWaitSupport.
type IResourceWithWaitSupport struct {
	ResourceBuilderBase
}

// NewIResourceWithWaitSupport creates a new IResourceWithWaitSupport.
func NewIResourceWithWaitSupport(handle *Handle, client *AspireClient) *IResourceWithWaitSupport {
	return &IResourceWithWaitSupport{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// IServiceProvider wraps a handle for System.ComponentModel/System.IServiceProvider.
type IServiceProvider struct {
	HandleWrapperBase
}

// NewIServiceProvider creates a new IServiceProvider.
func NewIServiceProvider(handle *Handle, client *AspireClient) *IServiceProvider {
	return &IServiceProvider{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// GetEventing gets the distributed application eventing service from the service provider
func (s *IServiceProvider) GetEventing() (*IDistributedApplicationEventing, error) {
	reqArgs := map[string]any{
		"serviceProvider": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getEventing", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IDistributedApplicationEventing), nil
}

// GetLoggerFactory gets the logger factory from the service provider
func (s *IServiceProvider) GetLoggerFactory() (*ILoggerFactory, error) {
	reqArgs := map[string]any{
		"serviceProvider": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getLoggerFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ILoggerFactory), nil
}

// GetResourceLoggerService gets the resource logger service from the service provider
func (s *IServiceProvider) GetResourceLoggerService() (*ResourceLoggerService, error) {
	reqArgs := map[string]any{
		"serviceProvider": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getResourceLoggerService", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ResourceLoggerService), nil
}

// GetDistributedApplicationModel gets the distributed application model from the service provider
func (s *IServiceProvider) GetDistributedApplicationModel() (*DistributedApplicationModel, error) {
	reqArgs := map[string]any{
		"serviceProvider": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getDistributedApplicationModel", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationModel), nil
}

// GetResourceNotificationService gets the resource notification service from the service provider
func (s *IServiceProvider) GetResourceNotificationService() (*ResourceNotificationService, error) {
	reqArgs := map[string]any{
		"serviceProvider": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getResourceNotificationService", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ResourceNotificationService), nil
}

// GetUserSecretsManager gets the user secrets manager from the service provider
func (s *IServiceProvider) GetUserSecretsManager() (*IUserSecretsManager, error) {
	reqArgs := map[string]any{
		"serviceProvider": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getUserSecretsManager", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IUserSecretsManager), nil
}

// ITestVaultResource wraps a handle for Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.ITestVaultResource.
type ITestVaultResource struct {
	ResourceBuilderBase
}

// NewITestVaultResource creates a new ITestVaultResource.
func NewITestVaultResource(handle *Handle, client *AspireClient) *ITestVaultResource {
	return &ITestVaultResource{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// IUserSecretsManager wraps a handle for Aspire.Hosting/Aspire.Hosting.IUserSecretsManager.
type IUserSecretsManager struct {
	HandleWrapperBase
}

// NewIUserSecretsManager creates a new IUserSecretsManager.
func NewIUserSecretsManager(handle *Handle, client *AspireClient) *IUserSecretsManager {
	return &IUserSecretsManager{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// IsAvailable gets the IsAvailable property
func (s *IUserSecretsManager) IsAvailable() (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/IUserSecretsManager.isAvailable", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// FilePath gets the FilePath property
func (s *IUserSecretsManager) FilePath() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/IUserSecretsManager.filePath", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// TrySetSecret attempts to set a user secret value
func (s *IUserSecretsManager) TrySetSecret(name string, value string) (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/IUserSecretsManager.trySetSecret", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// SaveStateJson saves state to user secrets from a JSON string
func (s *IUserSecretsManager) SaveStateJson(json string, cancellationToken *CancellationToken) error {
	reqArgs := map[string]any{
		"userSecretsManager": SerializeValue(s.Handle()),
	}
	reqArgs["json"] = SerializeValue(json)
	if cancellationToken != nil {
		reqArgs["cancellationToken"] = RegisterCancellation(cancellationToken, s.Client())
	}
	_, err := s.Client().InvokeCapability("Aspire.Hosting/saveStateJson", reqArgs)
	return err
}

// GetOrSetSecret gets a secret value if it exists, or sets it to the provided value if it does not
func (s *IUserSecretsManager) GetOrSetSecret(resourceBuilder *IResource, name string, value string) error {
	reqArgs := map[string]any{
		"userSecretsManager": SerializeValue(s.Handle()),
	}
	reqArgs["resourceBuilder"] = SerializeValue(resourceBuilder)
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	_, err := s.Client().InvokeCapability("Aspire.Hosting/getOrSetSecret", reqArgs)
	return err
}

// InitializeResourceEvent wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.InitializeResourceEvent.
type InitializeResourceEvent struct {
	HandleWrapperBase
}

// NewInitializeResourceEvent creates a new InitializeResourceEvent.
func NewInitializeResourceEvent(handle *Handle, client *AspireClient) *InitializeResourceEvent {
	return &InitializeResourceEvent{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Resource gets the Resource property
func (s *InitializeResourceEvent) Resource() (*IResource, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/InitializeResourceEvent.resource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// Eventing gets the Eventing property
func (s *InitializeResourceEvent) Eventing() (*IDistributedApplicationEventing, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/InitializeResourceEvent.eventing", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IDistributedApplicationEventing), nil
}

// Logger gets the Logger property
func (s *InitializeResourceEvent) Logger() (*ILogger, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/InitializeResourceEvent.logger", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ILogger), nil
}

// Notifications gets the Notifications property
func (s *InitializeResourceEvent) Notifications() (*ResourceNotificationService, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/InitializeResourceEvent.notifications", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ResourceNotificationService), nil
}

// Services gets the Services property
func (s *InitializeResourceEvent) Services() (*IServiceProvider, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/InitializeResourceEvent.services", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IServiceProvider), nil
}

// ParameterResource wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ParameterResource.
type ParameterResource struct {
	ResourceBuilderBase
}

// NewParameterResource creates a new ParameterResource.
func NewParameterResource(handle *Handle, client *AspireClient) *ParameterResource {
	return &ParameterResource{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// WithContainerRegistry configures a resource to use a container registry
func (s *ParameterResource) WithContainerRegistry(registry *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["registry"] = SerializeValue(registry)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerRegistry", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *ParameterResource) WithDockerfileBaseImage(buildImage *string, runtimeImage *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if buildImage != nil {
		reqArgs["buildImage"] = SerializeValue(buildImage)
	}
	if runtimeImage != nil {
		reqArgs["runtimeImage"] = SerializeValue(runtimeImage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfileBaseImage", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDescription sets a parameter description
func (s *ParameterResource) WithDescription(description string, enableMarkdown *bool) (*ParameterResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["description"] = SerializeValue(description)
	if enableMarkdown != nil {
		reqArgs["enableMarkdown"] = SerializeValue(enableMarkdown)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDescription", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ParameterResource), nil
}

// WithRequiredCommand adds a required command dependency
func (s *ParameterResource) WithRequiredCommand(command string, helpLink *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["command"] = SerializeValue(command)
	if helpLink != nil {
		reqArgs["helpLink"] = SerializeValue(helpLink)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRequiredCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlsCallback customizes displayed URLs via callback
func (s *ParameterResource) WithUrlsCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlsCallbackAsync customizes displayed URLs via async callback
func (s *ParameterResource) WithUrlsCallbackAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *ParameterResource) WithUrl(url string, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrl", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlExpression adds a URL using a reference expression
func (s *ParameterResource) WithUrlExpression(url *ReferenceExpression, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *ParameterResource) WithUrlForEndpoint(endpointName string, callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *ParameterResource) ExcludeFromManifest() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromManifest", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithExplicitStart prevents resource from starting automatically
func (s *ParameterResource) WithExplicitStart() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExplicitStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithHealthCheck adds a health check by key
func (s *ParameterResource) WithHealthCheck(key string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["key"] = SerializeValue(key)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCommand adds a resource command
func (s *ParameterResource) WithCommand(name string, displayName string, executeCommand func(...any) any, commandOptions *CommandOptions) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["displayName"] = SerializeValue(displayName)
	if executeCommand != nil {
		reqArgs["executeCommand"] = RegisterCallback(executeCommand)
	}
	if commandOptions != nil {
		reqArgs["commandOptions"] = SerializeValue(commandOptions)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithParentRelationship sets the parent relationship
func (s *ParameterResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParentRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithChildRelationship sets a child relationship
func (s *ParameterResource) WithChildRelationship(child *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["child"] = SerializeValue(child)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withChildRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithIconName sets the icon for the resource
func (s *ParameterResource) WithIconName(iconName string, iconVariant *IconVariant) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["iconName"] = SerializeValue(iconName)
	if iconVariant != nil {
		reqArgs["iconVariant"] = SerializeValue(iconVariant)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withIconName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *ParameterResource) ExcludeFromMcp() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromMcp", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *ParameterResource) WithPipelineStepFactory(stepName string, callback func(...any) any, dependsOn []string, requiredBy []string, tags []string, description *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["stepName"] = SerializeValue(stepName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if dependsOn != nil {
		reqArgs["dependsOn"] = SerializeValue(dependsOn)
	}
	if requiredBy != nil {
		reqArgs["requiredBy"] = SerializeValue(requiredBy)
	}
	if tags != nil {
		reqArgs["tags"] = SerializeValue(tags)
	}
	if description != nil {
		reqArgs["description"] = SerializeValue(description)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineStepFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfigurationAsync configures pipeline step dependencies via an async callback
func (s *ParameterResource) WithPipelineConfigurationAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfigurationAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *ParameterResource) WithPipelineConfiguration(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// GetResourceName gets the resource name
func (s *ParameterResource) GetResourceName() (*string, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *ParameterResource) OnBeforeResourceStarted(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onBeforeResourceStarted", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *ParameterResource) OnResourceStopped(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceStopped", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *ParameterResource) OnInitializeResource(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onInitializeResource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceReady subscribes to the ResourceReady event
func (s *ParameterResource) OnResourceReady(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceReady", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalString adds an optional string parameter
func (s *ParameterResource) WithOptionalString(value *string, enabled *bool) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if value != nil {
		reqArgs["value"] = SerializeValue(value)
	}
	if enabled != nil {
		reqArgs["enabled"] = SerializeValue(enabled)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithConfig configures the resource with a DTO
func (s *ParameterResource) WithConfig(config *TestConfigDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCreatedAt sets the created timestamp
func (s *ParameterResource) WithCreatedAt(createdAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["createdAt"] = SerializeValue(createdAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCreatedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithModifiedAt sets the modified timestamp
func (s *ParameterResource) WithModifiedAt(modifiedAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["modifiedAt"] = SerializeValue(modifiedAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withModifiedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCorrelationId sets the correlation ID
func (s *ParameterResource) WithCorrelationId(correlationId string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["correlationId"] = SerializeValue(correlationId)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCorrelationId", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalCallback configures with optional callback
func (s *ParameterResource) WithOptionalCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithStatus sets the resource status
func (s *ParameterResource) WithStatus(status TestResourceStatus) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["status"] = SerializeValue(status)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withStatus", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithNestedConfig configures with nested DTO
func (s *ParameterResource) WithNestedConfig(config *TestNestedDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withNestedConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithValidator adds validation callback
func (s *ParameterResource) WithValidator(validator func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if validator != nil {
		reqArgs["validator"] = RegisterCallback(validator)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withValidator", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestWaitFor waits for another resource (test version)
func (s *ParameterResource) TestWaitFor(dependency *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWaitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDependency adds a dependency on another resource
func (s *ParameterResource) WithDependency(dependency *IResourceWithConnectionString) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withDependency", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEndpoints sets the endpoints
func (s *ParameterResource) WithEndpoints(endpoints []string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpoints"] = SerializeValue(endpoints)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCancellableOperation performs a cancellable operation
func (s *ParameterResource) WithCancellableOperation(operation func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if operation != nil {
		reqArgs["operation"] = RegisterCallback(operation)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCancellableOperation", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// PipelineConfigurationContext wraps a handle for Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineConfigurationContext.
type PipelineConfigurationContext struct {
	HandleWrapperBase
}

// NewPipelineConfigurationContext creates a new PipelineConfigurationContext.
func NewPipelineConfigurationContext(handle *Handle, client *AspireClient) *PipelineConfigurationContext {
	return &PipelineConfigurationContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Services gets the Services property
func (s *PipelineConfigurationContext) Services() (*IServiceProvider, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.services", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IServiceProvider), nil
}

// SetServices sets the Services property
func (s *PipelineConfigurationContext) SetServices(value *IServiceProvider) (*PipelineConfigurationContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.setServices", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*PipelineConfigurationContext), nil
}

// Steps gets the Steps property
func (s *PipelineConfigurationContext) Steps() (*[]*PipelineStep, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.steps", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*[]*PipelineStep), nil
}

// SetSteps sets the Steps property
func (s *PipelineConfigurationContext) SetSteps(value []*PipelineStep) (*PipelineConfigurationContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.setSteps", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*PipelineConfigurationContext), nil
}

// Model gets the Model property
func (s *PipelineConfigurationContext) Model() (*DistributedApplicationModel, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.model", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationModel), nil
}

// SetModel sets the Model property
func (s *PipelineConfigurationContext) SetModel(value *DistributedApplicationModel) (*PipelineConfigurationContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.setModel", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*PipelineConfigurationContext), nil
}

// GetStepsByTag gets pipeline steps with the specified tag
func (s *PipelineConfigurationContext) GetStepsByTag(tag string) (*[]*PipelineStep, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["tag"] = SerializeValue(tag)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/getStepsByTag", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*[]*PipelineStep), nil
}

// PipelineContext wraps a handle for Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineContext.
type PipelineContext struct {
	HandleWrapperBase
}

// NewPipelineContext creates a new PipelineContext.
func NewPipelineContext(handle *Handle, client *AspireClient) *PipelineContext {
	return &PipelineContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Model gets the Model property
func (s *PipelineContext) Model() (*DistributedApplicationModel, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineContext.model", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationModel), nil
}

// ExecutionContext gets the ExecutionContext property
func (s *PipelineContext) ExecutionContext() (*DistributedApplicationExecutionContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineContext.executionContext", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationExecutionContext), nil
}

// Services gets the Services property
func (s *PipelineContext) Services() (*IServiceProvider, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineContext.services", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IServiceProvider), nil
}

// Logger gets the Logger property
func (s *PipelineContext) Logger() (*ILogger, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineContext.logger", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ILogger), nil
}

// CancellationToken gets the CancellationToken property
func (s *PipelineContext) CancellationToken() (*CancellationToken, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineContext.cancellationToken", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*CancellationToken), nil
}

// SetCancellationToken sets the CancellationToken property
func (s *PipelineContext) SetCancellationToken(value *CancellationToken) (*PipelineContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	if value != nil {
		reqArgs["value"] = RegisterCancellation(value, s.Client())
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineContext.setCancellationToken", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*PipelineContext), nil
}

// Summary gets the Summary property
func (s *PipelineContext) Summary() (*PipelineSummary, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineContext.summary", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*PipelineSummary), nil
}

// PipelineStep wraps a handle for Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineStep.
type PipelineStep struct {
	HandleWrapperBase
	dependsOnSteps *AspireList[string]
	requiredBySteps *AspireList[string]
	tags *AspireList[string]
}

// NewPipelineStep creates a new PipelineStep.
func NewPipelineStep(handle *Handle, client *AspireClient) *PipelineStep {
	return &PipelineStep{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Name gets the Name property
func (s *PipelineStep) Name() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStep.name", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// SetName sets the Name property
func (s *PipelineStep) SetName(value string) (*PipelineStep, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStep.setName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*PipelineStep), nil
}

// Description gets the Description property
func (s *PipelineStep) Description() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStep.description", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// SetDescription sets the Description property
func (s *PipelineStep) SetDescription(value string) (*PipelineStep, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStep.setDescription", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*PipelineStep), nil
}

// DependsOnSteps gets the DependsOnSteps property
func (s *PipelineStep) DependsOnSteps() *AspireList[string] {
	if s.dependsOnSteps == nil {
		s.dependsOnSteps = NewAspireListWithGetter[string](s.Handle(), s.Client(), "Aspire.Hosting.Pipelines/PipelineStep.dependsOnSteps")
	}
	return s.dependsOnSteps
}

// SetDependsOnSteps sets the DependsOnSteps property
func (s *PipelineStep) SetDependsOnSteps(value *AspireList[string]) (*PipelineStep, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStep.setDependsOnSteps", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*PipelineStep), nil
}

// RequiredBySteps gets the RequiredBySteps property
func (s *PipelineStep) RequiredBySteps() *AspireList[string] {
	if s.requiredBySteps == nil {
		s.requiredBySteps = NewAspireListWithGetter[string](s.Handle(), s.Client(), "Aspire.Hosting.Pipelines/PipelineStep.requiredBySteps")
	}
	return s.requiredBySteps
}

// SetRequiredBySteps sets the RequiredBySteps property
func (s *PipelineStep) SetRequiredBySteps(value *AspireList[string]) (*PipelineStep, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStep.setRequiredBySteps", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*PipelineStep), nil
}

// Tags gets the Tags property
func (s *PipelineStep) Tags() *AspireList[string] {
	if s.tags == nil {
		s.tags = NewAspireListWithGetter[string](s.Handle(), s.Client(), "Aspire.Hosting.Pipelines/PipelineStep.tags")
	}
	return s.tags
}

// SetTags sets the Tags property
func (s *PipelineStep) SetTags(value *AspireList[string]) (*PipelineStep, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStep.setTags", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*PipelineStep), nil
}

// Resource gets the Resource property
func (s *PipelineStep) Resource() (*IResource, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStep.resource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// SetResource sets the Resource property
func (s *PipelineStep) SetResource(value *IResource) (*PipelineStep, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStep.setResource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*PipelineStep), nil
}

// DependsOn adds a dependency on another step by name
func (s *PipelineStep) DependsOn(stepName string) error {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["stepName"] = SerializeValue(stepName)
	_, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/dependsOn", reqArgs)
	return err
}

// RequiredBy specifies that another step requires this step by name
func (s *PipelineStep) RequiredBy(stepName string) error {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["stepName"] = SerializeValue(stepName)
	_, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/requiredBy", reqArgs)
	return err
}

// PipelineStepContext wraps a handle for Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineStepContext.
type PipelineStepContext struct {
	HandleWrapperBase
}

// NewPipelineStepContext creates a new PipelineStepContext.
func NewPipelineStepContext(handle *Handle, client *AspireClient) *PipelineStepContext {
	return &PipelineStepContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// PipelineContext gets the PipelineContext property
func (s *PipelineStepContext) PipelineContext() (*PipelineContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStepContext.pipelineContext", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*PipelineContext), nil
}

// SetPipelineContext sets the PipelineContext property
func (s *PipelineStepContext) SetPipelineContext(value *PipelineContext) (*PipelineStepContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStepContext.setPipelineContext", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*PipelineStepContext), nil
}

// ReportingStep gets the ReportingStep property
func (s *PipelineStepContext) ReportingStep() (*IReportingStep, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStepContext.reportingStep", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IReportingStep), nil
}

// SetReportingStep sets the ReportingStep property
func (s *PipelineStepContext) SetReportingStep(value *IReportingStep) (*PipelineStepContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStepContext.setReportingStep", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*PipelineStepContext), nil
}

// Model gets the Model property
func (s *PipelineStepContext) Model() (*DistributedApplicationModel, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStepContext.model", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationModel), nil
}

// ExecutionContext gets the ExecutionContext property
func (s *PipelineStepContext) ExecutionContext() (*DistributedApplicationExecutionContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStepContext.executionContext", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationExecutionContext), nil
}

// Services gets the Services property
func (s *PipelineStepContext) Services() (*IServiceProvider, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStepContext.services", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IServiceProvider), nil
}

// Logger gets the Logger property
func (s *PipelineStepContext) Logger() (*ILogger, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStepContext.logger", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ILogger), nil
}

// CancellationToken gets the CancellationToken property
func (s *PipelineStepContext) CancellationToken() (*CancellationToken, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStepContext.cancellationToken", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*CancellationToken), nil
}

// Summary gets the Summary property
func (s *PipelineStepContext) Summary() (*PipelineSummary, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStepContext.summary", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*PipelineSummary), nil
}

// PipelineStepFactoryContext wraps a handle for Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineStepFactoryContext.
type PipelineStepFactoryContext struct {
	HandleWrapperBase
}

// NewPipelineStepFactoryContext creates a new PipelineStepFactoryContext.
func NewPipelineStepFactoryContext(handle *Handle, client *AspireClient) *PipelineStepFactoryContext {
	return &PipelineStepFactoryContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// PipelineContext gets the PipelineContext property
func (s *PipelineStepFactoryContext) PipelineContext() (*PipelineContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStepFactoryContext.pipelineContext", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*PipelineContext), nil
}

// SetPipelineContext sets the PipelineContext property
func (s *PipelineStepFactoryContext) SetPipelineContext(value *PipelineContext) (*PipelineStepFactoryContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStepFactoryContext.setPipelineContext", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*PipelineStepFactoryContext), nil
}

// Resource gets the Resource property
func (s *PipelineStepFactoryContext) Resource() (*IResource, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStepFactoryContext.resource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// SetResource sets the Resource property
func (s *PipelineStepFactoryContext) SetResource(value *IResource) (*PipelineStepFactoryContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineStepFactoryContext.setResource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*PipelineStepFactoryContext), nil
}

// PipelineSummary wraps a handle for Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineSummary.
type PipelineSummary struct {
	HandleWrapperBase
}

// NewPipelineSummary creates a new PipelineSummary.
func NewPipelineSummary(handle *Handle, client *AspireClient) *PipelineSummary {
	return &PipelineSummary{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Add invokes the Add method
func (s *PipelineSummary) Add(key string, value string) error {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["key"] = SerializeValue(key)
	reqArgs["value"] = SerializeValue(value)
	_, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineSummary.add", reqArgs)
	return err
}

// AddMarkdown adds a Markdown-formatted value to the pipeline summary
func (s *PipelineSummary) AddMarkdown(key string, markdownString string) error {
	reqArgs := map[string]any{
		"summary": SerializeValue(s.Handle()),
	}
	reqArgs["key"] = SerializeValue(key)
	reqArgs["markdownString"] = SerializeValue(markdownString)
	_, err := s.Client().InvokeCapability("Aspire.Hosting/addMarkdown", reqArgs)
	return err
}

// ProjectResource wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ProjectResource.
type ProjectResource struct {
	ResourceBuilderBase
}

// NewProjectResource creates a new ProjectResource.
func NewProjectResource(handle *Handle, client *AspireClient) *ProjectResource {
	return &ProjectResource{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// WithContainerRegistry configures a resource to use a container registry
func (s *ProjectResource) WithContainerRegistry(registry *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["registry"] = SerializeValue(registry)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerRegistry", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *ProjectResource) WithDockerfileBaseImage(buildImage *string, runtimeImage *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if buildImage != nil {
		reqArgs["buildImage"] = SerializeValue(buildImage)
	}
	if runtimeImage != nil {
		reqArgs["runtimeImage"] = SerializeValue(runtimeImage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfileBaseImage", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMcpServer configures an MCP server endpoint on the resource
func (s *ProjectResource) WithMcpServer(path *string, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withMcpServer", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithOtlpExporter configures OTLP telemetry export
func (s *ProjectResource) WithOtlpExporter() (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporter", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithOtlpExporterProtocol configures OTLP telemetry export with specific protocol
func (s *ProjectResource) WithOtlpExporterProtocol(protocol OtlpProtocol) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["protocol"] = SerializeValue(protocol)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporterProtocol", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReplicas sets the number of replicas
func (s *ProjectResource) WithReplicas(replicas float64) (*ProjectResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["replicas"] = SerializeValue(replicas)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReplicas", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ProjectResource), nil
}

// DisableForwardedHeaders disables forwarded headers for the project
func (s *ProjectResource) DisableForwardedHeaders() (*ProjectResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/disableForwardedHeaders", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ProjectResource), nil
}

// PublishAsDockerFile publishes a project as a Docker file with optional container configuration
func (s *ProjectResource) PublishAsDockerFile(configure func(...any) any) (*ProjectResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if configure != nil {
		reqArgs["configure"] = RegisterCallback(configure)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishProjectAsDockerFileWithConfigure", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ProjectResource), nil
}

// WithRequiredCommand adds a required command dependency
func (s *ProjectResource) WithRequiredCommand(command string, helpLink *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["command"] = SerializeValue(command)
	if helpLink != nil {
		reqArgs["helpLink"] = SerializeValue(helpLink)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRequiredCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEnvironment sets an environment variable
func (s *ProjectResource) WithEnvironment(name string, value string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentExpression adds an environment variable with a reference expression
func (s *ProjectResource) WithEnvironmentExpression(name string, value *ReferenceExpression) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentCallback sets environment variables via callback
func (s *ProjectResource) WithEnvironmentCallback(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentCallbackAsync sets environment variables via async callback
func (s *ProjectResource) WithEnvironmentCallbackAsync(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentEndpoint sets an environment variable from an endpoint reference
func (s *ProjectResource) WithEnvironmentEndpoint(name string, endpointReference *EndpointReference) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["endpointReference"] = SerializeValue(endpointReference)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentParameter sets an environment variable from a parameter resource
func (s *ProjectResource) WithEnvironmentParameter(name string, parameter *ParameterResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["parameter"] = SerializeValue(parameter)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentParameter", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentConnectionString sets an environment variable from a connection string resource
func (s *ProjectResource) WithEnvironmentConnectionString(envVarName string, resource *IResourceWithConnectionString) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["envVarName"] = SerializeValue(envVarName)
	reqArgs["resource"] = SerializeValue(resource)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentConnectionString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithArgs adds arguments
func (s *ProjectResource) WithArgs(args []string) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["args"] = SerializeValue(args)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithArgsCallback sets command-line arguments via callback
func (s *ProjectResource) WithArgsCallback(callback func(...any) any) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithArgsCallbackAsync sets command-line arguments via async callback
func (s *ProjectResource) WithArgsCallbackAsync(callback func(...any) any) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithReference adds a reference to another resource
func (s *ProjectResource) WithReference(source *IResource, connectionName *string, optional *bool, name *string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["source"] = SerializeValue(source)
	if connectionName != nil {
		reqArgs["connectionName"] = SerializeValue(connectionName)
	}
	if optional != nil {
		reqArgs["optional"] = SerializeValue(optional)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReference", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceUri adds a reference to a URI
func (s *ProjectResource) WithReferenceUri(name string, uri string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["uri"] = SerializeValue(uri)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceUri", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceExternalService adds a reference to an external service
func (s *ProjectResource) WithReferenceExternalService(externalService *ExternalServiceResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["externalService"] = SerializeValue(externalService)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceExternalService", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceEndpoint adds a reference to an endpoint
func (s *ProjectResource) WithReferenceEndpoint(endpointReference *EndpointReference) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointReference"] = SerializeValue(endpointReference)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEndpoint adds a network endpoint
func (s *ProjectResource) WithEndpoint(port *float64, targetPort *float64, scheme *string, name *string, env *string, isProxied *bool, isExternal *bool, protocol *ProtocolType) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if scheme != nil {
		reqArgs["scheme"] = SerializeValue(scheme)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	if isExternal != nil {
		reqArgs["isExternal"] = SerializeValue(isExternal)
	}
	if protocol != nil {
		reqArgs["protocol"] = SerializeValue(protocol)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpEndpoint adds an HTTP endpoint
func (s *ProjectResource) WithHttpEndpoint(port *float64, targetPort *float64, name *string, env *string, isProxied *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpsEndpoint adds an HTTPS endpoint
func (s *ProjectResource) WithHttpsEndpoint(port *float64, targetPort *float64, name *string, env *string, isProxied *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithExternalHttpEndpoints makes HTTP endpoints externally accessible
func (s *ProjectResource) WithExternalHttpEndpoints() (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExternalHttpEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// GetEndpoint gets an endpoint reference
func (s *ProjectResource) GetEndpoint(name string) (*EndpointReference, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointReference), nil
}

// AsHttp2Service configures resource for HTTP/2
func (s *ProjectResource) AsHttp2Service() (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/asHttp2Service", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithUrlsCallback customizes displayed URLs via callback
func (s *ProjectResource) WithUrlsCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlsCallbackAsync customizes displayed URLs via async callback
func (s *ProjectResource) WithUrlsCallbackAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *ProjectResource) WithUrl(url string, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrl", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlExpression adds a URL using a reference expression
func (s *ProjectResource) WithUrlExpression(url *ReferenceExpression, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *ProjectResource) WithUrlForEndpoint(endpointName string, callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpointFactory adds a URL for a specific endpoint via factory callback
func (s *ProjectResource) WithUrlForEndpointFactory(endpointName string, callback func(...any) any) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpointFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// PublishWithContainerFiles configures the resource to copy container files from the specified source during publishing
func (s *ProjectResource) PublishWithContainerFiles(source *IResourceWithContainerFiles, destinationPath string) (*IContainerFilesDestinationResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["source"] = SerializeValue(source)
	reqArgs["destinationPath"] = SerializeValue(destinationPath)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishWithContainerFiles", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IContainerFilesDestinationResource), nil
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *ProjectResource) ExcludeFromManifest() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromManifest", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WaitFor waits for another resource to be ready
func (s *ProjectResource) WaitFor(dependency *IResource) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForWithBehavior waits for another resource with specific behavior
func (s *ProjectResource) WaitForWithBehavior(dependency *IResource, waitBehavior WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForWithBehavior", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStart waits for another resource to start
func (s *ProjectResource) WaitForStart(dependency *IResource) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStartWithBehavior waits for another resource to start with specific behavior
func (s *ProjectResource) WaitForStartWithBehavior(dependency *IResource, waitBehavior WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStartWithBehavior", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WithExplicitStart prevents resource from starting automatically
func (s *ProjectResource) WithExplicitStart() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExplicitStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WaitForCompletion waits for resource completion
func (s *ProjectResource) WaitForCompletion(dependency *IResource, exitCode *float64) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if exitCode != nil {
		reqArgs["exitCode"] = SerializeValue(exitCode)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForCompletion", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WithHealthCheck adds a health check by key
func (s *ProjectResource) WithHealthCheck(key string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["key"] = SerializeValue(key)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithHttpHealthCheck adds an HTTP health check
func (s *ProjectResource) WithHttpHealthCheck(path *string, statusCode *float64, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if statusCode != nil {
		reqArgs["statusCode"] = SerializeValue(statusCode)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithCommand adds a resource command
func (s *ProjectResource) WithCommand(name string, displayName string, executeCommand func(...any) any, commandOptions *CommandOptions) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["displayName"] = SerializeValue(displayName)
	if executeCommand != nil {
		reqArgs["executeCommand"] = RegisterCallback(executeCommand)
	}
	if commandOptions != nil {
		reqArgs["commandOptions"] = SerializeValue(commandOptions)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDeveloperCertificateTrust configures developer certificate trust
func (s *ProjectResource) WithDeveloperCertificateTrust(trust bool) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["trust"] = SerializeValue(trust)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDeveloperCertificateTrust", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCertificateTrustScope sets the certificate trust scope
func (s *ProjectResource) WithCertificateTrustScope(scope CertificateTrustScope) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["scope"] = SerializeValue(scope)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCertificateTrustScope", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithHttpsDeveloperCertificate configures HTTPS with a developer certificate
func (s *ProjectResource) WithHttpsDeveloperCertificate(password *ParameterResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if password != nil {
		reqArgs["password"] = SerializeValue(password)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsDeveloperCertificate", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithoutHttpsCertificate removes HTTPS certificate configuration
func (s *ProjectResource) WithoutHttpsCertificate() (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withoutHttpsCertificate", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithParentRelationship sets the parent relationship
func (s *ProjectResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParentRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithChildRelationship sets a child relationship
func (s *ProjectResource) WithChildRelationship(child *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["child"] = SerializeValue(child)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withChildRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithIconName sets the icon for the resource
func (s *ProjectResource) WithIconName(iconName string, iconVariant *IconVariant) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["iconName"] = SerializeValue(iconName)
	if iconVariant != nil {
		reqArgs["iconVariant"] = SerializeValue(iconVariant)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withIconName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithHttpProbe adds an HTTP health probe to the resource
func (s *ProjectResource) WithHttpProbe(probeType ProbeType, path *string, initialDelaySeconds *float64, periodSeconds *float64, timeoutSeconds *float64, failureThreshold *float64, successThreshold *float64, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["probeType"] = SerializeValue(probeType)
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if initialDelaySeconds != nil {
		reqArgs["initialDelaySeconds"] = SerializeValue(initialDelaySeconds)
	}
	if periodSeconds != nil {
		reqArgs["periodSeconds"] = SerializeValue(periodSeconds)
	}
	if timeoutSeconds != nil {
		reqArgs["timeoutSeconds"] = SerializeValue(timeoutSeconds)
	}
	if failureThreshold != nil {
		reqArgs["failureThreshold"] = SerializeValue(failureThreshold)
	}
	if successThreshold != nil {
		reqArgs["successThreshold"] = SerializeValue(successThreshold)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpProbe", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *ProjectResource) ExcludeFromMcp() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromMcp", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithRemoteImageName sets the remote image name for publishing
func (s *ProjectResource) WithRemoteImageName(remoteImageName string) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["remoteImageName"] = SerializeValue(remoteImageName)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRemoteImageName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
}

// WithRemoteImageTag sets the remote image tag for publishing
func (s *ProjectResource) WithRemoteImageTag(remoteImageTag string) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["remoteImageTag"] = SerializeValue(remoteImageTag)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRemoteImageTag", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *ProjectResource) WithPipelineStepFactory(stepName string, callback func(...any) any, dependsOn []string, requiredBy []string, tags []string, description *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["stepName"] = SerializeValue(stepName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if dependsOn != nil {
		reqArgs["dependsOn"] = SerializeValue(dependsOn)
	}
	if requiredBy != nil {
		reqArgs["requiredBy"] = SerializeValue(requiredBy)
	}
	if tags != nil {
		reqArgs["tags"] = SerializeValue(tags)
	}
	if description != nil {
		reqArgs["description"] = SerializeValue(description)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineStepFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfigurationAsync configures pipeline step dependencies via an async callback
func (s *ProjectResource) WithPipelineConfigurationAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfigurationAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *ProjectResource) WithPipelineConfiguration(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// GetResourceName gets the resource name
func (s *ProjectResource) GetResourceName() (*string, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *ProjectResource) OnBeforeResourceStarted(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onBeforeResourceStarted", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *ProjectResource) OnResourceStopped(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceStopped", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *ProjectResource) OnInitializeResource(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onInitializeResource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceEndpointsAllocated subscribes to the ResourceEndpointsAllocated event
func (s *ProjectResource) OnResourceEndpointsAllocated(callback func(...any) any) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceEndpointsAllocated", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// OnResourceReady subscribes to the ResourceReady event
func (s *ProjectResource) OnResourceReady(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceReady", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalString adds an optional string parameter
func (s *ProjectResource) WithOptionalString(value *string, enabled *bool) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if value != nil {
		reqArgs["value"] = SerializeValue(value)
	}
	if enabled != nil {
		reqArgs["enabled"] = SerializeValue(enabled)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithConfig configures the resource with a DTO
func (s *ProjectResource) WithConfig(config *TestConfigDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestWithEnvironmentCallback configures environment with callback (test version)
func (s *ProjectResource) TestWithEnvironmentCallback(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWithEnvironmentCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCreatedAt sets the created timestamp
func (s *ProjectResource) WithCreatedAt(createdAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["createdAt"] = SerializeValue(createdAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCreatedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithModifiedAt sets the modified timestamp
func (s *ProjectResource) WithModifiedAt(modifiedAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["modifiedAt"] = SerializeValue(modifiedAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withModifiedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCorrelationId sets the correlation ID
func (s *ProjectResource) WithCorrelationId(correlationId string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["correlationId"] = SerializeValue(correlationId)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCorrelationId", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalCallback configures with optional callback
func (s *ProjectResource) WithOptionalCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithStatus sets the resource status
func (s *ProjectResource) WithStatus(status TestResourceStatus) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["status"] = SerializeValue(status)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withStatus", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithNestedConfig configures with nested DTO
func (s *ProjectResource) WithNestedConfig(config *TestNestedDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withNestedConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithValidator adds validation callback
func (s *ProjectResource) WithValidator(validator func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if validator != nil {
		reqArgs["validator"] = RegisterCallback(validator)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withValidator", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestWaitFor waits for another resource (test version)
func (s *ProjectResource) TestWaitFor(dependency *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWaitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDependency adds a dependency on another resource
func (s *ProjectResource) WithDependency(dependency *IResourceWithConnectionString) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withDependency", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEndpoints sets the endpoints
func (s *ProjectResource) WithEndpoints(endpoints []string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpoints"] = SerializeValue(endpoints)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEnvironmentVariables sets environment variables
func (s *ProjectResource) WithEnvironmentVariables(variables map[string]string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["variables"] = SerializeValue(variables)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEnvironmentVariables", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCancellableOperation performs a cancellable operation
func (s *ProjectResource) WithCancellableOperation(operation func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if operation != nil {
		reqArgs["operation"] = RegisterCallback(operation)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCancellableOperation", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// ProjectResourceOptions wraps a handle for Aspire.Hosting/Aspire.Hosting.ProjectResourceOptions.
type ProjectResourceOptions struct {
	HandleWrapperBase
}

// NewProjectResourceOptions creates a new ProjectResourceOptions.
func NewProjectResourceOptions(handle *Handle, client *AspireClient) *ProjectResourceOptions {
	return &ProjectResourceOptions{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// LaunchProfileName gets the LaunchProfileName property
func (s *ProjectResourceOptions) LaunchProfileName() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/ProjectResourceOptions.launchProfileName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// SetLaunchProfileName sets the LaunchProfileName property
func (s *ProjectResourceOptions) SetLaunchProfileName(value string) (*ProjectResourceOptions, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/ProjectResourceOptions.setLaunchProfileName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ProjectResourceOptions), nil
}

// ExcludeLaunchProfile gets the ExcludeLaunchProfile property
func (s *ProjectResourceOptions) ExcludeLaunchProfile() (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/ProjectResourceOptions.excludeLaunchProfile", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// SetExcludeLaunchProfile sets the ExcludeLaunchProfile property
func (s *ProjectResourceOptions) SetExcludeLaunchProfile(value bool) (*ProjectResourceOptions, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/ProjectResourceOptions.setExcludeLaunchProfile", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ProjectResourceOptions), nil
}

// ExcludeKestrelEndpoints gets the ExcludeKestrelEndpoints property
func (s *ProjectResourceOptions) ExcludeKestrelEndpoints() (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/ProjectResourceOptions.excludeKestrelEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// SetExcludeKestrelEndpoints sets the ExcludeKestrelEndpoints property
func (s *ProjectResourceOptions) SetExcludeKestrelEndpoints(value bool) (*ProjectResourceOptions, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/ProjectResourceOptions.setExcludeKestrelEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ProjectResourceOptions), nil
}

// ReferenceExpressionBuilder wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpressionBuilder.
type ReferenceExpressionBuilder struct {
	HandleWrapperBase
}

// NewReferenceExpressionBuilder creates a new ReferenceExpressionBuilder.
func NewReferenceExpressionBuilder(handle *Handle, client *AspireClient) *ReferenceExpressionBuilder {
	return &ReferenceExpressionBuilder{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// IsEmpty gets the IsEmpty property
func (s *ReferenceExpressionBuilder) IsEmpty() (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ReferenceExpressionBuilder.isEmpty", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// AppendLiteral appends a literal string to the reference expression
func (s *ReferenceExpressionBuilder) AppendLiteral(value string) error {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	_, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/appendLiteral", reqArgs)
	return err
}

// AppendFormatted appends a formatted string value to the reference expression
func (s *ReferenceExpressionBuilder) AppendFormatted(value string, format *string) error {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	if format != nil {
		reqArgs["format"] = SerializeValue(format)
	}
	_, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/appendFormatted", reqArgs)
	return err
}

// AppendValueProvider appends a value provider to the reference expression
func (s *ReferenceExpressionBuilder) AppendValueProvider(valueProvider any, format *string) error {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["valueProvider"] = SerializeValue(valueProvider)
	if format != nil {
		reqArgs["format"] = SerializeValue(format)
	}
	_, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/appendValueProvider", reqArgs)
	return err
}

// Build builds the reference expression
func (s *ReferenceExpressionBuilder) Build() (*ReferenceExpression, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/build", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ReferenceExpression), nil
}

// ResourceEndpointsAllocatedEvent wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceEndpointsAllocatedEvent.
type ResourceEndpointsAllocatedEvent struct {
	HandleWrapperBase
}

// NewResourceEndpointsAllocatedEvent creates a new ResourceEndpointsAllocatedEvent.
func NewResourceEndpointsAllocatedEvent(handle *Handle, client *AspireClient) *ResourceEndpointsAllocatedEvent {
	return &ResourceEndpointsAllocatedEvent{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Resource gets the Resource property
func (s *ResourceEndpointsAllocatedEvent) Resource() (*IResource, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ResourceEndpointsAllocatedEvent.resource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// Services gets the Services property
func (s *ResourceEndpointsAllocatedEvent) Services() (*IServiceProvider, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ResourceEndpointsAllocatedEvent.services", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IServiceProvider), nil
}

// ResourceLoggerService wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceLoggerService.
type ResourceLoggerService struct {
	HandleWrapperBase
}

// NewResourceLoggerService creates a new ResourceLoggerService.
func NewResourceLoggerService(handle *Handle, client *AspireClient) *ResourceLoggerService {
	return &ResourceLoggerService{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// CompleteLog completes the log stream for a resource
func (s *ResourceLoggerService) CompleteLog(resource *IResource) error {
	reqArgs := map[string]any{
		"loggerService": SerializeValue(s.Handle()),
	}
	reqArgs["resource"] = SerializeValue(resource)
	_, err := s.Client().InvokeCapability("Aspire.Hosting/completeLog", reqArgs)
	return err
}

// CompleteLogByName completes the log stream by resource name
func (s *ResourceLoggerService) CompleteLogByName(resourceName string) error {
	reqArgs := map[string]any{
		"loggerService": SerializeValue(s.Handle()),
	}
	reqArgs["resourceName"] = SerializeValue(resourceName)
	_, err := s.Client().InvokeCapability("Aspire.Hosting/completeLogByName", reqArgs)
	return err
}

// ResourceNotificationService wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceNotificationService.
type ResourceNotificationService struct {
	HandleWrapperBase
}

// NewResourceNotificationService creates a new ResourceNotificationService.
func NewResourceNotificationService(handle *Handle, client *AspireClient) *ResourceNotificationService {
	return &ResourceNotificationService{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// WaitForResourceState waits for a resource to reach a specified state
func (s *ResourceNotificationService) WaitForResourceState(resourceName string, targetState *string) error {
	reqArgs := map[string]any{
		"notificationService": SerializeValue(s.Handle()),
	}
	reqArgs["resourceName"] = SerializeValue(resourceName)
	if targetState != nil {
		reqArgs["targetState"] = SerializeValue(targetState)
	}
	_, err := s.Client().InvokeCapability("Aspire.Hosting/waitForResourceState", reqArgs)
	return err
}

// WaitForResourceStates waits for a resource to reach one of the specified states
func (s *ResourceNotificationService) WaitForResourceStates(resourceName string, targetStates []string) (*string, error) {
	reqArgs := map[string]any{
		"notificationService": SerializeValue(s.Handle()),
	}
	reqArgs["resourceName"] = SerializeValue(resourceName)
	reqArgs["targetStates"] = SerializeValue(targetStates)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForResourceStates", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// WaitForResourceHealthy waits for a resource to become healthy
func (s *ResourceNotificationService) WaitForResourceHealthy(resourceName string) (*ResourceEventDto, error) {
	reqArgs := map[string]any{
		"notificationService": SerializeValue(s.Handle()),
	}
	reqArgs["resourceName"] = SerializeValue(resourceName)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForResourceHealthy", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ResourceEventDto), nil
}

// WaitForDependencies waits for all dependencies of a resource to be ready
func (s *ResourceNotificationService) WaitForDependencies(resource *IResource) error {
	reqArgs := map[string]any{
		"notificationService": SerializeValue(s.Handle()),
	}
	reqArgs["resource"] = SerializeValue(resource)
	_, err := s.Client().InvokeCapability("Aspire.Hosting/waitForDependencies", reqArgs)
	return err
}

// TryGetResourceState tries to get the current state of a resource
func (s *ResourceNotificationService) TryGetResourceState(resourceName string) (*ResourceEventDto, error) {
	reqArgs := map[string]any{
		"notificationService": SerializeValue(s.Handle()),
	}
	reqArgs["resourceName"] = SerializeValue(resourceName)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/tryGetResourceState", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ResourceEventDto), nil
}

// PublishResourceUpdate publishes an update for a resource's state
func (s *ResourceNotificationService) PublishResourceUpdate(resource *IResource, state *string, stateStyle *string) error {
	reqArgs := map[string]any{
		"notificationService": SerializeValue(s.Handle()),
	}
	reqArgs["resource"] = SerializeValue(resource)
	if state != nil {
		reqArgs["state"] = SerializeValue(state)
	}
	if stateStyle != nil {
		reqArgs["stateStyle"] = SerializeValue(stateStyle)
	}
	_, err := s.Client().InvokeCapability("Aspire.Hosting/publishResourceUpdate", reqArgs)
	return err
}

// ResourceReadyEvent wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceReadyEvent.
type ResourceReadyEvent struct {
	HandleWrapperBase
}

// NewResourceReadyEvent creates a new ResourceReadyEvent.
func NewResourceReadyEvent(handle *Handle, client *AspireClient) *ResourceReadyEvent {
	return &ResourceReadyEvent{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Resource gets the Resource property
func (s *ResourceReadyEvent) Resource() (*IResource, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ResourceReadyEvent.resource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// Services gets the Services property
func (s *ResourceReadyEvent) Services() (*IServiceProvider, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ResourceReadyEvent.services", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IServiceProvider), nil
}

// ResourceStoppedEvent wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceStoppedEvent.
type ResourceStoppedEvent struct {
	HandleWrapperBase
}

// NewResourceStoppedEvent creates a new ResourceStoppedEvent.
func NewResourceStoppedEvent(handle *Handle, client *AspireClient) *ResourceStoppedEvent {
	return &ResourceStoppedEvent{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Resource gets the Resource property
func (s *ResourceStoppedEvent) Resource() (*IResource, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ResourceStoppedEvent.resource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// Services gets the Services property
func (s *ResourceStoppedEvent) Services() (*IServiceProvider, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ResourceStoppedEvent.services", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IServiceProvider), nil
}

// ResourceUrlsCallbackContext wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceUrlsCallbackContext.
type ResourceUrlsCallbackContext struct {
	HandleWrapperBase
	urls *AspireList[*ResourceUrlAnnotation]
}

// NewResourceUrlsCallbackContext creates a new ResourceUrlsCallbackContext.
func NewResourceUrlsCallbackContext(handle *Handle, client *AspireClient) *ResourceUrlsCallbackContext {
	return &ResourceUrlsCallbackContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Resource gets the Resource property
func (s *ResourceUrlsCallbackContext) Resource() (*IResource, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.resource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// Urls gets the Urls property
func (s *ResourceUrlsCallbackContext) Urls() *AspireList[*ResourceUrlAnnotation] {
	if s.urls == nil {
		s.urls = NewAspireListWithGetter[*ResourceUrlAnnotation](s.Handle(), s.Client(), "Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.urls")
	}
	return s.urls
}

// CancellationToken gets the CancellationToken property
func (s *ResourceUrlsCallbackContext) CancellationToken() (*CancellationToken, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.cancellationToken", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*CancellationToken), nil
}

// Logger gets the Logger property
func (s *ResourceUrlsCallbackContext) Logger() (*ILogger, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.logger", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ILogger), nil
}

// SetLogger sets the Logger property
func (s *ResourceUrlsCallbackContext) SetLogger(value *ILogger) (*ResourceUrlsCallbackContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.setLogger", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ResourceUrlsCallbackContext), nil
}

// ExecutionContext gets the ExecutionContext property
func (s *ResourceUrlsCallbackContext) ExecutionContext() (*DistributedApplicationExecutionContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.executionContext", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationExecutionContext), nil
}

// TestCallbackContext wraps a handle for Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCallbackContext.
type TestCallbackContext struct {
	HandleWrapperBase
}

// NewTestCallbackContext creates a new TestCallbackContext.
func NewTestCallbackContext(handle *Handle, client *AspireClient) *TestCallbackContext {
	return &TestCallbackContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Name gets the Name property
func (s *TestCallbackContext) Name() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.name", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// SetName sets the Name property
func (s *TestCallbackContext) SetName(value string) (*TestCallbackContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*TestCallbackContext), nil
}

// Value gets the Value property
func (s *TestCallbackContext) Value() (*float64, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.value", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*float64), nil
}

// SetValue sets the Value property
func (s *TestCallbackContext) SetValue(value float64) (*TestCallbackContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setValue", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*TestCallbackContext), nil
}

// CancellationToken gets the CancellationToken property
func (s *TestCallbackContext) CancellationToken() (*CancellationToken, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.cancellationToken", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*CancellationToken), nil
}

// SetCancellationToken sets the CancellationToken property
func (s *TestCallbackContext) SetCancellationToken(value *CancellationToken) (*TestCallbackContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	if value != nil {
		reqArgs["value"] = RegisterCancellation(value, s.Client())
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setCancellationToken", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*TestCallbackContext), nil
}

// TestCollectionContext wraps a handle for Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCollectionContext.
type TestCollectionContext struct {
	HandleWrapperBase
	items *AspireList[string]
	metadata *AspireDict[string, string]
}

// NewTestCollectionContext creates a new TestCollectionContext.
func NewTestCollectionContext(handle *Handle, client *AspireClient) *TestCollectionContext {
	return &TestCollectionContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Items gets the Items property
func (s *TestCollectionContext) Items() *AspireList[string] {
	if s.items == nil {
		s.items = NewAspireListWithGetter[string](s.Handle(), s.Client(), "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.items")
	}
	return s.items
}

// Metadata gets the Metadata property
func (s *TestCollectionContext) Metadata() *AspireDict[string, string] {
	if s.metadata == nil {
		s.metadata = NewAspireDictWithGetter[string, string](s.Handle(), s.Client(), "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.metadata")
	}
	return s.metadata
}

// TestDatabaseResource wraps a handle for Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestDatabaseResource.
type TestDatabaseResource struct {
	ResourceBuilderBase
}

// NewTestDatabaseResource creates a new TestDatabaseResource.
func NewTestDatabaseResource(handle *Handle, client *AspireClient) *TestDatabaseResource {
	return &TestDatabaseResource{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// WithContainerRegistry configures a resource to use a container registry
func (s *TestDatabaseResource) WithContainerRegistry(registry *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["registry"] = SerializeValue(registry)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerRegistry", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithBindMount adds a bind mount
func (s *TestDatabaseResource) WithBindMount(source string, target string, isReadOnly *bool) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["source"] = SerializeValue(source)
	reqArgs["target"] = SerializeValue(target)
	if isReadOnly != nil {
		reqArgs["isReadOnly"] = SerializeValue(isReadOnly)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBindMount", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithEntrypoint sets the container entrypoint
func (s *TestDatabaseResource) WithEntrypoint(entrypoint string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["entrypoint"] = SerializeValue(entrypoint)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEntrypoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImageTag sets the container image tag
func (s *TestDatabaseResource) WithImageTag(tag string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["tag"] = SerializeValue(tag)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImageTag", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImageRegistry sets the container image registry
func (s *TestDatabaseResource) WithImageRegistry(registry string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["registry"] = SerializeValue(registry)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImageRegistry", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImage sets the container image
func (s *TestDatabaseResource) WithImage(image string, tag *string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["image"] = SerializeValue(image)
	if tag != nil {
		reqArgs["tag"] = SerializeValue(tag)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImage", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImageSHA256 sets the image SHA256 digest
func (s *TestDatabaseResource) WithImageSHA256(sha256 string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["sha256"] = SerializeValue(sha256)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImageSHA256", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithContainerRuntimeArgs adds runtime arguments for the container
func (s *TestDatabaseResource) WithContainerRuntimeArgs(args []string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["args"] = SerializeValue(args)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerRuntimeArgs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithLifetime sets the lifetime behavior of the container resource
func (s *TestDatabaseResource) WithLifetime(lifetime ContainerLifetime) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["lifetime"] = SerializeValue(lifetime)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withLifetime", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImagePullPolicy sets the container image pull policy
func (s *TestDatabaseResource) WithImagePullPolicy(pullPolicy ImagePullPolicy) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["pullPolicy"] = SerializeValue(pullPolicy)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImagePullPolicy", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// PublishAsContainer configures the resource to be published as a container
func (s *TestDatabaseResource) PublishAsContainer() (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishAsContainer", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithDockerfile configures the resource to use a Dockerfile
func (s *TestDatabaseResource) WithDockerfile(contextPath string, dockerfilePath *string, stage *string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["contextPath"] = SerializeValue(contextPath)
	if dockerfilePath != nil {
		reqArgs["dockerfilePath"] = SerializeValue(dockerfilePath)
	}
	if stage != nil {
		reqArgs["stage"] = SerializeValue(stage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfile", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithContainerName sets the container name
func (s *TestDatabaseResource) WithContainerName(name string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithBuildArg adds a build argument from a parameter resource
func (s *TestDatabaseResource) WithBuildArg(name string, value *ParameterResource) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuildArg", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithBuildSecret adds a build secret from a parameter resource
func (s *TestDatabaseResource) WithBuildSecret(name string, value *ParameterResource) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuildSecret", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithEndpointProxySupport configures endpoint proxy support
func (s *TestDatabaseResource) WithEndpointProxySupport(proxyEnabled bool) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["proxyEnabled"] = SerializeValue(proxyEnabled)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpointProxySupport", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *TestDatabaseResource) WithDockerfileBaseImage(buildImage *string, runtimeImage *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if buildImage != nil {
		reqArgs["buildImage"] = SerializeValue(buildImage)
	}
	if runtimeImage != nil {
		reqArgs["runtimeImage"] = SerializeValue(runtimeImage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfileBaseImage", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithContainerNetworkAlias adds a network alias for the container
func (s *TestDatabaseResource) WithContainerNetworkAlias(alias string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["alias"] = SerializeValue(alias)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerNetworkAlias", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithMcpServer configures an MCP server endpoint on the resource
func (s *TestDatabaseResource) WithMcpServer(path *string, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withMcpServer", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithOtlpExporter configures OTLP telemetry export
func (s *TestDatabaseResource) WithOtlpExporter() (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporter", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithOtlpExporterProtocol configures OTLP telemetry export with specific protocol
func (s *TestDatabaseResource) WithOtlpExporterProtocol(protocol OtlpProtocol) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["protocol"] = SerializeValue(protocol)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporterProtocol", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// PublishAsConnectionString publishes the resource as a connection string
func (s *TestDatabaseResource) PublishAsConnectionString() (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishAsConnectionString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithRequiredCommand adds a required command dependency
func (s *TestDatabaseResource) WithRequiredCommand(command string, helpLink *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["command"] = SerializeValue(command)
	if helpLink != nil {
		reqArgs["helpLink"] = SerializeValue(helpLink)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRequiredCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEnvironment sets an environment variable
func (s *TestDatabaseResource) WithEnvironment(name string, value string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentExpression adds an environment variable with a reference expression
func (s *TestDatabaseResource) WithEnvironmentExpression(name string, value *ReferenceExpression) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentCallback sets environment variables via callback
func (s *TestDatabaseResource) WithEnvironmentCallback(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentCallbackAsync sets environment variables via async callback
func (s *TestDatabaseResource) WithEnvironmentCallbackAsync(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentEndpoint sets an environment variable from an endpoint reference
func (s *TestDatabaseResource) WithEnvironmentEndpoint(name string, endpointReference *EndpointReference) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["endpointReference"] = SerializeValue(endpointReference)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentParameter sets an environment variable from a parameter resource
func (s *TestDatabaseResource) WithEnvironmentParameter(name string, parameter *ParameterResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["parameter"] = SerializeValue(parameter)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentParameter", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentConnectionString sets an environment variable from a connection string resource
func (s *TestDatabaseResource) WithEnvironmentConnectionString(envVarName string, resource *IResourceWithConnectionString) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["envVarName"] = SerializeValue(envVarName)
	reqArgs["resource"] = SerializeValue(resource)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentConnectionString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithArgs adds arguments
func (s *TestDatabaseResource) WithArgs(args []string) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["args"] = SerializeValue(args)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithArgsCallback sets command-line arguments via callback
func (s *TestDatabaseResource) WithArgsCallback(callback func(...any) any) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithArgsCallbackAsync sets command-line arguments via async callback
func (s *TestDatabaseResource) WithArgsCallbackAsync(callback func(...any) any) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithReference adds a reference to another resource
func (s *TestDatabaseResource) WithReference(source *IResource, connectionName *string, optional *bool, name *string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["source"] = SerializeValue(source)
	if connectionName != nil {
		reqArgs["connectionName"] = SerializeValue(connectionName)
	}
	if optional != nil {
		reqArgs["optional"] = SerializeValue(optional)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReference", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceUri adds a reference to a URI
func (s *TestDatabaseResource) WithReferenceUri(name string, uri string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["uri"] = SerializeValue(uri)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceUri", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceExternalService adds a reference to an external service
func (s *TestDatabaseResource) WithReferenceExternalService(externalService *ExternalServiceResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["externalService"] = SerializeValue(externalService)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceExternalService", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceEndpoint adds a reference to an endpoint
func (s *TestDatabaseResource) WithReferenceEndpoint(endpointReference *EndpointReference) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointReference"] = SerializeValue(endpointReference)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEndpoint adds a network endpoint
func (s *TestDatabaseResource) WithEndpoint(port *float64, targetPort *float64, scheme *string, name *string, env *string, isProxied *bool, isExternal *bool, protocol *ProtocolType) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if scheme != nil {
		reqArgs["scheme"] = SerializeValue(scheme)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	if isExternal != nil {
		reqArgs["isExternal"] = SerializeValue(isExternal)
	}
	if protocol != nil {
		reqArgs["protocol"] = SerializeValue(protocol)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpEndpoint adds an HTTP endpoint
func (s *TestDatabaseResource) WithHttpEndpoint(port *float64, targetPort *float64, name *string, env *string, isProxied *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpsEndpoint adds an HTTPS endpoint
func (s *TestDatabaseResource) WithHttpsEndpoint(port *float64, targetPort *float64, name *string, env *string, isProxied *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithExternalHttpEndpoints makes HTTP endpoints externally accessible
func (s *TestDatabaseResource) WithExternalHttpEndpoints() (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExternalHttpEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// GetEndpoint gets an endpoint reference
func (s *TestDatabaseResource) GetEndpoint(name string) (*EndpointReference, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointReference), nil
}

// AsHttp2Service configures resource for HTTP/2
func (s *TestDatabaseResource) AsHttp2Service() (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/asHttp2Service", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithUrlsCallback customizes displayed URLs via callback
func (s *TestDatabaseResource) WithUrlsCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlsCallbackAsync customizes displayed URLs via async callback
func (s *TestDatabaseResource) WithUrlsCallbackAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *TestDatabaseResource) WithUrl(url string, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrl", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlExpression adds a URL using a reference expression
func (s *TestDatabaseResource) WithUrlExpression(url *ReferenceExpression, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *TestDatabaseResource) WithUrlForEndpoint(endpointName string, callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpointFactory adds a URL for a specific endpoint via factory callback
func (s *TestDatabaseResource) WithUrlForEndpointFactory(endpointName string, callback func(...any) any) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpointFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *TestDatabaseResource) ExcludeFromManifest() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromManifest", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WaitFor waits for another resource to be ready
func (s *TestDatabaseResource) WaitFor(dependency *IResource) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForWithBehavior waits for another resource with specific behavior
func (s *TestDatabaseResource) WaitForWithBehavior(dependency *IResource, waitBehavior WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForWithBehavior", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStart waits for another resource to start
func (s *TestDatabaseResource) WaitForStart(dependency *IResource) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStartWithBehavior waits for another resource to start with specific behavior
func (s *TestDatabaseResource) WaitForStartWithBehavior(dependency *IResource, waitBehavior WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStartWithBehavior", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WithExplicitStart prevents resource from starting automatically
func (s *TestDatabaseResource) WithExplicitStart() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExplicitStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WaitForCompletion waits for resource completion
func (s *TestDatabaseResource) WaitForCompletion(dependency *IResource, exitCode *float64) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if exitCode != nil {
		reqArgs["exitCode"] = SerializeValue(exitCode)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForCompletion", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WithHealthCheck adds a health check by key
func (s *TestDatabaseResource) WithHealthCheck(key string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["key"] = SerializeValue(key)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithHttpHealthCheck adds an HTTP health check
func (s *TestDatabaseResource) WithHttpHealthCheck(path *string, statusCode *float64, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if statusCode != nil {
		reqArgs["statusCode"] = SerializeValue(statusCode)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithCommand adds a resource command
func (s *TestDatabaseResource) WithCommand(name string, displayName string, executeCommand func(...any) any, commandOptions *CommandOptions) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["displayName"] = SerializeValue(displayName)
	if executeCommand != nil {
		reqArgs["executeCommand"] = RegisterCallback(executeCommand)
	}
	if commandOptions != nil {
		reqArgs["commandOptions"] = SerializeValue(commandOptions)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDeveloperCertificateTrust configures developer certificate trust
func (s *TestDatabaseResource) WithDeveloperCertificateTrust(trust bool) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["trust"] = SerializeValue(trust)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDeveloperCertificateTrust", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCertificateTrustScope sets the certificate trust scope
func (s *TestDatabaseResource) WithCertificateTrustScope(scope CertificateTrustScope) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["scope"] = SerializeValue(scope)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCertificateTrustScope", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithHttpsDeveloperCertificate configures HTTPS with a developer certificate
func (s *TestDatabaseResource) WithHttpsDeveloperCertificate(password *ParameterResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if password != nil {
		reqArgs["password"] = SerializeValue(password)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsDeveloperCertificate", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithoutHttpsCertificate removes HTTPS certificate configuration
func (s *TestDatabaseResource) WithoutHttpsCertificate() (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withoutHttpsCertificate", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithParentRelationship sets the parent relationship
func (s *TestDatabaseResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParentRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithChildRelationship sets a child relationship
func (s *TestDatabaseResource) WithChildRelationship(child *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["child"] = SerializeValue(child)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withChildRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithIconName sets the icon for the resource
func (s *TestDatabaseResource) WithIconName(iconName string, iconVariant *IconVariant) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["iconName"] = SerializeValue(iconName)
	if iconVariant != nil {
		reqArgs["iconVariant"] = SerializeValue(iconVariant)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withIconName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithHttpProbe adds an HTTP health probe to the resource
func (s *TestDatabaseResource) WithHttpProbe(probeType ProbeType, path *string, initialDelaySeconds *float64, periodSeconds *float64, timeoutSeconds *float64, failureThreshold *float64, successThreshold *float64, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["probeType"] = SerializeValue(probeType)
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if initialDelaySeconds != nil {
		reqArgs["initialDelaySeconds"] = SerializeValue(initialDelaySeconds)
	}
	if periodSeconds != nil {
		reqArgs["periodSeconds"] = SerializeValue(periodSeconds)
	}
	if timeoutSeconds != nil {
		reqArgs["timeoutSeconds"] = SerializeValue(timeoutSeconds)
	}
	if failureThreshold != nil {
		reqArgs["failureThreshold"] = SerializeValue(failureThreshold)
	}
	if successThreshold != nil {
		reqArgs["successThreshold"] = SerializeValue(successThreshold)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpProbe", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *TestDatabaseResource) ExcludeFromMcp() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromMcp", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithRemoteImageName sets the remote image name for publishing
func (s *TestDatabaseResource) WithRemoteImageName(remoteImageName string) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["remoteImageName"] = SerializeValue(remoteImageName)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRemoteImageName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
}

// WithRemoteImageTag sets the remote image tag for publishing
func (s *TestDatabaseResource) WithRemoteImageTag(remoteImageTag string) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["remoteImageTag"] = SerializeValue(remoteImageTag)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRemoteImageTag", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *TestDatabaseResource) WithPipelineStepFactory(stepName string, callback func(...any) any, dependsOn []string, requiredBy []string, tags []string, description *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["stepName"] = SerializeValue(stepName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if dependsOn != nil {
		reqArgs["dependsOn"] = SerializeValue(dependsOn)
	}
	if requiredBy != nil {
		reqArgs["requiredBy"] = SerializeValue(requiredBy)
	}
	if tags != nil {
		reqArgs["tags"] = SerializeValue(tags)
	}
	if description != nil {
		reqArgs["description"] = SerializeValue(description)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineStepFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfigurationAsync configures pipeline step dependencies via an async callback
func (s *TestDatabaseResource) WithPipelineConfigurationAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfigurationAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *TestDatabaseResource) WithPipelineConfiguration(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithVolume adds a volume
func (s *TestDatabaseResource) WithVolume(target string, name *string, isReadOnly *bool) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	reqArgs["target"] = SerializeValue(target)
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if isReadOnly != nil {
		reqArgs["isReadOnly"] = SerializeValue(isReadOnly)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withVolume", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// GetResourceName gets the resource name
func (s *TestDatabaseResource) GetResourceName() (*string, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *TestDatabaseResource) OnBeforeResourceStarted(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onBeforeResourceStarted", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *TestDatabaseResource) OnResourceStopped(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceStopped", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *TestDatabaseResource) OnInitializeResource(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onInitializeResource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceEndpointsAllocated subscribes to the ResourceEndpointsAllocated event
func (s *TestDatabaseResource) OnResourceEndpointsAllocated(callback func(...any) any) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceEndpointsAllocated", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// OnResourceReady subscribes to the ResourceReady event
func (s *TestDatabaseResource) OnResourceReady(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceReady", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalString adds an optional string parameter
func (s *TestDatabaseResource) WithOptionalString(value *string, enabled *bool) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if value != nil {
		reqArgs["value"] = SerializeValue(value)
	}
	if enabled != nil {
		reqArgs["enabled"] = SerializeValue(enabled)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithConfig configures the resource with a DTO
func (s *TestDatabaseResource) WithConfig(config *TestConfigDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestWithEnvironmentCallback configures environment with callback (test version)
func (s *TestDatabaseResource) TestWithEnvironmentCallback(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWithEnvironmentCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCreatedAt sets the created timestamp
func (s *TestDatabaseResource) WithCreatedAt(createdAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["createdAt"] = SerializeValue(createdAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCreatedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithModifiedAt sets the modified timestamp
func (s *TestDatabaseResource) WithModifiedAt(modifiedAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["modifiedAt"] = SerializeValue(modifiedAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withModifiedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCorrelationId sets the correlation ID
func (s *TestDatabaseResource) WithCorrelationId(correlationId string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["correlationId"] = SerializeValue(correlationId)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCorrelationId", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalCallback configures with optional callback
func (s *TestDatabaseResource) WithOptionalCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithStatus sets the resource status
func (s *TestDatabaseResource) WithStatus(status TestResourceStatus) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["status"] = SerializeValue(status)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withStatus", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithNestedConfig configures with nested DTO
func (s *TestDatabaseResource) WithNestedConfig(config *TestNestedDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withNestedConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithValidator adds validation callback
func (s *TestDatabaseResource) WithValidator(validator func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if validator != nil {
		reqArgs["validator"] = RegisterCallback(validator)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withValidator", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestWaitFor waits for another resource (test version)
func (s *TestDatabaseResource) TestWaitFor(dependency *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWaitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDependency adds a dependency on another resource
func (s *TestDatabaseResource) WithDependency(dependency *IResourceWithConnectionString) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withDependency", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEndpoints sets the endpoints
func (s *TestDatabaseResource) WithEndpoints(endpoints []string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpoints"] = SerializeValue(endpoints)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEnvironmentVariables sets environment variables
func (s *TestDatabaseResource) WithEnvironmentVariables(variables map[string]string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["variables"] = SerializeValue(variables)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEnvironmentVariables", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCancellableOperation performs a cancellable operation
func (s *TestDatabaseResource) WithCancellableOperation(operation func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if operation != nil {
		reqArgs["operation"] = RegisterCallback(operation)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCancellableOperation", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestEnvironmentContext wraps a handle for Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestEnvironmentContext.
type TestEnvironmentContext struct {
	HandleWrapperBase
}

// NewTestEnvironmentContext creates a new TestEnvironmentContext.
func NewTestEnvironmentContext(handle *Handle, client *AspireClient) *TestEnvironmentContext {
	return &TestEnvironmentContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Name gets the Name property
func (s *TestEnvironmentContext) Name() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.name", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// SetName sets the Name property
func (s *TestEnvironmentContext) SetName(value string) (*TestEnvironmentContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*TestEnvironmentContext), nil
}

// Description gets the Description property
func (s *TestEnvironmentContext) Description() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.description", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// SetDescription sets the Description property
func (s *TestEnvironmentContext) SetDescription(value string) (*TestEnvironmentContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setDescription", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*TestEnvironmentContext), nil
}

// Priority gets the Priority property
func (s *TestEnvironmentContext) Priority() (*float64, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.priority", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*float64), nil
}

// SetPriority sets the Priority property
func (s *TestEnvironmentContext) SetPriority(value float64) (*TestEnvironmentContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setPriority", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*TestEnvironmentContext), nil
}

// TestRedisResource wraps a handle for Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestRedisResource.
type TestRedisResource struct {
	ResourceBuilderBase
	getTags *AspireList[string]
	getMetadata *AspireDict[string, string]
}

// NewTestRedisResource creates a new TestRedisResource.
func NewTestRedisResource(handle *Handle, client *AspireClient) *TestRedisResource {
	return &TestRedisResource{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// WithContainerRegistry configures a resource to use a container registry
func (s *TestRedisResource) WithContainerRegistry(registry *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["registry"] = SerializeValue(registry)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerRegistry", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithBindMount adds a bind mount
func (s *TestRedisResource) WithBindMount(source string, target string, isReadOnly *bool) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["source"] = SerializeValue(source)
	reqArgs["target"] = SerializeValue(target)
	if isReadOnly != nil {
		reqArgs["isReadOnly"] = SerializeValue(isReadOnly)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBindMount", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithEntrypoint sets the container entrypoint
func (s *TestRedisResource) WithEntrypoint(entrypoint string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["entrypoint"] = SerializeValue(entrypoint)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEntrypoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImageTag sets the container image tag
func (s *TestRedisResource) WithImageTag(tag string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["tag"] = SerializeValue(tag)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImageTag", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImageRegistry sets the container image registry
func (s *TestRedisResource) WithImageRegistry(registry string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["registry"] = SerializeValue(registry)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImageRegistry", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImage sets the container image
func (s *TestRedisResource) WithImage(image string, tag *string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["image"] = SerializeValue(image)
	if tag != nil {
		reqArgs["tag"] = SerializeValue(tag)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImage", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImageSHA256 sets the image SHA256 digest
func (s *TestRedisResource) WithImageSHA256(sha256 string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["sha256"] = SerializeValue(sha256)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImageSHA256", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithContainerRuntimeArgs adds runtime arguments for the container
func (s *TestRedisResource) WithContainerRuntimeArgs(args []string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["args"] = SerializeValue(args)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerRuntimeArgs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithLifetime sets the lifetime behavior of the container resource
func (s *TestRedisResource) WithLifetime(lifetime ContainerLifetime) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["lifetime"] = SerializeValue(lifetime)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withLifetime", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImagePullPolicy sets the container image pull policy
func (s *TestRedisResource) WithImagePullPolicy(pullPolicy ImagePullPolicy) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["pullPolicy"] = SerializeValue(pullPolicy)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImagePullPolicy", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// PublishAsContainer configures the resource to be published as a container
func (s *TestRedisResource) PublishAsContainer() (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishAsContainer", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithDockerfile configures the resource to use a Dockerfile
func (s *TestRedisResource) WithDockerfile(contextPath string, dockerfilePath *string, stage *string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["contextPath"] = SerializeValue(contextPath)
	if dockerfilePath != nil {
		reqArgs["dockerfilePath"] = SerializeValue(dockerfilePath)
	}
	if stage != nil {
		reqArgs["stage"] = SerializeValue(stage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfile", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithContainerName sets the container name
func (s *TestRedisResource) WithContainerName(name string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithBuildArg adds a build argument from a parameter resource
func (s *TestRedisResource) WithBuildArg(name string, value *ParameterResource) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuildArg", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithBuildSecret adds a build secret from a parameter resource
func (s *TestRedisResource) WithBuildSecret(name string, value *ParameterResource) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuildSecret", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithEndpointProxySupport configures endpoint proxy support
func (s *TestRedisResource) WithEndpointProxySupport(proxyEnabled bool) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["proxyEnabled"] = SerializeValue(proxyEnabled)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpointProxySupport", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *TestRedisResource) WithDockerfileBaseImage(buildImage *string, runtimeImage *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if buildImage != nil {
		reqArgs["buildImage"] = SerializeValue(buildImage)
	}
	if runtimeImage != nil {
		reqArgs["runtimeImage"] = SerializeValue(runtimeImage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfileBaseImage", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithContainerNetworkAlias adds a network alias for the container
func (s *TestRedisResource) WithContainerNetworkAlias(alias string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["alias"] = SerializeValue(alias)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerNetworkAlias", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithMcpServer configures an MCP server endpoint on the resource
func (s *TestRedisResource) WithMcpServer(path *string, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withMcpServer", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithOtlpExporter configures OTLP telemetry export
func (s *TestRedisResource) WithOtlpExporter() (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporter", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithOtlpExporterProtocol configures OTLP telemetry export with specific protocol
func (s *TestRedisResource) WithOtlpExporterProtocol(protocol OtlpProtocol) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["protocol"] = SerializeValue(protocol)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporterProtocol", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// PublishAsConnectionString publishes the resource as a connection string
func (s *TestRedisResource) PublishAsConnectionString() (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishAsConnectionString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithRequiredCommand adds a required command dependency
func (s *TestRedisResource) WithRequiredCommand(command string, helpLink *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["command"] = SerializeValue(command)
	if helpLink != nil {
		reqArgs["helpLink"] = SerializeValue(helpLink)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRequiredCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEnvironment sets an environment variable
func (s *TestRedisResource) WithEnvironment(name string, value string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentExpression adds an environment variable with a reference expression
func (s *TestRedisResource) WithEnvironmentExpression(name string, value *ReferenceExpression) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentCallback sets environment variables via callback
func (s *TestRedisResource) WithEnvironmentCallback(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentCallbackAsync sets environment variables via async callback
func (s *TestRedisResource) WithEnvironmentCallbackAsync(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentEndpoint sets an environment variable from an endpoint reference
func (s *TestRedisResource) WithEnvironmentEndpoint(name string, endpointReference *EndpointReference) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["endpointReference"] = SerializeValue(endpointReference)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentParameter sets an environment variable from a parameter resource
func (s *TestRedisResource) WithEnvironmentParameter(name string, parameter *ParameterResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["parameter"] = SerializeValue(parameter)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentParameter", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentConnectionString sets an environment variable from a connection string resource
func (s *TestRedisResource) WithEnvironmentConnectionString(envVarName string, resource *IResourceWithConnectionString) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["envVarName"] = SerializeValue(envVarName)
	reqArgs["resource"] = SerializeValue(resource)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentConnectionString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithConnectionProperty adds a connection property with a reference expression
func (s *TestRedisResource) WithConnectionProperty(name string, value *ReferenceExpression) (*IResourceWithConnectionString, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withConnectionProperty", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithConnectionString), nil
}

// WithConnectionPropertyValue adds a connection property with a string value
func (s *TestRedisResource) WithConnectionPropertyValue(name string, value string) (*IResourceWithConnectionString, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withConnectionPropertyValue", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithConnectionString), nil
}

// WithArgs adds arguments
func (s *TestRedisResource) WithArgs(args []string) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["args"] = SerializeValue(args)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithArgsCallback sets command-line arguments via callback
func (s *TestRedisResource) WithArgsCallback(callback func(...any) any) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithArgsCallbackAsync sets command-line arguments via async callback
func (s *TestRedisResource) WithArgsCallbackAsync(callback func(...any) any) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithReference adds a reference to another resource
func (s *TestRedisResource) WithReference(source *IResource, connectionName *string, optional *bool, name *string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["source"] = SerializeValue(source)
	if connectionName != nil {
		reqArgs["connectionName"] = SerializeValue(connectionName)
	}
	if optional != nil {
		reqArgs["optional"] = SerializeValue(optional)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReference", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceUri adds a reference to a URI
func (s *TestRedisResource) WithReferenceUri(name string, uri string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["uri"] = SerializeValue(uri)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceUri", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceExternalService adds a reference to an external service
func (s *TestRedisResource) WithReferenceExternalService(externalService *ExternalServiceResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["externalService"] = SerializeValue(externalService)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceExternalService", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceEndpoint adds a reference to an endpoint
func (s *TestRedisResource) WithReferenceEndpoint(endpointReference *EndpointReference) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointReference"] = SerializeValue(endpointReference)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEndpoint adds a network endpoint
func (s *TestRedisResource) WithEndpoint(port *float64, targetPort *float64, scheme *string, name *string, env *string, isProxied *bool, isExternal *bool, protocol *ProtocolType) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if scheme != nil {
		reqArgs["scheme"] = SerializeValue(scheme)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	if isExternal != nil {
		reqArgs["isExternal"] = SerializeValue(isExternal)
	}
	if protocol != nil {
		reqArgs["protocol"] = SerializeValue(protocol)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpEndpoint adds an HTTP endpoint
func (s *TestRedisResource) WithHttpEndpoint(port *float64, targetPort *float64, name *string, env *string, isProxied *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpsEndpoint adds an HTTPS endpoint
func (s *TestRedisResource) WithHttpsEndpoint(port *float64, targetPort *float64, name *string, env *string, isProxied *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithExternalHttpEndpoints makes HTTP endpoints externally accessible
func (s *TestRedisResource) WithExternalHttpEndpoints() (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExternalHttpEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// GetEndpoint gets an endpoint reference
func (s *TestRedisResource) GetEndpoint(name string) (*EndpointReference, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointReference), nil
}

// AsHttp2Service configures resource for HTTP/2
func (s *TestRedisResource) AsHttp2Service() (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/asHttp2Service", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithUrlsCallback customizes displayed URLs via callback
func (s *TestRedisResource) WithUrlsCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlsCallbackAsync customizes displayed URLs via async callback
func (s *TestRedisResource) WithUrlsCallbackAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *TestRedisResource) WithUrl(url string, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrl", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlExpression adds a URL using a reference expression
func (s *TestRedisResource) WithUrlExpression(url *ReferenceExpression, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *TestRedisResource) WithUrlForEndpoint(endpointName string, callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpointFactory adds a URL for a specific endpoint via factory callback
func (s *TestRedisResource) WithUrlForEndpointFactory(endpointName string, callback func(...any) any) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpointFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *TestRedisResource) ExcludeFromManifest() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromManifest", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WaitFor waits for another resource to be ready
func (s *TestRedisResource) WaitFor(dependency *IResource) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForWithBehavior waits for another resource with specific behavior
func (s *TestRedisResource) WaitForWithBehavior(dependency *IResource, waitBehavior WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForWithBehavior", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStart waits for another resource to start
func (s *TestRedisResource) WaitForStart(dependency *IResource) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStartWithBehavior waits for another resource to start with specific behavior
func (s *TestRedisResource) WaitForStartWithBehavior(dependency *IResource, waitBehavior WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStartWithBehavior", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WithExplicitStart prevents resource from starting automatically
func (s *TestRedisResource) WithExplicitStart() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExplicitStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WaitForCompletion waits for resource completion
func (s *TestRedisResource) WaitForCompletion(dependency *IResource, exitCode *float64) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if exitCode != nil {
		reqArgs["exitCode"] = SerializeValue(exitCode)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForCompletion", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WithHealthCheck adds a health check by key
func (s *TestRedisResource) WithHealthCheck(key string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["key"] = SerializeValue(key)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithHttpHealthCheck adds an HTTP health check
func (s *TestRedisResource) WithHttpHealthCheck(path *string, statusCode *float64, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if statusCode != nil {
		reqArgs["statusCode"] = SerializeValue(statusCode)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithCommand adds a resource command
func (s *TestRedisResource) WithCommand(name string, displayName string, executeCommand func(...any) any, commandOptions *CommandOptions) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["displayName"] = SerializeValue(displayName)
	if executeCommand != nil {
		reqArgs["executeCommand"] = RegisterCallback(executeCommand)
	}
	if commandOptions != nil {
		reqArgs["commandOptions"] = SerializeValue(commandOptions)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDeveloperCertificateTrust configures developer certificate trust
func (s *TestRedisResource) WithDeveloperCertificateTrust(trust bool) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["trust"] = SerializeValue(trust)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDeveloperCertificateTrust", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCertificateTrustScope sets the certificate trust scope
func (s *TestRedisResource) WithCertificateTrustScope(scope CertificateTrustScope) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["scope"] = SerializeValue(scope)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCertificateTrustScope", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithHttpsDeveloperCertificate configures HTTPS with a developer certificate
func (s *TestRedisResource) WithHttpsDeveloperCertificate(password *ParameterResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if password != nil {
		reqArgs["password"] = SerializeValue(password)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsDeveloperCertificate", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithoutHttpsCertificate removes HTTPS certificate configuration
func (s *TestRedisResource) WithoutHttpsCertificate() (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withoutHttpsCertificate", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithParentRelationship sets the parent relationship
func (s *TestRedisResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParentRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithChildRelationship sets a child relationship
func (s *TestRedisResource) WithChildRelationship(child *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["child"] = SerializeValue(child)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withChildRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithIconName sets the icon for the resource
func (s *TestRedisResource) WithIconName(iconName string, iconVariant *IconVariant) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["iconName"] = SerializeValue(iconName)
	if iconVariant != nil {
		reqArgs["iconVariant"] = SerializeValue(iconVariant)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withIconName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithHttpProbe adds an HTTP health probe to the resource
func (s *TestRedisResource) WithHttpProbe(probeType ProbeType, path *string, initialDelaySeconds *float64, periodSeconds *float64, timeoutSeconds *float64, failureThreshold *float64, successThreshold *float64, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["probeType"] = SerializeValue(probeType)
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if initialDelaySeconds != nil {
		reqArgs["initialDelaySeconds"] = SerializeValue(initialDelaySeconds)
	}
	if periodSeconds != nil {
		reqArgs["periodSeconds"] = SerializeValue(periodSeconds)
	}
	if timeoutSeconds != nil {
		reqArgs["timeoutSeconds"] = SerializeValue(timeoutSeconds)
	}
	if failureThreshold != nil {
		reqArgs["failureThreshold"] = SerializeValue(failureThreshold)
	}
	if successThreshold != nil {
		reqArgs["successThreshold"] = SerializeValue(successThreshold)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpProbe", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *TestRedisResource) ExcludeFromMcp() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromMcp", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithRemoteImageName sets the remote image name for publishing
func (s *TestRedisResource) WithRemoteImageName(remoteImageName string) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["remoteImageName"] = SerializeValue(remoteImageName)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRemoteImageName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
}

// WithRemoteImageTag sets the remote image tag for publishing
func (s *TestRedisResource) WithRemoteImageTag(remoteImageTag string) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["remoteImageTag"] = SerializeValue(remoteImageTag)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRemoteImageTag", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *TestRedisResource) WithPipelineStepFactory(stepName string, callback func(...any) any, dependsOn []string, requiredBy []string, tags []string, description *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["stepName"] = SerializeValue(stepName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if dependsOn != nil {
		reqArgs["dependsOn"] = SerializeValue(dependsOn)
	}
	if requiredBy != nil {
		reqArgs["requiredBy"] = SerializeValue(requiredBy)
	}
	if tags != nil {
		reqArgs["tags"] = SerializeValue(tags)
	}
	if description != nil {
		reqArgs["description"] = SerializeValue(description)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineStepFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfigurationAsync configures pipeline step dependencies via an async callback
func (s *TestRedisResource) WithPipelineConfigurationAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfigurationAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *TestRedisResource) WithPipelineConfiguration(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithVolume adds a volume
func (s *TestRedisResource) WithVolume(target string, name *string, isReadOnly *bool) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	reqArgs["target"] = SerializeValue(target)
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if isReadOnly != nil {
		reqArgs["isReadOnly"] = SerializeValue(isReadOnly)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withVolume", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// GetResourceName gets the resource name
func (s *TestRedisResource) GetResourceName() (*string, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *TestRedisResource) OnBeforeResourceStarted(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onBeforeResourceStarted", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *TestRedisResource) OnResourceStopped(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceStopped", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnConnectionStringAvailable subscribes to the ConnectionStringAvailable event
func (s *TestRedisResource) OnConnectionStringAvailable(callback func(...any) any) (*IResourceWithConnectionString, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onConnectionStringAvailable", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithConnectionString), nil
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *TestRedisResource) OnInitializeResource(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onInitializeResource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceEndpointsAllocated subscribes to the ResourceEndpointsAllocated event
func (s *TestRedisResource) OnResourceEndpointsAllocated(callback func(...any) any) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceEndpointsAllocated", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// OnResourceReady subscribes to the ResourceReady event
func (s *TestRedisResource) OnResourceReady(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceReady", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// AddTestChildDatabase adds a child database to a test Redis resource
func (s *TestRedisResource) AddTestChildDatabase(name string, databaseName *string) (*TestDatabaseResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	if databaseName != nil {
		reqArgs["databaseName"] = SerializeValue(databaseName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/addTestChildDatabase", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*TestDatabaseResource), nil
}

// WithPersistence configures the Redis resource with persistence
func (s *TestRedisResource) WithPersistence(mode *TestPersistenceMode) (*TestRedisResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if mode != nil {
		reqArgs["mode"] = SerializeValue(mode)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withPersistence", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*TestRedisResource), nil
}

// WithOptionalString adds an optional string parameter
func (s *TestRedisResource) WithOptionalString(value *string, enabled *bool) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if value != nil {
		reqArgs["value"] = SerializeValue(value)
	}
	if enabled != nil {
		reqArgs["enabled"] = SerializeValue(enabled)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithConfig configures the resource with a DTO
func (s *TestRedisResource) WithConfig(config *TestConfigDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// GetTags gets the tags for the resource
func (s *TestRedisResource) GetTags() *AspireList[string] {
	if s.getTags == nil {
		s.getTags = NewAspireListWithGetter[string](s.Handle(), s.Client(), "Aspire.Hosting.CodeGeneration.Go.Tests/getTags")
	}
	return s.getTags
}

// GetMetadata gets the metadata for the resource
func (s *TestRedisResource) GetMetadata() *AspireDict[string, string] {
	if s.getMetadata == nil {
		s.getMetadata = NewAspireDictWithGetter[string, string](s.Handle(), s.Client(), "Aspire.Hosting.CodeGeneration.Go.Tests/getMetadata")
	}
	return s.getMetadata
}

// WithConnectionString sets the connection string using a reference expression
func (s *TestRedisResource) WithConnectionString(connectionString *ReferenceExpression) (*IResourceWithConnectionString, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["connectionString"] = SerializeValue(connectionString)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withConnectionString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithConnectionString), nil
}

// TestWithEnvironmentCallback configures environment with callback (test version)
func (s *TestRedisResource) TestWithEnvironmentCallback(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWithEnvironmentCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCreatedAt sets the created timestamp
func (s *TestRedisResource) WithCreatedAt(createdAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["createdAt"] = SerializeValue(createdAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCreatedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithModifiedAt sets the modified timestamp
func (s *TestRedisResource) WithModifiedAt(modifiedAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["modifiedAt"] = SerializeValue(modifiedAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withModifiedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCorrelationId sets the correlation ID
func (s *TestRedisResource) WithCorrelationId(correlationId string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["correlationId"] = SerializeValue(correlationId)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCorrelationId", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalCallback configures with optional callback
func (s *TestRedisResource) WithOptionalCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithStatus sets the resource status
func (s *TestRedisResource) WithStatus(status TestResourceStatus) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["status"] = SerializeValue(status)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withStatus", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithNestedConfig configures with nested DTO
func (s *TestRedisResource) WithNestedConfig(config *TestNestedDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withNestedConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithValidator adds validation callback
func (s *TestRedisResource) WithValidator(validator func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if validator != nil {
		reqArgs["validator"] = RegisterCallback(validator)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withValidator", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestWaitFor waits for another resource (test version)
func (s *TestRedisResource) TestWaitFor(dependency *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWaitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// GetEndpoints gets the endpoints
func (s *TestRedisResource) GetEndpoints() (*[]string, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/getEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*[]string), nil
}

// WithConnectionStringDirect sets connection string using direct interface target
func (s *TestRedisResource) WithConnectionStringDirect(connectionString string) (*IResourceWithConnectionString, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["connectionString"] = SerializeValue(connectionString)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withConnectionStringDirect", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithConnectionString), nil
}

// WithRedisSpecific redis-specific configuration
func (s *TestRedisResource) WithRedisSpecific(option string) (*TestRedisResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["option"] = SerializeValue(option)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withRedisSpecific", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*TestRedisResource), nil
}

// WithDependency adds a dependency on another resource
func (s *TestRedisResource) WithDependency(dependency *IResourceWithConnectionString) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withDependency", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEndpoints sets the endpoints
func (s *TestRedisResource) WithEndpoints(endpoints []string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpoints"] = SerializeValue(endpoints)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEnvironmentVariables sets environment variables
func (s *TestRedisResource) WithEnvironmentVariables(variables map[string]string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["variables"] = SerializeValue(variables)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEnvironmentVariables", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// GetStatusAsync gets the status of the resource asynchronously
func (s *TestRedisResource) GetStatusAsync(cancellationToken *CancellationToken) (*string, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if cancellationToken != nil {
		reqArgs["cancellationToken"] = RegisterCancellation(cancellationToken, s.Client())
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/getStatusAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// WithCancellableOperation performs a cancellable operation
func (s *TestRedisResource) WithCancellableOperation(operation func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if operation != nil {
		reqArgs["operation"] = RegisterCallback(operation)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCancellableOperation", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WaitForReadyAsync waits for the resource to be ready
func (s *TestRedisResource) WaitForReadyAsync(timeout float64, cancellationToken *CancellationToken) (*bool, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["timeout"] = SerializeValue(timeout)
	if cancellationToken != nil {
		reqArgs["cancellationToken"] = RegisterCancellation(cancellationToken, s.Client())
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/waitForReadyAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// WithMultiParamHandleCallback tests multi-param callback destructuring
func (s *TestRedisResource) WithMultiParamHandleCallback(callback func(...any) any) (*TestRedisResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMultiParamHandleCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*TestRedisResource), nil
}

// WithDataVolume adds a data volume with persistence
func (s *TestRedisResource) WithDataVolume(name *string, isReadOnly *bool) (*TestRedisResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if isReadOnly != nil {
		reqArgs["isReadOnly"] = SerializeValue(isReadOnly)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withDataVolume", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*TestRedisResource), nil
}

// TestResourceContext wraps a handle for Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestResourceContext.
type TestResourceContext struct {
	HandleWrapperBase
}

// NewTestResourceContext creates a new TestResourceContext.
func NewTestResourceContext(handle *Handle, client *AspireClient) *TestResourceContext {
	return &TestResourceContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Name gets the Name property
func (s *TestResourceContext) Name() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.name", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// SetName sets the Name property
func (s *TestResourceContext) SetName(value string) (*TestResourceContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*TestResourceContext), nil
}

// Value gets the Value property
func (s *TestResourceContext) Value() (*float64, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.value", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*float64), nil
}

// SetValue sets the Value property
func (s *TestResourceContext) SetValue(value float64) (*TestResourceContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setValue", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*TestResourceContext), nil
}

// GetValueAsync invokes the GetValueAsync method
func (s *TestResourceContext) GetValueAsync() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.getValueAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// SetValueAsync invokes the SetValueAsync method
func (s *TestResourceContext) SetValueAsync(value string) error {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	_, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setValueAsync", reqArgs)
	return err
}

// ValidateAsync invokes the ValidateAsync method
func (s *TestResourceContext) ValidateAsync() (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.validateAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// TestVaultResource wraps a handle for Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestVaultResource.
type TestVaultResource struct {
	ResourceBuilderBase
}

// NewTestVaultResource creates a new TestVaultResource.
func NewTestVaultResource(handle *Handle, client *AspireClient) *TestVaultResource {
	return &TestVaultResource{
		ResourceBuilderBase: NewResourceBuilderBase(handle, client),
	}
}

// WithContainerRegistry configures a resource to use a container registry
func (s *TestVaultResource) WithContainerRegistry(registry *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["registry"] = SerializeValue(registry)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerRegistry", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithBindMount adds a bind mount
func (s *TestVaultResource) WithBindMount(source string, target string, isReadOnly *bool) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["source"] = SerializeValue(source)
	reqArgs["target"] = SerializeValue(target)
	if isReadOnly != nil {
		reqArgs["isReadOnly"] = SerializeValue(isReadOnly)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBindMount", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithEntrypoint sets the container entrypoint
func (s *TestVaultResource) WithEntrypoint(entrypoint string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["entrypoint"] = SerializeValue(entrypoint)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEntrypoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImageTag sets the container image tag
func (s *TestVaultResource) WithImageTag(tag string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["tag"] = SerializeValue(tag)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImageTag", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImageRegistry sets the container image registry
func (s *TestVaultResource) WithImageRegistry(registry string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["registry"] = SerializeValue(registry)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImageRegistry", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImage sets the container image
func (s *TestVaultResource) WithImage(image string, tag *string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["image"] = SerializeValue(image)
	if tag != nil {
		reqArgs["tag"] = SerializeValue(tag)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImage", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImageSHA256 sets the image SHA256 digest
func (s *TestVaultResource) WithImageSHA256(sha256 string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["sha256"] = SerializeValue(sha256)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImageSHA256", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithContainerRuntimeArgs adds runtime arguments for the container
func (s *TestVaultResource) WithContainerRuntimeArgs(args []string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["args"] = SerializeValue(args)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerRuntimeArgs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithLifetime sets the lifetime behavior of the container resource
func (s *TestVaultResource) WithLifetime(lifetime ContainerLifetime) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["lifetime"] = SerializeValue(lifetime)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withLifetime", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithImagePullPolicy sets the container image pull policy
func (s *TestVaultResource) WithImagePullPolicy(pullPolicy ImagePullPolicy) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["pullPolicy"] = SerializeValue(pullPolicy)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImagePullPolicy", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// PublishAsContainer configures the resource to be published as a container
func (s *TestVaultResource) PublishAsContainer() (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishAsContainer", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithDockerfile configures the resource to use a Dockerfile
func (s *TestVaultResource) WithDockerfile(contextPath string, dockerfilePath *string, stage *string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["contextPath"] = SerializeValue(contextPath)
	if dockerfilePath != nil {
		reqArgs["dockerfilePath"] = SerializeValue(dockerfilePath)
	}
	if stage != nil {
		reqArgs["stage"] = SerializeValue(stage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfile", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithContainerName sets the container name
func (s *TestVaultResource) WithContainerName(name string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithBuildArg adds a build argument from a parameter resource
func (s *TestVaultResource) WithBuildArg(name string, value *ParameterResource) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuildArg", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithBuildSecret adds a build secret from a parameter resource
func (s *TestVaultResource) WithBuildSecret(name string, value *ParameterResource) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuildSecret", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithEndpointProxySupport configures endpoint proxy support
func (s *TestVaultResource) WithEndpointProxySupport(proxyEnabled bool) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["proxyEnabled"] = SerializeValue(proxyEnabled)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpointProxySupport", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *TestVaultResource) WithDockerfileBaseImage(buildImage *string, runtimeImage *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if buildImage != nil {
		reqArgs["buildImage"] = SerializeValue(buildImage)
	}
	if runtimeImage != nil {
		reqArgs["runtimeImage"] = SerializeValue(runtimeImage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfileBaseImage", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithContainerNetworkAlias adds a network alias for the container
func (s *TestVaultResource) WithContainerNetworkAlias(alias string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["alias"] = SerializeValue(alias)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerNetworkAlias", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithMcpServer configures an MCP server endpoint on the resource
func (s *TestVaultResource) WithMcpServer(path *string, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withMcpServer", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithOtlpExporter configures OTLP telemetry export
func (s *TestVaultResource) WithOtlpExporter() (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporter", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithOtlpExporterProtocol configures OTLP telemetry export with specific protocol
func (s *TestVaultResource) WithOtlpExporterProtocol(protocol OtlpProtocol) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["protocol"] = SerializeValue(protocol)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporterProtocol", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// PublishAsConnectionString publishes the resource as a connection string
func (s *TestVaultResource) PublishAsConnectionString() (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishAsConnectionString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithRequiredCommand adds a required command dependency
func (s *TestVaultResource) WithRequiredCommand(command string, helpLink *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["command"] = SerializeValue(command)
	if helpLink != nil {
		reqArgs["helpLink"] = SerializeValue(helpLink)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRequiredCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEnvironment sets an environment variable
func (s *TestVaultResource) WithEnvironment(name string, value string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentExpression adds an environment variable with a reference expression
func (s *TestVaultResource) WithEnvironmentExpression(name string, value *ReferenceExpression) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentCallback sets environment variables via callback
func (s *TestVaultResource) WithEnvironmentCallback(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentCallbackAsync sets environment variables via async callback
func (s *TestVaultResource) WithEnvironmentCallbackAsync(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentEndpoint sets an environment variable from an endpoint reference
func (s *TestVaultResource) WithEnvironmentEndpoint(name string, endpointReference *EndpointReference) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["endpointReference"] = SerializeValue(endpointReference)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentParameter sets an environment variable from a parameter resource
func (s *TestVaultResource) WithEnvironmentParameter(name string, parameter *ParameterResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["parameter"] = SerializeValue(parameter)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentParameter", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEnvironmentConnectionString sets an environment variable from a connection string resource
func (s *TestVaultResource) WithEnvironmentConnectionString(envVarName string, resource *IResourceWithConnectionString) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["envVarName"] = SerializeValue(envVarName)
	reqArgs["resource"] = SerializeValue(resource)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentConnectionString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithArgs adds arguments
func (s *TestVaultResource) WithArgs(args []string) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["args"] = SerializeValue(args)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithArgsCallback sets command-line arguments via callback
func (s *TestVaultResource) WithArgsCallback(callback func(...any) any) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithArgsCallbackAsync sets command-line arguments via async callback
func (s *TestVaultResource) WithArgsCallbackAsync(callback func(...any) any) (*IResourceWithArgs, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithArgs), nil
}

// WithReference adds a reference to another resource
func (s *TestVaultResource) WithReference(source *IResource, connectionName *string, optional *bool, name *string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["source"] = SerializeValue(source)
	if connectionName != nil {
		reqArgs["connectionName"] = SerializeValue(connectionName)
	}
	if optional != nil {
		reqArgs["optional"] = SerializeValue(optional)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReference", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceUri adds a reference to a URI
func (s *TestVaultResource) WithReferenceUri(name string, uri string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["uri"] = SerializeValue(uri)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceUri", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceExternalService adds a reference to an external service
func (s *TestVaultResource) WithReferenceExternalService(externalService *ExternalServiceResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["externalService"] = SerializeValue(externalService)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceExternalService", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReferenceEndpoint adds a reference to an endpoint
func (s *TestVaultResource) WithReferenceEndpoint(endpointReference *EndpointReference) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointReference"] = SerializeValue(endpointReference)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithEndpoint adds a network endpoint
func (s *TestVaultResource) WithEndpoint(port *float64, targetPort *float64, scheme *string, name *string, env *string, isProxied *bool, isExternal *bool, protocol *ProtocolType) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if scheme != nil {
		reqArgs["scheme"] = SerializeValue(scheme)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	if isExternal != nil {
		reqArgs["isExternal"] = SerializeValue(isExternal)
	}
	if protocol != nil {
		reqArgs["protocol"] = SerializeValue(protocol)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpEndpoint adds an HTTP endpoint
func (s *TestVaultResource) WithHttpEndpoint(port *float64, targetPort *float64, name *string, env *string, isProxied *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpsEndpoint adds an HTTPS endpoint
func (s *TestVaultResource) WithHttpsEndpoint(port *float64, targetPort *float64, name *string, env *string, isProxied *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if port != nil {
		reqArgs["port"] = SerializeValue(port)
	}
	if targetPort != nil {
		reqArgs["targetPort"] = SerializeValue(targetPort)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if env != nil {
		reqArgs["env"] = SerializeValue(env)
	}
	if isProxied != nil {
		reqArgs["isProxied"] = SerializeValue(isProxied)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithExternalHttpEndpoints makes HTTP endpoints externally accessible
func (s *TestVaultResource) WithExternalHttpEndpoints() (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExternalHttpEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// GetEndpoint gets an endpoint reference
func (s *TestVaultResource) GetEndpoint(name string) (*EndpointReference, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointReference), nil
}

// AsHttp2Service configures resource for HTTP/2
func (s *TestVaultResource) AsHttp2Service() (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/asHttp2Service", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithUrlsCallback customizes displayed URLs via callback
func (s *TestVaultResource) WithUrlsCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlsCallbackAsync customizes displayed URLs via async callback
func (s *TestVaultResource) WithUrlsCallbackAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlsCallbackAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *TestVaultResource) WithUrl(url string, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrl", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlExpression adds a URL using a reference expression
func (s *TestVaultResource) WithUrlExpression(url *ReferenceExpression, displayText *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlExpression", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *TestVaultResource) WithUrlForEndpoint(endpointName string, callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrlForEndpointFactory adds a URL for a specific endpoint via factory callback
func (s *TestVaultResource) WithUrlForEndpointFactory(endpointName string, callback func(...any) any) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrlForEndpointFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *TestVaultResource) ExcludeFromManifest() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromManifest", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WaitFor waits for another resource to be ready
func (s *TestVaultResource) WaitFor(dependency *IResource) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForWithBehavior waits for another resource with specific behavior
func (s *TestVaultResource) WaitForWithBehavior(dependency *IResource, waitBehavior WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForWithBehavior", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStart waits for another resource to start
func (s *TestVaultResource) WaitForStart(dependency *IResource) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStartWithBehavior waits for another resource to start with specific behavior
func (s *TestVaultResource) WaitForStartWithBehavior(dependency *IResource, waitBehavior WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStartWithBehavior", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WithExplicitStart prevents resource from starting automatically
func (s *TestVaultResource) WithExplicitStart() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withExplicitStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WaitForCompletion waits for resource completion
func (s *TestVaultResource) WaitForCompletion(dependency *IResource, exitCode *float64) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if exitCode != nil {
		reqArgs["exitCode"] = SerializeValue(exitCode)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForCompletion", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WithHealthCheck adds a health check by key
func (s *TestVaultResource) WithHealthCheck(key string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["key"] = SerializeValue(key)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithHttpHealthCheck adds an HTTP health check
func (s *TestVaultResource) WithHttpHealthCheck(path *string, statusCode *float64, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if statusCode != nil {
		reqArgs["statusCode"] = SerializeValue(statusCode)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpHealthCheck", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithCommand adds a resource command
func (s *TestVaultResource) WithCommand(name string, displayName string, executeCommand func(...any) any, commandOptions *CommandOptions) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["displayName"] = SerializeValue(displayName)
	if executeCommand != nil {
		reqArgs["executeCommand"] = RegisterCallback(executeCommand)
	}
	if commandOptions != nil {
		reqArgs["commandOptions"] = SerializeValue(commandOptions)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDeveloperCertificateTrust configures developer certificate trust
func (s *TestVaultResource) WithDeveloperCertificateTrust(trust bool) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["trust"] = SerializeValue(trust)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDeveloperCertificateTrust", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCertificateTrustScope sets the certificate trust scope
func (s *TestVaultResource) WithCertificateTrustScope(scope CertificateTrustScope) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["scope"] = SerializeValue(scope)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCertificateTrustScope", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithHttpsDeveloperCertificate configures HTTPS with a developer certificate
func (s *TestVaultResource) WithHttpsDeveloperCertificate(password *ParameterResource) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if password != nil {
		reqArgs["password"] = SerializeValue(password)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsDeveloperCertificate", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithoutHttpsCertificate removes HTTPS certificate configuration
func (s *TestVaultResource) WithoutHttpsCertificate() (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withoutHttpsCertificate", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithParentRelationship sets the parent relationship
func (s *TestVaultResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParentRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithChildRelationship sets a child relationship
func (s *TestVaultResource) WithChildRelationship(child *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["child"] = SerializeValue(child)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withChildRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithIconName sets the icon for the resource
func (s *TestVaultResource) WithIconName(iconName string, iconVariant *IconVariant) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["iconName"] = SerializeValue(iconName)
	if iconVariant != nil {
		reqArgs["iconVariant"] = SerializeValue(iconVariant)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withIconName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithHttpProbe adds an HTTP health probe to the resource
func (s *TestVaultResource) WithHttpProbe(probeType ProbeType, path *string, initialDelaySeconds *float64, periodSeconds *float64, timeoutSeconds *float64, failureThreshold *float64, successThreshold *float64, endpointName *string) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["probeType"] = SerializeValue(probeType)
	if path != nil {
		reqArgs["path"] = SerializeValue(path)
	}
	if initialDelaySeconds != nil {
		reqArgs["initialDelaySeconds"] = SerializeValue(initialDelaySeconds)
	}
	if periodSeconds != nil {
		reqArgs["periodSeconds"] = SerializeValue(periodSeconds)
	}
	if timeoutSeconds != nil {
		reqArgs["timeoutSeconds"] = SerializeValue(timeoutSeconds)
	}
	if failureThreshold != nil {
		reqArgs["failureThreshold"] = SerializeValue(failureThreshold)
	}
	if successThreshold != nil {
		reqArgs["successThreshold"] = SerializeValue(successThreshold)
	}
	if endpointName != nil {
		reqArgs["endpointName"] = SerializeValue(endpointName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpProbe", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *TestVaultResource) ExcludeFromMcp() (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/excludeFromMcp", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithRemoteImageName sets the remote image name for publishing
func (s *TestVaultResource) WithRemoteImageName(remoteImageName string) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["remoteImageName"] = SerializeValue(remoteImageName)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRemoteImageName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
}

// WithRemoteImageTag sets the remote image tag for publishing
func (s *TestVaultResource) WithRemoteImageTag(remoteImageTag string) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["remoteImageTag"] = SerializeValue(remoteImageTag)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withRemoteImageTag", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *TestVaultResource) WithPipelineStepFactory(stepName string, callback func(...any) any, dependsOn []string, requiredBy []string, tags []string, description *string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["stepName"] = SerializeValue(stepName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if dependsOn != nil {
		reqArgs["dependsOn"] = SerializeValue(dependsOn)
	}
	if requiredBy != nil {
		reqArgs["requiredBy"] = SerializeValue(requiredBy)
	}
	if tags != nil {
		reqArgs["tags"] = SerializeValue(tags)
	}
	if description != nil {
		reqArgs["description"] = SerializeValue(description)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineStepFactory", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfigurationAsync configures pipeline step dependencies via an async callback
func (s *TestVaultResource) WithPipelineConfigurationAsync(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfigurationAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *TestVaultResource) WithPipelineConfiguration(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withPipelineConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithVolume adds a volume
func (s *TestVaultResource) WithVolume(target string, name *string, isReadOnly *bool) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	reqArgs["target"] = SerializeValue(target)
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if isReadOnly != nil {
		reqArgs["isReadOnly"] = SerializeValue(isReadOnly)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withVolume", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// GetResourceName gets the resource name
func (s *TestVaultResource) GetResourceName() (*string, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *TestVaultResource) OnBeforeResourceStarted(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onBeforeResourceStarted", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *TestVaultResource) OnResourceStopped(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceStopped", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *TestVaultResource) OnInitializeResource(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onInitializeResource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// OnResourceEndpointsAllocated subscribes to the ResourceEndpointsAllocated event
func (s *TestVaultResource) OnResourceEndpointsAllocated(callback func(...any) any) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceEndpointsAllocated", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// OnResourceReady subscribes to the ResourceReady event
func (s *TestVaultResource) OnResourceReady(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/onResourceReady", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalString adds an optional string parameter
func (s *TestVaultResource) WithOptionalString(value *string, enabled *bool) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if value != nil {
		reqArgs["value"] = SerializeValue(value)
	}
	if enabled != nil {
		reqArgs["enabled"] = SerializeValue(enabled)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithConfig configures the resource with a DTO
func (s *TestVaultResource) WithConfig(config *TestConfigDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestWithEnvironmentCallback configures environment with callback (test version)
func (s *TestVaultResource) TestWithEnvironmentCallback(callback func(...any) any) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWithEnvironmentCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCreatedAt sets the created timestamp
func (s *TestVaultResource) WithCreatedAt(createdAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["createdAt"] = SerializeValue(createdAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCreatedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithModifiedAt sets the modified timestamp
func (s *TestVaultResource) WithModifiedAt(modifiedAt string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["modifiedAt"] = SerializeValue(modifiedAt)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withModifiedAt", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithCorrelationId sets the correlation ID
func (s *TestVaultResource) WithCorrelationId(correlationId string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["correlationId"] = SerializeValue(correlationId)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCorrelationId", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithOptionalCallback configures with optional callback
func (s *TestVaultResource) WithOptionalCallback(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithStatus sets the resource status
func (s *TestVaultResource) WithStatus(status TestResourceStatus) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["status"] = SerializeValue(status)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withStatus", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithNestedConfig configures with nested DTO
func (s *TestVaultResource) WithNestedConfig(config *TestNestedDto) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["config"] = SerializeValue(config)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withNestedConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithValidator adds validation callback
func (s *TestVaultResource) WithValidator(validator func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if validator != nil {
		reqArgs["validator"] = RegisterCallback(validator)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withValidator", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// TestWaitFor waits for another resource (test version)
func (s *TestVaultResource) TestWaitFor(dependency *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/testWaitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithDependency adds a dependency on another resource
func (s *TestVaultResource) WithDependency(dependency *IResourceWithConnectionString) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withDependency", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEndpoints sets the endpoints
func (s *TestVaultResource) WithEndpoints(endpoints []string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpoints"] = SerializeValue(endpoints)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEndpoints", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithEnvironmentVariables sets environment variables
func (s *TestVaultResource) WithEnvironmentVariables(variables map[string]string) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["variables"] = SerializeValue(variables)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withEnvironmentVariables", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithCancellableOperation performs a cancellable operation
func (s *TestVaultResource) WithCancellableOperation(operation func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if operation != nil {
		reqArgs["operation"] = RegisterCallback(operation)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withCancellableOperation", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithVaultDirect configures vault using direct interface target
func (s *TestVaultResource) WithVaultDirect(option string) (*ITestVaultResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["option"] = SerializeValue(option)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withVaultDirect", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ITestVaultResource), nil
}

// UpdateCommandStateContext wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.UpdateCommandStateContext.
type UpdateCommandStateContext struct {
	HandleWrapperBase
}

// NewUpdateCommandStateContext creates a new UpdateCommandStateContext.
func NewUpdateCommandStateContext(handle *Handle, client *AspireClient) *UpdateCommandStateContext {
	return &UpdateCommandStateContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// ServiceProvider gets the ServiceProvider property
func (s *UpdateCommandStateContext) ServiceProvider() (*IServiceProvider, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/UpdateCommandStateContext.serviceProvider", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IServiceProvider), nil
}

// SetServiceProvider sets the ServiceProvider property
func (s *UpdateCommandStateContext) SetServiceProvider(value *IServiceProvider) (*UpdateCommandStateContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/UpdateCommandStateContext.setServiceProvider", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*UpdateCommandStateContext), nil
}

// ============================================================================
// Handle wrapper registrations
// ============================================================================

func init() {
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder", func(h *Handle, c *AspireClient) any {
		return NewIDistributedApplicationBuilder(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.DistributedApplication", func(h *Handle, c *AspireClient) any {
		return NewDistributedApplication(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointReference", func(h *Handle, c *AspireClient) any {
		return NewEndpointReference(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResource", func(h *Handle, c *AspireClient) any {
		return NewIResource(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithEnvironment", func(h *Handle, c *AspireClient) any {
		return NewIResourceWithEnvironment(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithEndpoints", func(h *Handle, c *AspireClient) any {
		return NewIResourceWithEndpoints(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithArgs", func(h *Handle, c *AspireClient) any {
		return NewIResourceWithArgs(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithConnectionString", func(h *Handle, c *AspireClient) any {
		return NewIResourceWithConnectionString(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithWaitSupport", func(h *Handle, c *AspireClient) any {
		return NewIResourceWithWaitSupport(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithParent", func(h *Handle, c *AspireClient) any {
		return NewIResourceWithParent(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerResource", func(h *Handle, c *AspireClient) any {
		return NewContainerResource(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ExecutableResource", func(h *Handle, c *AspireClient) any {
		return NewExecutableResource(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ProjectResource", func(h *Handle, c *AspireClient) any {
		return NewProjectResource(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ParameterResource", func(h *Handle, c *AspireClient) any {
		return NewParameterResource(h, c)
	})
	RegisterHandleWrapper("System.ComponentModel/System.IServiceProvider", func(h *Handle, c *AspireClient) any {
		return NewIServiceProvider(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceNotificationService", func(h *Handle, c *AspireClient) any {
		return NewResourceNotificationService(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceLoggerService", func(h *Handle, c *AspireClient) any {
		return NewResourceLoggerService(h, c)
	})
	RegisterHandleWrapper("Microsoft.Extensions.Configuration.Abstractions/Microsoft.Extensions.Configuration.IConfiguration", func(h *Handle, c *AspireClient) any {
		return NewIConfiguration(h, c)
	})
	RegisterHandleWrapper("Microsoft.Extensions.Configuration.Abstractions/Microsoft.Extensions.Configuration.IConfigurationSection", func(h *Handle, c *AspireClient) any {
		return NewIConfigurationSection(h, c)
	})
	RegisterHandleWrapper("Microsoft.Extensions.Hosting.Abstractions/Microsoft.Extensions.Hosting.IHostEnvironment", func(h *Handle, c *AspireClient) any {
		return NewIHostEnvironment(h, c)
	})
	RegisterHandleWrapper("Microsoft.Extensions.Logging.Abstractions/Microsoft.Extensions.Logging.ILogger", func(h *Handle, c *AspireClient) any {
		return NewILogger(h, c)
	})
	RegisterHandleWrapper("Microsoft.Extensions.Logging.Abstractions/Microsoft.Extensions.Logging.ILoggerFactory", func(h *Handle, c *AspireClient) any {
		return NewILoggerFactory(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.IReportingStep", func(h *Handle, c *AspireClient) any {
		return NewIReportingStep(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.IReportingTask", func(h *Handle, c *AspireClient) any {
		return NewIReportingTask(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.Eventing.DistributedApplicationEventSubscription", func(h *Handle, c *AspireClient) any {
		return NewDistributedApplicationEventSubscription(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.DistributedApplicationExecutionContext", func(h *Handle, c *AspireClient) any {
		return NewDistributedApplicationExecutionContext(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.DistributedApplicationExecutionContextOptions", func(h *Handle, c *AspireClient) any {
		return NewDistributedApplicationExecutionContextOptions(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ProjectResourceOptions", func(h *Handle, c *AspireClient) any {
		return NewProjectResourceOptions(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.IUserSecretsManager", func(h *Handle, c *AspireClient) any {
		return NewIUserSecretsManager(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineConfigurationContext", func(h *Handle, c *AspireClient) any {
		return NewPipelineConfigurationContext(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineContext", func(h *Handle, c *AspireClient) any {
		return NewPipelineContext(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineStep", func(h *Handle, c *AspireClient) any {
		return NewPipelineStep(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineStepContext", func(h *Handle, c *AspireClient) any {
		return NewPipelineStepContext(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineStepFactoryContext", func(h *Handle, c *AspireClient) any {
		return NewPipelineStepFactoryContext(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineSummary", func(h *Handle, c *AspireClient) any {
		return NewPipelineSummary(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.Eventing.DistributedApplicationResourceEventSubscription", func(h *Handle, c *AspireClient) any {
		return NewDistributedApplicationResourceEventSubscription(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.Eventing.IDistributedApplicationEvent", func(h *Handle, c *AspireClient) any {
		return NewIDistributedApplicationEvent(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.Eventing.IDistributedApplicationResourceEvent", func(h *Handle, c *AspireClient) any {
		return NewIDistributedApplicationResourceEvent(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.Eventing.IDistributedApplicationEventing", func(h *Handle, c *AspireClient) any {
		return NewIDistributedApplicationEventing(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.AfterResourcesCreatedEvent", func(h *Handle, c *AspireClient) any {
		return NewAfterResourcesCreatedEvent(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.BeforeResourceStartedEvent", func(h *Handle, c *AspireClient) any {
		return NewBeforeResourceStartedEvent(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.BeforeStartEvent", func(h *Handle, c *AspireClient) any {
		return NewBeforeStartEvent(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.CommandLineArgsCallbackContext", func(h *Handle, c *AspireClient) any {
		return NewCommandLineArgsCallbackContext(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ConnectionStringAvailableEvent", func(h *Handle, c *AspireClient) any {
		return NewConnectionStringAvailableEvent(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.DistributedApplicationModel", func(h *Handle, c *AspireClient) any {
		return NewDistributedApplicationModel(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointReferenceExpression", func(h *Handle, c *AspireClient) any {
		return NewEndpointReferenceExpression(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EnvironmentCallbackContext", func(h *Handle, c *AspireClient) any {
		return NewEnvironmentCallbackContext(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.InitializeResourceEvent", func(h *Handle, c *AspireClient) any {
		return NewInitializeResourceEvent(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpressionBuilder", func(h *Handle, c *AspireClient) any {
		return NewReferenceExpressionBuilder(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.UpdateCommandStateContext", func(h *Handle, c *AspireClient) any {
		return NewUpdateCommandStateContext(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ExecuteCommandContext", func(h *Handle, c *AspireClient) any {
		return NewExecuteCommandContext(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceEndpointsAllocatedEvent", func(h *Handle, c *AspireClient) any {
		return NewResourceEndpointsAllocatedEvent(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceReadyEvent", func(h *Handle, c *AspireClient) any {
		return NewResourceReadyEvent(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceStoppedEvent", func(h *Handle, c *AspireClient) any {
		return NewResourceStoppedEvent(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceUrlsCallbackContext", func(h *Handle, c *AspireClient) any {
		return NewResourceUrlsCallbackContext(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ConnectionStringResource", func(h *Handle, c *AspireClient) any {
		return NewConnectionStringResource(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerRegistryResource", func(h *Handle, c *AspireClient) any {
		return NewContainerRegistryResource(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.DotnetToolResource", func(h *Handle, c *AspireClient) any {
		return NewDotnetToolResource(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ExternalServiceResource", func(h *Handle, c *AspireClient) any {
		return NewExternalServiceResource(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.CSharpAppResource", func(h *Handle, c *AspireClient) any {
		return NewCSharpAppResource(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.IResourceWithContainerFiles", func(h *Handle, c *AspireClient) any {
		return NewIResourceWithContainerFiles(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCallbackContext", func(h *Handle, c *AspireClient) any {
		return NewTestCallbackContext(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestResourceContext", func(h *Handle, c *AspireClient) any {
		return NewTestResourceContext(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestEnvironmentContext", func(h *Handle, c *AspireClient) any {
		return NewTestEnvironmentContext(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCollectionContext", func(h *Handle, c *AspireClient) any {
		return NewTestCollectionContext(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestRedisResource", func(h *Handle, c *AspireClient) any {
		return NewTestRedisResource(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestDatabaseResource", func(h *Handle, c *AspireClient) any {
		return NewTestDatabaseResource(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestVaultResource", func(h *Handle, c *AspireClient) any {
		return NewTestVaultResource(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.ITestVaultResource", func(h *Handle, c *AspireClient) any {
		return NewITestVaultResource(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IContainerFilesDestinationResource", func(h *Handle, c *AspireClient) any {
		return NewIContainerFilesDestinationResource(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IComputeResource", func(h *Handle, c *AspireClient) any {
		return NewIComputeResource(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/List<string>", func(h *Handle, c *AspireClient) any {
		return &AspireList[any]{HandleWrapperBase: NewHandleWrapperBase(h, c)}
	})
	RegisterHandleWrapper("Aspire.Hosting/Dict<string,any>", func(h *Handle, c *AspireClient) any {
		return &AspireDict[any, any]{HandleWrapperBase: NewHandleWrapperBase(h, c)}
	})
	RegisterHandleWrapper("Aspire.Hosting/List<any>", func(h *Handle, c *AspireClient) any {
		return &AspireList[any]{HandleWrapperBase: NewHandleWrapperBase(h, c)}
	})
	RegisterHandleWrapper("Aspire.Hosting/Dict<string,string|Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpression>", func(h *Handle, c *AspireClient) any {
		return &AspireDict[any, any]{HandleWrapperBase: NewHandleWrapperBase(h, c)}
	})
	RegisterHandleWrapper("Aspire.Hosting/List<Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceUrlAnnotation>", func(h *Handle, c *AspireClient) any {
		return &AspireList[any]{HandleWrapperBase: NewHandleWrapperBase(h, c)}
	})
	RegisterHandleWrapper("Aspire.Hosting/Dict<string,string>", func(h *Handle, c *AspireClient) any {
		return &AspireDict[any, any]{HandleWrapperBase: NewHandleWrapperBase(h, c)}
	})
}

// ============================================================================
// Connection Helpers
// ============================================================================

// Connect establishes a connection to the AppHost server.
func Connect() (*AspireClient, error) {
	socketPath := os.Getenv("REMOTE_APP_HOST_SOCKET_PATH")
	if socketPath == "" {
		return nil, fmt.Errorf("REMOTE_APP_HOST_SOCKET_PATH environment variable not set. Run this application using `aspire run`")
	}
	client := NewAspireClient(socketPath)
	if err := client.Connect(); err != nil {
		return nil, err
	}
	client.OnDisconnect(func() { os.Exit(1) })
	return client, nil
}

// CreateBuilder creates a new distributed application builder.
func CreateBuilder(options *CreateBuilderOptions) (*IDistributedApplicationBuilder, error) {
	client, err := Connect()
	if err != nil {
		return nil, err
	}
	resolvedOptions := make(map[string]any)
	if options != nil {
		for k, v := range options.ToMap() {
			resolvedOptions[k] = v
		}
	}
	if _, ok := resolvedOptions["Args"]; !ok {
		resolvedOptions["Args"] = os.Args[1:]
	}
	if _, ok := resolvedOptions["ProjectDirectory"]; !ok {
		if pwd, err := os.Getwd(); err == nil {
			resolvedOptions["ProjectDirectory"] = pwd
		}
	}
	result, err := client.InvokeCapability("Aspire.Hosting/createBuilderWithOptions", map[string]any{"options": resolvedOptions})
	if err != nil {
		return nil, err
	}
	return result.(*IDistributedApplicationBuilder), nil
}

