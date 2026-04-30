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

// BrowserUserDataMode represents BrowserUserDataMode.
type BrowserUserDataMode string

const (
	BrowserUserDataModeShared BrowserUserDataMode = "Shared"
	BrowserUserDataModeIsolated BrowserUserDataMode = "Isolated"
)

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

// HttpCommandResultMode represents HttpCommandResultMode.
type HttpCommandResultMode string

const (
	HttpCommandResultModeNone HttpCommandResultMode = "None"
	HttpCommandResultModeAuto HttpCommandResultMode = "Auto"
	HttpCommandResultModeJson HttpCommandResultMode = "Json"
	HttpCommandResultModeText HttpCommandResultMode = "Text"
)

// CommandResultFormat represents CommandResultFormat.
type CommandResultFormat string

const (
	CommandResultFormatText CommandResultFormat = "Text"
	CommandResultFormatJson CommandResultFormat = "Json"
	CommandResultFormatMarkdown CommandResultFormat = "Markdown"
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

// AddContainerOptions represents AddContainerOptions.
type AddContainerOptions struct {
	Image string `json:"Image,omitempty"`
	Tag string `json:"Tag,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *AddContainerOptions) ToMap() map[string]any {
	return map[string]any{
		"Image": SerializeValue(d.Image),
		"Tag": SerializeValue(d.Tag),
	}
}

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

// HttpsCertificateInfo represents HttpsCertificateInfo.
type HttpsCertificateInfo struct {
	Subject string `json:"Subject,omitempty"`
	Issuer string `json:"Issuer,omitempty"`
	Thumbprint string `json:"Thumbprint,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *HttpsCertificateInfo) ToMap() map[string]any {
	return map[string]any{
		"Subject": SerializeValue(d.Subject),
		"Issuer": SerializeValue(d.Issuer),
		"Thumbprint": SerializeValue(d.Thumbprint),
	}
}

// CertificateTrustExecutionConfigurationExportData represents CertificateTrustExecutionConfigurationExportData.
type CertificateTrustExecutionConfigurationExportData struct {
	Scope CertificateTrustScope `json:"Scope,omitempty"`
	CertificateSubjects []string `json:"CertificateSubjects,omitempty"`
	CustomBundlePaths []string `json:"CustomBundlePaths,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *CertificateTrustExecutionConfigurationExportData) ToMap() map[string]any {
	return map[string]any{
		"Scope": SerializeValue(d.Scope),
		"CertificateSubjects": SerializeValue(d.CertificateSubjects),
		"CustomBundlePaths": SerializeValue(d.CustomBundlePaths),
	}
}

// HttpsCertificateExecutionConfigurationExportData represents HttpsCertificateExecutionConfigurationExportData.
type HttpsCertificateExecutionConfigurationExportData struct {
	Subject string `json:"Subject,omitempty"`
	Thumbprint string `json:"Thumbprint,omitempty"`
	KeyPathExpression string `json:"KeyPathExpression,omitempty"`
	PfxPathExpression string `json:"PfxPathExpression,omitempty"`
	IsKeyPathReferenced bool `json:"IsKeyPathReferenced,omitempty"`
	IsPfxPathReferenced bool `json:"IsPfxPathReferenced,omitempty"`
	Password string `json:"Password,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *HttpsCertificateExecutionConfigurationExportData) ToMap() map[string]any {
	return map[string]any{
		"Subject": SerializeValue(d.Subject),
		"Thumbprint": SerializeValue(d.Thumbprint),
		"KeyPathExpression": SerializeValue(d.KeyPathExpression),
		"PfxPathExpression": SerializeValue(d.PfxPathExpression),
		"IsKeyPathReferenced": SerializeValue(d.IsKeyPathReferenced),
		"IsPfxPathReferenced": SerializeValue(d.IsPfxPathReferenced),
		"Password": SerializeValue(d.Password),
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

// ReferenceEnvironmentInjectionOptions represents ReferenceEnvironmentInjectionOptions.
type ReferenceEnvironmentInjectionOptions struct {
	ConnectionString bool `json:"ConnectionString,omitempty"`
	ConnectionProperties bool `json:"ConnectionProperties,omitempty"`
	ServiceDiscovery bool `json:"ServiceDiscovery,omitempty"`
	Endpoints bool `json:"Endpoints,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *ReferenceEnvironmentInjectionOptions) ToMap() map[string]any {
	return map[string]any{
		"ConnectionString": SerializeValue(d.ConnectionString),
		"ConnectionProperties": SerializeValue(d.ConnectionProperties),
		"ServiceDiscovery": SerializeValue(d.ServiceDiscovery),
		"Endpoints": SerializeValue(d.Endpoints),
	}
}

// CertificateTrustExecutionConfigurationContext represents CertificateTrustExecutionConfigurationContext.
type CertificateTrustExecutionConfigurationContext struct {
	CertificateBundlePath *ReferenceExpression `json:"CertificateBundlePath,omitempty"`
	CertificateDirectoriesPath *ReferenceExpression `json:"CertificateDirectoriesPath,omitempty"`
	RootCertificatesPath string `json:"RootCertificatesPath,omitempty"`
	IsContainer bool `json:"IsContainer,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *CertificateTrustExecutionConfigurationContext) ToMap() map[string]any {
	return map[string]any{
		"CertificateBundlePath": SerializeValue(d.CertificateBundlePath),
		"CertificateDirectoriesPath": SerializeValue(d.CertificateDirectoriesPath),
		"RootCertificatesPath": SerializeValue(d.RootCertificatesPath),
		"IsContainer": SerializeValue(d.IsContainer),
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

// HttpCommandExportOptions represents HttpCommandExportOptions.
type HttpCommandExportOptions struct {
	Description string `json:"Description,omitempty"`
	ConfirmationMessage string `json:"ConfirmationMessage,omitempty"`
	IconName string `json:"IconName,omitempty"`
	IconVariant IconVariant `json:"IconVariant,omitempty"`
	IsHighlighted bool `json:"IsHighlighted,omitempty"`
	CommandName string `json:"CommandName,omitempty"`
	EndpointName string `json:"EndpointName,omitempty"`
	MethodName string `json:"MethodName,omitempty"`
	ResultMode HttpCommandResultMode `json:"ResultMode,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *HttpCommandExportOptions) ToMap() map[string]any {
	return map[string]any{
		"Description": SerializeValue(d.Description),
		"ConfirmationMessage": SerializeValue(d.ConfirmationMessage),
		"IconName": SerializeValue(d.IconName),
		"IconVariant": SerializeValue(d.IconVariant),
		"IsHighlighted": SerializeValue(d.IsHighlighted),
		"CommandName": SerializeValue(d.CommandName),
		"EndpointName": SerializeValue(d.EndpointName),
		"MethodName": SerializeValue(d.MethodName),
		"ResultMode": SerializeValue(d.ResultMode),
	}
}

// HttpsCertificateExecutionConfigurationContext represents HttpsCertificateExecutionConfigurationContext.
type HttpsCertificateExecutionConfigurationContext struct {
	CertificatePath *ReferenceExpression `json:"CertificatePath,omitempty"`
	KeyPath *ReferenceExpression `json:"KeyPath,omitempty"`
	PfxPath *ReferenceExpression `json:"PfxPath,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *HttpsCertificateExecutionConfigurationContext) ToMap() map[string]any {
	return map[string]any{
		"CertificatePath": SerializeValue(d.CertificatePath),
		"KeyPath": SerializeValue(d.KeyPath),
		"PfxPath": SerializeValue(d.PfxPath),
	}
}

// GenerateParameterDefault represents GenerateParameterDefault.
type GenerateParameterDefault struct {
	MinLength float64 `json:"MinLength,omitempty"`
	Lower bool `json:"Lower,omitempty"`
	Upper bool `json:"Upper,omitempty"`
	Numeric bool `json:"Numeric,omitempty"`
	Special bool `json:"Special,omitempty"`
	MinLower float64 `json:"MinLower,omitempty"`
	MinUpper float64 `json:"MinUpper,omitempty"`
	MinNumeric float64 `json:"MinNumeric,omitempty"`
	MinSpecial float64 `json:"MinSpecial,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *GenerateParameterDefault) ToMap() map[string]any {
	return map[string]any{
		"MinLength": SerializeValue(d.MinLength),
		"Lower": SerializeValue(d.Lower),
		"Upper": SerializeValue(d.Upper),
		"Numeric": SerializeValue(d.Numeric),
		"Special": SerializeValue(d.Special),
		"MinLower": SerializeValue(d.MinLower),
		"MinUpper": SerializeValue(d.MinUpper),
		"MinNumeric": SerializeValue(d.MinNumeric),
		"MinSpecial": SerializeValue(d.MinSpecial),
	}
}

// ExecuteCommandResult represents ExecuteCommandResult.
type ExecuteCommandResult struct {
	Success bool `json:"Success,omitempty"`
	Canceled bool `json:"Canceled,omitempty"`
	ErrorMessage string `json:"ErrorMessage,omitempty"`
	Message string `json:"Message,omitempty"`
	Data *CommandResultData `json:"Data,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *ExecuteCommandResult) ToMap() map[string]any {
	return map[string]any{
		"Success": SerializeValue(d.Success),
		"Canceled": SerializeValue(d.Canceled),
		"ErrorMessage": SerializeValue(d.ErrorMessage),
		"Message": SerializeValue(d.Message),
		"Data": SerializeValue(d.Data),
	}
}

// CommandResultData represents CommandResultData.
type CommandResultData struct {
	Value string `json:"Value,omitempty"`
	Format CommandResultFormat `json:"Format,omitempty"`
	DisplayImmediately bool `json:"DisplayImmediately,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *CommandResultData) ToMap() map[string]any {
	return map[string]any{
		"Value": SerializeValue(d.Value),
		"Format": SerializeValue(d.Format),
		"DisplayImmediately": SerializeValue(d.DisplayImmediately),
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
// Exported Values
// ============================================================================

var TestConfigs = struct {
	Default *TestConfigDto
	Profiles struct {
		Development *TestConfigDto
	}
	Secure *TestConfigDto
	UnicodeGreeting string
}{
	Default: &TestConfigDto{Name: "default", Port: 6379, Enabled: true, OptionalField: "cache"},
	Profiles: struct {
		Development *TestConfigDto
	}{
		Development: &TestConfigDto{Name: "development", Port: 5001, Enabled: false, OptionalField: nil},
	},
	Secure: &TestConfigDto{Name: "secure", Port: 6380, Enabled: true, OptionalField: nil},
	UnicodeGreeting: "你好こんにちは",
}

var WellKnownPipelineSteps = struct {
	Build string
	BuildPrereq string
	Deploy string
	DeployPrereq string
	Destroy string
	DestroyPrereq string
	Diagnostics string
	ProcessParameters string
	Publish string
	PublishPrereq string
	Push string
	PushPrereq string
	ValidateComputeEnvironments string
}{
	Build: "build",
	BuildPrereq: "build-prereq",
	Deploy: "deploy",
	DeployPrereq: "deploy-prereq",
	Destroy: "destroy",
	DestroyPrereq: "destroy-prereq",
	Diagnostics: "diagnostics",
	ProcessParameters: "process-parameters",
	Publish: "publish",
	PublishPrereq: "publish-prereq",
	Push: "push",
	PushPrereq: "push-prereq",
	ValidateComputeEnvironments: "validate-compute-environments",
}

var WellKnownPipelineTags = struct {
	BuildCompute string
	DeployCompute string
	ProvisionInfrastructure string
	PushContainerImage string
}{
	BuildCompute: "build-compute",
	DeployCompute: "deploy-compute",
	ProvisionInfrastructure: "provision-infra",
	PushContainerImage: "push-container-image",
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

// WithBrowserLogs adds a child browser logs resource that opens tracked browser sessions, captures browser logs, and captures screenshots.
func (s *CSharpAppResource) WithBrowserLogs(browser *string, profile *string, userDataMode *BrowserUserDataMode) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if browser != nil {
		reqArgs["browser"] = SerializeValue(browser)
	}
	if profile != nil {
		reqArgs["profile"] = SerializeValue(profile)
	}
	if userDataMode != nil {
		reqArgs["userDataMode"] = SerializeValue(userDataMode)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBrowserLogs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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
func (s *CSharpAppResource) WithOtlpExporter(protocol *OtlpProtocol) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if protocol != nil {
		reqArgs["protocol"] = SerializeValue(protocol)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporter", reqArgs)
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
func (s *CSharpAppResource) WithEnvironment(name string, value any) (*IResourceWithEnvironment, error) {
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

// WithReferenceEnvironment configures which reference values are injected into environment variables
func (s *CSharpAppResource) WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["options"] = SerializeValue(options)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceEnvironment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReference adds a reference to another resource
func (s *CSharpAppResource) WithReference(source any, connectionName *string, optional *bool, name *string) (*IResourceWithEnvironment, error) {
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

// WithEndpointCallback updates a named endpoint via callback
func (s *CSharpAppResource) WithEndpointCallback(endpointName string, callback func(...any) any, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpEndpointCallback updates an HTTP endpoint via callback
func (s *CSharpAppResource) WithHttpEndpointCallback(callback func(...any) any, name *string, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpsEndpointCallback updates an HTTPS endpoint via callback
func (s *CSharpAppResource) WithHttpsEndpointCallback(callback func(...any) any, name *string, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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

// WithUrls customizes displayed URLs via callback
func (s *CSharpAppResource) WithUrls(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrls", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *CSharpAppResource) WithUrl(url any, displayText *string) (*IResource, error) {
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

// PublishWithContainerFiles configures the resource to copy container files from the specified source during publishing
func (s *CSharpAppResource) PublishWithContainerFiles(source *IResourceWithContainerFiles, destinationPath string) (*IContainerFilesDestinationResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["source"] = SerializeValue(source)
	reqArgs["destinationPath"] = SerializeValue(destinationPath)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishWithContainerFilesFromResource", reqArgs)
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
func (s *CSharpAppResource) WaitFor(dependency *IResource, waitBehavior *WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if waitBehavior != nil {
		reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStart waits for another resource to start
func (s *CSharpAppResource) WaitForStart(dependency *IResource, waitBehavior *WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if waitBehavior != nil {
		reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStart", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForResourceCompletion", reqArgs)
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

// WithHttpCommand adds an HTTP resource command
func (s *CSharpAppResource) WithHttpCommand(path string, displayName string, options *HttpCommandExportOptions) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["displayName"] = SerializeValue(displayName)
	if options != nil {
		reqArgs["options"] = SerializeValue(options)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParameterHttpsDeveloperCertificate", reqArgs)
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

// WithRelationship adds a relationship to another resource
func (s *CSharpAppResource) WithRelationship(resourceBuilder *IResource, type_ string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["resourceBuilder"] = SerializeValue(resourceBuilder)
	reqArgs["type"] = SerializeValue(type_)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithParentRelationship sets the parent relationship
func (s *CSharpAppResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderParentRelationship", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderChildRelationship", reqArgs)
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

// WithImagePushOptions sets image push options via callback
func (s *CSharpAppResource) WithImagePushOptions(callback func(...any) any) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImagePushOptions", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
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

// CreateExecutionConfiguration creates an execution configuration builder
func (s *CSharpAppResource) CreateExecutionConfiguration() (*IExecutionConfigurationBuilder, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IExecutionConfigurationBuilder), nil
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

// WithUnionDependency adds a dependency from a string or another resource
func (s *CSharpAppResource) WithUnionDependency(dependency any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withUnionDependency", reqArgs)
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

// WithMergeLabel adds a label to the resource
func (s *CSharpAppResource) WithMergeLabel(label string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabel", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLabelCategorized adds a categorized label to the resource
func (s *CSharpAppResource) WithMergeLabelCategorized(label string, category string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	reqArgs["category"] = SerializeValue(category)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabelCategorized", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpoint configures a named endpoint
func (s *CSharpAppResource) WithMergeEndpoint(endpointName string, port float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpointScheme configures a named endpoint with scheme
func (s *CSharpAppResource) WithMergeEndpointScheme(endpointName string, port float64, scheme string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	reqArgs["scheme"] = SerializeValue(scheme)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpointScheme", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLogging configures resource logging
func (s *CSharpAppResource) WithMergeLogging(logLevel string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLogging", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLoggingPath configures resource logging with file path
func (s *CSharpAppResource) WithMergeLoggingPath(logLevel string, logPath string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	reqArgs["logPath"] = SerializeValue(logPath)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLoggingPath", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRoute configures a route
func (s *CSharpAppResource) WithMergeRoute(path string, method string, handler string, priority float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRoute", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRouteMiddleware configures a route with middleware
func (s *CSharpAppResource) WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	reqArgs["middleware"] = SerializeValue(middleware)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRouteMiddleware", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// CommandLineArgsCallbackContext wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.CommandLineArgsCallbackContext.
type CommandLineArgsCallbackContext struct {
	HandleWrapperBase
}

// NewCommandLineArgsCallbackContext creates a new CommandLineArgsCallbackContext.
func NewCommandLineArgsCallbackContext(handle *Handle, client *AspireClient) *CommandLineArgsCallbackContext {
	return &CommandLineArgsCallbackContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Args gets the command-line argument editor
func (s *CommandLineArgsCallbackContext) Args() (*CommandLineArgsEditor, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.args", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*CommandLineArgsEditor), nil
}

// Log gets the callback logger facade
func (s *CommandLineArgsCallbackContext) Log() (*LogFacade, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.log", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*LogFacade), nil
}

// Resource gets the resource associated with this callback
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

// ExecutionContext gets the execution context for this callback invocation
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

// CommandLineArgsEditor wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.CommandLineArgsEditor.
type CommandLineArgsEditor struct {
	HandleWrapperBase
}

// NewCommandLineArgsEditor creates a new CommandLineArgsEditor.
func NewCommandLineArgsEditor(handle *Handle, client *AspireClient) *CommandLineArgsEditor {
	return &CommandLineArgsEditor{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Add adds a command-line argument
func (s *CommandLineArgsEditor) Add(value any) error {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	_, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/add", reqArgs)
	return err
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

// ContainerImagePushOptions wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerImagePushOptions.
type ContainerImagePushOptions struct {
	HandleWrapperBase
}

// NewContainerImagePushOptions creates a new ContainerImagePushOptions.
func NewContainerImagePushOptions(handle *Handle, client *AspireClient) *ContainerImagePushOptions {
	return &ContainerImagePushOptions{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// RemoteImageName gets the RemoteImageName property
func (s *ContainerImagePushOptions) RemoteImageName() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ContainerImagePushOptions.remoteImageName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// SetRemoteImageName sets the RemoteImageName property
func (s *ContainerImagePushOptions) SetRemoteImageName(value string) (*ContainerImagePushOptions, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ContainerImagePushOptions.setRemoteImageName", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerImagePushOptions), nil
}

// RemoteImageTag gets the RemoteImageTag property
func (s *ContainerImagePushOptions) RemoteImageTag() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ContainerImagePushOptions.remoteImageTag", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// SetRemoteImageTag sets the RemoteImageTag property
func (s *ContainerImagePushOptions) SetRemoteImageTag(value string) (*ContainerImagePushOptions, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ContainerImagePushOptions.setRemoteImageTag", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerImagePushOptions), nil
}

// ContainerImagePushOptionsCallbackContext wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerImagePushOptionsCallbackContext.
type ContainerImagePushOptionsCallbackContext struct {
	HandleWrapperBase
}

// NewContainerImagePushOptionsCallbackContext creates a new ContainerImagePushOptionsCallbackContext.
func NewContainerImagePushOptionsCallbackContext(handle *Handle, client *AspireClient) *ContainerImagePushOptionsCallbackContext {
	return &ContainerImagePushOptionsCallbackContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Resource gets the Resource property
func (s *ContainerImagePushOptionsCallbackContext) Resource() (*IResource, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ContainerImagePushOptionsCallbackContext.resource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// SetResource sets the Resource property
func (s *ContainerImagePushOptionsCallbackContext) SetResource(value *IResource) (*ContainerImagePushOptionsCallbackContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ContainerImagePushOptionsCallbackContext.setResource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerImagePushOptionsCallbackContext), nil
}

// CancellationToken gets the CancellationToken property
func (s *ContainerImagePushOptionsCallbackContext) CancellationToken() (*CancellationToken, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ContainerImagePushOptionsCallbackContext.cancellationToken", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*CancellationToken), nil
}

// SetCancellationToken sets the CancellationToken property
func (s *ContainerImagePushOptionsCallbackContext) SetCancellationToken(value *CancellationToken) (*ContainerImagePushOptionsCallbackContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	if value != nil {
		reqArgs["value"] = RegisterCancellation(value, s.Client())
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ContainerImagePushOptionsCallbackContext.setCancellationToken", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerImagePushOptionsCallbackContext), nil
}

// Options gets the Options property
func (s *ContainerImagePushOptionsCallbackContext) Options() (*ContainerImagePushOptions, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ContainerImagePushOptionsCallbackContext.options", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerImagePushOptions), nil
}

// SetOptions sets the Options property
func (s *ContainerImagePushOptionsCallbackContext) SetOptions(value *ContainerImagePushOptions) (*ContainerImagePushOptionsCallbackContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ContainerImagePushOptionsCallbackContext.setOptions", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerImagePushOptionsCallbackContext), nil
}

// ContainerImageReference wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerImageReference.
type ContainerImageReference struct {
	HandleWrapperBase
}

// NewContainerImageReference creates a new ContainerImageReference.
func NewContainerImageReference(handle *Handle, client *AspireClient) *ContainerImageReference {
	return &ContainerImageReference{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// ContainerMountAnnotation wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerMountAnnotation.
type ContainerMountAnnotation struct {
	HandleWrapperBase
}

// NewContainerMountAnnotation creates a new ContainerMountAnnotation.
func NewContainerMountAnnotation(handle *Handle, client *AspireClient) *ContainerMountAnnotation {
	return &ContainerMountAnnotation{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// ContainerPortReference wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerPortReference.
type ContainerPortReference struct {
	HandleWrapperBase
}

// NewContainerPortReference creates a new ContainerPortReference.
func NewContainerPortReference(handle *Handle, client *AspireClient) *ContainerPortReference {
	return &ContainerPortReference{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
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

// WithUrls customizes displayed URLs via callback
func (s *ContainerRegistryResource) WithUrls(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrls", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *ContainerRegistryResource) WithUrl(url any, displayText *string) (*IResource, error) {
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

// WithRelationship adds a relationship to another resource
func (s *ContainerRegistryResource) WithRelationship(resourceBuilder *IResource, type_ string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["resourceBuilder"] = SerializeValue(resourceBuilder)
	reqArgs["type"] = SerializeValue(type_)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderRelationship", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderParentRelationship", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderChildRelationship", reqArgs)
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

// CreateExecutionConfiguration creates an execution configuration builder
func (s *ContainerRegistryResource) CreateExecutionConfiguration() (*IExecutionConfigurationBuilder, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IExecutionConfigurationBuilder), nil
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

// WithUnionDependency adds a dependency from a string or another resource
func (s *ContainerRegistryResource) WithUnionDependency(dependency any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withUnionDependency", reqArgs)
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

// WithMergeLabel adds a label to the resource
func (s *ContainerRegistryResource) WithMergeLabel(label string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabel", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLabelCategorized adds a categorized label to the resource
func (s *ContainerRegistryResource) WithMergeLabelCategorized(label string, category string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	reqArgs["category"] = SerializeValue(category)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabelCategorized", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpoint configures a named endpoint
func (s *ContainerRegistryResource) WithMergeEndpoint(endpointName string, port float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpointScheme configures a named endpoint with scheme
func (s *ContainerRegistryResource) WithMergeEndpointScheme(endpointName string, port float64, scheme string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	reqArgs["scheme"] = SerializeValue(scheme)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpointScheme", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLogging configures resource logging
func (s *ContainerRegistryResource) WithMergeLogging(logLevel string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLogging", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLoggingPath configures resource logging with file path
func (s *ContainerRegistryResource) WithMergeLoggingPath(logLevel string, logPath string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	reqArgs["logPath"] = SerializeValue(logPath)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLoggingPath", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRoute configures a route
func (s *ContainerRegistryResource) WithMergeRoute(path string, method string, handler string, priority float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRoute", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRouteMiddleware configures a route with middleware
func (s *ContainerRegistryResource) WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	reqArgs["middleware"] = SerializeValue(middleware)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRouteMiddleware", reqArgs)
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

// WithBrowserLogs adds a child browser logs resource that opens tracked browser sessions, captures browser logs, and captures screenshots.
func (s *ContainerResource) WithBrowserLogs(browser *string, profile *string, userDataMode *BrowserUserDataMode) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if browser != nil {
		reqArgs["browser"] = SerializeValue(browser)
	}
	if profile != nil {
		reqArgs["profile"] = SerializeValue(profile)
	}
	if userDataMode != nil {
		reqArgs["userDataMode"] = SerializeValue(userDataMode)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBrowserLogs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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

// WithBuildArg adds a build argument from a string value or parameter resource
func (s *ContainerResource) WithBuildArg(name string, value any) (*ContainerResource, error) {
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParameterBuildSecret", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithContainerCertificatePaths overrides container certificate bundle and directory paths used for trust configuration
func (s *ContainerResource) WithContainerCertificatePaths(customCertificatesDestination *string, defaultCertificateBundlePaths []string, defaultCertificateDirectoryPaths []string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if customCertificatesDestination != nil {
		reqArgs["customCertificatesDestination"] = SerializeValue(customCertificatesDestination)
	}
	if defaultCertificateBundlePaths != nil {
		reqArgs["defaultCertificateBundlePaths"] = SerializeValue(defaultCertificateBundlePaths)
	}
	if defaultCertificateDirectoryPaths != nil {
		reqArgs["defaultCertificateDirectoryPaths"] = SerializeValue(defaultCertificateDirectoryPaths)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerCertificatePaths", reqArgs)
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

// WithDockerfileBuilder configures the resource to use a programmatically generated Dockerfile
func (s *ContainerResource) WithDockerfileBuilder(contextPath string, callback func(...any) any, stage *string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["contextPath"] = SerializeValue(contextPath)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if stage != nil {
		reqArgs["stage"] = SerializeValue(stage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfileBuilder", reqArgs)
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
func (s *ContainerResource) WithOtlpExporter(protocol *OtlpProtocol) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if protocol != nil {
		reqArgs["protocol"] = SerializeValue(protocol)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporter", reqArgs)
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
func (s *ContainerResource) WithEnvironment(name string, value any) (*IResourceWithEnvironment, error) {
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

// WithReferenceEnvironment configures which reference values are injected into environment variables
func (s *ContainerResource) WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["options"] = SerializeValue(options)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceEnvironment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReference adds a reference to another resource
func (s *ContainerResource) WithReference(source any, connectionName *string, optional *bool, name *string) (*IResourceWithEnvironment, error) {
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

// WithEndpointCallback updates a named endpoint via callback
func (s *ContainerResource) WithEndpointCallback(endpointName string, callback func(...any) any, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpEndpointCallback updates an HTTP endpoint via callback
func (s *ContainerResource) WithHttpEndpointCallback(callback func(...any) any, name *string, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpsEndpointCallback updates an HTTPS endpoint via callback
func (s *ContainerResource) WithHttpsEndpointCallback(callback func(...any) any, name *string, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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

// WithUrls customizes displayed URLs via callback
func (s *ContainerResource) WithUrls(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrls", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *ContainerResource) WithUrl(url any, displayText *string) (*IResource, error) {
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
func (s *ContainerResource) WaitFor(dependency *IResource, waitBehavior *WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if waitBehavior != nil {
		reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStart waits for another resource to start
func (s *ContainerResource) WaitForStart(dependency *IResource, waitBehavior *WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if waitBehavior != nil {
		reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStart", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForResourceCompletion", reqArgs)
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

// WithHttpCommand adds an HTTP resource command
func (s *ContainerResource) WithHttpCommand(path string, displayName string, options *HttpCommandExportOptions) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["displayName"] = SerializeValue(displayName)
	if options != nil {
		reqArgs["options"] = SerializeValue(options)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParameterHttpsDeveloperCertificate", reqArgs)
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

// WithRelationship adds a relationship to another resource
func (s *ContainerResource) WithRelationship(resourceBuilder *IResource, type_ string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["resourceBuilder"] = SerializeValue(resourceBuilder)
	reqArgs["type"] = SerializeValue(type_)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithParentRelationship sets the parent relationship
func (s *ContainerResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderParentRelationship", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderChildRelationship", reqArgs)
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

// WithImagePushOptions sets image push options via callback
func (s *ContainerResource) WithImagePushOptions(callback func(...any) any) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImagePushOptions", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
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

// CreateExecutionConfiguration creates an execution configuration builder
func (s *ContainerResource) CreateExecutionConfiguration() (*IExecutionConfigurationBuilder, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IExecutionConfigurationBuilder), nil
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

// WithUnionDependency adds a dependency from a string or another resource
func (s *ContainerResource) WithUnionDependency(dependency any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withUnionDependency", reqArgs)
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

// WithMergeLabel adds a label to the resource
func (s *ContainerResource) WithMergeLabel(label string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabel", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLabelCategorized adds a categorized label to the resource
func (s *ContainerResource) WithMergeLabelCategorized(label string, category string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	reqArgs["category"] = SerializeValue(category)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabelCategorized", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpoint configures a named endpoint
func (s *ContainerResource) WithMergeEndpoint(endpointName string, port float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpointScheme configures a named endpoint with scheme
func (s *ContainerResource) WithMergeEndpointScheme(endpointName string, port float64, scheme string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	reqArgs["scheme"] = SerializeValue(scheme)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpointScheme", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLogging configures resource logging
func (s *ContainerResource) WithMergeLogging(logLevel string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLogging", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLoggingPath configures resource logging with file path
func (s *ContainerResource) WithMergeLoggingPath(logLevel string, logPath string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	reqArgs["logPath"] = SerializeValue(logPath)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLoggingPath", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRoute configures a route
func (s *ContainerResource) WithMergeRoute(path string, method string, handler string, priority float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRoute", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRouteMiddleware configures a route with middleware
func (s *ContainerResource) WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	reqArgs["middleware"] = SerializeValue(middleware)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRouteMiddleware", reqArgs)
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

// DockerfileBuilder wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.Docker.DockerfileBuilder.
type DockerfileBuilder struct {
	HandleWrapperBase
}

// NewDockerfileBuilder creates a new DockerfileBuilder.
func NewDockerfileBuilder(handle *Handle, client *AspireClient) *DockerfileBuilder {
	return &DockerfileBuilder{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Arg adds a global ARG statement to the Dockerfile
func (s *DockerfileBuilder) Arg(name string, defaultValue *string) (*DockerfileBuilder, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	if defaultValue != nil {
		reqArgs["defaultValue"] = SerializeValue(defaultValue)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/dockerfileBuilderArg", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DockerfileBuilder), nil
}

// From adds a FROM statement to start a Dockerfile stage
func (s *DockerfileBuilder) From(image string, stageName *string) (*DockerfileStage, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["image"] = SerializeValue(image)
	if stageName != nil {
		reqArgs["stageName"] = SerializeValue(stageName)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/dockerfileBuilderFrom", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DockerfileStage), nil
}

// AddContainerFilesStages adds Dockerfile stages for published container files
func (s *DockerfileBuilder) AddContainerFilesStages(resource *IResource, logger *ILogger) (*DockerfileBuilder, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["resource"] = SerializeValue(resource)
	if logger != nil {
		reqArgs["logger"] = SerializeValue(logger)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/dockerfileBuilderAddContainerFilesStages", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DockerfileBuilder), nil
}

// DockerfileBuilderCallbackContext wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.DockerfileBuilderCallbackContext.
type DockerfileBuilderCallbackContext struct {
	HandleWrapperBase
}

// NewDockerfileBuilderCallbackContext creates a new DockerfileBuilderCallbackContext.
func NewDockerfileBuilderCallbackContext(handle *Handle, client *AspireClient) *DockerfileBuilderCallbackContext {
	return &DockerfileBuilderCallbackContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Resource gets the Resource property
func (s *DockerfileBuilderCallbackContext) Resource() (*IResource, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/DockerfileBuilderCallbackContext.resource", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// Builder gets the Builder property
func (s *DockerfileBuilderCallbackContext) Builder() (*DockerfileBuilder, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/DockerfileBuilderCallbackContext.builder", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DockerfileBuilder), nil
}

// Services gets the Services property
func (s *DockerfileBuilderCallbackContext) Services() (*IServiceProvider, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/DockerfileBuilderCallbackContext.services", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IServiceProvider), nil
}

// CancellationToken gets the CancellationToken property
func (s *DockerfileBuilderCallbackContext) CancellationToken() (*CancellationToken, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/DockerfileBuilderCallbackContext.cancellationToken", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*CancellationToken), nil
}

// DockerfileStage wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.Docker.DockerfileStage.
type DockerfileStage struct {
	HandleWrapperBase
}

// NewDockerfileStage creates a new DockerfileStage.
func NewDockerfileStage(handle *Handle, client *AspireClient) *DockerfileStage {
	return &DockerfileStage{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Arg adds an ARG statement to a Dockerfile stage
func (s *DockerfileStage) Arg(name string, defaultValue *string) (*DockerfileStage, error) {
	reqArgs := map[string]any{
		"stage": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	if defaultValue != nil {
		reqArgs["defaultValue"] = SerializeValue(defaultValue)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/dockerfileStageArg", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DockerfileStage), nil
}

// WorkDir adds a WORKDIR statement to a Dockerfile stage
func (s *DockerfileStage) WorkDir(path string) (*DockerfileStage, error) {
	reqArgs := map[string]any{
		"stage": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/workDir", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DockerfileStage), nil
}

// Run adds a RUN statement to a Dockerfile stage
func (s *DockerfileStage) Run(command string) (*DockerfileStage, error) {
	reqArgs := map[string]any{
		"stage": SerializeValue(s.Handle()),
	}
	reqArgs["command"] = SerializeValue(command)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/dockerfileStageRun", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DockerfileStage), nil
}

// Copy adds a COPY statement to a Dockerfile stage
func (s *DockerfileStage) Copy(source string, destination string, chown *string) (*DockerfileStage, error) {
	reqArgs := map[string]any{
		"stage": SerializeValue(s.Handle()),
	}
	reqArgs["source"] = SerializeValue(source)
	reqArgs["destination"] = SerializeValue(destination)
	if chown != nil {
		reqArgs["chown"] = SerializeValue(chown)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/dockerfileStageCopy", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DockerfileStage), nil
}

// CopyFrom adds a COPY --from statement to a Dockerfile stage
func (s *DockerfileStage) CopyFrom(from string, source string, destination string, chown *string) (*DockerfileStage, error) {
	reqArgs := map[string]any{
		"stage": SerializeValue(s.Handle()),
	}
	reqArgs["from"] = SerializeValue(from)
	reqArgs["source"] = SerializeValue(source)
	reqArgs["destination"] = SerializeValue(destination)
	if chown != nil {
		reqArgs["chown"] = SerializeValue(chown)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/dockerfileStageCopyFrom", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DockerfileStage), nil
}

// Env adds an ENV statement to a Dockerfile stage
func (s *DockerfileStage) Env(name string, value string) (*DockerfileStage, error) {
	reqArgs := map[string]any{
		"stage": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/env", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DockerfileStage), nil
}

// Expose adds an EXPOSE statement to a Dockerfile stage
func (s *DockerfileStage) Expose(port float64) (*DockerfileStage, error) {
	reqArgs := map[string]any{
		"stage": SerializeValue(s.Handle()),
	}
	reqArgs["port"] = SerializeValue(port)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/expose", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DockerfileStage), nil
}

// Cmd adds a CMD statement to a Dockerfile stage
func (s *DockerfileStage) Cmd(command []string) (*DockerfileStage, error) {
	reqArgs := map[string]any{
		"stage": SerializeValue(s.Handle()),
	}
	reqArgs["command"] = SerializeValue(command)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/cmd", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DockerfileStage), nil
}

// Entrypoint adds an ENTRYPOINT statement to a Dockerfile stage
func (s *DockerfileStage) Entrypoint(command []string) (*DockerfileStage, error) {
	reqArgs := map[string]any{
		"stage": SerializeValue(s.Handle()),
	}
	reqArgs["command"] = SerializeValue(command)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/entrypoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DockerfileStage), nil
}

// RunWithMounts adds a RUN statement with mounts to a Dockerfile stage
func (s *DockerfileStage) RunWithMounts(command string, mounts []string) (*DockerfileStage, error) {
	reqArgs := map[string]any{
		"stage": SerializeValue(s.Handle()),
	}
	reqArgs["command"] = SerializeValue(command)
	reqArgs["mounts"] = SerializeValue(mounts)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/runWithMounts", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DockerfileStage), nil
}

// User adds a USER statement to a Dockerfile stage
func (s *DockerfileStage) User(user string) (*DockerfileStage, error) {
	reqArgs := map[string]any{
		"stage": SerializeValue(s.Handle()),
	}
	reqArgs["user"] = SerializeValue(user)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/user", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DockerfileStage), nil
}

// EmptyLine adds an empty line to a Dockerfile stage
func (s *DockerfileStage) EmptyLine() (*DockerfileStage, error) {
	reqArgs := map[string]any{
		"stage": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/emptyLine", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DockerfileStage), nil
}

// Comment adds a comment to a Dockerfile stage
func (s *DockerfileStage) Comment(comment string) (*DockerfileStage, error) {
	reqArgs := map[string]any{
		"stage": SerializeValue(s.Handle()),
	}
	reqArgs["comment"] = SerializeValue(comment)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/comment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DockerfileStage), nil
}

// AddContainerFiles adds COPY --from statements for published container files
func (s *DockerfileStage) AddContainerFiles(resource *IResource, rootDestinationPath string, logger *ILogger) (*DockerfileStage, error) {
	reqArgs := map[string]any{
		"stage": SerializeValue(s.Handle()),
	}
	reqArgs["resource"] = SerializeValue(resource)
	reqArgs["rootDestinationPath"] = SerializeValue(rootDestinationPath)
	if logger != nil {
		reqArgs["logger"] = SerializeValue(logger)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/dockerfileStageAddContainerFiles", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DockerfileStage), nil
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

// WithBrowserLogs adds a child browser logs resource that opens tracked browser sessions, captures browser logs, and captures screenshots.
func (s *DotnetToolResource) WithBrowserLogs(browser *string, profile *string, userDataMode *BrowserUserDataMode) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if browser != nil {
		reqArgs["browser"] = SerializeValue(browser)
	}
	if profile != nil {
		reqArgs["profile"] = SerializeValue(profile)
	}
	if userDataMode != nil {
		reqArgs["userDataMode"] = SerializeValue(userDataMode)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBrowserLogs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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

// PublishAsDockerFile publishes an executable as a Docker file
func (s *DotnetToolResource) PublishAsDockerFile(configure func(...any) any) (*ExecutableResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if configure != nil {
		reqArgs["configure"] = RegisterCallback(configure)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishAsDockerFile", reqArgs)
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
func (s *DotnetToolResource) WithOtlpExporter(protocol *OtlpProtocol) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if protocol != nil {
		reqArgs["protocol"] = SerializeValue(protocol)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporter", reqArgs)
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
func (s *DotnetToolResource) WithEnvironment(name string, value any) (*IResourceWithEnvironment, error) {
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

// WithReferenceEnvironment configures which reference values are injected into environment variables
func (s *DotnetToolResource) WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["options"] = SerializeValue(options)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceEnvironment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReference adds a reference to another resource
func (s *DotnetToolResource) WithReference(source any, connectionName *string, optional *bool, name *string) (*IResourceWithEnvironment, error) {
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

// WithEndpointCallback updates a named endpoint via callback
func (s *DotnetToolResource) WithEndpointCallback(endpointName string, callback func(...any) any, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpEndpointCallback updates an HTTP endpoint via callback
func (s *DotnetToolResource) WithHttpEndpointCallback(callback func(...any) any, name *string, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpsEndpointCallback updates an HTTPS endpoint via callback
func (s *DotnetToolResource) WithHttpsEndpointCallback(callback func(...any) any, name *string, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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

// WithUrls customizes displayed URLs via callback
func (s *DotnetToolResource) WithUrls(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrls", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *DotnetToolResource) WithUrl(url any, displayText *string) (*IResource, error) {
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
func (s *DotnetToolResource) WaitFor(dependency *IResource, waitBehavior *WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if waitBehavior != nil {
		reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStart waits for another resource to start
func (s *DotnetToolResource) WaitForStart(dependency *IResource, waitBehavior *WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if waitBehavior != nil {
		reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStart", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForResourceCompletion", reqArgs)
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

// WithHttpCommand adds an HTTP resource command
func (s *DotnetToolResource) WithHttpCommand(path string, displayName string, options *HttpCommandExportOptions) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["displayName"] = SerializeValue(displayName)
	if options != nil {
		reqArgs["options"] = SerializeValue(options)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParameterHttpsDeveloperCertificate", reqArgs)
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

// WithRelationship adds a relationship to another resource
func (s *DotnetToolResource) WithRelationship(resourceBuilder *IResource, type_ string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["resourceBuilder"] = SerializeValue(resourceBuilder)
	reqArgs["type"] = SerializeValue(type_)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithParentRelationship sets the parent relationship
func (s *DotnetToolResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderParentRelationship", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderChildRelationship", reqArgs)
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

// WithImagePushOptions sets image push options via callback
func (s *DotnetToolResource) WithImagePushOptions(callback func(...any) any) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImagePushOptions", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
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

// CreateExecutionConfiguration creates an execution configuration builder
func (s *DotnetToolResource) CreateExecutionConfiguration() (*IExecutionConfigurationBuilder, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IExecutionConfigurationBuilder), nil
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

// WithUnionDependency adds a dependency from a string or another resource
func (s *DotnetToolResource) WithUnionDependency(dependency any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withUnionDependency", reqArgs)
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

// WithMergeLabel adds a label to the resource
func (s *DotnetToolResource) WithMergeLabel(label string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabel", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLabelCategorized adds a categorized label to the resource
func (s *DotnetToolResource) WithMergeLabelCategorized(label string, category string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	reqArgs["category"] = SerializeValue(category)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabelCategorized", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpoint configures a named endpoint
func (s *DotnetToolResource) WithMergeEndpoint(endpointName string, port float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpointScheme configures a named endpoint with scheme
func (s *DotnetToolResource) WithMergeEndpointScheme(endpointName string, port float64, scheme string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	reqArgs["scheme"] = SerializeValue(scheme)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpointScheme", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLogging configures resource logging
func (s *DotnetToolResource) WithMergeLogging(logLevel string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLogging", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLoggingPath configures resource logging with file path
func (s *DotnetToolResource) WithMergeLoggingPath(logLevel string, logPath string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	reqArgs["logPath"] = SerializeValue(logPath)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLoggingPath", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRoute configures a route
func (s *DotnetToolResource) WithMergeRoute(path string, method string, handler string, priority float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRoute", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRouteMiddleware configures a route with middleware
func (s *DotnetToolResource) WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	reqArgs["middleware"] = SerializeValue(middleware)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRouteMiddleware", reqArgs)
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

// IsHttpSchemeNamedEndpoint gets the IsHttpSchemeNamedEndpoint property
func (s *EndpointReference) IsHttpSchemeNamedEndpoint() (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReference.isHttpSchemeNamedEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// ExcludeReferenceEndpoint gets the ExcludeReferenceEndpoint property
func (s *EndpointReference) ExcludeReferenceEndpoint() (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReference.excludeReferenceEndpoint", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReference.getValueAsync", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// Property gets the specified property expression of the endpoint
func (s *EndpointReference) Property(property EndpointProperty) (*EndpointReferenceExpression, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["property"] = SerializeValue(property)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointReference.property", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointReferenceExpression), nil
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

// EndpointUpdateContext wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointUpdateContext.
type EndpointUpdateContext struct {
	HandleWrapperBase
}

// NewEndpointUpdateContext creates a new EndpointUpdateContext.
func NewEndpointUpdateContext(handle *Handle, client *AspireClient) *EndpointUpdateContext {
	return &EndpointUpdateContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Name gets the Name property
func (s *EndpointUpdateContext) Name() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.name", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// Protocol gets the Protocol property
func (s *EndpointUpdateContext) Protocol() (*ProtocolType, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.protocol", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ProtocolType), nil
}

// SetProtocol sets the Protocol property
func (s *EndpointUpdateContext) SetProtocol(value ProtocolType) (*EndpointUpdateContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setProtocol", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointUpdateContext), nil
}

// Port gets the Port property
func (s *EndpointUpdateContext) Port() (*float64, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.port", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*float64), nil
}

// SetPort sets the Port property
func (s *EndpointUpdateContext) SetPort(value float64) (*EndpointUpdateContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setPort", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointUpdateContext), nil
}

// TargetPort gets the TargetPort property
func (s *EndpointUpdateContext) TargetPort() (*float64, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.targetPort", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*float64), nil
}

// SetTargetPort sets the TargetPort property
func (s *EndpointUpdateContext) SetTargetPort(value float64) (*EndpointUpdateContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setTargetPort", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointUpdateContext), nil
}

// UriScheme gets the UriScheme property
func (s *EndpointUpdateContext) UriScheme() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.uriScheme", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// SetUriScheme sets the UriScheme property
func (s *EndpointUpdateContext) SetUriScheme(value string) (*EndpointUpdateContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setUriScheme", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointUpdateContext), nil
}

// TargetHost gets the TargetHost property
func (s *EndpointUpdateContext) TargetHost() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.targetHost", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// SetTargetHost sets the TargetHost property
func (s *EndpointUpdateContext) SetTargetHost(value string) (*EndpointUpdateContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setTargetHost", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointUpdateContext), nil
}

// Transport gets the Transport property
func (s *EndpointUpdateContext) Transport() (*string, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.transport", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
}

// SetTransport sets the Transport property
func (s *EndpointUpdateContext) SetTransport(value string) (*EndpointUpdateContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setTransport", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointUpdateContext), nil
}

// IsExternal gets the IsExternal property
func (s *EndpointUpdateContext) IsExternal() (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.isExternal", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// SetIsExternal sets the IsExternal property
func (s *EndpointUpdateContext) SetIsExternal(value bool) (*EndpointUpdateContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setIsExternal", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointUpdateContext), nil
}

// IsProxied gets the IsProxied property
func (s *EndpointUpdateContext) IsProxied() (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.isProxied", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// SetIsProxied sets the IsProxied property
func (s *EndpointUpdateContext) SetIsProxied(value bool) (*EndpointUpdateContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setIsProxied", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointUpdateContext), nil
}

// ExcludeReferenceEndpoint gets the ExcludeReferenceEndpoint property
func (s *EndpointUpdateContext) ExcludeReferenceEndpoint() (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.excludeReferenceEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// SetExcludeReferenceEndpoint sets the ExcludeReferenceEndpoint property
func (s *EndpointUpdateContext) SetExcludeReferenceEndpoint(value bool) (*EndpointUpdateContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setExcludeReferenceEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointUpdateContext), nil
}

// TlsEnabled gets the TlsEnabled property
func (s *EndpointUpdateContext) TlsEnabled() (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.tlsEnabled", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*bool), nil
}

// SetTlsEnabled sets the TlsEnabled property
func (s *EndpointUpdateContext) SetTlsEnabled(value bool) (*EndpointUpdateContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setTlsEnabled", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointUpdateContext), nil
}

// EnvironmentCallbackContext wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.EnvironmentCallbackContext.
type EnvironmentCallbackContext struct {
	HandleWrapperBase
}

// NewEnvironmentCallbackContext creates a new EnvironmentCallbackContext.
func NewEnvironmentCallbackContext(handle *Handle, client *AspireClient) *EnvironmentCallbackContext {
	return &EnvironmentCallbackContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Environment gets the environment variable editor
func (s *EnvironmentCallbackContext) Environment() (*EnvironmentEditor, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.environment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EnvironmentEditor), nil
}

// Log gets the callback logger facade
func (s *EnvironmentCallbackContext) Log() (*LogFacade, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.log", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*LogFacade), nil
}

// Resource gets the resource associated with this callback
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

// ExecutionContext gets the execution context for this callback invocation
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

// EnvironmentEditor wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.EnvironmentEditor.
type EnvironmentEditor struct {
	HandleWrapperBase
}

// NewEnvironmentEditor creates a new EnvironmentEditor.
func NewEnvironmentEditor(handle *Handle, client *AspireClient) *EnvironmentEditor {
	return &EnvironmentEditor{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Set sets an environment variable
func (s *EnvironmentEditor) Set(name string, value any) error {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	_, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/set", reqArgs)
	return err
}

// EventingSubscriberRegistrationContext wraps a handle for Aspire.Hosting/Aspire.Hosting.Ats.EventingSubscriberRegistrationContext.
type EventingSubscriberRegistrationContext struct {
	HandleWrapperBase
}

// NewEventingSubscriberRegistrationContext creates a new EventingSubscriberRegistrationContext.
func NewEventingSubscriberRegistrationContext(handle *Handle, client *AspireClient) *EventingSubscriberRegistrationContext {
	return &EventingSubscriberRegistrationContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// OnBeforeStart subscribes an eventing subscriber to the BeforeStart event
func (s *EventingSubscriberRegistrationContext) OnBeforeStart(callback func(...any) any) (*DistributedApplicationEventSubscription, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/eventingSubscriberOnBeforeStart", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationEventSubscription), nil
}

// OnAfterResourcesCreated subscribes an eventing subscriber to the AfterResourcesCreated event
func (s *EventingSubscriberRegistrationContext) OnAfterResourcesCreated(callback func(...any) any) (*DistributedApplicationEventSubscription, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/eventingSubscriberOnAfterResourcesCreated", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationEventSubscription), nil
}

// ExecutionContext gets the ExecutionContext property
func (s *EventingSubscriberRegistrationContext) ExecutionContext() (*DistributedApplicationExecutionContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Ats/EventingSubscriberRegistrationContext.executionContext", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationExecutionContext), nil
}

// CancellationToken gets the CancellationToken property
func (s *EventingSubscriberRegistrationContext) CancellationToken() (*CancellationToken, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Ats/EventingSubscriberRegistrationContext.cancellationToken", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*CancellationToken), nil
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

// WithBrowserLogs adds a child browser logs resource that opens tracked browser sessions, captures browser logs, and captures screenshots.
func (s *ExecutableResource) WithBrowserLogs(browser *string, profile *string, userDataMode *BrowserUserDataMode) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if browser != nil {
		reqArgs["browser"] = SerializeValue(browser)
	}
	if profile != nil {
		reqArgs["profile"] = SerializeValue(profile)
	}
	if userDataMode != nil {
		reqArgs["userDataMode"] = SerializeValue(userDataMode)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBrowserLogs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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

// PublishAsDockerFile publishes an executable as a Docker file
func (s *ExecutableResource) PublishAsDockerFile(configure func(...any) any) (*ExecutableResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if configure != nil {
		reqArgs["configure"] = RegisterCallback(configure)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishAsDockerFile", reqArgs)
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
func (s *ExecutableResource) WithOtlpExporter(protocol *OtlpProtocol) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if protocol != nil {
		reqArgs["protocol"] = SerializeValue(protocol)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporter", reqArgs)
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
func (s *ExecutableResource) WithEnvironment(name string, value any) (*IResourceWithEnvironment, error) {
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

// WithReferenceEnvironment configures which reference values are injected into environment variables
func (s *ExecutableResource) WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["options"] = SerializeValue(options)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceEnvironment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReference adds a reference to another resource
func (s *ExecutableResource) WithReference(source any, connectionName *string, optional *bool, name *string) (*IResourceWithEnvironment, error) {
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

// WithEndpointCallback updates a named endpoint via callback
func (s *ExecutableResource) WithEndpointCallback(endpointName string, callback func(...any) any, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpEndpointCallback updates an HTTP endpoint via callback
func (s *ExecutableResource) WithHttpEndpointCallback(callback func(...any) any, name *string, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpsEndpointCallback updates an HTTPS endpoint via callback
func (s *ExecutableResource) WithHttpsEndpointCallback(callback func(...any) any, name *string, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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

// WithUrls customizes displayed URLs via callback
func (s *ExecutableResource) WithUrls(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrls", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *ExecutableResource) WithUrl(url any, displayText *string) (*IResource, error) {
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
func (s *ExecutableResource) WaitFor(dependency *IResource, waitBehavior *WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if waitBehavior != nil {
		reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStart waits for another resource to start
func (s *ExecutableResource) WaitForStart(dependency *IResource, waitBehavior *WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if waitBehavior != nil {
		reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStart", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForResourceCompletion", reqArgs)
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

// WithHttpCommand adds an HTTP resource command
func (s *ExecutableResource) WithHttpCommand(path string, displayName string, options *HttpCommandExportOptions) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["displayName"] = SerializeValue(displayName)
	if options != nil {
		reqArgs["options"] = SerializeValue(options)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParameterHttpsDeveloperCertificate", reqArgs)
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

// WithRelationship adds a relationship to another resource
func (s *ExecutableResource) WithRelationship(resourceBuilder *IResource, type_ string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["resourceBuilder"] = SerializeValue(resourceBuilder)
	reqArgs["type"] = SerializeValue(type_)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithParentRelationship sets the parent relationship
func (s *ExecutableResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderParentRelationship", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderChildRelationship", reqArgs)
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

// WithImagePushOptions sets image push options via callback
func (s *ExecutableResource) WithImagePushOptions(callback func(...any) any) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImagePushOptions", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
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

// CreateExecutionConfiguration creates an execution configuration builder
func (s *ExecutableResource) CreateExecutionConfiguration() (*IExecutionConfigurationBuilder, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IExecutionConfigurationBuilder), nil
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

// WithUnionDependency adds a dependency from a string or another resource
func (s *ExecutableResource) WithUnionDependency(dependency any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withUnionDependency", reqArgs)
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

// WithMergeLabel adds a label to the resource
func (s *ExecutableResource) WithMergeLabel(label string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabel", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLabelCategorized adds a categorized label to the resource
func (s *ExecutableResource) WithMergeLabelCategorized(label string, category string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	reqArgs["category"] = SerializeValue(category)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabelCategorized", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpoint configures a named endpoint
func (s *ExecutableResource) WithMergeEndpoint(endpointName string, port float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpointScheme configures a named endpoint with scheme
func (s *ExecutableResource) WithMergeEndpointScheme(endpointName string, port float64, scheme string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	reqArgs["scheme"] = SerializeValue(scheme)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpointScheme", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLogging configures resource logging
func (s *ExecutableResource) WithMergeLogging(logLevel string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLogging", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLoggingPath configures resource logging with file path
func (s *ExecutableResource) WithMergeLoggingPath(logLevel string, logPath string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	reqArgs["logPath"] = SerializeValue(logPath)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLoggingPath", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRoute configures a route
func (s *ExecutableResource) WithMergeRoute(path string, method string, handler string, priority float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRoute", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRouteMiddleware configures a route with middleware
func (s *ExecutableResource) WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	reqArgs["middleware"] = SerializeValue(middleware)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRouteMiddleware", reqArgs)
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

// Logger gets the Logger property
func (s *ExecuteCommandContext) Logger() (*ILogger, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.logger", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ILogger), nil
}

// SetLogger sets the Logger property
func (s *ExecuteCommandContext) SetLogger(value *ILogger) (*ExecuteCommandContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.setLogger", reqArgs)
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

// WithHttpHealthCheck adds an HTTP health check to the external service
func (s *ExternalServiceResource) WithHttpHealthCheck(path *string, statusCode *float64, endpointName *string) (*ExternalServiceResource, error) {
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

// WithUrls customizes displayed URLs via callback
func (s *ExternalServiceResource) WithUrls(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrls", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *ExternalServiceResource) WithUrl(url any, displayText *string) (*IResource, error) {
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

// WithRelationship adds a relationship to another resource
func (s *ExternalServiceResource) WithRelationship(resourceBuilder *IResource, type_ string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["resourceBuilder"] = SerializeValue(resourceBuilder)
	reqArgs["type"] = SerializeValue(type_)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderRelationship", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderParentRelationship", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderChildRelationship", reqArgs)
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

// CreateExecutionConfiguration creates an execution configuration builder
func (s *ExternalServiceResource) CreateExecutionConfiguration() (*IExecutionConfigurationBuilder, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IExecutionConfigurationBuilder), nil
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

// WithUnionDependency adds a dependency from a string or another resource
func (s *ExternalServiceResource) WithUnionDependency(dependency any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withUnionDependency", reqArgs)
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

// WithMergeLabel adds a label to the resource
func (s *ExternalServiceResource) WithMergeLabel(label string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabel", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLabelCategorized adds a categorized label to the resource
func (s *ExternalServiceResource) WithMergeLabelCategorized(label string, category string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	reqArgs["category"] = SerializeValue(category)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabelCategorized", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpoint configures a named endpoint
func (s *ExternalServiceResource) WithMergeEndpoint(endpointName string, port float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpointScheme configures a named endpoint with scheme
func (s *ExternalServiceResource) WithMergeEndpointScheme(endpointName string, port float64, scheme string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	reqArgs["scheme"] = SerializeValue(scheme)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpointScheme", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLogging configures resource logging
func (s *ExternalServiceResource) WithMergeLogging(logLevel string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLogging", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLoggingPath configures resource logging with file path
func (s *ExternalServiceResource) WithMergeLoggingPath(logLevel string, logPath string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	reqArgs["logPath"] = SerializeValue(logPath)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLoggingPath", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRoute configures a route
func (s *ExternalServiceResource) WithMergeRoute(path string, method string, handler string, priority float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRoute", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRouteMiddleware configures a route with middleware
func (s *ExternalServiceResource) WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	reqArgs["middleware"] = SerializeValue(middleware)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRouteMiddleware", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// IAspireStore wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IAspireStore.
type IAspireStore struct {
	HandleWrapperBase
}

// NewIAspireStore creates a new IAspireStore.
func NewIAspireStore(handle *Handle, client *AspireClient) *IAspireStore {
	return &IAspireStore{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// GetFileNameWithContent gets a deterministic file path for the specified file contents
func (s *IAspireStore) GetFileNameWithContent(filenameTemplate string, sourceFilename string) (*string, error) {
	reqArgs := map[string]any{
		"aspireStore": SerializeValue(s.Handle()),
	}
	reqArgs["filenameTemplate"] = SerializeValue(filenameTemplate)
	reqArgs["sourceFilename"] = SerializeValue(sourceFilename)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getFileNameWithContent", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*string), nil
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

// AddContainerRegistry adds a container registry resource
func (s *IDistributedApplicationBuilder) AddContainerRegistry(name string, endpoint any, repository any) (*ContainerRegistryResource, error) {
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

// AddContainer adds a container resource
func (s *IDistributedApplicationBuilder) AddContainer(name string, image any) (*ContainerResource, error) {
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

// AddDockerfileBuilder adds a container resource built from a programmatically generated Dockerfile
func (s *IDistributedApplicationBuilder) AddDockerfileBuilder(name string, contextPath string, callback func(...any) any, stage *string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["contextPath"] = SerializeValue(contextPath)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if stage != nil {
		reqArgs["stage"] = SerializeValue(stage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addDockerfileBuilder", reqArgs)
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
func (s *IDistributedApplicationBuilder) AddExternalService(name string, url any) (*ExternalServiceResource, error) {
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

// Pipeline gets the Pipeline property
func (s *IDistributedApplicationBuilder) Pipeline() (*IDistributedApplicationPipeline, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/IDistributedApplicationBuilder.pipeline", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IDistributedApplicationPipeline), nil
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
func (s *IDistributedApplicationBuilder) AddParameter(name string, value *string, publishValueAsDefault *bool, secret *bool) (*ParameterResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	if value != nil {
		reqArgs["value"] = SerializeValue(value)
	}
	if publishValueAsDefault != nil {
		reqArgs["publishValueAsDefault"] = SerializeValue(publishValueAsDefault)
	}
	if secret != nil {
		reqArgs["secret"] = SerializeValue(secret)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addParameter", reqArgs)
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

// AddParameterWithGeneratedValue adds a parameter with a generated default value
func (s *IDistributedApplicationBuilder) AddParameterWithGeneratedValue(name string, value *GenerateParameterDefault, secret *bool, persist *bool) (*ParameterResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["value"] = SerializeValue(value)
	if secret != nil {
		reqArgs["secret"] = SerializeValue(secret)
	}
	if persist != nil {
		reqArgs["persist"] = SerializeValue(persist)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addParameterWithGeneratedValue", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ParameterResource), nil
}

// AddConnectionString adds a connection string resource
func (s *IDistributedApplicationBuilder) AddConnectionString(name string, environmentVariableNameOrExpression any) (*IResourceWithConnectionString, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	if environmentVariableNameOrExpression != nil {
		reqArgs["environmentVariableNameOrExpression"] = SerializeValue(environmentVariableNameOrExpression)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addConnectionString", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithConnectionString), nil
}

// AddProject adds a .NET project resource
func (s *IDistributedApplicationBuilder) AddProject(name string, projectPath string, launchProfileOrOptions any) (*ProjectResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["projectPath"] = SerializeValue(projectPath)
	if launchProfileOrOptions != nil {
		reqArgs["launchProfileOrOptions"] = SerializeValue(launchProfileOrOptions)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addProject", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ProjectResource), nil
}

// AddCSharpApp adds a C# application resource
func (s *IDistributedApplicationBuilder) AddCSharpApp(name string, path string, options *ProjectResourceOptions) (*CSharpAppResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	reqArgs["path"] = SerializeValue(path)
	if options != nil {
		reqArgs["options"] = SerializeValue(options)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/addCSharpApp", reqArgs)
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

// AddEventingSubscriber adds an eventing subscriber
func (s *IDistributedApplicationBuilder) AddEventingSubscriber(subscribe func(...any) any) error {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if subscribe != nil {
		reqArgs["subscribe"] = RegisterCallback(subscribe)
	}
	_, err := s.Client().InvokeCapability("Aspire.Hosting/addEventingSubscriber", reqArgs)
	return err
}

// TryAddEventingSubscriber attempts to add an eventing subscriber
func (s *IDistributedApplicationBuilder) TryAddEventingSubscriber(subscribe func(...any) any) error {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if subscribe != nil {
		reqArgs["subscribe"] = RegisterCallback(subscribe)
	}
	_, err := s.Client().InvokeCapability("Aspire.Hosting/tryAddEventingSubscriber", reqArgs)
	return err
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

// IDistributedApplicationPipeline wraps a handle for Aspire.Hosting/Aspire.Hosting.Pipelines.IDistributedApplicationPipeline.
type IDistributedApplicationPipeline struct {
	HandleWrapperBase
}

// NewIDistributedApplicationPipeline creates a new IDistributedApplicationPipeline.
func NewIDistributedApplicationPipeline(handle *Handle, client *AspireClient) *IDistributedApplicationPipeline {
	return &IDistributedApplicationPipeline{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// DisableBuildOnlyContainerValidation disables publish and deploy validation for unconsumed build-only containers.
func (s *IDistributedApplicationPipeline) DisableBuildOnlyContainerValidation() (*IDistributedApplicationPipeline, error) {
	reqArgs := map[string]any{
		"pipeline": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/disableBuildOnlyContainerValidation", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IDistributedApplicationPipeline), nil
}

// AddStep adds a pipeline step to the application
func (s *IDistributedApplicationPipeline) AddStep(stepName string, callback func(...any) any, dependsOn []string, requiredBy []string) error {
	reqArgs := map[string]any{
		"pipeline": SerializeValue(s.Handle()),
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
	_, err := s.Client().InvokeCapability("Aspire.Hosting/addStep", reqArgs)
	return err
}

// Configure configures the application pipeline via a callback
func (s *IDistributedApplicationPipeline) Configure(callback func(...any) any) error {
	reqArgs := map[string]any{
		"pipeline": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	_, err := s.Client().InvokeCapability("Aspire.Hosting/configure", reqArgs)
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

// IExecutionConfigurationBuilder wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IExecutionConfigurationBuilder.
type IExecutionConfigurationBuilder struct {
	HandleWrapperBase
}

// NewIExecutionConfigurationBuilder creates a new IExecutionConfigurationBuilder.
func NewIExecutionConfigurationBuilder(handle *Handle, client *AspireClient) *IExecutionConfigurationBuilder {
	return &IExecutionConfigurationBuilder{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Build builds the execution configuration
func (s *IExecutionConfigurationBuilder) Build(executionContext *DistributedApplicationExecutionContext, resourceLogger *ILogger, cancellationToken *CancellationToken) (*IExecutionConfigurationResult, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["executionContext"] = SerializeValue(executionContext)
	if resourceLogger != nil {
		reqArgs["resourceLogger"] = SerializeValue(resourceLogger)
	}
	if cancellationToken != nil {
		reqArgs["cancellationToken"] = RegisterCancellation(cancellationToken, s.Client())
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/buildExecutionConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IExecutionConfigurationResult), nil
}

// WithHttpsCertificateConfig adds an HTTPS certificate configuration gatherer
func (s *IExecutionConfigurationBuilder) WithHttpsCertificateConfig(configContextFactory func(...any) any) (*IExecutionConfigurationBuilder, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if configContextFactory != nil {
		reqArgs["configContextFactory"] = RegisterCallback(configContextFactory)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsCertificateConfigExport", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IExecutionConfigurationBuilder), nil
}

// WithArgumentsConfig adds an arguments configuration gatherer
func (s *IExecutionConfigurationBuilder) WithArgumentsConfig() (*IExecutionConfigurationBuilder, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withArgumentsConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IExecutionConfigurationBuilder), nil
}

// WithEnvironmentVariablesConfig adds an environment variables configuration gatherer
func (s *IExecutionConfigurationBuilder) WithEnvironmentVariablesConfig() (*IExecutionConfigurationBuilder, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEnvironmentVariablesConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IExecutionConfigurationBuilder), nil
}

// WithCertificateTrustConfig adds a certificate trust configuration gatherer
func (s *IExecutionConfigurationBuilder) WithCertificateTrustConfig(configContextFactory func(...any) any) (*IExecutionConfigurationBuilder, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if configContextFactory != nil {
		reqArgs["configContextFactory"] = RegisterCallback(configContextFactory)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withCertificateTrustConfig", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IExecutionConfigurationBuilder), nil
}

// IExecutionConfigurationResult wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IExecutionConfigurationResult.
type IExecutionConfigurationResult struct {
	HandleWrapperBase
}

// NewIExecutionConfigurationResult creates a new IExecutionConfigurationResult.
func NewIExecutionConfigurationResult(handle *Handle, client *AspireClient) *IExecutionConfigurationResult {
	return &IExecutionConfigurationResult{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// GetCertificateTrustData gets certificate trust execution-configuration data
func (s *IExecutionConfigurationResult) GetCertificateTrustData() (*CertificateTrustExecutionConfigurationExportData, error) {
	reqArgs := map[string]any{
		"configuration": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getCertificateTrustData", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*CertificateTrustExecutionConfigurationExportData), nil
}

// GetHttpsCertificateData gets HTTPS certificate execution-configuration data
func (s *IExecutionConfigurationResult) GetHttpsCertificateData() (*HttpsCertificateExecutionConfigurationExportData, error) {
	reqArgs := map[string]any{
		"configuration": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getHttpsCertificateData", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*HttpsCertificateExecutionConfigurationExportData), nil
}

// IExpressionValue wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IExpressionValue.
type IExpressionValue struct {
	HandleWrapperBase
}

// NewIExpressionValue creates a new IExpressionValue.
func NewIExpressionValue(handle *Handle, client *AspireClient) *IExpressionValue {
	return &IExpressionValue{
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

// GetAspireStore gets the Aspire store from the service provider
func (s *IServiceProvider) GetAspireStore() (*IAspireStore, error) {
	reqArgs := map[string]any{
		"serviceProvider": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getAspireStore", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IAspireStore), nil
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

// TryDeleteSecret attempts to delete a user secret value
func (s *IUserSecretsManager) TryDeleteSecret(name string) (*bool, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/IUserSecretsManager.tryDeleteSecret", reqArgs)
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

// LogFacade wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.LogFacade.
type LogFacade struct {
	HandleWrapperBase
}

// NewLogFacade creates a new LogFacade.
func NewLogFacade(handle *Handle, client *AspireClient) *LogFacade {
	return &LogFacade{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Info writes an informational log message
func (s *LogFacade) Info(message string) error {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["message"] = SerializeValue(message)
	_, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/info", reqArgs)
	return err
}

// Warning writes a warning log message
func (s *LogFacade) Warning(message string) error {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["message"] = SerializeValue(message)
	_, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/warning", reqArgs)
	return err
}

// Error writes an error log message
func (s *LogFacade) Error(message string) error {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["message"] = SerializeValue(message)
	_, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/error", reqArgs)
	return err
}

// Debug writes a debug log message
func (s *LogFacade) Debug(message string) error {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["message"] = SerializeValue(message)
	_, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/debug", reqArgs)
	return err
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

// WithUrls customizes displayed URLs via callback
func (s *ParameterResource) WithUrls(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrls", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *ParameterResource) WithUrl(url any, displayText *string) (*IResource, error) {
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

// WithRelationship adds a relationship to another resource
func (s *ParameterResource) WithRelationship(resourceBuilder *IResource, type_ string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["resourceBuilder"] = SerializeValue(resourceBuilder)
	reqArgs["type"] = SerializeValue(type_)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderRelationship", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderParentRelationship", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderChildRelationship", reqArgs)
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

// CreateExecutionConfiguration creates an execution configuration builder
func (s *ParameterResource) CreateExecutionConfiguration() (*IExecutionConfigurationBuilder, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IExecutionConfigurationBuilder), nil
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

// WithUnionDependency adds a dependency from a string or another resource
func (s *ParameterResource) WithUnionDependency(dependency any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withUnionDependency", reqArgs)
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

// WithMergeLabel adds a label to the resource
func (s *ParameterResource) WithMergeLabel(label string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabel", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLabelCategorized adds a categorized label to the resource
func (s *ParameterResource) WithMergeLabelCategorized(label string, category string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	reqArgs["category"] = SerializeValue(category)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabelCategorized", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpoint configures a named endpoint
func (s *ParameterResource) WithMergeEndpoint(endpointName string, port float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpointScheme configures a named endpoint with scheme
func (s *ParameterResource) WithMergeEndpointScheme(endpointName string, port float64, scheme string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	reqArgs["scheme"] = SerializeValue(scheme)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpointScheme", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLogging configures resource logging
func (s *ParameterResource) WithMergeLogging(logLevel string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLogging", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLoggingPath configures resource logging with file path
func (s *ParameterResource) WithMergeLoggingPath(logLevel string, logPath string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	reqArgs["logPath"] = SerializeValue(logPath)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLoggingPath", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRoute configures a route
func (s *ParameterResource) WithMergeRoute(path string, method string, handler string, priority float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRoute", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRouteMiddleware configures a route with middleware
func (s *ParameterResource) WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	reqArgs["middleware"] = SerializeValue(middleware)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRouteMiddleware", reqArgs)
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

// Pipeline gets the pipeline editor
func (s *PipelineConfigurationContext) Pipeline() (*PipelineEditor, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.pipeline", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*PipelineEditor), nil
}

// Log gets the callback logger facade
func (s *PipelineConfigurationContext) Log() (*LogFacade, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.log", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*LogFacade), nil
}

// GetSteps gets pipeline steps with the specified tag
func (s *PipelineConfigurationContext) GetSteps(tag string) (*[]*PipelineStep, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["tag"] = SerializeValue(tag)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/getSteps", reqArgs)
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

// PipelineEditor wraps a handle for Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineEditor.
type PipelineEditor struct {
	HandleWrapperBase
}

// NewPipelineEditor creates a new PipelineEditor.
func NewPipelineEditor(handle *Handle, client *AspireClient) *PipelineEditor {
	return &PipelineEditor{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Steps gets all configured pipeline steps
func (s *PipelineEditor) Steps() (*[]*PipelineStep, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/steps", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*[]*PipelineStep), nil
}

// StepsByTag gets pipeline steps with the specified tag
func (s *PipelineEditor) StepsByTag(tag string) (*[]*PipelineStep, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["tag"] = SerializeValue(tag)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/stepsByTag", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*[]*PipelineStep), nil
}

// PipelineStep wraps a handle for Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineStep.
type PipelineStep struct {
	HandleWrapperBase
}

// NewPipelineStep creates a new PipelineStep.
func NewPipelineStep(handle *Handle, client *AspireClient) *PipelineStep {
	return &PipelineStep{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Name gets the unique name of the step
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

// Description gets the human-readable description of the step
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

// AddTag adds a tag to the step
func (s *PipelineStep) AddTag(tag string) error {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["tag"] = SerializeValue(tag)
	_, err := s.Client().InvokeCapability("Aspire.Hosting.Pipelines/addTag", reqArgs)
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

// WithBrowserLogs adds a child browser logs resource that opens tracked browser sessions, captures browser logs, and captures screenshots.
func (s *ProjectResource) WithBrowserLogs(browser *string, profile *string, userDataMode *BrowserUserDataMode) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if browser != nil {
		reqArgs["browser"] = SerializeValue(browser)
	}
	if profile != nil {
		reqArgs["profile"] = SerializeValue(profile)
	}
	if userDataMode != nil {
		reqArgs["userDataMode"] = SerializeValue(userDataMode)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBrowserLogs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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
func (s *ProjectResource) WithOtlpExporter(protocol *OtlpProtocol) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if protocol != nil {
		reqArgs["protocol"] = SerializeValue(protocol)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporter", reqArgs)
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
func (s *ProjectResource) WithEnvironment(name string, value any) (*IResourceWithEnvironment, error) {
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

// WithReferenceEnvironment configures which reference values are injected into environment variables
func (s *ProjectResource) WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["options"] = SerializeValue(options)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceEnvironment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReference adds a reference to another resource
func (s *ProjectResource) WithReference(source any, connectionName *string, optional *bool, name *string) (*IResourceWithEnvironment, error) {
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

// WithEndpointCallback updates a named endpoint via callback
func (s *ProjectResource) WithEndpointCallback(endpointName string, callback func(...any) any, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpEndpointCallback updates an HTTP endpoint via callback
func (s *ProjectResource) WithHttpEndpointCallback(callback func(...any) any, name *string, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpsEndpointCallback updates an HTTPS endpoint via callback
func (s *ProjectResource) WithHttpsEndpointCallback(callback func(...any) any, name *string, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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

// WithUrls customizes displayed URLs via callback
func (s *ProjectResource) WithUrls(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrls", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *ProjectResource) WithUrl(url any, displayText *string) (*IResource, error) {
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

// PublishWithContainerFiles configures the resource to copy container files from the specified source during publishing
func (s *ProjectResource) PublishWithContainerFiles(source *IResourceWithContainerFiles, destinationPath string) (*IContainerFilesDestinationResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["source"] = SerializeValue(source)
	reqArgs["destinationPath"] = SerializeValue(destinationPath)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/publishWithContainerFilesFromResource", reqArgs)
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
func (s *ProjectResource) WaitFor(dependency *IResource, waitBehavior *WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if waitBehavior != nil {
		reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStart waits for another resource to start
func (s *ProjectResource) WaitForStart(dependency *IResource, waitBehavior *WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if waitBehavior != nil {
		reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStart", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForResourceCompletion", reqArgs)
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

// WithHttpCommand adds an HTTP resource command
func (s *ProjectResource) WithHttpCommand(path string, displayName string, options *HttpCommandExportOptions) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["displayName"] = SerializeValue(displayName)
	if options != nil {
		reqArgs["options"] = SerializeValue(options)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParameterHttpsDeveloperCertificate", reqArgs)
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

// WithRelationship adds a relationship to another resource
func (s *ProjectResource) WithRelationship(resourceBuilder *IResource, type_ string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["resourceBuilder"] = SerializeValue(resourceBuilder)
	reqArgs["type"] = SerializeValue(type_)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithParentRelationship sets the parent relationship
func (s *ProjectResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderParentRelationship", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderChildRelationship", reqArgs)
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

// WithImagePushOptions sets image push options via callback
func (s *ProjectResource) WithImagePushOptions(callback func(...any) any) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImagePushOptions", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
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

// CreateExecutionConfiguration creates an execution configuration builder
func (s *ProjectResource) CreateExecutionConfiguration() (*IExecutionConfigurationBuilder, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IExecutionConfigurationBuilder), nil
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

// WithUnionDependency adds a dependency from a string or another resource
func (s *ProjectResource) WithUnionDependency(dependency any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withUnionDependency", reqArgs)
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

// WithMergeLabel adds a label to the resource
func (s *ProjectResource) WithMergeLabel(label string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabel", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLabelCategorized adds a categorized label to the resource
func (s *ProjectResource) WithMergeLabelCategorized(label string, category string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	reqArgs["category"] = SerializeValue(category)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabelCategorized", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpoint configures a named endpoint
func (s *ProjectResource) WithMergeEndpoint(endpointName string, port float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpointScheme configures a named endpoint with scheme
func (s *ProjectResource) WithMergeEndpointScheme(endpointName string, port float64, scheme string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	reqArgs["scheme"] = SerializeValue(scheme)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpointScheme", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLogging configures resource logging
func (s *ProjectResource) WithMergeLogging(logLevel string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLogging", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLoggingPath configures resource logging with file path
func (s *ProjectResource) WithMergeLoggingPath(logLevel string, logPath string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	reqArgs["logPath"] = SerializeValue(logPath)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLoggingPath", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRoute configures a route
func (s *ProjectResource) WithMergeRoute(path string, method string, handler string, priority float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRoute", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRouteMiddleware configures a route with middleware
func (s *ProjectResource) WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	reqArgs["middleware"] = SerializeValue(middleware)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRouteMiddleware", reqArgs)
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
}

// NewResourceUrlsCallbackContext creates a new ResourceUrlsCallbackContext.
func NewResourceUrlsCallbackContext(handle *Handle, client *AspireClient) *ResourceUrlsCallbackContext {
	return &ResourceUrlsCallbackContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Resource gets the resource associated with these URLs
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

// Urls gets the URL editor
func (s *ResourceUrlsCallbackContext) Urls() (*ResourceUrlsEditor, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.urls", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ResourceUrlsEditor), nil
}

// Log gets the callback logger facade
func (s *ResourceUrlsCallbackContext) Log() (*LogFacade, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.log", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*LogFacade), nil
}

// ExecutionContext gets the execution context for this callback invocation
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

// GetEndpoint gets an endpoint reference from the associated resource
func (s *ResourceUrlsCallbackContext) GetEndpoint(name string) (*EndpointReference, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["name"] = SerializeValue(name)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/getEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*EndpointReference), nil
}

// ResourceUrlsEditor wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceUrlsEditor.
type ResourceUrlsEditor struct {
	HandleWrapperBase
}

// NewResourceUrlsEditor creates a new ResourceUrlsEditor.
func NewResourceUrlsEditor(handle *Handle, client *AspireClient) *ResourceUrlsEditor {
	return &ResourceUrlsEditor{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// ExecutionContext gets the execution context for this URL editor
func (s *ResourceUrlsEditor) ExecutionContext() (*DistributedApplicationExecutionContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ResourceUrlsEditor.executionContext", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*DistributedApplicationExecutionContext), nil
}

// Add adds a displayed URL
func (s *ResourceUrlsEditor) Add(url any, displayText *string) error {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	_, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ResourceUrlsEditor.add", reqArgs)
	return err
}

// AddForEndpoint adds a displayed URL for a specific endpoint
func (s *ResourceUrlsEditor) AddForEndpoint(endpoint *EndpointReference, url any, displayText *string) error {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["endpoint"] = SerializeValue(endpoint)
	reqArgs["url"] = SerializeValue(url)
	if displayText != nil {
		reqArgs["displayText"] = SerializeValue(displayText)
	}
	_, err := s.Client().InvokeCapability("Aspire.Hosting.ApplicationModel/ResourceUrlsEditor.addForEndpoint", reqArgs)
	return err
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

// WithBrowserLogs adds a child browser logs resource that opens tracked browser sessions, captures browser logs, and captures screenshots.
func (s *TestDatabaseResource) WithBrowserLogs(browser *string, profile *string, userDataMode *BrowserUserDataMode) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if browser != nil {
		reqArgs["browser"] = SerializeValue(browser)
	}
	if profile != nil {
		reqArgs["profile"] = SerializeValue(profile)
	}
	if userDataMode != nil {
		reqArgs["userDataMode"] = SerializeValue(userDataMode)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBrowserLogs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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

// WithBuildArg adds a build argument from a string value or parameter resource
func (s *TestDatabaseResource) WithBuildArg(name string, value any) (*ContainerResource, error) {
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParameterBuildSecret", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithContainerCertificatePaths overrides container certificate bundle and directory paths used for trust configuration
func (s *TestDatabaseResource) WithContainerCertificatePaths(customCertificatesDestination *string, defaultCertificateBundlePaths []string, defaultCertificateDirectoryPaths []string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if customCertificatesDestination != nil {
		reqArgs["customCertificatesDestination"] = SerializeValue(customCertificatesDestination)
	}
	if defaultCertificateBundlePaths != nil {
		reqArgs["defaultCertificateBundlePaths"] = SerializeValue(defaultCertificateBundlePaths)
	}
	if defaultCertificateDirectoryPaths != nil {
		reqArgs["defaultCertificateDirectoryPaths"] = SerializeValue(defaultCertificateDirectoryPaths)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerCertificatePaths", reqArgs)
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

// WithDockerfileBuilder configures the resource to use a programmatically generated Dockerfile
func (s *TestDatabaseResource) WithDockerfileBuilder(contextPath string, callback func(...any) any, stage *string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["contextPath"] = SerializeValue(contextPath)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if stage != nil {
		reqArgs["stage"] = SerializeValue(stage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfileBuilder", reqArgs)
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
func (s *TestDatabaseResource) WithOtlpExporter(protocol *OtlpProtocol) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if protocol != nil {
		reqArgs["protocol"] = SerializeValue(protocol)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporter", reqArgs)
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
func (s *TestDatabaseResource) WithEnvironment(name string, value any) (*IResourceWithEnvironment, error) {
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

// WithReferenceEnvironment configures which reference values are injected into environment variables
func (s *TestDatabaseResource) WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["options"] = SerializeValue(options)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceEnvironment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReference adds a reference to another resource
func (s *TestDatabaseResource) WithReference(source any, connectionName *string, optional *bool, name *string) (*IResourceWithEnvironment, error) {
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

// WithEndpointCallback updates a named endpoint via callback
func (s *TestDatabaseResource) WithEndpointCallback(endpointName string, callback func(...any) any, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpEndpointCallback updates an HTTP endpoint via callback
func (s *TestDatabaseResource) WithHttpEndpointCallback(callback func(...any) any, name *string, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpsEndpointCallback updates an HTTPS endpoint via callback
func (s *TestDatabaseResource) WithHttpsEndpointCallback(callback func(...any) any, name *string, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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

// WithUrls customizes displayed URLs via callback
func (s *TestDatabaseResource) WithUrls(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrls", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *TestDatabaseResource) WithUrl(url any, displayText *string) (*IResource, error) {
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
func (s *TestDatabaseResource) WaitFor(dependency *IResource, waitBehavior *WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if waitBehavior != nil {
		reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStart waits for another resource to start
func (s *TestDatabaseResource) WaitForStart(dependency *IResource, waitBehavior *WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if waitBehavior != nil {
		reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStart", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForResourceCompletion", reqArgs)
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

// WithHttpCommand adds an HTTP resource command
func (s *TestDatabaseResource) WithHttpCommand(path string, displayName string, options *HttpCommandExportOptions) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["displayName"] = SerializeValue(displayName)
	if options != nil {
		reqArgs["options"] = SerializeValue(options)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParameterHttpsDeveloperCertificate", reqArgs)
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

// WithRelationship adds a relationship to another resource
func (s *TestDatabaseResource) WithRelationship(resourceBuilder *IResource, type_ string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["resourceBuilder"] = SerializeValue(resourceBuilder)
	reqArgs["type"] = SerializeValue(type_)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithParentRelationship sets the parent relationship
func (s *TestDatabaseResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderParentRelationship", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderChildRelationship", reqArgs)
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

// WithImagePushOptions sets image push options via callback
func (s *TestDatabaseResource) WithImagePushOptions(callback func(...any) any) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImagePushOptions", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
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

// CreateExecutionConfiguration creates an execution configuration builder
func (s *TestDatabaseResource) CreateExecutionConfiguration() (*IExecutionConfigurationBuilder, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IExecutionConfigurationBuilder), nil
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

// WithUnionDependency adds a dependency from a string or another resource
func (s *TestDatabaseResource) WithUnionDependency(dependency any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withUnionDependency", reqArgs)
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

// WithMergeLabel adds a label to the resource
func (s *TestDatabaseResource) WithMergeLabel(label string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabel", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLabelCategorized adds a categorized label to the resource
func (s *TestDatabaseResource) WithMergeLabelCategorized(label string, category string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	reqArgs["category"] = SerializeValue(category)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabelCategorized", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpoint configures a named endpoint
func (s *TestDatabaseResource) WithMergeEndpoint(endpointName string, port float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpointScheme configures a named endpoint with scheme
func (s *TestDatabaseResource) WithMergeEndpointScheme(endpointName string, port float64, scheme string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	reqArgs["scheme"] = SerializeValue(scheme)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpointScheme", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLogging configures resource logging
func (s *TestDatabaseResource) WithMergeLogging(logLevel string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLogging", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLoggingPath configures resource logging with file path
func (s *TestDatabaseResource) WithMergeLoggingPath(logLevel string, logPath string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	reqArgs["logPath"] = SerializeValue(logPath)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLoggingPath", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRoute configures a route
func (s *TestDatabaseResource) WithMergeRoute(path string, method string, handler string, priority float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRoute", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRouteMiddleware configures a route with middleware
func (s *TestDatabaseResource) WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	reqArgs["middleware"] = SerializeValue(middleware)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRouteMiddleware", reqArgs)
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

// TestMutableCollectionContext wraps a handle for Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestMutableCollectionContext.
type TestMutableCollectionContext struct {
	HandleWrapperBase
	tags *AspireList[string]
	counts *AspireDict[string, float64]
}

// NewTestMutableCollectionContext creates a new TestMutableCollectionContext.
func NewTestMutableCollectionContext(handle *Handle, client *AspireClient) *TestMutableCollectionContext {
	return &TestMutableCollectionContext{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
}

// Tags gets the Tags property
func (s *TestMutableCollectionContext) Tags() *AspireList[string] {
	if s.tags == nil {
		s.tags = NewAspireListWithGetter[string](s.Handle(), s.Client(), "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestMutableCollectionContext.tags")
	}
	return s.tags
}

// SetTags sets the Tags property
func (s *TestMutableCollectionContext) SetTags(value *AspireList[string]) (*TestMutableCollectionContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestMutableCollectionContext.setTags", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*TestMutableCollectionContext), nil
}

// Counts gets the Counts property
func (s *TestMutableCollectionContext) Counts() *AspireDict[string, float64] {
	if s.counts == nil {
		s.counts = NewAspireDictWithGetter[string, float64](s.Handle(), s.Client(), "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestMutableCollectionContext.counts")
	}
	return s.counts
}

// SetCounts sets the Counts property
func (s *TestMutableCollectionContext) SetCounts(value *AspireDict[string, float64]) (*TestMutableCollectionContext, error) {
	reqArgs := map[string]any{
		"context": SerializeValue(s.Handle()),
	}
	reqArgs["value"] = SerializeValue(value)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestMutableCollectionContext.setCounts", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*TestMutableCollectionContext), nil
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

// WithBrowserLogs adds a child browser logs resource that opens tracked browser sessions, captures browser logs, and captures screenshots.
func (s *TestRedisResource) WithBrowserLogs(browser *string, profile *string, userDataMode *BrowserUserDataMode) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if browser != nil {
		reqArgs["browser"] = SerializeValue(browser)
	}
	if profile != nil {
		reqArgs["profile"] = SerializeValue(profile)
	}
	if userDataMode != nil {
		reqArgs["userDataMode"] = SerializeValue(userDataMode)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBrowserLogs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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

// WithBuildArg adds a build argument from a string value or parameter resource
func (s *TestRedisResource) WithBuildArg(name string, value any) (*ContainerResource, error) {
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParameterBuildSecret", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithContainerCertificatePaths overrides container certificate bundle and directory paths used for trust configuration
func (s *TestRedisResource) WithContainerCertificatePaths(customCertificatesDestination *string, defaultCertificateBundlePaths []string, defaultCertificateDirectoryPaths []string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if customCertificatesDestination != nil {
		reqArgs["customCertificatesDestination"] = SerializeValue(customCertificatesDestination)
	}
	if defaultCertificateBundlePaths != nil {
		reqArgs["defaultCertificateBundlePaths"] = SerializeValue(defaultCertificateBundlePaths)
	}
	if defaultCertificateDirectoryPaths != nil {
		reqArgs["defaultCertificateDirectoryPaths"] = SerializeValue(defaultCertificateDirectoryPaths)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerCertificatePaths", reqArgs)
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

// WithDockerfileBuilder configures the resource to use a programmatically generated Dockerfile
func (s *TestRedisResource) WithDockerfileBuilder(contextPath string, callback func(...any) any, stage *string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["contextPath"] = SerializeValue(contextPath)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if stage != nil {
		reqArgs["stage"] = SerializeValue(stage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfileBuilder", reqArgs)
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
func (s *TestRedisResource) WithOtlpExporter(protocol *OtlpProtocol) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if protocol != nil {
		reqArgs["protocol"] = SerializeValue(protocol)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporter", reqArgs)
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
func (s *TestRedisResource) WithEnvironment(name string, value any) (*IResourceWithEnvironment, error) {
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

// WithConnectionProperty adds a connection property with a string or reference expression value
func (s *TestRedisResource) WithConnectionProperty(name string, value any) (*IResourceWithConnectionString, error) {
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

// WithReferenceEnvironment configures which reference values are injected into environment variables
func (s *TestRedisResource) WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["options"] = SerializeValue(options)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceEnvironment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReference adds a reference to another resource
func (s *TestRedisResource) WithReference(source any, connectionName *string, optional *bool, name *string) (*IResourceWithEnvironment, error) {
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

// GetConnectionProperty gets a connection property by key
func (s *TestRedisResource) GetConnectionProperty(key string) (*ReferenceExpression, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	reqArgs["key"] = SerializeValue(key)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/getConnectionProperty", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ReferenceExpression), nil
}

// WithEndpointCallback updates a named endpoint via callback
func (s *TestRedisResource) WithEndpointCallback(endpointName string, callback func(...any) any, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpEndpointCallback updates an HTTP endpoint via callback
func (s *TestRedisResource) WithHttpEndpointCallback(callback func(...any) any, name *string, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpsEndpointCallback updates an HTTPS endpoint via callback
func (s *TestRedisResource) WithHttpsEndpointCallback(callback func(...any) any, name *string, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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

// WithUrls customizes displayed URLs via callback
func (s *TestRedisResource) WithUrls(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrls", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *TestRedisResource) WithUrl(url any, displayText *string) (*IResource, error) {
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
func (s *TestRedisResource) WaitFor(dependency *IResource, waitBehavior *WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if waitBehavior != nil {
		reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStart waits for another resource to start
func (s *TestRedisResource) WaitForStart(dependency *IResource, waitBehavior *WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if waitBehavior != nil {
		reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStart", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForResourceCompletion", reqArgs)
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

// WithHttpCommand adds an HTTP resource command
func (s *TestRedisResource) WithHttpCommand(path string, displayName string, options *HttpCommandExportOptions) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["displayName"] = SerializeValue(displayName)
	if options != nil {
		reqArgs["options"] = SerializeValue(options)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParameterHttpsDeveloperCertificate", reqArgs)
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

// WithRelationship adds a relationship to another resource
func (s *TestRedisResource) WithRelationship(resourceBuilder *IResource, type_ string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["resourceBuilder"] = SerializeValue(resourceBuilder)
	reqArgs["type"] = SerializeValue(type_)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithParentRelationship sets the parent relationship
func (s *TestRedisResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderParentRelationship", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderChildRelationship", reqArgs)
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

// WithImagePushOptions sets image push options via callback
func (s *TestRedisResource) WithImagePushOptions(callback func(...any) any) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImagePushOptions", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
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

// CreateExecutionConfiguration creates an execution configuration builder
func (s *TestRedisResource) CreateExecutionConfiguration() (*IExecutionConfigurationBuilder, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IExecutionConfigurationBuilder), nil
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

// WithUnionDependency adds a dependency from a string or another resource
func (s *TestRedisResource) WithUnionDependency(dependency any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withUnionDependency", reqArgs)
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

// WithMergeLabel adds a label to the resource
func (s *TestRedisResource) WithMergeLabel(label string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabel", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLabelCategorized adds a categorized label to the resource
func (s *TestRedisResource) WithMergeLabelCategorized(label string, category string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	reqArgs["category"] = SerializeValue(category)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabelCategorized", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpoint configures a named endpoint
func (s *TestRedisResource) WithMergeEndpoint(endpointName string, port float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpointScheme configures a named endpoint with scheme
func (s *TestRedisResource) WithMergeEndpointScheme(endpointName string, port float64, scheme string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	reqArgs["scheme"] = SerializeValue(scheme)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpointScheme", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLogging configures resource logging
func (s *TestRedisResource) WithMergeLogging(logLevel string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLogging", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLoggingPath configures resource logging with file path
func (s *TestRedisResource) WithMergeLoggingPath(logLevel string, logPath string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	reqArgs["logPath"] = SerializeValue(logPath)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLoggingPath", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRoute configures a route
func (s *TestRedisResource) WithMergeRoute(path string, method string, handler string, priority float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRoute", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRouteMiddleware configures a route with middleware
func (s *TestRedisResource) WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	reqArgs["middleware"] = SerializeValue(middleware)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRouteMiddleware", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
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

// WithBrowserLogs adds a child browser logs resource that opens tracked browser sessions, captures browser logs, and captures screenshots.
func (s *TestVaultResource) WithBrowserLogs(browser *string, profile *string, userDataMode *BrowserUserDataMode) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if browser != nil {
		reqArgs["browser"] = SerializeValue(browser)
	}
	if profile != nil {
		reqArgs["profile"] = SerializeValue(profile)
	}
	if userDataMode != nil {
		reqArgs["userDataMode"] = SerializeValue(userDataMode)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBrowserLogs", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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

// WithBuildArg adds a build argument from a string value or parameter resource
func (s *TestVaultResource) WithBuildArg(name string, value any) (*ContainerResource, error) {
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParameterBuildSecret", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*ContainerResource), nil
}

// WithContainerCertificatePaths overrides container certificate bundle and directory paths used for trust configuration
func (s *TestVaultResource) WithContainerCertificatePaths(customCertificatesDestination *string, defaultCertificateBundlePaths []string, defaultCertificateDirectoryPaths []string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if customCertificatesDestination != nil {
		reqArgs["customCertificatesDestination"] = SerializeValue(customCertificatesDestination)
	}
	if defaultCertificateBundlePaths != nil {
		reqArgs["defaultCertificateBundlePaths"] = SerializeValue(defaultCertificateBundlePaths)
	}
	if defaultCertificateDirectoryPaths != nil {
		reqArgs["defaultCertificateDirectoryPaths"] = SerializeValue(defaultCertificateDirectoryPaths)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withContainerCertificatePaths", reqArgs)
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

// WithDockerfileBuilder configures the resource to use a programmatically generated Dockerfile
func (s *TestVaultResource) WithDockerfileBuilder(contextPath string, callback func(...any) any, stage *string) (*ContainerResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["contextPath"] = SerializeValue(contextPath)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if stage != nil {
		reqArgs["stage"] = SerializeValue(stage)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withDockerfileBuilder", reqArgs)
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
func (s *TestVaultResource) WithOtlpExporter(protocol *OtlpProtocol) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if protocol != nil {
		reqArgs["protocol"] = SerializeValue(protocol)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withOtlpExporter", reqArgs)
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
func (s *TestVaultResource) WithEnvironment(name string, value any) (*IResourceWithEnvironment, error) {
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

// WithReferenceEnvironment configures which reference values are injected into environment variables
func (s *TestVaultResource) WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) (*IResourceWithEnvironment, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["options"] = SerializeValue(options)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withReferenceEnvironment", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEnvironment), nil
}

// WithReference adds a reference to another resource
func (s *TestVaultResource) WithReference(source any, connectionName *string, optional *bool, name *string) (*IResourceWithEnvironment, error) {
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

// WithEndpointCallback updates a named endpoint via callback
func (s *TestVaultResource) WithEndpointCallback(endpointName string, callback func(...any) any, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpEndpointCallback updates an HTTP endpoint via callback
func (s *TestVaultResource) WithHttpEndpointCallback(callback func(...any) any, name *string, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
}

// WithHttpsEndpointCallback updates an HTTPS endpoint via callback
func (s *TestVaultResource) WithHttpsEndpointCallback(callback func(...any) any, name *string, createIfNotExists *bool) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	if createIfNotExists != nil {
		reqArgs["createIfNotExists"] = SerializeValue(createIfNotExists)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpsEndpointCallback", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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

// WithUrls customizes displayed URLs via callback
func (s *TestVaultResource) WithUrls(callback func(...any) any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withUrls", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithUrl adds or modifies displayed URLs
func (s *TestVaultResource) WithUrl(url any, displayText *string) (*IResource, error) {
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
func (s *TestVaultResource) WaitFor(dependency *IResource, waitBehavior *WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if waitBehavior != nil {
		reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitFor", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithWaitSupport), nil
}

// WaitForStart waits for another resource to start
func (s *TestVaultResource) WaitForStart(dependency *IResource, waitBehavior *WaitBehavior) (*IResourceWithWaitSupport, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	if waitBehavior != nil {
		reqArgs["waitBehavior"] = SerializeValue(waitBehavior)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForStart", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/waitForResourceCompletion", reqArgs)
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

// WithHttpCommand adds an HTTP resource command
func (s *TestVaultResource) WithHttpCommand(path string, displayName string, options *HttpCommandExportOptions) (*IResourceWithEndpoints, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["displayName"] = SerializeValue(displayName)
	if options != nil {
		reqArgs["options"] = SerializeValue(options)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withHttpCommand", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResourceWithEndpoints), nil
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withParameterHttpsDeveloperCertificate", reqArgs)
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

// WithRelationship adds a relationship to another resource
func (s *TestVaultResource) WithRelationship(resourceBuilder *IResource, type_ string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["resourceBuilder"] = SerializeValue(resourceBuilder)
	reqArgs["type"] = SerializeValue(type_)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderRelationship", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithParentRelationship sets the parent relationship
func (s *TestVaultResource) WithParentRelationship(parent *IResource) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["parent"] = SerializeValue(parent)
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderParentRelationship", reqArgs)
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
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withBuilderChildRelationship", reqArgs)
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

// WithImagePushOptions sets image push options via callback
func (s *TestVaultResource) WithImagePushOptions(callback func(...any) any) (*IComputeResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if callback != nil {
		reqArgs["callback"] = RegisterCallback(callback)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/withImagePushOptions", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IComputeResource), nil
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

// CreateExecutionConfiguration creates an execution configuration builder
func (s *TestVaultResource) CreateExecutionConfiguration() (*IExecutionConfigurationBuilder, error) {
	reqArgs := map[string]any{
		"resource": SerializeValue(s.Handle()),
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IExecutionConfigurationBuilder), nil
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

// WithUnionDependency adds a dependency from a string or another resource
func (s *TestVaultResource) WithUnionDependency(dependency any) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["dependency"] = SerializeValue(dependency)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withUnionDependency", reqArgs)
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

// WithMergeLabel adds a label to the resource
func (s *TestVaultResource) WithMergeLabel(label string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabel", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLabelCategorized adds a categorized label to the resource
func (s *TestVaultResource) WithMergeLabelCategorized(label string, category string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["label"] = SerializeValue(label)
	reqArgs["category"] = SerializeValue(category)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabelCategorized", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpoint configures a named endpoint
func (s *TestVaultResource) WithMergeEndpoint(endpointName string, port float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpoint", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeEndpointScheme configures a named endpoint with scheme
func (s *TestVaultResource) WithMergeEndpointScheme(endpointName string, port float64, scheme string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["endpointName"] = SerializeValue(endpointName)
	reqArgs["port"] = SerializeValue(port)
	reqArgs["scheme"] = SerializeValue(scheme)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpointScheme", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLogging configures resource logging
func (s *TestVaultResource) WithMergeLogging(logLevel string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLogging", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeLoggingPath configures resource logging with file path
func (s *TestVaultResource) WithMergeLoggingPath(logLevel string, logPath string, enableConsole *bool, maxFiles *float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["logLevel"] = SerializeValue(logLevel)
	reqArgs["logPath"] = SerializeValue(logPath)
	if enableConsole != nil {
		reqArgs["enableConsole"] = SerializeValue(enableConsole)
	}
	if maxFiles != nil {
		reqArgs["maxFiles"] = SerializeValue(maxFiles)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLoggingPath", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRoute configures a route
func (s *TestVaultResource) WithMergeRoute(path string, method string, handler string, priority float64) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRoute", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
}

// WithMergeRouteMiddleware configures a route with middleware
func (s *TestVaultResource) WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) (*IResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	reqArgs["path"] = SerializeValue(path)
	reqArgs["method"] = SerializeValue(method)
	reqArgs["handler"] = SerializeValue(handler)
	reqArgs["priority"] = SerializeValue(priority)
	reqArgs["middleware"] = SerializeValue(middleware)
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRouteMiddleware", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*IResource), nil
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
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.IDistributedApplicationPipeline", func(h *Handle, c *AspireClient) any {
		return NewIDistributedApplicationPipeline(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.DistributedApplication", func(h *Handle, c *AspireClient) any {
		return NewDistributedApplication(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointReference", func(h *Handle, c *AspireClient) any {
		return NewEndpointReference(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IAspireStore", func(h *Handle, c *AspireClient) any {
		return NewIAspireStore(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IExecutionConfigurationBuilder", func(h *Handle, c *AspireClient) any {
		return NewIExecutionConfigurationBuilder(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IExecutionConfigurationResult", func(h *Handle, c *AspireClient) any {
		return NewIExecutionConfigurationResult(h, c)
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
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerMountAnnotation", func(h *Handle, c *AspireClient) any {
		return NewContainerMountAnnotation(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerImageReference", func(h *Handle, c *AspireClient) any {
		return NewContainerImageReference(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerPortReference", func(h *Handle, c *AspireClient) any {
		return NewContainerPortReference(h, c)
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
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineEditor", func(h *Handle, c *AspireClient) any {
		return NewPipelineEditor(h, c)
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
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.Ats.EventingSubscriberRegistrationContext", func(h *Handle, c *AspireClient) any {
		return NewEventingSubscriberRegistrationContext(h, c)
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
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.CommandLineArgsEditor", func(h *Handle, c *AspireClient) any {
		return NewCommandLineArgsEditor(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ConnectionStringAvailableEvent", func(h *Handle, c *AspireClient) any {
		return NewConnectionStringAvailableEvent(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerImagePushOptions", func(h *Handle, c *AspireClient) any {
		return NewContainerImagePushOptions(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerImagePushOptionsCallbackContext", func(h *Handle, c *AspireClient) any {
		return NewContainerImagePushOptionsCallbackContext(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.DistributedApplicationModel", func(h *Handle, c *AspireClient) any {
		return NewDistributedApplicationModel(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.DockerfileBuilderCallbackContext", func(h *Handle, c *AspireClient) any {
		return NewDockerfileBuilderCallbackContext(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointReferenceExpression", func(h *Handle, c *AspireClient) any {
		return NewEndpointReferenceExpression(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointUpdateContext", func(h *Handle, c *AspireClient) any {
		return NewEndpointUpdateContext(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EnvironmentCallbackContext", func(h *Handle, c *AspireClient) any {
		return NewEnvironmentCallbackContext(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EnvironmentEditor", func(h *Handle, c *AspireClient) any {
		return NewEnvironmentEditor(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IExpressionValue", func(h *Handle, c *AspireClient) any {
		return NewIExpressionValue(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.InitializeResourceEvent", func(h *Handle, c *AspireClient) any {
		return NewInitializeResourceEvent(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.LogFacade", func(h *Handle, c *AspireClient) any {
		return NewLogFacade(h, c)
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
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceUrlsEditor", func(h *Handle, c *AspireClient) any {
		return NewResourceUrlsEditor(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.Docker.DockerfileBuilder", func(h *Handle, c *AspireClient) any {
		return NewDockerfileBuilder(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.Docker.DockerfileStage", func(h *Handle, c *AspireClient) any {
		return NewDockerfileStage(h, c)
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
	RegisterHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestMutableCollectionContext", func(h *Handle, c *AspireClient) any {
		return NewTestMutableCollectionContext(h, c)
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
	RegisterHandleWrapper("Aspire.Hosting/Dict<string,any>", func(h *Handle, c *AspireClient) any {
		return &AspireDict[any, any]{HandleWrapperBase: NewHandleWrapperBase(h, c)}
	})
	RegisterHandleWrapper("Aspire.Hosting/List<any>", func(h *Handle, c *AspireClient) any {
		return &AspireList[any]{HandleWrapperBase: NewHandleWrapperBase(h, c)}
	})
	RegisterHandleWrapper("Aspire.Hosting/List<string>", func(h *Handle, c *AspireClient) any {
		return &AspireList[any]{HandleWrapperBase: NewHandleWrapperBase(h, c)}
	})
	RegisterHandleWrapper("Aspire.Hosting/Dict<string,string>", func(h *Handle, c *AspireClient) any {
		return &AspireDict[any, any]{HandleWrapperBase: NewHandleWrapperBase(h, c)}
	})
	RegisterHandleWrapper("Aspire.Hosting/Dict<string,number>", func(h *Handle, c *AspireClient) any {
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
	authToken := os.Getenv("ASPIRE_REMOTE_APPHOST_TOKEN")
	if authToken == "" {
		return nil, fmt.Errorf("ASPIRE_REMOTE_APPHOST_TOKEN environment variable not set. Run this application using `aspire run`")
	}
	if err := client.Authenticate(authToken); err != nil {
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
	result, err := client.InvokeCapability("Aspire.Hosting/createBuilder", map[string]any{"argsOrOptions": resolvedOptions})
	if err != nil {
		return nil, err
	}
	return result.(*IDistributedApplicationBuilder), nil
}

