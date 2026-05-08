// aspire.go - Capability-based Aspire SDK
// This SDK uses the ATS (Aspire Type System) capability API.
// Capabilities are endpoints like 'Aspire.Hosting/createBuilder'.
//
// GENERATED CODE - DO NOT EDIT

package aspire

import (
    "context"
    "fmt"
    "os"
    "time"
)

// Compile-time references to keep imports used in minimal SDKs.
var _ = context.Background
var _ = fmt.Errorf
var _ = os.Getenv
var _ = time.Second

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
	m := map[string]any{}
	m["Image"] = serializeValue(d.Image)
	m["Tag"] = serializeValue(d.Tag)
	return m
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
	m := map[string]any{}
	if d.Args != nil { m["Args"] = serializeValue(d.Args) }
	m["ProjectDirectory"] = serializeValue(d.ProjectDirectory)
	m["AppHostFilePath"] = serializeValue(d.AppHostFilePath)
	m["ContainerRegistryOverride"] = serializeValue(d.ContainerRegistryOverride)
	m["DisableDashboard"] = serializeValue(d.DisableDashboard)
	m["DashboardApplicationName"] = serializeValue(d.DashboardApplicationName)
	m["AllowUnsecuredTransport"] = serializeValue(d.AllowUnsecuredTransport)
	m["EnableResourceLogging"] = serializeValue(d.EnableResourceLogging)
	return m
}

// HttpsCertificateInfo represents HttpsCertificateInfo.
type HttpsCertificateInfo struct {
	Subject string `json:"Subject,omitempty"`
	Issuer string `json:"Issuer,omitempty"`
	Thumbprint string `json:"Thumbprint,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *HttpsCertificateInfo) ToMap() map[string]any {
	m := map[string]any{}
	m["Subject"] = serializeValue(d.Subject)
	m["Issuer"] = serializeValue(d.Issuer)
	m["Thumbprint"] = serializeValue(d.Thumbprint)
	return m
}

// CertificateTrustExecutionConfigurationExportData represents CertificateTrustExecutionConfigurationExportData.
type CertificateTrustExecutionConfigurationExportData struct {
	Scope CertificateTrustScope `json:"Scope,omitempty"`
	CertificateSubjects []string `json:"CertificateSubjects,omitempty"`
	CustomBundlePaths []string `json:"CustomBundlePaths,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *CertificateTrustExecutionConfigurationExportData) ToMap() map[string]any {
	m := map[string]any{}
	m["Scope"] = serializeValue(d.Scope)
	if d.CertificateSubjects != nil { m["CertificateSubjects"] = serializeValue(d.CertificateSubjects) }
	if d.CustomBundlePaths != nil { m["CustomBundlePaths"] = serializeValue(d.CustomBundlePaths) }
	return m
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
	m := map[string]any{}
	m["Subject"] = serializeValue(d.Subject)
	m["Thumbprint"] = serializeValue(d.Thumbprint)
	m["KeyPathExpression"] = serializeValue(d.KeyPathExpression)
	m["PfxPathExpression"] = serializeValue(d.PfxPathExpression)
	m["IsKeyPathReferenced"] = serializeValue(d.IsKeyPathReferenced)
	m["IsPfxPathReferenced"] = serializeValue(d.IsPfxPathReferenced)
	m["Password"] = serializeValue(d.Password)
	return m
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
	m := map[string]any{}
	m["ResourceName"] = serializeValue(d.ResourceName)
	m["ResourceId"] = serializeValue(d.ResourceId)
	m["State"] = serializeValue(d.State)
	m["StateStyle"] = serializeValue(d.StateStyle)
	m["HealthStatus"] = serializeValue(d.HealthStatus)
	m["ExitCode"] = serializeValue(d.ExitCode)
	return m
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
	m := map[string]any{}
	m["ConnectionString"] = serializeValue(d.ConnectionString)
	m["ConnectionProperties"] = serializeValue(d.ConnectionProperties)
	m["ServiceDiscovery"] = serializeValue(d.ServiceDiscovery)
	m["Endpoints"] = serializeValue(d.Endpoints)
	return m
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
	m := map[string]any{}
	if d.CertificateBundlePath != nil { m["CertificateBundlePath"] = serializeValue(d.CertificateBundlePath) }
	if d.CertificateDirectoriesPath != nil { m["CertificateDirectoriesPath"] = serializeValue(d.CertificateDirectoriesPath) }
	m["RootCertificatesPath"] = serializeValue(d.RootCertificatesPath)
	m["IsContainer"] = serializeValue(d.IsContainer)
	return m
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
	m := map[string]any{}
	m["Description"] = serializeValue(d.Description)
	if d.Parameter != nil { m["Parameter"] = serializeValue(d.Parameter) }
	m["ConfirmationMessage"] = serializeValue(d.ConfirmationMessage)
	m["IconName"] = serializeValue(d.IconName)
	m["IconVariant"] = serializeValue(d.IconVariant)
	m["IsHighlighted"] = serializeValue(d.IsHighlighted)
	if d.UpdateState != nil { m["UpdateState"] = serializeValue(d.UpdateState) }
	return m
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
	m := map[string]any{}
	m["Description"] = serializeValue(d.Description)
	m["ConfirmationMessage"] = serializeValue(d.ConfirmationMessage)
	m["IconName"] = serializeValue(d.IconName)
	m["IconVariant"] = serializeValue(d.IconVariant)
	m["IsHighlighted"] = serializeValue(d.IsHighlighted)
	m["CommandName"] = serializeValue(d.CommandName)
	m["EndpointName"] = serializeValue(d.EndpointName)
	m["MethodName"] = serializeValue(d.MethodName)
	m["ResultMode"] = serializeValue(d.ResultMode)
	return m
}

// HttpsCertificateExecutionConfigurationContext represents HttpsCertificateExecutionConfigurationContext.
type HttpsCertificateExecutionConfigurationContext struct {
	CertificatePath *ReferenceExpression `json:"CertificatePath,omitempty"`
	KeyPath *ReferenceExpression `json:"KeyPath,omitempty"`
	PfxPath *ReferenceExpression `json:"PfxPath,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *HttpsCertificateExecutionConfigurationContext) ToMap() map[string]any {
	m := map[string]any{}
	if d.CertificatePath != nil { m["CertificatePath"] = serializeValue(d.CertificatePath) }
	if d.KeyPath != nil { m["KeyPath"] = serializeValue(d.KeyPath) }
	if d.PfxPath != nil { m["PfxPath"] = serializeValue(d.PfxPath) }
	return m
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
	m := map[string]any{}
	m["MinLength"] = serializeValue(d.MinLength)
	m["Lower"] = serializeValue(d.Lower)
	m["Upper"] = serializeValue(d.Upper)
	m["Numeric"] = serializeValue(d.Numeric)
	m["Special"] = serializeValue(d.Special)
	m["MinLower"] = serializeValue(d.MinLower)
	m["MinUpper"] = serializeValue(d.MinUpper)
	m["MinNumeric"] = serializeValue(d.MinNumeric)
	m["MinSpecial"] = serializeValue(d.MinSpecial)
	return m
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
	m := map[string]any{}
	m["Success"] = serializeValue(d.Success)
	m["Canceled"] = serializeValue(d.Canceled)
	m["ErrorMessage"] = serializeValue(d.ErrorMessage)
	m["Message"] = serializeValue(d.Message)
	if d.Data != nil { m["Data"] = serializeValue(d.Data) }
	return m
}

// CommandResultData represents CommandResultData.
type CommandResultData struct {
	Value string `json:"Value,omitempty"`
	Format CommandResultFormat `json:"Format,omitempty"`
	DisplayImmediately bool `json:"DisplayImmediately,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *CommandResultData) ToMap() map[string]any {
	m := map[string]any{}
	m["Value"] = serializeValue(d.Value)
	m["Format"] = serializeValue(d.Format)
	m["DisplayImmediately"] = serializeValue(d.DisplayImmediately)
	return m
}

// ResourceUrlAnnotation represents ResourceUrlAnnotation.
type ResourceUrlAnnotation struct {
	Url string `json:"Url,omitempty"`
	DisplayText string `json:"DisplayText,omitempty"`
	Endpoint EndpointReference `json:"Endpoint,omitempty"`
	DisplayLocation UrlDisplayLocation `json:"DisplayLocation,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *ResourceUrlAnnotation) ToMap() map[string]any {
	m := map[string]any{}
	m["Url"] = serializeValue(d.Url)
	m["DisplayText"] = serializeValue(d.DisplayText)
	m["Endpoint"] = serializeValue(d.Endpoint)
	m["DisplayLocation"] = serializeValue(d.DisplayLocation)
	return m
}

// ============================================================================
// Exported Values
// ============================================================================

var WellKnownPipelineSteps = struct {
	BeforeStart string
	Build string
	BuildPrereq string
	CheckContainerRuntime string
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
	BeforeStart: "before-start",
	Build: "build",
	BuildPrereq: "build-prereq",
	CheckContainerRuntime: "check-container-runtime",
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
// Marker interfaces (from interface-metadata types)
// ============================================================================

// ConfigurationSection marks types implementing IConfigurationSection.
// Marker interface.
type ConfigurationSection interface {
	handleReference
}

// DistributedApplicationEvent marks types implementing IDistributedApplicationEvent.
// Marker interface.
type DistributedApplicationEvent interface {
	handleReference
}

// DistributedApplicationResourceEvent marks types implementing IDistributedApplicationResourceEvent.
// Marker interface.
type DistributedApplicationResourceEvent interface {
	handleReference
}

// ExpressionValue marks types implementing IExpressionValue.
// Marker interface.
type ExpressionValue interface {
	handleReference
}

// Resource marks types implementing IResource.
// Methods are emitted on concrete impls; this interface is a marker for type assertions.
type Resource interface {
	handleReference
}

// ResourceWithArgs marks types implementing IResourceWithArgs.
// Methods are emitted on concrete impls; this interface is a marker for type assertions.
type ResourceWithArgs interface {
	handleReference
}

// ResourceWithEndpoints marks types implementing IResourceWithEndpoints.
// Methods are emitted on concrete impls; this interface is a marker for type assertions.
type ResourceWithEndpoints interface {
	handleReference
}

// ResourceWithEnvironment marks types implementing IResourceWithEnvironment.
// Methods are emitted on concrete impls; this interface is a marker for type assertions.
type ResourceWithEnvironment interface {
	handleReference
}

// ResourceWithParent marks types implementing IResourceWithParent.
// Marker interface.
type ResourceWithParent interface {
	handleReference
}

// ResourceWithWaitSupport marks types implementing IResourceWithWaitSupport.
// Methods are emitted on concrete impls; this interface is a marker for type assertions.
type ResourceWithWaitSupport interface {
	handleReference
}

// ============================================================================
// Handle wrappers
// ============================================================================

// AfterResourcesCreatedEvent is the public interface for handle type AfterResourcesCreatedEvent.
type AfterResourcesCreatedEvent interface {
	handleReference
	Model() DistributedApplicationModel
	Services() ServiceProvider
	Err() error
}

// afterResourcesCreatedEvent is the unexported impl of AfterResourcesCreatedEvent.
type afterResourcesCreatedEvent struct {
	*resourceBuilderBase
}

// newAfterResourcesCreatedEventFromHandle wraps an existing handle as AfterResourcesCreatedEvent.
func newAfterResourcesCreatedEventFromHandle(h *handle, c *client) AfterResourcesCreatedEvent {
	return &afterResourcesCreatedEvent{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Model gets the Model property
func (s *afterResourcesCreatedEvent) Model() DistributedApplicationModel {
	if s.err != nil { return &distributedApplicationModel{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/AfterResourcesCreatedEvent.model", reqArgs)
	if err != nil {
		return &distributedApplicationModel{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/AfterResourcesCreatedEvent.model returned unexpected type %T", result)
		return &distributedApplicationModel{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationModel{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Services gets the Services property
func (s *afterResourcesCreatedEvent) Services() ServiceProvider {
	if s.err != nil { return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/AfterResourcesCreatedEvent.services", reqArgs)
	if err != nil {
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/AfterResourcesCreatedEvent.services returned unexpected type %T", result)
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &serviceProvider{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// AspireStore is the public interface for handle type AspireStore.
type AspireStore interface {
	handleReference
	GetFileNameWithContent(filenameTemplate string, sourceFilename string) (string, error)
	Err() error
}

// aspireStore is the unexported impl of AspireStore.
type aspireStore struct {
	*resourceBuilderBase
}

// newAspireStoreFromHandle wraps an existing handle as AspireStore.
func newAspireStoreFromHandle(h *handle, c *client) AspireStore {
	return &aspireStore{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// GetFileNameWithContent gets a deterministic file path for the specified file contents
func (s *aspireStore) GetFileNameWithContent(filenameTemplate string, sourceFilename string) (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"aspireStore": s.handle.ToJSON(),
	}
	reqArgs["filenameTemplate"] = serializeValue(filenameTemplate)
	reqArgs["sourceFilename"] = serializeValue(sourceFilename)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getFileNameWithContent", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// BeforeResourceStartedEvent is the public interface for handle type BeforeResourceStartedEvent.
type BeforeResourceStartedEvent interface {
	handleReference
	Resource() Resource
	Services() ServiceProvider
	Err() error
}

// beforeResourceStartedEvent is the unexported impl of BeforeResourceStartedEvent.
type beforeResourceStartedEvent struct {
	*resourceBuilderBase
}

// newBeforeResourceStartedEventFromHandle wraps an existing handle as BeforeResourceStartedEvent.
func newBeforeResourceStartedEventFromHandle(h *handle, c *client) BeforeResourceStartedEvent {
	return &beforeResourceStartedEvent{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Resource gets the Resource property
func (s *beforeResourceStartedEvent) Resource() Resource {
	if s.err != nil { return nil }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/BeforeResourceStartedEvent.resource", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(Resource)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/BeforeResourceStartedEvent.resource returned unexpected type %T", result))
		return nil
	}
	return typed
}

// Services gets the Services property
func (s *beforeResourceStartedEvent) Services() ServiceProvider {
	if s.err != nil { return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/BeforeResourceStartedEvent.services", reqArgs)
	if err != nil {
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/BeforeResourceStartedEvent.services returned unexpected type %T", result)
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &serviceProvider{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// BeforeStartEvent is the public interface for handle type BeforeStartEvent.
type BeforeStartEvent interface {
	handleReference
	Model() DistributedApplicationModel
	Services() ServiceProvider
	Err() error
}

// beforeStartEvent is the unexported impl of BeforeStartEvent.
type beforeStartEvent struct {
	*resourceBuilderBase
}

// newBeforeStartEventFromHandle wraps an existing handle as BeforeStartEvent.
func newBeforeStartEventFromHandle(h *handle, c *client) BeforeStartEvent {
	return &beforeStartEvent{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Model gets the Model property
func (s *beforeStartEvent) Model() DistributedApplicationModel {
	if s.err != nil { return &distributedApplicationModel{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/BeforeStartEvent.model", reqArgs)
	if err != nil {
		return &distributedApplicationModel{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/BeforeStartEvent.model returned unexpected type %T", result)
		return &distributedApplicationModel{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationModel{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Services gets the Services property
func (s *beforeStartEvent) Services() ServiceProvider {
	if s.err != nil { return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/BeforeStartEvent.services", reqArgs)
	if err != nil {
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/BeforeStartEvent.services returned unexpected type %T", result)
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &serviceProvider{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// CSharpAppResource is the public interface for handle type CSharpAppResource.
type CSharpAppResource interface {
	handleReference
	AsHttp2Service() CSharpAppResource
	CreateExecutionConfiguration() ExecutionConfigurationBuilder
	DisableForwardedHeaders() CSharpAppResource
	ExcludeFromManifest() CSharpAppResource
	ExcludeFromMcp() CSharpAppResource
	GetEndpoint(name string) EndpointReference
	GetResourceName() (string, error)
	OnBeforeResourceStarted(callback func(arg BeforeResourceStartedEvent)) CSharpAppResource
	OnInitializeResource(callback func(arg InitializeResourceEvent)) CSharpAppResource
	OnResourceEndpointsAllocated(callback func(arg ResourceEndpointsAllocatedEvent)) CSharpAppResource
	OnResourceReady(callback func(arg ResourceReadyEvent)) CSharpAppResource
	OnResourceStopped(callback func(arg ResourceStoppedEvent)) CSharpAppResource
	PublishAsDockerFile(options ...*PublishAsDockerFileOptions) CSharpAppResource
	PublishWithContainerFiles(source ResourceWithContainerFiles, destinationPath string) CSharpAppResource
	WaitFor(dependency Resource, options ...*WaitForOptions) CSharpAppResource
	WaitForCompletion(dependency Resource, options ...*WaitForCompletionOptions) CSharpAppResource
	WaitForStart(dependency Resource, options ...*WaitForStartOptions) CSharpAppResource
	WithArgs(args []string) CSharpAppResource
	WithArgsCallback(callback func(obj CommandLineArgsCallbackContext)) CSharpAppResource
	WithCertificateTrustScope(scope CertificateTrustScope) CSharpAppResource
	WithChildRelationship(child Resource) CSharpAppResource
	WithCommand(name string, displayName string, executeCommand func(arg ExecuteCommandContext) *ExecuteCommandResult, options ...*WithCommandOptions) CSharpAppResource
	WithContainerRegistry(registry Resource) CSharpAppResource
	WithDeveloperCertificateTrust(trust bool) CSharpAppResource
	WithDockerfileBaseImage(options ...*WithDockerfileBaseImageOptions) CSharpAppResource
	WithEndpoint(options ...*WithEndpointOptions) CSharpAppResource
	WithEndpointCallback(endpointName string, callback func(obj EndpointUpdateContext), options ...*WithEndpointCallbackOptions) CSharpAppResource
	WithEnvironment(name string, value any) CSharpAppResource
	WithEnvironmentCallback(callback func(arg EnvironmentCallbackContext)) CSharpAppResource
	WithExplicitStart() CSharpAppResource
	WithExternalHttpEndpoints() CSharpAppResource
	WithHealthCheck(key string) CSharpAppResource
	WithHttpCommand(path string, displayName string, options ...*WithHttpCommandOptions) CSharpAppResource
	WithHttpEndpoint(options ...*WithHttpEndpointOptions) CSharpAppResource
	WithHttpEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpEndpointCallbackOptions) CSharpAppResource
	WithHttpHealthCheck(options ...*WithHttpHealthCheckOptions) CSharpAppResource
	WithHttpProbe(probeType ProbeType, options ...*WithHttpProbeOptions) CSharpAppResource
	WithHttpsDeveloperCertificate(options ...*WithHttpsDeveloperCertificateOptions) CSharpAppResource
	WithHttpsEndpoint(options ...*WithHttpsEndpointOptions) CSharpAppResource
	WithHttpsEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpsEndpointCallbackOptions) CSharpAppResource
	WithIconName(iconName string, options ...*WithIconNameOptions) CSharpAppResource
	WithImagePushOptions(callback func(arg ContainerImagePushOptionsCallbackContext)) CSharpAppResource
	WithMcpServer(options ...*WithMcpServerOptions) CSharpAppResource
	WithOtlpExporter(options ...*WithOtlpExporterOptions) CSharpAppResource
	WithParentRelationship(parent Resource) CSharpAppResource
	WithPipelineConfiguration(callback func(obj PipelineConfigurationContext)) CSharpAppResource
	WithPipelineStepFactory(stepName string, callback func(arg PipelineStepContext), options ...*WithPipelineStepFactoryOptions) CSharpAppResource
	WithReference(source any, options ...*WithReferenceOptions) CSharpAppResource
	WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) CSharpAppResource
	WithRelationship(resourceBuilder Resource, type_ string) CSharpAppResource
	WithRemoteImageName(remoteImageName string) CSharpAppResource
	WithRemoteImageTag(remoteImageTag string) CSharpAppResource
	WithReplicas(replicas float64) CSharpAppResource
	WithRequiredCommand(command string, options ...*WithRequiredCommandOptions) CSharpAppResource
	WithUrl(url any, options ...*WithUrlOptions) CSharpAppResource
	WithUrlForEndpoint(endpointName string, callback func(obj *ResourceUrlAnnotation)) CSharpAppResource
	WithUrls(callback func(obj ResourceUrlsCallbackContext)) CSharpAppResource
	WithoutHttpsCertificate() CSharpAppResource
	Err() error
}

// cSharpAppResource is the unexported impl of CSharpAppResource.
type cSharpAppResource struct {
	*resourceBuilderBase
}

// newCSharpAppResourceFromHandle wraps an existing handle as CSharpAppResource.
func newCSharpAppResourceFromHandle(h *handle, c *client) CSharpAppResource {
	return &cSharpAppResource{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// AsHttp2Service configures resource for HTTP/2
func (s *cSharpAppResource) AsHttp2Service() CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/asHttp2Service", reqArgs); err != nil { s.setErr(err) }
	return s
}

// CreateExecutionConfiguration creates an execution configuration builder
func (s *cSharpAppResource) CreateExecutionConfiguration() ExecutionConfigurationBuilder {
	if s.err != nil { return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/createExecutionConfiguration returned unexpected type %T", result)
		return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &executionConfigurationBuilder{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// DisableForwardedHeaders disables forwarded headers for the project
func (s *cSharpAppResource) DisableForwardedHeaders() CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/disableForwardedHeaders", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *cSharpAppResource) ExcludeFromManifest() CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/excludeFromManifest", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *cSharpAppResource) ExcludeFromMcp() CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/excludeFromMcp", reqArgs); err != nil { s.setErr(err) }
	return s
}

// GetEndpoint gets an endpoint reference
func (s *cSharpAppResource) GetEndpoint(name string) EndpointReference {
	if s.err != nil { return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getEndpoint", reqArgs)
	if err != nil {
		return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/getEndpoint returned unexpected type %T", result)
		return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &endpointReference{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// GetResourceName gets the resource name
func (s *cSharpAppResource) GetResourceName() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *cSharpAppResource) OnBeforeResourceStarted(callback func(arg BeforeResourceStartedEvent)) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[BeforeResourceStartedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onBeforeResourceStarted", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *cSharpAppResource) OnInitializeResource(callback func(arg InitializeResourceEvent)) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[InitializeResourceEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onInitializeResource", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceEndpointsAllocated subscribes to the ResourceEndpointsAllocated event
func (s *cSharpAppResource) OnResourceEndpointsAllocated(callback func(arg ResourceEndpointsAllocatedEvent)) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceEndpointsAllocatedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceEndpointsAllocated", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceReady subscribes to the ResourceReady event
func (s *cSharpAppResource) OnResourceReady(callback func(arg ResourceReadyEvent)) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceReadyEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceReady", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *cSharpAppResource) OnResourceStopped(callback func(arg ResourceStoppedEvent)) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceStoppedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceStopped", reqArgs); err != nil { s.setErr(err) }
	return s
}

// PublishAsDockerFile publishes a project as a Docker file with optional container configuration
func (s *cSharpAppResource) PublishAsDockerFile(options ...*PublishAsDockerFileOptions) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &PublishAsDockerFileOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.Configure != nil {
			cb := merged.Configure
			shim := func(args ...any) any {
				cb(callbackArg[ContainerResource](args, 0))
				return nil
			}
			reqArgs["configure"] = s.client.registerCallback(shim)
		}
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/publishProjectAsDockerFileWithConfigure", reqArgs); err != nil { s.setErr(err) }
	return s
}

// PublishWithContainerFiles configures the resource to copy container files from the specified source during publishing
func (s *cSharpAppResource) PublishWithContainerFiles(source ResourceWithContainerFiles, destinationPath string) CSharpAppResource {
	if s.err != nil { return s }
	if source != nil { if err := source.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["source"] = serializeValue(source)
	reqArgs["destinationPath"] = serializeValue(destinationPath)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/publishWithContainerFilesFromResource", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WaitFor waits for another resource to be ready
func (s *cSharpAppResource) WaitFor(dependency Resource, options ...*WaitForOptions) CSharpAppResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if len(options) > 0 {
		merged := &WaitForOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitFor", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WaitForCompletion waits for resource completion
func (s *cSharpAppResource) WaitForCompletion(dependency Resource, options ...*WaitForCompletionOptions) CSharpAppResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if len(options) > 0 {
		merged := &WaitForCompletionOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitForResourceCompletion", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WaitForStart waits for another resource to start
func (s *cSharpAppResource) WaitForStart(dependency Resource, options ...*WaitForStartOptions) CSharpAppResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if len(options) > 0 {
		merged := &WaitForStartOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitForStart", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithArgs adds arguments
func (s *cSharpAppResource) WithArgs(args []string) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if args != nil { reqArgs["args"] = serializeValue(args) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withArgs", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithArgsCallback sets command-line arguments via callback
func (s *cSharpAppResource) WithArgsCallback(callback func(obj CommandLineArgsCallbackContext)) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[CommandLineArgsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withArgsCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCertificateTrustScope sets the certificate trust scope
func (s *cSharpAppResource) WithCertificateTrustScope(scope CertificateTrustScope) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["scope"] = serializeValue(scope)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withCertificateTrustScope", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithChildRelationship sets a child relationship
func (s *cSharpAppResource) WithChildRelationship(child Resource) CSharpAppResource {
	if s.err != nil { return s }
	if child != nil { if err := child.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["child"] = serializeValue(child)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderChildRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCommand adds a resource command
func (s *cSharpAppResource) WithCommand(name string, displayName string, executeCommand func(arg ExecuteCommandContext) *ExecuteCommandResult, options ...*WithCommandOptions) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["displayName"] = serializeValue(displayName)
	if executeCommand != nil {
		cb := executeCommand
		shim := func(args ...any) any {
			return cb(callbackArg[ExecuteCommandContext](args, 0))
		}
		reqArgs["executeCommand"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithContainerRegistry configures a resource to use a container registry
func (s *cSharpAppResource) WithContainerRegistry(registry Resource) CSharpAppResource {
	if s.err != nil { return s }
	if registry != nil { if err := registry.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["registry"] = serializeValue(registry)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withContainerRegistry", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDeveloperCertificateTrust configures developer certificate trust
func (s *cSharpAppResource) WithDeveloperCertificateTrust(trust bool) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["trust"] = serializeValue(trust)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withDeveloperCertificateTrust", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *cSharpAppResource) WithDockerfileBaseImage(options ...*WithDockerfileBaseImageOptions) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithDockerfileBaseImageOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withDockerfileBaseImage", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEndpoint adds a network endpoint
func (s *cSharpAppResource) WithEndpoint(options ...*WithEndpointOptions) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithEndpointOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEndpointCallback updates a named endpoint via callback
func (s *cSharpAppResource) WithEndpointCallback(endpointName string, callback func(obj EndpointUpdateContext), options ...*WithEndpointCallbackOptions) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EndpointUpdateContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithEndpointCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEndpointCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEnvironment sets an environment variable
// Allowed types for parameter value: string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue.
func (s *cSharpAppResource) WithEnvironment(name string, value any) CSharpAppResource {
	if s.err != nil { return s }
	switch value.(type) {
	case string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue:
	default:
		err := fmt.Errorf("aspire: WithEnvironment: parameter %q must be one of [string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue], got %T", "value", value)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if value != nil { reqArgs["value"] = serializeValue(value) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEnvironment", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEnvironmentCallback sets environment variables via callback
func (s *cSharpAppResource) WithEnvironmentCallback(callback func(arg EnvironmentCallbackContext)) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EnvironmentCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEnvironmentCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithExplicitStart prevents resource from starting automatically
func (s *cSharpAppResource) WithExplicitStart() CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withExplicitStart", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithExternalHttpEndpoints makes HTTP endpoints externally accessible
func (s *cSharpAppResource) WithExternalHttpEndpoints() CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withExternalHttpEndpoints", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHealthCheck adds a health check by key
func (s *cSharpAppResource) WithHealthCheck(key string) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["key"] = serializeValue(key)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHealthCheck", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpCommand adds an HTTP resource command
func (s *cSharpAppResource) WithHttpCommand(path string, displayName string, options ...*WithHttpCommandOptions) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["path"] = serializeValue(path)
	reqArgs["displayName"] = serializeValue(displayName)
	if len(options) > 0 {
		merged := &WithHttpCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpEndpoint adds an HTTP endpoint
func (s *cSharpAppResource) WithHttpEndpoint(options ...*WithHttpEndpointOptions) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpEndpointOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpEndpointCallback updates an HTTP endpoint via callback
func (s *cSharpAppResource) WithHttpEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpEndpointCallbackOptions) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EndpointUpdateContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithHttpEndpointCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpEndpointCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpHealthCheck adds an HTTP health check
func (s *cSharpAppResource) WithHttpHealthCheck(options ...*WithHttpHealthCheckOptions) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpHealthCheckOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpHealthCheck", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpProbe adds an HTTP health probe to the resource
func (s *cSharpAppResource) WithHttpProbe(probeType ProbeType, options ...*WithHttpProbeOptions) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["probeType"] = serializeValue(probeType)
	if len(options) > 0 {
		merged := &WithHttpProbeOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpProbe", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpsDeveloperCertificate configures HTTPS with a developer certificate
func (s *cSharpAppResource) WithHttpsDeveloperCertificate(options ...*WithHttpsDeveloperCertificateOptions) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpsDeveloperCertificateOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withParameterHttpsDeveloperCertificate", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpsEndpoint adds an HTTPS endpoint
func (s *cSharpAppResource) WithHttpsEndpoint(options ...*WithHttpsEndpointOptions) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpsEndpointOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpsEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpsEndpointCallback updates an HTTPS endpoint via callback
func (s *cSharpAppResource) WithHttpsEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpsEndpointCallbackOptions) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EndpointUpdateContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithHttpsEndpointCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpsEndpointCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithIconName sets the icon for the resource
func (s *cSharpAppResource) WithIconName(iconName string, options ...*WithIconNameOptions) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["iconName"] = serializeValue(iconName)
	if len(options) > 0 {
		merged := &WithIconNameOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withIconName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithImagePushOptions sets image push options via callback
func (s *cSharpAppResource) WithImagePushOptions(callback func(arg ContainerImagePushOptionsCallbackContext)) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ContainerImagePushOptionsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withImagePushOptions", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMcpServer configures an MCP server endpoint on the resource
func (s *cSharpAppResource) WithMcpServer(options ...*WithMcpServerOptions) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithMcpServerOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withMcpServer", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithOtlpExporter configures OTLP telemetry export
func (s *cSharpAppResource) WithOtlpExporter(options ...*WithOtlpExporterOptions) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithOtlpExporterOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withOtlpExporter", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithParentRelationship sets the parent relationship
func (s *cSharpAppResource) WithParentRelationship(parent Resource) CSharpAppResource {
	if s.err != nil { return s }
	if parent != nil { if err := parent.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["parent"] = serializeValue(parent)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderParentRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *cSharpAppResource) WithPipelineConfiguration(callback func(obj PipelineConfigurationContext)) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineConfigurationContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withPipelineConfiguration", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *cSharpAppResource) WithPipelineStepFactory(stepName string, callback func(arg PipelineStepContext), options ...*WithPipelineStepFactoryOptions) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["stepName"] = serializeValue(stepName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineStepContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithPipelineStepFactoryOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withPipelineStepFactory", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithReference adds a reference to another resource
// Allowed types for parameter source: Resource, EndpointReference, string.
func (s *cSharpAppResource) WithReference(source any, options ...*WithReferenceOptions) CSharpAppResource {
	if s.err != nil { return s }
	switch source.(type) {
	case Resource, EndpointReference, string:
	default:
		err := fmt.Errorf("aspire: WithReference: parameter %q must be one of [Resource, EndpointReference, string], got %T", "source", source)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if source != nil { reqArgs["source"] = serializeValue(source) }
	if len(options) > 0 {
		merged := &WithReferenceOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withReference", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithReferenceEnvironment configures which reference values are injected into environment variables
func (s *cSharpAppResource) WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if options != nil { reqArgs["options"] = serializeValue(options) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withReferenceEnvironment", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRelationship adds a relationship to another resource
func (s *cSharpAppResource) WithRelationship(resourceBuilder Resource, type_ string) CSharpAppResource {
	if s.err != nil { return s }
	if resourceBuilder != nil { if err := resourceBuilder.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["resourceBuilder"] = serializeValue(resourceBuilder)
	reqArgs["type"] = serializeValue(type_)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRemoteImageName sets the remote image name for publishing
func (s *cSharpAppResource) WithRemoteImageName(remoteImageName string) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["remoteImageName"] = serializeValue(remoteImageName)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRemoteImageName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRemoteImageTag sets the remote image tag for publishing
func (s *cSharpAppResource) WithRemoteImageTag(remoteImageTag string) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["remoteImageTag"] = serializeValue(remoteImageTag)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRemoteImageTag", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithReplicas sets the number of replicas
func (s *cSharpAppResource) WithReplicas(replicas float64) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["replicas"] = serializeValue(replicas)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withReplicas", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRequiredCommand adds a required command dependency
func (s *cSharpAppResource) WithRequiredCommand(command string, options ...*WithRequiredCommandOptions) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["command"] = serializeValue(command)
	if len(options) > 0 {
		merged := &WithRequiredCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRequiredCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrl adds or modifies displayed URLs
// Allowed types for parameter url: string, *ReferenceExpression.
func (s *cSharpAppResource) WithUrl(url any, options ...*WithUrlOptions) CSharpAppResource {
	if s.err != nil { return s }
	switch url.(type) {
	case string, *ReferenceExpression:
	default:
		err := fmt.Errorf("aspire: WithUrl: parameter %q must be one of [string, *ReferenceExpression], got %T", "url", url)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if url != nil { reqArgs["url"] = serializeValue(url) }
	if len(options) > 0 {
		merged := &WithUrlOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrl", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *cSharpAppResource) WithUrlForEndpoint(endpointName string, callback func(obj *ResourceUrlAnnotation)) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			arg0 := callbackArg[*ResourceUrlAnnotation](args, 0)
			cb(arg0)
			return map[string]any{
				"p0": serializeValue(arg0),
			}
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrlForEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrls customizes displayed URLs via callback
func (s *cSharpAppResource) WithUrls(callback func(obj ResourceUrlsCallbackContext)) CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceUrlsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrls", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithoutHttpsCertificate removes HTTPS certificate configuration
func (s *cSharpAppResource) WithoutHttpsCertificate() CSharpAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withoutHttpsCertificate", reqArgs); err != nil { s.setErr(err) }
	return s
}

// CommandLineArgsCallbackContext is the public interface for handle type CommandLineArgsCallbackContext.
type CommandLineArgsCallbackContext interface {
	handleReference
	Args() CommandLineArgsEditor
	ExecutionContext() DistributedApplicationExecutionContext
	Log() LogFacade
	Resource() Resource
	Err() error
}

// commandLineArgsCallbackContext is the unexported impl of CommandLineArgsCallbackContext.
type commandLineArgsCallbackContext struct {
	*resourceBuilderBase
}

// newCommandLineArgsCallbackContextFromHandle wraps an existing handle as CommandLineArgsCallbackContext.
func newCommandLineArgsCallbackContextFromHandle(h *handle, c *client) CommandLineArgsCallbackContext {
	return &commandLineArgsCallbackContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Args gets the command-line argument editor
func (s *commandLineArgsCallbackContext) Args() CommandLineArgsEditor {
	if s.err != nil { return &commandLineArgsEditor{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.args", reqArgs)
	if err != nil {
		return &commandLineArgsEditor{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.args returned unexpected type %T", result)
		return &commandLineArgsEditor{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &commandLineArgsEditor{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ExecutionContext gets the execution context for this callback invocation
func (s *commandLineArgsCallbackContext) ExecutionContext() DistributedApplicationExecutionContext {
	if s.err != nil { return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.executionContext", reqArgs)
	if err != nil {
		return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.executionContext returned unexpected type %T", result)
		return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationExecutionContext{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Log gets the callback logger facade
func (s *commandLineArgsCallbackContext) Log() LogFacade {
	if s.err != nil { return &logFacade{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.log", reqArgs)
	if err != nil {
		return &logFacade{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.log returned unexpected type %T", result)
		return &logFacade{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &logFacade{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Resource gets the resource associated with this callback
func (s *commandLineArgsCallbackContext) Resource() Resource {
	if s.err != nil { return nil }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.resource", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(Resource)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.resource returned unexpected type %T", result))
		return nil
	}
	return typed
}

// CommandLineArgsEditor is the public interface for handle type CommandLineArgsEditor.
type CommandLineArgsEditor interface {
	handleReference
	Add(value any) error
	Err() error
}

// commandLineArgsEditor is the unexported impl of CommandLineArgsEditor.
type commandLineArgsEditor struct {
	*resourceBuilderBase
}

// newCommandLineArgsEditorFromHandle wraps an existing handle as CommandLineArgsEditor.
func newCommandLineArgsEditorFromHandle(h *handle, c *client) CommandLineArgsEditor {
	return &commandLineArgsEditor{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Add adds a command-line argument
// Allowed types for parameter value: string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue.
func (s *commandLineArgsEditor) Add(value any) error {
	if s.err != nil { return s.err }
	switch value.(type) {
	case string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue:
	default:
		err := fmt.Errorf("aspire: Add: parameter %q must be one of [string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue], got %T", "value", value)
		return err
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	if value != nil { reqArgs["value"] = serializeValue(value) }
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/add", reqArgs)
	return err
}

// Configuration is the public interface for handle type Configuration.
type Configuration interface {
	handleReference
	Exists(key string) (bool, error)
	GetChildren() ([]ConfigurationSection, error)
	GetConfigValue(key string) (string, error)
	GetConnectionString(name string) (string, error)
	GetSection(key string) ConfigurationSection
	Err() error
}

// configuration is the unexported impl of Configuration.
type configuration struct {
	*resourceBuilderBase
}

// newConfigurationFromHandle wraps an existing handle as Configuration.
func newConfigurationFromHandle(h *handle, c *client) Configuration {
	return &configuration{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Exists checks whether a configuration section exists
func (s *configuration) Exists(key string) (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"configuration": s.handle.ToJSON(),
	}
	reqArgs["key"] = serializeValue(key)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/exists", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// GetChildren gets child configuration sections
func (s *configuration) GetChildren() ([]ConfigurationSection, error) {
	if s.err != nil { var zero []ConfigurationSection; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"configuration": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getChildren", reqArgs)
	if err != nil {
		var zero []ConfigurationSection
		return zero, err
	}
	return decodeAs[[]ConfigurationSection](result)
}

// GetConfigValue gets a configuration value by key
func (s *configuration) GetConfigValue(key string) (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"configuration": s.handle.ToJSON(),
	}
	reqArgs["key"] = serializeValue(key)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getConfigValue", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// GetConnectionString gets a connection string by name
func (s *configuration) GetConnectionString(name string) (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"configuration": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getConnectionString", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// GetSection gets a configuration section by key
func (s *configuration) GetSection(key string) ConfigurationSection {
	if s.err != nil { return nil }
	ctx := context.Background()
	reqArgs := map[string]any{
		"configuration": s.handle.ToJSON(),
	}
	reqArgs["key"] = serializeValue(key)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getSection", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(ConfigurationSection)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Aspire.Hosting/getSection returned unexpected type %T", result))
		return nil
	}
	return typed
}

// ConnectionStringAvailableEvent is the public interface for handle type ConnectionStringAvailableEvent.
type ConnectionStringAvailableEvent interface {
	handleReference
	Resource() Resource
	Services() ServiceProvider
	Err() error
}

// connectionStringAvailableEvent is the unexported impl of ConnectionStringAvailableEvent.
type connectionStringAvailableEvent struct {
	*resourceBuilderBase
}

// newConnectionStringAvailableEventFromHandle wraps an existing handle as ConnectionStringAvailableEvent.
func newConnectionStringAvailableEventFromHandle(h *handle, c *client) ConnectionStringAvailableEvent {
	return &connectionStringAvailableEvent{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Resource gets the Resource property
func (s *connectionStringAvailableEvent) Resource() Resource {
	if s.err != nil { return nil }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ConnectionStringAvailableEvent.resource", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(Resource)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/ConnectionStringAvailableEvent.resource returned unexpected type %T", result))
		return nil
	}
	return typed
}

// Services gets the Services property
func (s *connectionStringAvailableEvent) Services() ServiceProvider {
	if s.err != nil { return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ConnectionStringAvailableEvent.services", reqArgs)
	if err != nil {
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/ConnectionStringAvailableEvent.services returned unexpected type %T", result)
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &serviceProvider{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ContainerImagePushOptions is the public interface for handle type ContainerImagePushOptions.
type ContainerImagePushOptions interface {
	handleReference
	RemoteImageName() (string, error)
	RemoteImageTag() (string, error)
	SetRemoteImageName(value string) ContainerImagePushOptions
	SetRemoteImageTag(value string) ContainerImagePushOptions
	Err() error
}

// containerImagePushOptions is the unexported impl of ContainerImagePushOptions.
type containerImagePushOptions struct {
	*resourceBuilderBase
}

// newContainerImagePushOptionsFromHandle wraps an existing handle as ContainerImagePushOptions.
func newContainerImagePushOptionsFromHandle(h *handle, c *client) ContainerImagePushOptions {
	return &containerImagePushOptions{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// RemoteImageName gets the RemoteImageName property
func (s *containerImagePushOptions) RemoteImageName() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ContainerImagePushOptions.remoteImageName", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// RemoteImageTag gets the RemoteImageTag property
func (s *containerImagePushOptions) RemoteImageTag() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ContainerImagePushOptions.remoteImageTag", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// SetRemoteImageName sets the RemoteImageName property
func (s *containerImagePushOptions) SetRemoteImageName(value string) ContainerImagePushOptions {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ContainerImagePushOptions.setRemoteImageName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetRemoteImageTag sets the RemoteImageTag property
func (s *containerImagePushOptions) SetRemoteImageTag(value string) ContainerImagePushOptions {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ContainerImagePushOptions.setRemoteImageTag", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ContainerImagePushOptionsCallbackContext is the public interface for handle type ContainerImagePushOptionsCallbackContext.
type ContainerImagePushOptionsCallbackContext interface {
	handleReference
	CancellationToken() (*CancellationToken, error)
	Options() ContainerImagePushOptions
	Resource() Resource
	SetCancellationToken(options ...*SetCancellationTokenOptions) ContainerImagePushOptionsCallbackContext
	SetOptions(value ContainerImagePushOptions) ContainerImagePushOptionsCallbackContext
	SetResource(value Resource) ContainerImagePushOptionsCallbackContext
	Err() error
}

// containerImagePushOptionsCallbackContext is the unexported impl of ContainerImagePushOptionsCallbackContext.
type containerImagePushOptionsCallbackContext struct {
	*resourceBuilderBase
}

// newContainerImagePushOptionsCallbackContextFromHandle wraps an existing handle as ContainerImagePushOptionsCallbackContext.
func newContainerImagePushOptionsCallbackContextFromHandle(h *handle, c *client) ContainerImagePushOptionsCallbackContext {
	return &containerImagePushOptionsCallbackContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// CancellationToken gets the CancellationToken property
func (s *containerImagePushOptionsCallbackContext) CancellationToken() (*CancellationToken, error) {
	if s.err != nil { var zero *CancellationToken; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ContainerImagePushOptionsCallbackContext.cancellationToken", reqArgs)
	if err != nil {
		var zero *CancellationToken
		return zero, err
	}
	return decodeAs[*CancellationToken](result)
}

// Options gets the Options property
func (s *containerImagePushOptionsCallbackContext) Options() ContainerImagePushOptions {
	if s.err != nil { return &containerImagePushOptions{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ContainerImagePushOptionsCallbackContext.options", reqArgs)
	if err != nil {
		return &containerImagePushOptions{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/ContainerImagePushOptionsCallbackContext.options returned unexpected type %T", result)
		return &containerImagePushOptions{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &containerImagePushOptions{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Resource gets the Resource property
func (s *containerImagePushOptionsCallbackContext) Resource() Resource {
	if s.err != nil { return nil }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ContainerImagePushOptionsCallbackContext.resource", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(Resource)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/ContainerImagePushOptionsCallbackContext.resource returned unexpected type %T", result))
		return nil
	}
	return typed
}

// SetCancellationToken sets the CancellationToken property
func (s *containerImagePushOptionsCallbackContext) SetCancellationToken(options ...*SetCancellationTokenOptions) ContainerImagePushOptionsCallbackContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &SetCancellationTokenOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.Value != nil {
			ctx = merged.Value.Context()
			if id := s.client.registerCancellation(merged.Value); id != "" {
				reqArgs["value"] = id
			}
		}
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ContainerImagePushOptionsCallbackContext.setCancellationToken", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetOptions sets the Options property
func (s *containerImagePushOptionsCallbackContext) SetOptions(value ContainerImagePushOptions) ContainerImagePushOptionsCallbackContext {
	if s.err != nil { return s }
	if value != nil { if err := value.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ContainerImagePushOptionsCallbackContext.setOptions", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetResource sets the Resource property
func (s *containerImagePushOptionsCallbackContext) SetResource(value Resource) ContainerImagePushOptionsCallbackContext {
	if s.err != nil { return s }
	if value != nil { if err := value.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ContainerImagePushOptionsCallbackContext.setResource", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ContainerImageReference is the public interface for handle type ContainerImageReference.
type ContainerImageReference interface {
	handleReference
	Err() error
}

// containerImageReference is the unexported impl of ContainerImageReference.
type containerImageReference struct {
	*resourceBuilderBase
}

// newContainerImageReferenceFromHandle wraps an existing handle as ContainerImageReference.
func newContainerImageReferenceFromHandle(h *handle, c *client) ContainerImageReference {
	return &containerImageReference{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// ContainerMountAnnotation is the public interface for handle type ContainerMountAnnotation.
type ContainerMountAnnotation interface {
	handleReference
	Err() error
}

// containerMountAnnotation is the unexported impl of ContainerMountAnnotation.
type containerMountAnnotation struct {
	*resourceBuilderBase
}

// newContainerMountAnnotationFromHandle wraps an existing handle as ContainerMountAnnotation.
func newContainerMountAnnotationFromHandle(h *handle, c *client) ContainerMountAnnotation {
	return &containerMountAnnotation{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// ContainerPortReference is the public interface for handle type ContainerPortReference.
type ContainerPortReference interface {
	handleReference
	Err() error
}

// containerPortReference is the unexported impl of ContainerPortReference.
type containerPortReference struct {
	*resourceBuilderBase
}

// newContainerPortReferenceFromHandle wraps an existing handle as ContainerPortReference.
func newContainerPortReferenceFromHandle(h *handle, c *client) ContainerPortReference {
	return &containerPortReference{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// ContainerRegistryResource is the public interface for handle type ContainerRegistryResource.
type ContainerRegistryResource interface {
	handleReference
	CreateExecutionConfiguration() ExecutionConfigurationBuilder
	ExcludeFromManifest() ContainerRegistryResource
	ExcludeFromMcp() ContainerRegistryResource
	GetResourceName() (string, error)
	OnBeforeResourceStarted(callback func(arg BeforeResourceStartedEvent)) ContainerRegistryResource
	OnInitializeResource(callback func(arg InitializeResourceEvent)) ContainerRegistryResource
	OnResourceReady(callback func(arg ResourceReadyEvent)) ContainerRegistryResource
	OnResourceStopped(callback func(arg ResourceStoppedEvent)) ContainerRegistryResource
	WithChildRelationship(child Resource) ContainerRegistryResource
	WithCommand(name string, displayName string, executeCommand func(arg ExecuteCommandContext) *ExecuteCommandResult, options ...*WithCommandOptions) ContainerRegistryResource
	WithContainerRegistry(registry Resource) ContainerRegistryResource
	WithDockerfileBaseImage(options ...*WithDockerfileBaseImageOptions) ContainerRegistryResource
	WithExplicitStart() ContainerRegistryResource
	WithHealthCheck(key string) ContainerRegistryResource
	WithIconName(iconName string, options ...*WithIconNameOptions) ContainerRegistryResource
	WithParentRelationship(parent Resource) ContainerRegistryResource
	WithPipelineConfiguration(callback func(obj PipelineConfigurationContext)) ContainerRegistryResource
	WithPipelineStepFactory(stepName string, callback func(arg PipelineStepContext), options ...*WithPipelineStepFactoryOptions) ContainerRegistryResource
	WithRelationship(resourceBuilder Resource, type_ string) ContainerRegistryResource
	WithRequiredCommand(command string, options ...*WithRequiredCommandOptions) ContainerRegistryResource
	WithUrl(url any, options ...*WithUrlOptions) ContainerRegistryResource
	WithUrlForEndpoint(endpointName string, callback func(obj *ResourceUrlAnnotation)) ContainerRegistryResource
	WithUrls(callback func(obj ResourceUrlsCallbackContext)) ContainerRegistryResource
	Err() error
}

// containerRegistryResource is the unexported impl of ContainerRegistryResource.
type containerRegistryResource struct {
	*resourceBuilderBase
}

// newContainerRegistryResourceFromHandle wraps an existing handle as ContainerRegistryResource.
func newContainerRegistryResourceFromHandle(h *handle, c *client) ContainerRegistryResource {
	return &containerRegistryResource{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// CreateExecutionConfiguration creates an execution configuration builder
func (s *containerRegistryResource) CreateExecutionConfiguration() ExecutionConfigurationBuilder {
	if s.err != nil { return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/createExecutionConfiguration returned unexpected type %T", result)
		return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &executionConfigurationBuilder{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *containerRegistryResource) ExcludeFromManifest() ContainerRegistryResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/excludeFromManifest", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *containerRegistryResource) ExcludeFromMcp() ContainerRegistryResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/excludeFromMcp", reqArgs); err != nil { s.setErr(err) }
	return s
}

// GetResourceName gets the resource name
func (s *containerRegistryResource) GetResourceName() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *containerRegistryResource) OnBeforeResourceStarted(callback func(arg BeforeResourceStartedEvent)) ContainerRegistryResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[BeforeResourceStartedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onBeforeResourceStarted", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *containerRegistryResource) OnInitializeResource(callback func(arg InitializeResourceEvent)) ContainerRegistryResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[InitializeResourceEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onInitializeResource", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceReady subscribes to the ResourceReady event
func (s *containerRegistryResource) OnResourceReady(callback func(arg ResourceReadyEvent)) ContainerRegistryResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceReadyEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceReady", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *containerRegistryResource) OnResourceStopped(callback func(arg ResourceStoppedEvent)) ContainerRegistryResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceStoppedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceStopped", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithChildRelationship sets a child relationship
func (s *containerRegistryResource) WithChildRelationship(child Resource) ContainerRegistryResource {
	if s.err != nil { return s }
	if child != nil { if err := child.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["child"] = serializeValue(child)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderChildRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCommand adds a resource command
func (s *containerRegistryResource) WithCommand(name string, displayName string, executeCommand func(arg ExecuteCommandContext) *ExecuteCommandResult, options ...*WithCommandOptions) ContainerRegistryResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["displayName"] = serializeValue(displayName)
	if executeCommand != nil {
		cb := executeCommand
		shim := func(args ...any) any {
			return cb(callbackArg[ExecuteCommandContext](args, 0))
		}
		reqArgs["executeCommand"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithContainerRegistry configures a resource to use a container registry
func (s *containerRegistryResource) WithContainerRegistry(registry Resource) ContainerRegistryResource {
	if s.err != nil { return s }
	if registry != nil { if err := registry.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["registry"] = serializeValue(registry)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withContainerRegistry", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *containerRegistryResource) WithDockerfileBaseImage(options ...*WithDockerfileBaseImageOptions) ContainerRegistryResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithDockerfileBaseImageOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withDockerfileBaseImage", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithExplicitStart prevents resource from starting automatically
func (s *containerRegistryResource) WithExplicitStart() ContainerRegistryResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withExplicitStart", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHealthCheck adds a health check by key
func (s *containerRegistryResource) WithHealthCheck(key string) ContainerRegistryResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["key"] = serializeValue(key)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHealthCheck", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithIconName sets the icon for the resource
func (s *containerRegistryResource) WithIconName(iconName string, options ...*WithIconNameOptions) ContainerRegistryResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["iconName"] = serializeValue(iconName)
	if len(options) > 0 {
		merged := &WithIconNameOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withIconName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithParentRelationship sets the parent relationship
func (s *containerRegistryResource) WithParentRelationship(parent Resource) ContainerRegistryResource {
	if s.err != nil { return s }
	if parent != nil { if err := parent.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["parent"] = serializeValue(parent)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderParentRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *containerRegistryResource) WithPipelineConfiguration(callback func(obj PipelineConfigurationContext)) ContainerRegistryResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineConfigurationContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withPipelineConfiguration", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *containerRegistryResource) WithPipelineStepFactory(stepName string, callback func(arg PipelineStepContext), options ...*WithPipelineStepFactoryOptions) ContainerRegistryResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["stepName"] = serializeValue(stepName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineStepContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithPipelineStepFactoryOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withPipelineStepFactory", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRelationship adds a relationship to another resource
func (s *containerRegistryResource) WithRelationship(resourceBuilder Resource, type_ string) ContainerRegistryResource {
	if s.err != nil { return s }
	if resourceBuilder != nil { if err := resourceBuilder.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["resourceBuilder"] = serializeValue(resourceBuilder)
	reqArgs["type"] = serializeValue(type_)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRequiredCommand adds a required command dependency
func (s *containerRegistryResource) WithRequiredCommand(command string, options ...*WithRequiredCommandOptions) ContainerRegistryResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["command"] = serializeValue(command)
	if len(options) > 0 {
		merged := &WithRequiredCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRequiredCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrl adds or modifies displayed URLs
// Allowed types for parameter url: string, *ReferenceExpression.
func (s *containerRegistryResource) WithUrl(url any, options ...*WithUrlOptions) ContainerRegistryResource {
	if s.err != nil { return s }
	switch url.(type) {
	case string, *ReferenceExpression:
	default:
		err := fmt.Errorf("aspire: WithUrl: parameter %q must be one of [string, *ReferenceExpression], got %T", "url", url)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if url != nil { reqArgs["url"] = serializeValue(url) }
	if len(options) > 0 {
		merged := &WithUrlOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrl", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *containerRegistryResource) WithUrlForEndpoint(endpointName string, callback func(obj *ResourceUrlAnnotation)) ContainerRegistryResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			arg0 := callbackArg[*ResourceUrlAnnotation](args, 0)
			cb(arg0)
			return map[string]any{
				"p0": serializeValue(arg0),
			}
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrlForEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrls customizes displayed URLs via callback
func (s *containerRegistryResource) WithUrls(callback func(obj ResourceUrlsCallbackContext)) ContainerRegistryResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceUrlsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrls", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ContainerResource is the public interface for handle type ContainerResource.
type ContainerResource interface {
	handleReference
	AsHttp2Service() ContainerResource
	CreateExecutionConfiguration() ExecutionConfigurationBuilder
	ExcludeFromManifest() ContainerResource
	ExcludeFromMcp() ContainerResource
	GetEndpoint(name string) EndpointReference
	GetResourceName() (string, error)
	OnBeforeResourceStarted(callback func(arg BeforeResourceStartedEvent)) ContainerResource
	OnInitializeResource(callback func(arg InitializeResourceEvent)) ContainerResource
	OnResourceEndpointsAllocated(callback func(arg ResourceEndpointsAllocatedEvent)) ContainerResource
	OnResourceReady(callback func(arg ResourceReadyEvent)) ContainerResource
	OnResourceStopped(callback func(arg ResourceStoppedEvent)) ContainerResource
	PublishAsConnectionString() ContainerResource
	PublishAsContainer() ContainerResource
	WaitFor(dependency Resource, options ...*WaitForOptions) ContainerResource
	WaitForCompletion(dependency Resource, options ...*WaitForCompletionOptions) ContainerResource
	WaitForStart(dependency Resource, options ...*WaitForStartOptions) ContainerResource
	WithArgs(args []string) ContainerResource
	WithArgsCallback(callback func(obj CommandLineArgsCallbackContext)) ContainerResource
	WithBindMount(source string, target string, options ...*WithBindMountOptions) ContainerResource
	WithBuildArg(name string, value any) ContainerResource
	WithBuildSecret(name string, value ParameterResource) ContainerResource
	WithCertificateTrustScope(scope CertificateTrustScope) ContainerResource
	WithChildRelationship(child Resource) ContainerResource
	WithCommand(name string, displayName string, executeCommand func(arg ExecuteCommandContext) *ExecuteCommandResult, options ...*WithCommandOptions) ContainerResource
	WithContainerCertificatePaths(options ...*WithContainerCertificatePathsOptions) ContainerResource
	WithContainerName(name string) ContainerResource
	WithContainerNetworkAlias(alias string) ContainerResource
	WithContainerRegistry(registry Resource) ContainerResource
	WithContainerRuntimeArgs(args []string) ContainerResource
	WithDeveloperCertificateTrust(trust bool) ContainerResource
	WithDockerfile(contextPath string, options ...*WithDockerfileOptions) ContainerResource
	WithDockerfileBaseImage(options ...*WithDockerfileBaseImageOptions) ContainerResource
	WithDockerfileBuilder(contextPath string, callback func(arg DockerfileBuilderCallbackContext), options ...*WithDockerfileBuilderOptions) ContainerResource
	WithEndpoint(options ...*WithEndpointOptions) ContainerResource
	WithEndpointCallback(endpointName string, callback func(obj EndpointUpdateContext), options ...*WithEndpointCallbackOptions) ContainerResource
	WithEndpointProxySupport(proxyEnabled bool) ContainerResource
	WithEntrypoint(entrypoint string) ContainerResource
	WithEnvironment(name string, value any) ContainerResource
	WithEnvironmentCallback(callback func(arg EnvironmentCallbackContext)) ContainerResource
	WithExplicitStart() ContainerResource
	WithExternalHttpEndpoints() ContainerResource
	WithHealthCheck(key string) ContainerResource
	WithHttpCommand(path string, displayName string, options ...*WithHttpCommandOptions) ContainerResource
	WithHttpEndpoint(options ...*WithHttpEndpointOptions) ContainerResource
	WithHttpEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpEndpointCallbackOptions) ContainerResource
	WithHttpHealthCheck(options ...*WithHttpHealthCheckOptions) ContainerResource
	WithHttpProbe(probeType ProbeType, options ...*WithHttpProbeOptions) ContainerResource
	WithHttpsDeveloperCertificate(options ...*WithHttpsDeveloperCertificateOptions) ContainerResource
	WithHttpsEndpoint(options ...*WithHttpsEndpointOptions) ContainerResource
	WithHttpsEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpsEndpointCallbackOptions) ContainerResource
	WithIconName(iconName string, options ...*WithIconNameOptions) ContainerResource
	WithImage(image string, options ...*WithImageOptions) ContainerResource
	WithImagePullPolicy(pullPolicy ImagePullPolicy) ContainerResource
	WithImagePushOptions(callback func(arg ContainerImagePushOptionsCallbackContext)) ContainerResource
	WithImageRegistry(registry string) ContainerResource
	WithImageSHA256(sha256 string) ContainerResource
	WithImageTag(tag string) ContainerResource
	WithLifetime(lifetime ContainerLifetime) ContainerResource
	WithMcpServer(options ...*WithMcpServerOptions) ContainerResource
	WithOtlpExporter(options ...*WithOtlpExporterOptions) ContainerResource
	WithParentRelationship(parent Resource) ContainerResource
	WithPipelineConfiguration(callback func(obj PipelineConfigurationContext)) ContainerResource
	WithPipelineStepFactory(stepName string, callback func(arg PipelineStepContext), options ...*WithPipelineStepFactoryOptions) ContainerResource
	WithReference(source any, options ...*WithReferenceOptions) ContainerResource
	WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) ContainerResource
	WithRelationship(resourceBuilder Resource, type_ string) ContainerResource
	WithRemoteImageName(remoteImageName string) ContainerResource
	WithRemoteImageTag(remoteImageTag string) ContainerResource
	WithRequiredCommand(command string, options ...*WithRequiredCommandOptions) ContainerResource
	WithUrl(url any, options ...*WithUrlOptions) ContainerResource
	WithUrlForEndpoint(endpointName string, callback func(obj *ResourceUrlAnnotation)) ContainerResource
	WithUrls(callback func(obj ResourceUrlsCallbackContext)) ContainerResource
	WithVolume(target string, options ...*WithVolumeOptions) ContainerResource
	WithoutHttpsCertificate() ContainerResource
	Err() error
}

// containerResource is the unexported impl of ContainerResource.
type containerResource struct {
	*resourceBuilderBase
}

// newContainerResourceFromHandle wraps an existing handle as ContainerResource.
func newContainerResourceFromHandle(h *handle, c *client) ContainerResource {
	return &containerResource{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// AsHttp2Service configures resource for HTTP/2
func (s *containerResource) AsHttp2Service() ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/asHttp2Service", reqArgs); err != nil { s.setErr(err) }
	return s
}

// CreateExecutionConfiguration creates an execution configuration builder
func (s *containerResource) CreateExecutionConfiguration() ExecutionConfigurationBuilder {
	if s.err != nil { return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/createExecutionConfiguration returned unexpected type %T", result)
		return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &executionConfigurationBuilder{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *containerResource) ExcludeFromManifest() ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/excludeFromManifest", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *containerResource) ExcludeFromMcp() ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/excludeFromMcp", reqArgs); err != nil { s.setErr(err) }
	return s
}

// GetEndpoint gets an endpoint reference
func (s *containerResource) GetEndpoint(name string) EndpointReference {
	if s.err != nil { return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getEndpoint", reqArgs)
	if err != nil {
		return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/getEndpoint returned unexpected type %T", result)
		return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &endpointReference{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// GetResourceName gets the resource name
func (s *containerResource) GetResourceName() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *containerResource) OnBeforeResourceStarted(callback func(arg BeforeResourceStartedEvent)) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[BeforeResourceStartedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onBeforeResourceStarted", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *containerResource) OnInitializeResource(callback func(arg InitializeResourceEvent)) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[InitializeResourceEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onInitializeResource", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceEndpointsAllocated subscribes to the ResourceEndpointsAllocated event
func (s *containerResource) OnResourceEndpointsAllocated(callback func(arg ResourceEndpointsAllocatedEvent)) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceEndpointsAllocatedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceEndpointsAllocated", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceReady subscribes to the ResourceReady event
func (s *containerResource) OnResourceReady(callback func(arg ResourceReadyEvent)) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceReadyEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceReady", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *containerResource) OnResourceStopped(callback func(arg ResourceStoppedEvent)) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceStoppedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceStopped", reqArgs); err != nil { s.setErr(err) }
	return s
}

// PublishAsConnectionString publishes the resource as a connection string
func (s *containerResource) PublishAsConnectionString() ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/publishAsConnectionString", reqArgs); err != nil { s.setErr(err) }
	return s
}

// PublishAsContainer configures the resource to be published as a container
func (s *containerResource) PublishAsContainer() ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/publishAsContainer", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WaitFor waits for another resource to be ready
func (s *containerResource) WaitFor(dependency Resource, options ...*WaitForOptions) ContainerResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if len(options) > 0 {
		merged := &WaitForOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitFor", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WaitForCompletion waits for resource completion
func (s *containerResource) WaitForCompletion(dependency Resource, options ...*WaitForCompletionOptions) ContainerResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if len(options) > 0 {
		merged := &WaitForCompletionOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitForResourceCompletion", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WaitForStart waits for another resource to start
func (s *containerResource) WaitForStart(dependency Resource, options ...*WaitForStartOptions) ContainerResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if len(options) > 0 {
		merged := &WaitForStartOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitForStart", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithArgs adds arguments
func (s *containerResource) WithArgs(args []string) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if args != nil { reqArgs["args"] = serializeValue(args) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withArgs", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithArgsCallback sets command-line arguments via callback
func (s *containerResource) WithArgsCallback(callback func(obj CommandLineArgsCallbackContext)) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[CommandLineArgsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withArgsCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithBindMount adds a bind mount
func (s *containerResource) WithBindMount(source string, target string, options ...*WithBindMountOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["source"] = serializeValue(source)
	reqArgs["target"] = serializeValue(target)
	if len(options) > 0 {
		merged := &WithBindMountOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBindMount", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithBuildArg adds a build argument from a string value or parameter resource
// Allowed types for parameter value: string, ParameterResource.
func (s *containerResource) WithBuildArg(name string, value any) ContainerResource {
	if s.err != nil { return s }
	switch value.(type) {
	case string, ParameterResource:
	default:
		err := fmt.Errorf("aspire: WithBuildArg: parameter %q must be one of [string, ParameterResource], got %T", "value", value)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if value != nil { reqArgs["value"] = serializeValue(value) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuildArg", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithBuildSecret adds a build secret from a parameter resource
func (s *containerResource) WithBuildSecret(name string, value ParameterResource) ContainerResource {
	if s.err != nil { return s }
	if value != nil { if err := value.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withParameterBuildSecret", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCertificateTrustScope sets the certificate trust scope
func (s *containerResource) WithCertificateTrustScope(scope CertificateTrustScope) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["scope"] = serializeValue(scope)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withCertificateTrustScope", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithChildRelationship sets a child relationship
func (s *containerResource) WithChildRelationship(child Resource) ContainerResource {
	if s.err != nil { return s }
	if child != nil { if err := child.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["child"] = serializeValue(child)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderChildRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCommand adds a resource command
func (s *containerResource) WithCommand(name string, displayName string, executeCommand func(arg ExecuteCommandContext) *ExecuteCommandResult, options ...*WithCommandOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["displayName"] = serializeValue(displayName)
	if executeCommand != nil {
		cb := executeCommand
		shim := func(args ...any) any {
			return cb(callbackArg[ExecuteCommandContext](args, 0))
		}
		reqArgs["executeCommand"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithContainerCertificatePaths overrides container certificate bundle and directory paths used for trust configuration
func (s *containerResource) WithContainerCertificatePaths(options ...*WithContainerCertificatePathsOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithContainerCertificatePathsOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withContainerCertificatePaths", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithContainerName sets the container name
func (s *containerResource) WithContainerName(name string) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withContainerName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithContainerNetworkAlias adds a network alias for the container
func (s *containerResource) WithContainerNetworkAlias(alias string) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["alias"] = serializeValue(alias)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withContainerNetworkAlias", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithContainerRegistry configures a resource to use a container registry
func (s *containerResource) WithContainerRegistry(registry Resource) ContainerResource {
	if s.err != nil { return s }
	if registry != nil { if err := registry.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["registry"] = serializeValue(registry)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withContainerRegistry", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithContainerRuntimeArgs adds runtime arguments for the container
func (s *containerResource) WithContainerRuntimeArgs(args []string) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if args != nil { reqArgs["args"] = serializeValue(args) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withContainerRuntimeArgs", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDeveloperCertificateTrust configures developer certificate trust
func (s *containerResource) WithDeveloperCertificateTrust(trust bool) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["trust"] = serializeValue(trust)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withDeveloperCertificateTrust", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDockerfile configures the resource to use a Dockerfile
func (s *containerResource) WithDockerfile(contextPath string, options ...*WithDockerfileOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["contextPath"] = serializeValue(contextPath)
	if len(options) > 0 {
		merged := &WithDockerfileOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withDockerfile", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *containerResource) WithDockerfileBaseImage(options ...*WithDockerfileBaseImageOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithDockerfileBaseImageOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withDockerfileBaseImage", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDockerfileBuilder configures the resource to use a programmatically generated Dockerfile
func (s *containerResource) WithDockerfileBuilder(contextPath string, callback func(arg DockerfileBuilderCallbackContext), options ...*WithDockerfileBuilderOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["contextPath"] = serializeValue(contextPath)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[DockerfileBuilderCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithDockerfileBuilderOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withDockerfileBuilder", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEndpoint adds a network endpoint
func (s *containerResource) WithEndpoint(options ...*WithEndpointOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithEndpointOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEndpointCallback updates a named endpoint via callback
func (s *containerResource) WithEndpointCallback(endpointName string, callback func(obj EndpointUpdateContext), options ...*WithEndpointCallbackOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EndpointUpdateContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithEndpointCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEndpointCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEndpointProxySupport configures endpoint proxy support
func (s *containerResource) WithEndpointProxySupport(proxyEnabled bool) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["proxyEnabled"] = serializeValue(proxyEnabled)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEndpointProxySupport", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEntrypoint sets the container entrypoint
func (s *containerResource) WithEntrypoint(entrypoint string) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["entrypoint"] = serializeValue(entrypoint)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEntrypoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEnvironment sets an environment variable
// Allowed types for parameter value: string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue.
func (s *containerResource) WithEnvironment(name string, value any) ContainerResource {
	if s.err != nil { return s }
	switch value.(type) {
	case string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue:
	default:
		err := fmt.Errorf("aspire: WithEnvironment: parameter %q must be one of [string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue], got %T", "value", value)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if value != nil { reqArgs["value"] = serializeValue(value) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEnvironment", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEnvironmentCallback sets environment variables via callback
func (s *containerResource) WithEnvironmentCallback(callback func(arg EnvironmentCallbackContext)) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EnvironmentCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEnvironmentCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithExplicitStart prevents resource from starting automatically
func (s *containerResource) WithExplicitStart() ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withExplicitStart", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithExternalHttpEndpoints makes HTTP endpoints externally accessible
func (s *containerResource) WithExternalHttpEndpoints() ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withExternalHttpEndpoints", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHealthCheck adds a health check by key
func (s *containerResource) WithHealthCheck(key string) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["key"] = serializeValue(key)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHealthCheck", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpCommand adds an HTTP resource command
func (s *containerResource) WithHttpCommand(path string, displayName string, options ...*WithHttpCommandOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["path"] = serializeValue(path)
	reqArgs["displayName"] = serializeValue(displayName)
	if len(options) > 0 {
		merged := &WithHttpCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpEndpoint adds an HTTP endpoint
func (s *containerResource) WithHttpEndpoint(options ...*WithHttpEndpointOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpEndpointOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpEndpointCallback updates an HTTP endpoint via callback
func (s *containerResource) WithHttpEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpEndpointCallbackOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EndpointUpdateContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithHttpEndpointCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpEndpointCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpHealthCheck adds an HTTP health check
func (s *containerResource) WithHttpHealthCheck(options ...*WithHttpHealthCheckOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpHealthCheckOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpHealthCheck", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpProbe adds an HTTP health probe to the resource
func (s *containerResource) WithHttpProbe(probeType ProbeType, options ...*WithHttpProbeOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["probeType"] = serializeValue(probeType)
	if len(options) > 0 {
		merged := &WithHttpProbeOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpProbe", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpsDeveloperCertificate configures HTTPS with a developer certificate
func (s *containerResource) WithHttpsDeveloperCertificate(options ...*WithHttpsDeveloperCertificateOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpsDeveloperCertificateOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withParameterHttpsDeveloperCertificate", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpsEndpoint adds an HTTPS endpoint
func (s *containerResource) WithHttpsEndpoint(options ...*WithHttpsEndpointOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpsEndpointOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpsEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpsEndpointCallback updates an HTTPS endpoint via callback
func (s *containerResource) WithHttpsEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpsEndpointCallbackOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EndpointUpdateContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithHttpsEndpointCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpsEndpointCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithIconName sets the icon for the resource
func (s *containerResource) WithIconName(iconName string, options ...*WithIconNameOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["iconName"] = serializeValue(iconName)
	if len(options) > 0 {
		merged := &WithIconNameOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withIconName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithImage sets the container image
func (s *containerResource) WithImage(image string, options ...*WithImageOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["image"] = serializeValue(image)
	if len(options) > 0 {
		merged := &WithImageOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withImage", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithImagePullPolicy sets the container image pull policy
func (s *containerResource) WithImagePullPolicy(pullPolicy ImagePullPolicy) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["pullPolicy"] = serializeValue(pullPolicy)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withImagePullPolicy", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithImagePushOptions sets image push options via callback
func (s *containerResource) WithImagePushOptions(callback func(arg ContainerImagePushOptionsCallbackContext)) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ContainerImagePushOptionsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withImagePushOptions", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithImageRegistry sets the container image registry
func (s *containerResource) WithImageRegistry(registry string) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["registry"] = serializeValue(registry)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withImageRegistry", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithImageSHA256 sets the image SHA256 digest
func (s *containerResource) WithImageSHA256(sha256 string) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["sha256"] = serializeValue(sha256)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withImageSHA256", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithImageTag sets the container image tag
func (s *containerResource) WithImageTag(tag string) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["tag"] = serializeValue(tag)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withImageTag", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithLifetime sets the lifetime behavior of the container resource
func (s *containerResource) WithLifetime(lifetime ContainerLifetime) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["lifetime"] = serializeValue(lifetime)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withLifetime", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMcpServer configures an MCP server endpoint on the resource
func (s *containerResource) WithMcpServer(options ...*WithMcpServerOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithMcpServerOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withMcpServer", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithOtlpExporter configures OTLP telemetry export
func (s *containerResource) WithOtlpExporter(options ...*WithOtlpExporterOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithOtlpExporterOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withOtlpExporter", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithParentRelationship sets the parent relationship
func (s *containerResource) WithParentRelationship(parent Resource) ContainerResource {
	if s.err != nil { return s }
	if parent != nil { if err := parent.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["parent"] = serializeValue(parent)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderParentRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *containerResource) WithPipelineConfiguration(callback func(obj PipelineConfigurationContext)) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineConfigurationContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withPipelineConfiguration", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *containerResource) WithPipelineStepFactory(stepName string, callback func(arg PipelineStepContext), options ...*WithPipelineStepFactoryOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["stepName"] = serializeValue(stepName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineStepContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithPipelineStepFactoryOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withPipelineStepFactory", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithReference adds a reference to another resource
// Allowed types for parameter source: Resource, EndpointReference, string.
func (s *containerResource) WithReference(source any, options ...*WithReferenceOptions) ContainerResource {
	if s.err != nil { return s }
	switch source.(type) {
	case Resource, EndpointReference, string:
	default:
		err := fmt.Errorf("aspire: WithReference: parameter %q must be one of [Resource, EndpointReference, string], got %T", "source", source)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if source != nil { reqArgs["source"] = serializeValue(source) }
	if len(options) > 0 {
		merged := &WithReferenceOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withReference", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithReferenceEnvironment configures which reference values are injected into environment variables
func (s *containerResource) WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if options != nil { reqArgs["options"] = serializeValue(options) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withReferenceEnvironment", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRelationship adds a relationship to another resource
func (s *containerResource) WithRelationship(resourceBuilder Resource, type_ string) ContainerResource {
	if s.err != nil { return s }
	if resourceBuilder != nil { if err := resourceBuilder.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["resourceBuilder"] = serializeValue(resourceBuilder)
	reqArgs["type"] = serializeValue(type_)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRemoteImageName sets the remote image name for publishing
func (s *containerResource) WithRemoteImageName(remoteImageName string) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["remoteImageName"] = serializeValue(remoteImageName)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRemoteImageName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRemoteImageTag sets the remote image tag for publishing
func (s *containerResource) WithRemoteImageTag(remoteImageTag string) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["remoteImageTag"] = serializeValue(remoteImageTag)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRemoteImageTag", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRequiredCommand adds a required command dependency
func (s *containerResource) WithRequiredCommand(command string, options ...*WithRequiredCommandOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["command"] = serializeValue(command)
	if len(options) > 0 {
		merged := &WithRequiredCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRequiredCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrl adds or modifies displayed URLs
// Allowed types for parameter url: string, *ReferenceExpression.
func (s *containerResource) WithUrl(url any, options ...*WithUrlOptions) ContainerResource {
	if s.err != nil { return s }
	switch url.(type) {
	case string, *ReferenceExpression:
	default:
		err := fmt.Errorf("aspire: WithUrl: parameter %q must be one of [string, *ReferenceExpression], got %T", "url", url)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if url != nil { reqArgs["url"] = serializeValue(url) }
	if len(options) > 0 {
		merged := &WithUrlOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrl", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *containerResource) WithUrlForEndpoint(endpointName string, callback func(obj *ResourceUrlAnnotation)) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			arg0 := callbackArg[*ResourceUrlAnnotation](args, 0)
			cb(arg0)
			return map[string]any{
				"p0": serializeValue(arg0),
			}
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrlForEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrls customizes displayed URLs via callback
func (s *containerResource) WithUrls(callback func(obj ResourceUrlsCallbackContext)) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceUrlsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrls", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithVolume adds a volume
func (s *containerResource) WithVolume(target string, options ...*WithVolumeOptions) ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	reqArgs["target"] = serializeValue(target)
	if len(options) > 0 {
		merged := &WithVolumeOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withVolume", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithoutHttpsCertificate removes HTTPS certificate configuration
func (s *containerResource) WithoutHttpsCertificate() ContainerResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withoutHttpsCertificate", reqArgs); err != nil { s.setErr(err) }
	return s
}

// DistributedApplication is the public interface for handle type DistributedApplication.
type DistributedApplication interface {
	handleReference
	Run(options ...*RunOptions) error
	Err() error
}

// distributedApplication is the unexported impl of DistributedApplication.
type distributedApplication struct {
	*resourceBuilderBase
}

// newDistributedApplicationFromHandle wraps an existing handle as DistributedApplication.
func newDistributedApplicationFromHandle(h *handle, c *client) DistributedApplication {
	return &distributedApplication{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Run runs the distributed application
func (s *distributedApplication) Run(options ...*RunOptions) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &RunOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.CancellationToken != nil {
			ctx = merged.CancellationToken.Context()
			if id := s.client.registerCancellation(merged.CancellationToken); id != "" {
				reqArgs["cancellationToken"] = id
			}
		}
	}
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/run", reqArgs)
	return err
}

// DistributedApplicationBuilder is the public interface for handle type DistributedApplicationBuilder.
type DistributedApplicationBuilder interface {
	handleReference
	AddCSharpApp(name string, path string, options ...*AddCSharpAppOptions) CSharpAppResource
	AddConnectionString(name string, options ...*AddConnectionStringOptions) ResourceWithConnectionString
	AddContainer(name string, image any) ContainerResource
	AddContainerRegistry(name string, endpoint any, options ...*AddContainerRegistryOptions) ContainerRegistryResource
	AddDockerfile(name string, contextPath string, options ...*AddDockerfileOptions) ContainerResource
	AddDockerfileBuilder(name string, contextPath string, callback func(arg DockerfileBuilderCallbackContext), options ...*AddDockerfileBuilderOptions) ContainerResource
	AddDotnetTool(name string, packageId string) DotnetToolResource
	AddEventingSubscriber(subscribe func(arg EventingSubscriberRegistrationContext)) error
	AddExecutable(name string, command string, workingDirectory string, args []string) ExecutableResource
	AddExternalService(name string, url any) ExternalServiceResource
	AddGoApp(name string, appDirectory string, options ...*AddGoAppOptions) GoAppResource
	AddParameter(name string, options ...*AddParameterOptions) ParameterResource
	AddParameterFromConfiguration(name string, configurationKey string, options ...*AddParameterFromConfigurationOptions) ParameterResource
	AddParameterWithGeneratedValue(name string, value *GenerateParameterDefault, options ...*AddParameterWithGeneratedValueOptions) ParameterResource
	AddProject(name string, projectPath string, options ...*AddProjectOptions) ProjectResource
	AppHostDirectory() (string, error)
	Environment() HostEnvironment
	Eventing() DistributedApplicationEventing
	ExecutionContext() DistributedApplicationExecutionContext
	GetConfiguration() Configuration
	Pipeline() DistributedApplicationPipeline
	SubscribeAfterResourcesCreated(callback func(arg AfterResourcesCreatedEvent)) DistributedApplicationEventSubscription
	SubscribeBeforeStart(callback func(arg BeforeStartEvent)) DistributedApplicationEventSubscription
	TryAddEventingSubscriber(subscribe func(arg EventingSubscriberRegistrationContext)) error
	UserSecretsManager() UserSecretsManager
	Build() (DistributedApplication, error)
	Err() error
}

// distributedApplicationBuilder is the unexported impl of DistributedApplicationBuilder.
type distributedApplicationBuilder struct {
	*resourceBuilderBase
}

// newDistributedApplicationBuilderFromHandle wraps an existing handle as DistributedApplicationBuilder.
func newDistributedApplicationBuilderFromHandle(h *handle, c *client) DistributedApplicationBuilder {
	return &distributedApplicationBuilder{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// AddCSharpApp adds a C# application resource
func (s *distributedApplicationBuilder) AddCSharpApp(name string, path string, options ...*AddCSharpAppOptions) CSharpAppResource {
	if s.err != nil { return &cSharpAppResource{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["path"] = serializeValue(path)
	if len(options) > 0 {
		merged := &AddCSharpAppOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/addCSharpApp", reqArgs)
	if err != nil {
		return &cSharpAppResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/addCSharpApp returned unexpected type %T", result)
		return &cSharpAppResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &cSharpAppResource{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// AddConnectionString adds a connection string resource
func (s *distributedApplicationBuilder) AddConnectionString(name string, options ...*AddConnectionStringOptions) ResourceWithConnectionString {
	if s.err != nil { return &resourceWithConnectionString{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if len(options) > 0 {
		merged := &AddConnectionStringOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/addConnectionString", reqArgs)
	if err != nil {
		return &resourceWithConnectionString{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/addConnectionString returned unexpected type %T", result)
		return &resourceWithConnectionString{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &resourceWithConnectionString{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// AddContainer adds a container resource
// Allowed types for parameter image: string, *AddContainerOptions.
func (s *distributedApplicationBuilder) AddContainer(name string, image any) ContainerResource {
	if s.err != nil { return &containerResource{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	switch image.(type) {
	case string, *AddContainerOptions:
	default:
		err := fmt.Errorf("aspire: AddContainer: parameter %q must be one of [string, *AddContainerOptions], got %T", "image", image)
		return &containerResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if image != nil { reqArgs["image"] = serializeValue(image) }
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/addContainer", reqArgs)
	if err != nil {
		return &containerResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/addContainer returned unexpected type %T", result)
		return &containerResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &containerResource{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// AddContainerRegistry adds a container registry resource
// Allowed types for parameter endpoint: string, ParameterResource.
func (s *distributedApplicationBuilder) AddContainerRegistry(name string, endpoint any, options ...*AddContainerRegistryOptions) ContainerRegistryResource {
	if s.err != nil { return &containerRegistryResource{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	switch endpoint.(type) {
	case string, ParameterResource:
	default:
		err := fmt.Errorf("aspire: AddContainerRegistry: parameter %q must be one of [string, ParameterResource], got %T", "endpoint", endpoint)
		return &containerRegistryResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if endpoint != nil { reqArgs["endpoint"] = serializeValue(endpoint) }
	if len(options) > 0 {
		merged := &AddContainerRegistryOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/addContainerRegistry", reqArgs)
	if err != nil {
		return &containerRegistryResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/addContainerRegistry returned unexpected type %T", result)
		return &containerRegistryResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &containerRegistryResource{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// AddDockerfile adds a container resource built from a Dockerfile
func (s *distributedApplicationBuilder) AddDockerfile(name string, contextPath string, options ...*AddDockerfileOptions) ContainerResource {
	if s.err != nil { return &containerResource{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["contextPath"] = serializeValue(contextPath)
	if len(options) > 0 {
		merged := &AddDockerfileOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/addDockerfile", reqArgs)
	if err != nil {
		return &containerResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/addDockerfile returned unexpected type %T", result)
		return &containerResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &containerResource{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// AddDockerfileBuilder adds a container resource built from a programmatically generated Dockerfile
func (s *distributedApplicationBuilder) AddDockerfileBuilder(name string, contextPath string, callback func(arg DockerfileBuilderCallbackContext), options ...*AddDockerfileBuilderOptions) ContainerResource {
	if s.err != nil { return &containerResource{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["contextPath"] = serializeValue(contextPath)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[DockerfileBuilderCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &AddDockerfileBuilderOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/addDockerfileBuilder", reqArgs)
	if err != nil {
		return &containerResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/addDockerfileBuilder returned unexpected type %T", result)
		return &containerResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &containerResource{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// AddDotnetTool adds a .NET tool resource
func (s *distributedApplicationBuilder) AddDotnetTool(name string, packageId string) DotnetToolResource {
	if s.err != nil { return &dotnetToolResource{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["packageId"] = serializeValue(packageId)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/addDotnetTool", reqArgs)
	if err != nil {
		return &dotnetToolResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/addDotnetTool returned unexpected type %T", result)
		return &dotnetToolResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &dotnetToolResource{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// AddEventingSubscriber adds an eventing subscriber
func (s *distributedApplicationBuilder) AddEventingSubscriber(subscribe func(arg EventingSubscriberRegistrationContext)) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if subscribe != nil {
		cb := subscribe
		shim := func(args ...any) any {
			cb(callbackArg[EventingSubscriberRegistrationContext](args, 0))
			return nil
		}
		reqArgs["subscribe"] = s.client.registerCallback(shim)
	}
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/addEventingSubscriber", reqArgs)
	return err
}

// AddExecutable adds an executable resource
func (s *distributedApplicationBuilder) AddExecutable(name string, command string, workingDirectory string, args []string) ExecutableResource {
	if s.err != nil { return &executableResource{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["command"] = serializeValue(command)
	reqArgs["workingDirectory"] = serializeValue(workingDirectory)
	if args != nil { reqArgs["args"] = serializeValue(args) }
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/addExecutable", reqArgs)
	if err != nil {
		return &executableResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/addExecutable returned unexpected type %T", result)
		return &executableResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &executableResource{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// AddExternalService adds an external service resource
// Allowed types for parameter url: string, ParameterResource.
func (s *distributedApplicationBuilder) AddExternalService(name string, url any) ExternalServiceResource {
	if s.err != nil { return &externalServiceResource{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	switch url.(type) {
	case string, ParameterResource:
	default:
		err := fmt.Errorf("aspire: AddExternalService: parameter %q must be one of [string, ParameterResource], got %T", "url", url)
		return &externalServiceResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if url != nil { reqArgs["url"] = serializeValue(url) }
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/addExternalService", reqArgs)
	if err != nil {
		return &externalServiceResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/addExternalService returned unexpected type %T", result)
		return &externalServiceResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &externalServiceResource{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// AddGoApp adds a Go application resource
func (s *distributedApplicationBuilder) AddGoApp(name string, appDirectory string, options ...*AddGoAppOptions) GoAppResource {
	if s.err != nil { return &goAppResource{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["appDirectory"] = serializeValue(appDirectory)
	if len(options) > 0 {
		merged := &AddGoAppOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Go/addGoApp", reqArgs)
	if err != nil {
		return &goAppResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.Go/addGoApp returned unexpected type %T", result)
		return &goAppResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &goAppResource{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// AddParameter adds a parameter resource
func (s *distributedApplicationBuilder) AddParameter(name string, options ...*AddParameterOptions) ParameterResource {
	if s.err != nil { return &parameterResource{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if len(options) > 0 {
		merged := &AddParameterOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/addParameter", reqArgs)
	if err != nil {
		return &parameterResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/addParameter returned unexpected type %T", result)
		return &parameterResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &parameterResource{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// AddParameterFromConfiguration adds a parameter sourced from configuration
func (s *distributedApplicationBuilder) AddParameterFromConfiguration(name string, configurationKey string, options ...*AddParameterFromConfigurationOptions) ParameterResource {
	if s.err != nil { return &parameterResource{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["configurationKey"] = serializeValue(configurationKey)
	if len(options) > 0 {
		merged := &AddParameterFromConfigurationOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/addParameterFromConfiguration", reqArgs)
	if err != nil {
		return &parameterResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/addParameterFromConfiguration returned unexpected type %T", result)
		return &parameterResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &parameterResource{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// AddParameterWithGeneratedValue adds a parameter with a generated default value
func (s *distributedApplicationBuilder) AddParameterWithGeneratedValue(name string, value *GenerateParameterDefault, options ...*AddParameterWithGeneratedValueOptions) ParameterResource {
	if s.err != nil { return &parameterResource{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if value != nil { reqArgs["value"] = serializeValue(value) }
	if len(options) > 0 {
		merged := &AddParameterWithGeneratedValueOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/addParameterWithGeneratedValue", reqArgs)
	if err != nil {
		return &parameterResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/addParameterWithGeneratedValue returned unexpected type %T", result)
		return &parameterResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &parameterResource{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// AddProject adds a .NET project resource
func (s *distributedApplicationBuilder) AddProject(name string, projectPath string, options ...*AddProjectOptions) ProjectResource {
	if s.err != nil { return &projectResource{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["projectPath"] = serializeValue(projectPath)
	if len(options) > 0 {
		merged := &AddProjectOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/addProject", reqArgs)
	if err != nil {
		return &projectResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/addProject returned unexpected type %T", result)
		return &projectResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &projectResource{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// AppHostDirectory gets the AppHostDirectory property
func (s *distributedApplicationBuilder) AppHostDirectory() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/IDistributedApplicationBuilder.appHostDirectory", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// Environment gets the Environment property
func (s *distributedApplicationBuilder) Environment() HostEnvironment {
	if s.err != nil { return &hostEnvironment{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/IDistributedApplicationBuilder.environment", reqArgs)
	if err != nil {
		return &hostEnvironment{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/IDistributedApplicationBuilder.environment returned unexpected type %T", result)
		return &hostEnvironment{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &hostEnvironment{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Eventing gets the Eventing property
func (s *distributedApplicationBuilder) Eventing() DistributedApplicationEventing {
	if s.err != nil { return &distributedApplicationEventing{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/IDistributedApplicationBuilder.eventing", reqArgs)
	if err != nil {
		return &distributedApplicationEventing{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/IDistributedApplicationBuilder.eventing returned unexpected type %T", result)
		return &distributedApplicationEventing{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationEventing{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ExecutionContext gets the ExecutionContext property
func (s *distributedApplicationBuilder) ExecutionContext() DistributedApplicationExecutionContext {
	if s.err != nil { return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/IDistributedApplicationBuilder.executionContext", reqArgs)
	if err != nil {
		return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/IDistributedApplicationBuilder.executionContext returned unexpected type %T", result)
		return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationExecutionContext{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// GetConfiguration gets the application configuration
func (s *distributedApplicationBuilder) GetConfiguration() Configuration {
	if s.err != nil { return &configuration{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getConfiguration", reqArgs)
	if err != nil {
		return &configuration{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/getConfiguration returned unexpected type %T", result)
		return &configuration{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &configuration{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Pipeline gets the Pipeline property
func (s *distributedApplicationBuilder) Pipeline() DistributedApplicationPipeline {
	if s.err != nil { return &distributedApplicationPipeline{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/IDistributedApplicationBuilder.pipeline", reqArgs)
	if err != nil {
		return &distributedApplicationPipeline{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/IDistributedApplicationBuilder.pipeline returned unexpected type %T", result)
		return &distributedApplicationPipeline{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationPipeline{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// SubscribeAfterResourcesCreated subscribes to the AfterResourcesCreated event
func (s *distributedApplicationBuilder) SubscribeAfterResourcesCreated(callback func(arg AfterResourcesCreatedEvent)) DistributedApplicationEventSubscription {
	if s.err != nil { return &distributedApplicationEventSubscription{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[AfterResourcesCreatedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/subscribeAfterResourcesCreated", reqArgs)
	if err != nil {
		return &distributedApplicationEventSubscription{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/subscribeAfterResourcesCreated returned unexpected type %T", result)
		return &distributedApplicationEventSubscription{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationEventSubscription{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// SubscribeBeforeStart subscribes to the BeforeStart event
func (s *distributedApplicationBuilder) SubscribeBeforeStart(callback func(arg BeforeStartEvent)) DistributedApplicationEventSubscription {
	if s.err != nil { return &distributedApplicationEventSubscription{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[BeforeStartEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/subscribeBeforeStart", reqArgs)
	if err != nil {
		return &distributedApplicationEventSubscription{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/subscribeBeforeStart returned unexpected type %T", result)
		return &distributedApplicationEventSubscription{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationEventSubscription{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// TryAddEventingSubscriber attempts to add an eventing subscriber
func (s *distributedApplicationBuilder) TryAddEventingSubscriber(subscribe func(arg EventingSubscriberRegistrationContext)) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if subscribe != nil {
		cb := subscribe
		shim := func(args ...any) any {
			cb(callbackArg[EventingSubscriberRegistrationContext](args, 0))
			return nil
		}
		reqArgs["subscribe"] = s.client.registerCallback(shim)
	}
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/tryAddEventingSubscriber", reqArgs)
	return err
}

// UserSecretsManager gets the UserSecretsManager property
func (s *distributedApplicationBuilder) UserSecretsManager() UserSecretsManager {
	if s.err != nil { return &userSecretsManager{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/IDistributedApplicationBuilder.userSecretsManager", reqArgs)
	if err != nil {
		return &userSecretsManager{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/IDistributedApplicationBuilder.userSecretsManager returned unexpected type %T", result)
		return &userSecretsManager{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &userSecretsManager{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// DistributedApplicationEventSubscription is the public interface for handle type DistributedApplicationEventSubscription.
type DistributedApplicationEventSubscription interface {
	handleReference
	Err() error
}

// distributedApplicationEventSubscription is the unexported impl of DistributedApplicationEventSubscription.
type distributedApplicationEventSubscription struct {
	*resourceBuilderBase
}

// newDistributedApplicationEventSubscriptionFromHandle wraps an existing handle as DistributedApplicationEventSubscription.
func newDistributedApplicationEventSubscriptionFromHandle(h *handle, c *client) DistributedApplicationEventSubscription {
	return &distributedApplicationEventSubscription{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// DistributedApplicationEventing is the public interface for handle type DistributedApplicationEventing.
type DistributedApplicationEventing interface {
	handleReference
	Unsubscribe(subscription DistributedApplicationEventSubscription) error
	Err() error
}

// distributedApplicationEventing is the unexported impl of DistributedApplicationEventing.
type distributedApplicationEventing struct {
	*resourceBuilderBase
}

// newDistributedApplicationEventingFromHandle wraps an existing handle as DistributedApplicationEventing.
func newDistributedApplicationEventingFromHandle(h *handle, c *client) DistributedApplicationEventing {
	return &distributedApplicationEventing{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Unsubscribe invokes the Unsubscribe method
func (s *distributedApplicationEventing) Unsubscribe(subscription DistributedApplicationEventSubscription) error {
	if s.err != nil { return s.err }
	if subscription != nil { if err := subscription.Err(); err != nil { return err } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["subscription"] = serializeValue(subscription)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Eventing/IDistributedApplicationEventing.unsubscribe", reqArgs)
	return err
}

// DistributedApplicationExecutionContext is the public interface for handle type DistributedApplicationExecutionContext.
type DistributedApplicationExecutionContext interface {
	handleReference
	IsPublishMode() (bool, error)
	IsRunMode() (bool, error)
	Operation() (DistributedApplicationOperation, error)
	PublisherName() (string, error)
	ServiceProvider() ServiceProvider
	SetPublisherName(value string) DistributedApplicationExecutionContext
	Err() error
}

// distributedApplicationExecutionContext is the unexported impl of DistributedApplicationExecutionContext.
type distributedApplicationExecutionContext struct {
	*resourceBuilderBase
}

// newDistributedApplicationExecutionContextFromHandle wraps an existing handle as DistributedApplicationExecutionContext.
func newDistributedApplicationExecutionContextFromHandle(h *handle, c *client) DistributedApplicationExecutionContext {
	return &distributedApplicationExecutionContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// IsPublishMode gets the IsPublishMode property
func (s *distributedApplicationExecutionContext) IsPublishMode() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/DistributedApplicationExecutionContext.isPublishMode", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// IsRunMode gets the IsRunMode property
func (s *distributedApplicationExecutionContext) IsRunMode() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/DistributedApplicationExecutionContext.isRunMode", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// Operation gets the Operation property
func (s *distributedApplicationExecutionContext) Operation() (DistributedApplicationOperation, error) {
	if s.err != nil { var zero DistributedApplicationOperation; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/DistributedApplicationExecutionContext.operation", reqArgs)
	if err != nil {
		var zero DistributedApplicationOperation
		return zero, err
	}
	return decodeAs[DistributedApplicationOperation](result)
}

// PublisherName gets the PublisherName property
func (s *distributedApplicationExecutionContext) PublisherName() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/DistributedApplicationExecutionContext.publisherName", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// ServiceProvider gets the ServiceProvider property
func (s *distributedApplicationExecutionContext) ServiceProvider() ServiceProvider {
	if s.err != nil { return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/DistributedApplicationExecutionContext.serviceProvider", reqArgs)
	if err != nil {
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/DistributedApplicationExecutionContext.serviceProvider returned unexpected type %T", result)
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &serviceProvider{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// SetPublisherName sets the PublisherName property
func (s *distributedApplicationExecutionContext) SetPublisherName(value string) DistributedApplicationExecutionContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/DistributedApplicationExecutionContext.setPublisherName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// DistributedApplicationExecutionContextOptions is the public interface for handle type DistributedApplicationExecutionContextOptions.
type DistributedApplicationExecutionContextOptions interface {
	handleReference
	Err() error
}

// distributedApplicationExecutionContextOptions is the unexported impl of DistributedApplicationExecutionContextOptions.
type distributedApplicationExecutionContextOptions struct {
	*resourceBuilderBase
}

// newDistributedApplicationExecutionContextOptionsFromHandle wraps an existing handle as DistributedApplicationExecutionContextOptions.
func newDistributedApplicationExecutionContextOptionsFromHandle(h *handle, c *client) DistributedApplicationExecutionContextOptions {
	return &distributedApplicationExecutionContextOptions{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// DistributedApplicationModel is the public interface for handle type DistributedApplicationModel.
type DistributedApplicationModel interface {
	handleReference
	FindResourceByName(name string) Resource
	GetResources() ([]Resource, error)
	Err() error
}

// distributedApplicationModel is the unexported impl of DistributedApplicationModel.
type distributedApplicationModel struct {
	*resourceBuilderBase
}

// newDistributedApplicationModelFromHandle wraps an existing handle as DistributedApplicationModel.
func newDistributedApplicationModelFromHandle(h *handle, c *client) DistributedApplicationModel {
	return &distributedApplicationModel{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// FindResourceByName finds a resource by name
func (s *distributedApplicationModel) FindResourceByName(name string) Resource {
	if s.err != nil { return nil }
	ctx := context.Background()
	reqArgs := map[string]any{
		"model": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/findResourceByName", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(Resource)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Aspire.Hosting/findResourceByName returned unexpected type %T", result))
		return nil
	}
	return typed
}

// GetResources gets resources from the distributed application model
func (s *distributedApplicationModel) GetResources() ([]Resource, error) {
	if s.err != nil { var zero []Resource; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"model": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getResources", reqArgs)
	if err != nil {
		var zero []Resource
		return zero, err
	}
	return decodeAs[[]Resource](result)
}

// DistributedApplicationPipeline is the public interface for handle type DistributedApplicationPipeline.
type DistributedApplicationPipeline interface {
	handleReference
	AddStep(stepName string, callback func(arg PipelineStepContext), options ...*AddStepOptions) error
	Configure(callback func(arg PipelineConfigurationContext)) error
	DisableBuildOnlyContainerValidation() DistributedApplicationPipeline
	Err() error
}

// distributedApplicationPipeline is the unexported impl of DistributedApplicationPipeline.
type distributedApplicationPipeline struct {
	*resourceBuilderBase
}

// newDistributedApplicationPipelineFromHandle wraps an existing handle as DistributedApplicationPipeline.
func newDistributedApplicationPipelineFromHandle(h *handle, c *client) DistributedApplicationPipeline {
	return &distributedApplicationPipeline{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// AddStep adds a pipeline step to the application
func (s *distributedApplicationPipeline) AddStep(stepName string, callback func(arg PipelineStepContext), options ...*AddStepOptions) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"pipeline": s.handle.ToJSON(),
	}
	reqArgs["stepName"] = serializeValue(stepName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineStepContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &AddStepOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/addStep", reqArgs)
	return err
}

// Configure configures the application pipeline via a callback
func (s *distributedApplicationPipeline) Configure(callback func(arg PipelineConfigurationContext)) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"pipeline": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineConfigurationContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/configure", reqArgs)
	return err
}

// DisableBuildOnlyContainerValidation disables publish and deploy validation for unconsumed build-only containers.
func (s *distributedApplicationPipeline) DisableBuildOnlyContainerValidation() DistributedApplicationPipeline {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"pipeline": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/disableBuildOnlyContainerValidation", reqArgs); err != nil { s.setErr(err) }
	return s
}

// DistributedApplicationResourceEventSubscription is the public interface for handle type DistributedApplicationResourceEventSubscription.
type DistributedApplicationResourceEventSubscription interface {
	handleReference
	Err() error
}

// distributedApplicationResourceEventSubscription is the unexported impl of DistributedApplicationResourceEventSubscription.
type distributedApplicationResourceEventSubscription struct {
	*resourceBuilderBase
}

// newDistributedApplicationResourceEventSubscriptionFromHandle wraps an existing handle as DistributedApplicationResourceEventSubscription.
func newDistributedApplicationResourceEventSubscriptionFromHandle(h *handle, c *client) DistributedApplicationResourceEventSubscription {
	return &distributedApplicationResourceEventSubscription{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// DockerfileBuilder is the public interface for handle type DockerfileBuilder.
type DockerfileBuilder interface {
	handleReference
	AddContainerFilesStages(resource Resource, options ...*AddContainerFilesStagesOptions) DockerfileBuilder
	Arg(name string, options ...*ArgOptions) DockerfileBuilder
	From(image string, options ...*FromOptions) DockerfileStage
	Err() error
}

// dockerfileBuilder is the unexported impl of DockerfileBuilder.
type dockerfileBuilder struct {
	*resourceBuilderBase
}

// newDockerfileBuilderFromHandle wraps an existing handle as DockerfileBuilder.
func newDockerfileBuilderFromHandle(h *handle, c *client) DockerfileBuilder {
	return &dockerfileBuilder{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// AddContainerFilesStages adds Dockerfile stages for published container files
func (s *dockerfileBuilder) AddContainerFilesStages(resource Resource, options ...*AddContainerFilesStagesOptions) DockerfileBuilder {
	if s.err != nil { return s }
	if resource != nil { if err := resource.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["resource"] = serializeValue(resource)
	if len(options) > 0 {
		merged := &AddContainerFilesStagesOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/dockerfileBuilderAddContainerFilesStages", reqArgs); err != nil { s.setErr(err) }
	return s
}

// Arg adds a global ARG statement to the Dockerfile
func (s *dockerfileBuilder) Arg(name string, options ...*ArgOptions) DockerfileBuilder {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if len(options) > 0 {
		merged := &ArgOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/dockerfileBuilderArg", reqArgs); err != nil { s.setErr(err) }
	return s
}

// From adds a FROM statement to start a Dockerfile stage
func (s *dockerfileBuilder) From(image string, options ...*FromOptions) DockerfileStage {
	if s.err != nil { return &dockerfileStage{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["image"] = serializeValue(image)
	if len(options) > 0 {
		merged := &FromOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/dockerfileBuilderFrom", reqArgs)
	if err != nil {
		return &dockerfileStage{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/dockerfileBuilderFrom returned unexpected type %T", result)
		return &dockerfileStage{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &dockerfileStage{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// DockerfileBuilderCallbackContext is the public interface for handle type DockerfileBuilderCallbackContext.
type DockerfileBuilderCallbackContext interface {
	handleReference
	Builder() DockerfileBuilder
	CancellationToken() (*CancellationToken, error)
	Resource() Resource
	Services() ServiceProvider
	Err() error
}

// dockerfileBuilderCallbackContext is the unexported impl of DockerfileBuilderCallbackContext.
type dockerfileBuilderCallbackContext struct {
	*resourceBuilderBase
}

// newDockerfileBuilderCallbackContextFromHandle wraps an existing handle as DockerfileBuilderCallbackContext.
func newDockerfileBuilderCallbackContextFromHandle(h *handle, c *client) DockerfileBuilderCallbackContext {
	return &dockerfileBuilderCallbackContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Builder gets the Builder property
func (s *dockerfileBuilderCallbackContext) Builder() DockerfileBuilder {
	if s.err != nil { return &dockerfileBuilder{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/DockerfileBuilderCallbackContext.builder", reqArgs)
	if err != nil {
		return &dockerfileBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/DockerfileBuilderCallbackContext.builder returned unexpected type %T", result)
		return &dockerfileBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &dockerfileBuilder{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// CancellationToken gets the CancellationToken property
func (s *dockerfileBuilderCallbackContext) CancellationToken() (*CancellationToken, error) {
	if s.err != nil { var zero *CancellationToken; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/DockerfileBuilderCallbackContext.cancellationToken", reqArgs)
	if err != nil {
		var zero *CancellationToken
		return zero, err
	}
	return decodeAs[*CancellationToken](result)
}

// Resource gets the Resource property
func (s *dockerfileBuilderCallbackContext) Resource() Resource {
	if s.err != nil { return nil }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/DockerfileBuilderCallbackContext.resource", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(Resource)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/DockerfileBuilderCallbackContext.resource returned unexpected type %T", result))
		return nil
	}
	return typed
}

// Services gets the Services property
func (s *dockerfileBuilderCallbackContext) Services() ServiceProvider {
	if s.err != nil { return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/DockerfileBuilderCallbackContext.services", reqArgs)
	if err != nil {
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/DockerfileBuilderCallbackContext.services returned unexpected type %T", result)
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &serviceProvider{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// DockerfileStage is the public interface for handle type DockerfileStage.
type DockerfileStage interface {
	handleReference
	AddContainerFiles(resource Resource, rootDestinationPath string, options ...*AddContainerFilesOptions) DockerfileStage
	Arg(name string, options ...*ArgOptions) DockerfileStage
	Cmd(command []string) DockerfileStage
	Comment(comment string) DockerfileStage
	Copy(source string, destination string, options ...*CopyOptions) DockerfileStage
	CopyFrom(from string, source string, destination string, options ...*CopyFromOptions) DockerfileStage
	EmptyLine() DockerfileStage
	Entrypoint(command []string) DockerfileStage
	Env(name string, value string) DockerfileStage
	Expose(port float64) DockerfileStage
	Run(command string) DockerfileStage
	RunWithMounts(command string, mounts []string) DockerfileStage
	User(user string) DockerfileStage
	WorkDir(path string) DockerfileStage
	Err() error
}

// dockerfileStage is the unexported impl of DockerfileStage.
type dockerfileStage struct {
	*resourceBuilderBase
}

// newDockerfileStageFromHandle wraps an existing handle as DockerfileStage.
func newDockerfileStageFromHandle(h *handle, c *client) DockerfileStage {
	return &dockerfileStage{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// AddContainerFiles adds COPY --from statements for published container files
func (s *dockerfileStage) AddContainerFiles(resource Resource, rootDestinationPath string, options ...*AddContainerFilesOptions) DockerfileStage {
	if s.err != nil { return s }
	if resource != nil { if err := resource.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"stage": s.handle.ToJSON(),
	}
	reqArgs["resource"] = serializeValue(resource)
	reqArgs["rootDestinationPath"] = serializeValue(rootDestinationPath)
	if len(options) > 0 {
		merged := &AddContainerFilesOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/dockerfileStageAddContainerFiles", reqArgs); err != nil { s.setErr(err) }
	return s
}

// Arg adds an ARG statement to a Dockerfile stage
func (s *dockerfileStage) Arg(name string, options ...*ArgOptions) DockerfileStage {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"stage": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if len(options) > 0 {
		merged := &ArgOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/dockerfileStageArg", reqArgs); err != nil { s.setErr(err) }
	return s
}

// Cmd adds a CMD statement to a Dockerfile stage
func (s *dockerfileStage) Cmd(command []string) DockerfileStage {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"stage": s.handle.ToJSON(),
	}
	if command != nil { reqArgs["command"] = serializeValue(command) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/cmd", reqArgs); err != nil { s.setErr(err) }
	return s
}

// Comment adds a comment to a Dockerfile stage
func (s *dockerfileStage) Comment(comment string) DockerfileStage {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"stage": s.handle.ToJSON(),
	}
	reqArgs["comment"] = serializeValue(comment)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/comment", reqArgs); err != nil { s.setErr(err) }
	return s
}

// Copy adds a COPY statement to a Dockerfile stage
func (s *dockerfileStage) Copy(source string, destination string, options ...*CopyOptions) DockerfileStage {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"stage": s.handle.ToJSON(),
	}
	reqArgs["source"] = serializeValue(source)
	reqArgs["destination"] = serializeValue(destination)
	if len(options) > 0 {
		merged := &CopyOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/dockerfileStageCopy", reqArgs); err != nil { s.setErr(err) }
	return s
}

// CopyFrom adds a COPY --from statement to a Dockerfile stage
func (s *dockerfileStage) CopyFrom(from string, source string, destination string, options ...*CopyFromOptions) DockerfileStage {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"stage": s.handle.ToJSON(),
	}
	reqArgs["from"] = serializeValue(from)
	reqArgs["source"] = serializeValue(source)
	reqArgs["destination"] = serializeValue(destination)
	if len(options) > 0 {
		merged := &CopyFromOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/dockerfileStageCopyFrom", reqArgs); err != nil { s.setErr(err) }
	return s
}

// EmptyLine adds an empty line to a Dockerfile stage
func (s *dockerfileStage) EmptyLine() DockerfileStage {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"stage": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/emptyLine", reqArgs); err != nil { s.setErr(err) }
	return s
}

// Entrypoint adds an ENTRYPOINT statement to a Dockerfile stage
func (s *dockerfileStage) Entrypoint(command []string) DockerfileStage {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"stage": s.handle.ToJSON(),
	}
	if command != nil { reqArgs["command"] = serializeValue(command) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/entrypoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// Env adds an ENV statement to a Dockerfile stage
func (s *dockerfileStage) Env(name string, value string) DockerfileStage {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"stage": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/env", reqArgs); err != nil { s.setErr(err) }
	return s
}

// Expose adds an EXPOSE statement to a Dockerfile stage
func (s *dockerfileStage) Expose(port float64) DockerfileStage {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"stage": s.handle.ToJSON(),
	}
	reqArgs["port"] = serializeValue(port)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/expose", reqArgs); err != nil { s.setErr(err) }
	return s
}

// Run adds a RUN statement to a Dockerfile stage
func (s *dockerfileStage) Run(command string) DockerfileStage {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"stage": s.handle.ToJSON(),
	}
	reqArgs["command"] = serializeValue(command)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/dockerfileStageRun", reqArgs); err != nil { s.setErr(err) }
	return s
}

// RunWithMounts adds a RUN statement with mounts to a Dockerfile stage
func (s *dockerfileStage) RunWithMounts(command string, mounts []string) DockerfileStage {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"stage": s.handle.ToJSON(),
	}
	reqArgs["command"] = serializeValue(command)
	if mounts != nil { reqArgs["mounts"] = serializeValue(mounts) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/runWithMounts", reqArgs); err != nil { s.setErr(err) }
	return s
}

// User adds a USER statement to a Dockerfile stage
func (s *dockerfileStage) User(user string) DockerfileStage {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"stage": s.handle.ToJSON(),
	}
	reqArgs["user"] = serializeValue(user)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/user", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WorkDir adds a WORKDIR statement to a Dockerfile stage
func (s *dockerfileStage) WorkDir(path string) DockerfileStage {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"stage": s.handle.ToJSON(),
	}
	reqArgs["path"] = serializeValue(path)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/workDir", reqArgs); err != nil { s.setErr(err) }
	return s
}

// DotnetToolResource is the public interface for handle type DotnetToolResource.
type DotnetToolResource interface {
	handleReference
	AsHttp2Service() DotnetToolResource
	CreateExecutionConfiguration() ExecutionConfigurationBuilder
	ExcludeFromManifest() DotnetToolResource
	ExcludeFromMcp() DotnetToolResource
	GetEndpoint(name string) EndpointReference
	GetResourceName() (string, error)
	OnBeforeResourceStarted(callback func(arg BeforeResourceStartedEvent)) DotnetToolResource
	OnInitializeResource(callback func(arg InitializeResourceEvent)) DotnetToolResource
	OnResourceEndpointsAllocated(callback func(arg ResourceEndpointsAllocatedEvent)) DotnetToolResource
	OnResourceReady(callback func(arg ResourceReadyEvent)) DotnetToolResource
	OnResourceStopped(callback func(arg ResourceStoppedEvent)) DotnetToolResource
	PublishAsDockerFile(configure func(obj ContainerResource)) DotnetToolResource
	WaitFor(dependency Resource, options ...*WaitForOptions) DotnetToolResource
	WaitForCompletion(dependency Resource, options ...*WaitForCompletionOptions) DotnetToolResource
	WaitForStart(dependency Resource, options ...*WaitForStartOptions) DotnetToolResource
	WithArgs(args []string) DotnetToolResource
	WithArgsCallback(callback func(obj CommandLineArgsCallbackContext)) DotnetToolResource
	WithCertificateTrustScope(scope CertificateTrustScope) DotnetToolResource
	WithChildRelationship(child Resource) DotnetToolResource
	WithCommand(name string, displayName string, executeCommand func(arg ExecuteCommandContext) *ExecuteCommandResult, options ...*WithCommandOptions) DotnetToolResource
	WithContainerRegistry(registry Resource) DotnetToolResource
	WithDeveloperCertificateTrust(trust bool) DotnetToolResource
	WithDockerfileBaseImage(options ...*WithDockerfileBaseImageOptions) DotnetToolResource
	WithEndpoint(options ...*WithEndpointOptions) DotnetToolResource
	WithEndpointCallback(endpointName string, callback func(obj EndpointUpdateContext), options ...*WithEndpointCallbackOptions) DotnetToolResource
	WithEnvironment(name string, value any) DotnetToolResource
	WithEnvironmentCallback(callback func(arg EnvironmentCallbackContext)) DotnetToolResource
	WithExecutableCommand(command string) DotnetToolResource
	WithExplicitStart() DotnetToolResource
	WithExternalHttpEndpoints() DotnetToolResource
	WithHealthCheck(key string) DotnetToolResource
	WithHttpCommand(path string, displayName string, options ...*WithHttpCommandOptions) DotnetToolResource
	WithHttpEndpoint(options ...*WithHttpEndpointOptions) DotnetToolResource
	WithHttpEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpEndpointCallbackOptions) DotnetToolResource
	WithHttpHealthCheck(options ...*WithHttpHealthCheckOptions) DotnetToolResource
	WithHttpProbe(probeType ProbeType, options ...*WithHttpProbeOptions) DotnetToolResource
	WithHttpsDeveloperCertificate(options ...*WithHttpsDeveloperCertificateOptions) DotnetToolResource
	WithHttpsEndpoint(options ...*WithHttpsEndpointOptions) DotnetToolResource
	WithHttpsEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpsEndpointCallbackOptions) DotnetToolResource
	WithIconName(iconName string, options ...*WithIconNameOptions) DotnetToolResource
	WithImagePushOptions(callback func(arg ContainerImagePushOptionsCallbackContext)) DotnetToolResource
	WithMcpServer(options ...*WithMcpServerOptions) DotnetToolResource
	WithOtlpExporter(options ...*WithOtlpExporterOptions) DotnetToolResource
	WithParentRelationship(parent Resource) DotnetToolResource
	WithPipelineConfiguration(callback func(obj PipelineConfigurationContext)) DotnetToolResource
	WithPipelineStepFactory(stepName string, callback func(arg PipelineStepContext), options ...*WithPipelineStepFactoryOptions) DotnetToolResource
	WithReference(source any, options ...*WithReferenceOptions) DotnetToolResource
	WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) DotnetToolResource
	WithRelationship(resourceBuilder Resource, type_ string) DotnetToolResource
	WithRemoteImageName(remoteImageName string) DotnetToolResource
	WithRemoteImageTag(remoteImageTag string) DotnetToolResource
	WithRequiredCommand(command string, options ...*WithRequiredCommandOptions) DotnetToolResource
	WithToolIgnoreExistingFeeds() DotnetToolResource
	WithToolIgnoreFailedSources() DotnetToolResource
	WithToolPackage(packageId string) DotnetToolResource
	WithToolPrerelease() DotnetToolResource
	WithToolSource(source string) DotnetToolResource
	WithToolVersion(version string) DotnetToolResource
	WithUrl(url any, options ...*WithUrlOptions) DotnetToolResource
	WithUrlForEndpoint(endpointName string, callback func(obj *ResourceUrlAnnotation)) DotnetToolResource
	WithUrls(callback func(obj ResourceUrlsCallbackContext)) DotnetToolResource
	WithWorkingDirectory(workingDirectory string) DotnetToolResource
	WithoutHttpsCertificate() DotnetToolResource
	Err() error
}

// dotnetToolResource is the unexported impl of DotnetToolResource.
type dotnetToolResource struct {
	*resourceBuilderBase
}

// newDotnetToolResourceFromHandle wraps an existing handle as DotnetToolResource.
func newDotnetToolResourceFromHandle(h *handle, c *client) DotnetToolResource {
	return &dotnetToolResource{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// AsHttp2Service configures resource for HTTP/2
func (s *dotnetToolResource) AsHttp2Service() DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/asHttp2Service", reqArgs); err != nil { s.setErr(err) }
	return s
}

// CreateExecutionConfiguration creates an execution configuration builder
func (s *dotnetToolResource) CreateExecutionConfiguration() ExecutionConfigurationBuilder {
	if s.err != nil { return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/createExecutionConfiguration returned unexpected type %T", result)
		return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &executionConfigurationBuilder{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *dotnetToolResource) ExcludeFromManifest() DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/excludeFromManifest", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *dotnetToolResource) ExcludeFromMcp() DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/excludeFromMcp", reqArgs); err != nil { s.setErr(err) }
	return s
}

// GetEndpoint gets an endpoint reference
func (s *dotnetToolResource) GetEndpoint(name string) EndpointReference {
	if s.err != nil { return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getEndpoint", reqArgs)
	if err != nil {
		return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/getEndpoint returned unexpected type %T", result)
		return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &endpointReference{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// GetResourceName gets the resource name
func (s *dotnetToolResource) GetResourceName() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *dotnetToolResource) OnBeforeResourceStarted(callback func(arg BeforeResourceStartedEvent)) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[BeforeResourceStartedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onBeforeResourceStarted", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *dotnetToolResource) OnInitializeResource(callback func(arg InitializeResourceEvent)) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[InitializeResourceEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onInitializeResource", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceEndpointsAllocated subscribes to the ResourceEndpointsAllocated event
func (s *dotnetToolResource) OnResourceEndpointsAllocated(callback func(arg ResourceEndpointsAllocatedEvent)) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceEndpointsAllocatedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceEndpointsAllocated", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceReady subscribes to the ResourceReady event
func (s *dotnetToolResource) OnResourceReady(callback func(arg ResourceReadyEvent)) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceReadyEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceReady", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *dotnetToolResource) OnResourceStopped(callback func(arg ResourceStoppedEvent)) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceStoppedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceStopped", reqArgs); err != nil { s.setErr(err) }
	return s
}

// PublishAsDockerFile publishes an executable as a Docker file
func (s *dotnetToolResource) PublishAsDockerFile(configure func(obj ContainerResource)) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if configure != nil {
		cb := configure
		shim := func(args ...any) any {
			cb(callbackArg[ContainerResource](args, 0))
			return nil
		}
		reqArgs["configure"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/publishAsDockerFile", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WaitFor waits for another resource to be ready
func (s *dotnetToolResource) WaitFor(dependency Resource, options ...*WaitForOptions) DotnetToolResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if len(options) > 0 {
		merged := &WaitForOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitFor", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WaitForCompletion waits for resource completion
func (s *dotnetToolResource) WaitForCompletion(dependency Resource, options ...*WaitForCompletionOptions) DotnetToolResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if len(options) > 0 {
		merged := &WaitForCompletionOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitForResourceCompletion", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WaitForStart waits for another resource to start
func (s *dotnetToolResource) WaitForStart(dependency Resource, options ...*WaitForStartOptions) DotnetToolResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if len(options) > 0 {
		merged := &WaitForStartOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitForStart", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithArgs adds arguments
func (s *dotnetToolResource) WithArgs(args []string) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if args != nil { reqArgs["args"] = serializeValue(args) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withArgs", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithArgsCallback sets command-line arguments via callback
func (s *dotnetToolResource) WithArgsCallback(callback func(obj CommandLineArgsCallbackContext)) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[CommandLineArgsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withArgsCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCertificateTrustScope sets the certificate trust scope
func (s *dotnetToolResource) WithCertificateTrustScope(scope CertificateTrustScope) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["scope"] = serializeValue(scope)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withCertificateTrustScope", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithChildRelationship sets a child relationship
func (s *dotnetToolResource) WithChildRelationship(child Resource) DotnetToolResource {
	if s.err != nil { return s }
	if child != nil { if err := child.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["child"] = serializeValue(child)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderChildRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCommand adds a resource command
func (s *dotnetToolResource) WithCommand(name string, displayName string, executeCommand func(arg ExecuteCommandContext) *ExecuteCommandResult, options ...*WithCommandOptions) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["displayName"] = serializeValue(displayName)
	if executeCommand != nil {
		cb := executeCommand
		shim := func(args ...any) any {
			return cb(callbackArg[ExecuteCommandContext](args, 0))
		}
		reqArgs["executeCommand"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithContainerRegistry configures a resource to use a container registry
func (s *dotnetToolResource) WithContainerRegistry(registry Resource) DotnetToolResource {
	if s.err != nil { return s }
	if registry != nil { if err := registry.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["registry"] = serializeValue(registry)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withContainerRegistry", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDeveloperCertificateTrust configures developer certificate trust
func (s *dotnetToolResource) WithDeveloperCertificateTrust(trust bool) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["trust"] = serializeValue(trust)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withDeveloperCertificateTrust", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *dotnetToolResource) WithDockerfileBaseImage(options ...*WithDockerfileBaseImageOptions) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithDockerfileBaseImageOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withDockerfileBaseImage", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEndpoint adds a network endpoint
func (s *dotnetToolResource) WithEndpoint(options ...*WithEndpointOptions) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithEndpointOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEndpointCallback updates a named endpoint via callback
func (s *dotnetToolResource) WithEndpointCallback(endpointName string, callback func(obj EndpointUpdateContext), options ...*WithEndpointCallbackOptions) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EndpointUpdateContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithEndpointCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEndpointCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEnvironment sets an environment variable
// Allowed types for parameter value: string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue.
func (s *dotnetToolResource) WithEnvironment(name string, value any) DotnetToolResource {
	if s.err != nil { return s }
	switch value.(type) {
	case string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue:
	default:
		err := fmt.Errorf("aspire: WithEnvironment: parameter %q must be one of [string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue], got %T", "value", value)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if value != nil { reqArgs["value"] = serializeValue(value) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEnvironment", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEnvironmentCallback sets environment variables via callback
func (s *dotnetToolResource) WithEnvironmentCallback(callback func(arg EnvironmentCallbackContext)) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EnvironmentCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEnvironmentCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithExecutableCommand sets the executable command
func (s *dotnetToolResource) WithExecutableCommand(command string) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["command"] = serializeValue(command)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withExecutableCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithExplicitStart prevents resource from starting automatically
func (s *dotnetToolResource) WithExplicitStart() DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withExplicitStart", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithExternalHttpEndpoints makes HTTP endpoints externally accessible
func (s *dotnetToolResource) WithExternalHttpEndpoints() DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withExternalHttpEndpoints", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHealthCheck adds a health check by key
func (s *dotnetToolResource) WithHealthCheck(key string) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["key"] = serializeValue(key)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHealthCheck", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpCommand adds an HTTP resource command
func (s *dotnetToolResource) WithHttpCommand(path string, displayName string, options ...*WithHttpCommandOptions) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["path"] = serializeValue(path)
	reqArgs["displayName"] = serializeValue(displayName)
	if len(options) > 0 {
		merged := &WithHttpCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpEndpoint adds an HTTP endpoint
func (s *dotnetToolResource) WithHttpEndpoint(options ...*WithHttpEndpointOptions) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpEndpointOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpEndpointCallback updates an HTTP endpoint via callback
func (s *dotnetToolResource) WithHttpEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpEndpointCallbackOptions) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EndpointUpdateContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithHttpEndpointCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpEndpointCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpHealthCheck adds an HTTP health check
func (s *dotnetToolResource) WithHttpHealthCheck(options ...*WithHttpHealthCheckOptions) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpHealthCheckOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpHealthCheck", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpProbe adds an HTTP health probe to the resource
func (s *dotnetToolResource) WithHttpProbe(probeType ProbeType, options ...*WithHttpProbeOptions) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["probeType"] = serializeValue(probeType)
	if len(options) > 0 {
		merged := &WithHttpProbeOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpProbe", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpsDeveloperCertificate configures HTTPS with a developer certificate
func (s *dotnetToolResource) WithHttpsDeveloperCertificate(options ...*WithHttpsDeveloperCertificateOptions) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpsDeveloperCertificateOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withParameterHttpsDeveloperCertificate", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpsEndpoint adds an HTTPS endpoint
func (s *dotnetToolResource) WithHttpsEndpoint(options ...*WithHttpsEndpointOptions) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpsEndpointOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpsEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpsEndpointCallback updates an HTTPS endpoint via callback
func (s *dotnetToolResource) WithHttpsEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpsEndpointCallbackOptions) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EndpointUpdateContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithHttpsEndpointCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpsEndpointCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithIconName sets the icon for the resource
func (s *dotnetToolResource) WithIconName(iconName string, options ...*WithIconNameOptions) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["iconName"] = serializeValue(iconName)
	if len(options) > 0 {
		merged := &WithIconNameOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withIconName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithImagePushOptions sets image push options via callback
func (s *dotnetToolResource) WithImagePushOptions(callback func(arg ContainerImagePushOptionsCallbackContext)) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ContainerImagePushOptionsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withImagePushOptions", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMcpServer configures an MCP server endpoint on the resource
func (s *dotnetToolResource) WithMcpServer(options ...*WithMcpServerOptions) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithMcpServerOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withMcpServer", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithOtlpExporter configures OTLP telemetry export
func (s *dotnetToolResource) WithOtlpExporter(options ...*WithOtlpExporterOptions) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithOtlpExporterOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withOtlpExporter", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithParentRelationship sets the parent relationship
func (s *dotnetToolResource) WithParentRelationship(parent Resource) DotnetToolResource {
	if s.err != nil { return s }
	if parent != nil { if err := parent.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["parent"] = serializeValue(parent)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderParentRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *dotnetToolResource) WithPipelineConfiguration(callback func(obj PipelineConfigurationContext)) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineConfigurationContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withPipelineConfiguration", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *dotnetToolResource) WithPipelineStepFactory(stepName string, callback func(arg PipelineStepContext), options ...*WithPipelineStepFactoryOptions) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["stepName"] = serializeValue(stepName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineStepContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithPipelineStepFactoryOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withPipelineStepFactory", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithReference adds a reference to another resource
// Allowed types for parameter source: Resource, EndpointReference, string.
func (s *dotnetToolResource) WithReference(source any, options ...*WithReferenceOptions) DotnetToolResource {
	if s.err != nil { return s }
	switch source.(type) {
	case Resource, EndpointReference, string:
	default:
		err := fmt.Errorf("aspire: WithReference: parameter %q must be one of [Resource, EndpointReference, string], got %T", "source", source)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if source != nil { reqArgs["source"] = serializeValue(source) }
	if len(options) > 0 {
		merged := &WithReferenceOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withReference", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithReferenceEnvironment configures which reference values are injected into environment variables
func (s *dotnetToolResource) WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if options != nil { reqArgs["options"] = serializeValue(options) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withReferenceEnvironment", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRelationship adds a relationship to another resource
func (s *dotnetToolResource) WithRelationship(resourceBuilder Resource, type_ string) DotnetToolResource {
	if s.err != nil { return s }
	if resourceBuilder != nil { if err := resourceBuilder.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["resourceBuilder"] = serializeValue(resourceBuilder)
	reqArgs["type"] = serializeValue(type_)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRemoteImageName sets the remote image name for publishing
func (s *dotnetToolResource) WithRemoteImageName(remoteImageName string) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["remoteImageName"] = serializeValue(remoteImageName)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRemoteImageName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRemoteImageTag sets the remote image tag for publishing
func (s *dotnetToolResource) WithRemoteImageTag(remoteImageTag string) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["remoteImageTag"] = serializeValue(remoteImageTag)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRemoteImageTag", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRequiredCommand adds a required command dependency
func (s *dotnetToolResource) WithRequiredCommand(command string, options ...*WithRequiredCommandOptions) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["command"] = serializeValue(command)
	if len(options) > 0 {
		merged := &WithRequiredCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRequiredCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithToolIgnoreExistingFeeds ignores existing NuGet feeds
func (s *dotnetToolResource) WithToolIgnoreExistingFeeds() DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withToolIgnoreExistingFeeds", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithToolIgnoreFailedSources ignores failed NuGet sources
func (s *dotnetToolResource) WithToolIgnoreFailedSources() DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withToolIgnoreFailedSources", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithToolPackage sets the tool package ID
func (s *dotnetToolResource) WithToolPackage(packageId string) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["packageId"] = serializeValue(packageId)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withToolPackage", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithToolPrerelease allows prerelease tool versions
func (s *dotnetToolResource) WithToolPrerelease() DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withToolPrerelease", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithToolSource adds a NuGet source for the tool
func (s *dotnetToolResource) WithToolSource(source string) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["source"] = serializeValue(source)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withToolSource", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithToolVersion sets the tool version
func (s *dotnetToolResource) WithToolVersion(version string) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["version"] = serializeValue(version)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withToolVersion", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrl adds or modifies displayed URLs
// Allowed types for parameter url: string, *ReferenceExpression.
func (s *dotnetToolResource) WithUrl(url any, options ...*WithUrlOptions) DotnetToolResource {
	if s.err != nil { return s }
	switch url.(type) {
	case string, *ReferenceExpression:
	default:
		err := fmt.Errorf("aspire: WithUrl: parameter %q must be one of [string, *ReferenceExpression], got %T", "url", url)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if url != nil { reqArgs["url"] = serializeValue(url) }
	if len(options) > 0 {
		merged := &WithUrlOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrl", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *dotnetToolResource) WithUrlForEndpoint(endpointName string, callback func(obj *ResourceUrlAnnotation)) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			arg0 := callbackArg[*ResourceUrlAnnotation](args, 0)
			cb(arg0)
			return map[string]any{
				"p0": serializeValue(arg0),
			}
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrlForEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrls customizes displayed URLs via callback
func (s *dotnetToolResource) WithUrls(callback func(obj ResourceUrlsCallbackContext)) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceUrlsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrls", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithWorkingDirectory sets the executable working directory
func (s *dotnetToolResource) WithWorkingDirectory(workingDirectory string) DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["workingDirectory"] = serializeValue(workingDirectory)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withWorkingDirectory", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithoutHttpsCertificate removes HTTPS certificate configuration
func (s *dotnetToolResource) WithoutHttpsCertificate() DotnetToolResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withoutHttpsCertificate", reqArgs); err != nil { s.setErr(err) }
	return s
}

// EndpointReference is the public interface for handle type EndpointReference.
type EndpointReference interface {
	handleReference
	EndpointName() (string, error)
	ErrorMessage() (string, error)
	ExcludeReferenceEndpoint() (bool, error)
	Exists() (bool, error)
	GetTlsValue(enabledValue *ReferenceExpression, disabledValue *ReferenceExpression) *ReferenceExpression
	GetValueAsync(options ...*GetValueAsyncOptions) (string, error)
	Host() (string, error)
	IsAllocated() (bool, error)
	IsHttp() (bool, error)
	IsHttpSchemeNamedEndpoint() (bool, error)
	IsHttps() (bool, error)
	Port() (float64, error)
	Property(property EndpointProperty) EndpointReferenceExpression
	Resource() ResourceWithEndpoints
	Scheme() (string, error)
	SetErrorMessage(value string) EndpointReference
	TargetPort() (float64, error)
	TlsEnabled() (bool, error)
	Url() (string, error)
	Err() error
}

// endpointReference is the unexported impl of EndpointReference.
type endpointReference struct {
	*resourceBuilderBase
}

// newEndpointReferenceFromHandle wraps an existing handle as EndpointReference.
func newEndpointReferenceFromHandle(h *handle, c *client) EndpointReference {
	return &endpointReference{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// EndpointName gets the EndpointName property
func (s *endpointReference) EndpointName() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReference.endpointName", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// ErrorMessage gets the ErrorMessage property
func (s *endpointReference) ErrorMessage() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReference.errorMessage", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// ExcludeReferenceEndpoint gets the ExcludeReferenceEndpoint property
func (s *endpointReference) ExcludeReferenceEndpoint() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReference.excludeReferenceEndpoint", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// Exists gets the Exists property
func (s *endpointReference) Exists() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReference.exists", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// GetTlsValue gets a conditional expression that resolves to the enabledValue when TLS is enabled on the endpoint, or to the disabledValue otherwise.
func (s *endpointReference) GetTlsValue(enabledValue *ReferenceExpression, disabledValue *ReferenceExpression) *ReferenceExpression {
	if s.err != nil { return nil }
	if enabledValue != nil { if err := enabledValue.Err(); err != nil { s.setErr(err); return nil } }
	if disabledValue != nil { if err := disabledValue.Err(); err != nil { s.setErr(err); return nil } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	if enabledValue != nil { reqArgs["enabledValue"] = serializeValue(enabledValue) }
	if disabledValue != nil { reqArgs["disabledValue"] = serializeValue(disabledValue) }
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReference.getTlsValue", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(*ReferenceExpression)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/EndpointReference.getTlsValue returned unexpected type %T", result))
		return nil
	}
	return typed
}

// GetValueAsync gets the URL of the endpoint asynchronously
func (s *endpointReference) GetValueAsync(options ...*GetValueAsyncOptions) (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &GetValueAsyncOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.CancellationToken != nil {
			ctx = merged.CancellationToken.Context()
			if id := s.client.registerCancellation(merged.CancellationToken); id != "" {
				reqArgs["cancellationToken"] = id
			}
		}
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReference.getValueAsync", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// Host gets the Host property
func (s *endpointReference) Host() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReference.host", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// IsAllocated gets the IsAllocated property
func (s *endpointReference) IsAllocated() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReference.isAllocated", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// IsHttp gets the IsHttp property
func (s *endpointReference) IsHttp() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReference.isHttp", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// IsHttpSchemeNamedEndpoint gets the IsHttpSchemeNamedEndpoint property
func (s *endpointReference) IsHttpSchemeNamedEndpoint() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReference.isHttpSchemeNamedEndpoint", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// IsHttps gets the IsHttps property
func (s *endpointReference) IsHttps() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReference.isHttps", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// Port gets the Port property
func (s *endpointReference) Port() (float64, error) {
	if s.err != nil { var zero float64; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReference.port", reqArgs)
	if err != nil {
		var zero float64
		return zero, err
	}
	return decodeAs[float64](result)
}

// Property gets the specified property expression of the endpoint
func (s *endpointReference) Property(property EndpointProperty) EndpointReferenceExpression {
	if s.err != nil { return &endpointReferenceExpression{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["property"] = serializeValue(property)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReference.property", reqArgs)
	if err != nil {
		return &endpointReferenceExpression{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/EndpointReference.property returned unexpected type %T", result)
		return &endpointReferenceExpression{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &endpointReferenceExpression{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Resource gets the Resource property
func (s *endpointReference) Resource() ResourceWithEndpoints {
	if s.err != nil { return nil }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReference.resource", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(ResourceWithEndpoints)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/EndpointReference.resource returned unexpected type %T", result))
		return nil
	}
	return typed
}

// Scheme gets the Scheme property
func (s *endpointReference) Scheme() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReference.scheme", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// SetErrorMessage sets the ErrorMessage property
func (s *endpointReference) SetErrorMessage(value string) EndpointReference {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReference.setErrorMessage", reqArgs); err != nil { s.setErr(err) }
	return s
}

// TargetPort gets the TargetPort property
func (s *endpointReference) TargetPort() (float64, error) {
	if s.err != nil { var zero float64; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReference.targetPort", reqArgs)
	if err != nil {
		var zero float64
		return zero, err
	}
	return decodeAs[float64](result)
}

// TlsEnabled gets the TlsEnabled property
func (s *endpointReference) TlsEnabled() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReference.tlsEnabled", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// Url gets the Url property
func (s *endpointReference) Url() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReference.url", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// EndpointReferenceExpression is the public interface for handle type EndpointReferenceExpression.
type EndpointReferenceExpression interface {
	handleReference
	Endpoint() EndpointReference
	Property() (EndpointProperty, error)
	ValueExpression() (string, error)
	Err() error
}

// endpointReferenceExpression is the unexported impl of EndpointReferenceExpression.
type endpointReferenceExpression struct {
	*resourceBuilderBase
}

// newEndpointReferenceExpressionFromHandle wraps an existing handle as EndpointReferenceExpression.
func newEndpointReferenceExpressionFromHandle(h *handle, c *client) EndpointReferenceExpression {
	return &endpointReferenceExpression{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Endpoint gets the Endpoint property
func (s *endpointReferenceExpression) Endpoint() EndpointReference {
	if s.err != nil { return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReferenceExpression.endpoint", reqArgs)
	if err != nil {
		return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/EndpointReferenceExpression.endpoint returned unexpected type %T", result)
		return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &endpointReference{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Property gets the Property property
func (s *endpointReferenceExpression) Property() (EndpointProperty, error) {
	if s.err != nil { var zero EndpointProperty; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReferenceExpression.property", reqArgs)
	if err != nil {
		var zero EndpointProperty
		return zero, err
	}
	return decodeAs[EndpointProperty](result)
}

// ValueExpression gets the ValueExpression property
func (s *endpointReferenceExpression) ValueExpression() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointReferenceExpression.valueExpression", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// EndpointUpdateContext is the public interface for handle type EndpointUpdateContext.
type EndpointUpdateContext interface {
	handleReference
	ExcludeReferenceEndpoint() (bool, error)
	IsExternal() (bool, error)
	IsProxied() (bool, error)
	Name() (string, error)
	Port() (float64, error)
	Protocol() (ProtocolType, error)
	SetExcludeReferenceEndpoint(value bool) EndpointUpdateContext
	SetIsExternal(value bool) EndpointUpdateContext
	SetIsProxied(value bool) EndpointUpdateContext
	SetPort(value float64) EndpointUpdateContext
	SetProtocol(value ProtocolType) EndpointUpdateContext
	SetTargetHost(value string) EndpointUpdateContext
	SetTargetPort(value float64) EndpointUpdateContext
	SetTlsEnabled(value bool) EndpointUpdateContext
	SetTransport(value string) EndpointUpdateContext
	SetUriScheme(value string) EndpointUpdateContext
	TargetHost() (string, error)
	TargetPort() (float64, error)
	TlsEnabled() (bool, error)
	Transport() (string, error)
	UriScheme() (string, error)
	Err() error
}

// endpointUpdateContext is the unexported impl of EndpointUpdateContext.
type endpointUpdateContext struct {
	*resourceBuilderBase
}

// newEndpointUpdateContextFromHandle wraps an existing handle as EndpointUpdateContext.
func newEndpointUpdateContextFromHandle(h *handle, c *client) EndpointUpdateContext {
	return &endpointUpdateContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// ExcludeReferenceEndpoint gets the ExcludeReferenceEndpoint property
func (s *endpointUpdateContext) ExcludeReferenceEndpoint() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.excludeReferenceEndpoint", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// IsExternal gets the IsExternal property
func (s *endpointUpdateContext) IsExternal() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.isExternal", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// IsProxied gets the IsProxied property
func (s *endpointUpdateContext) IsProxied() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.isProxied", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// Name gets the Name property
func (s *endpointUpdateContext) Name() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.name", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// Port gets the Port property
func (s *endpointUpdateContext) Port() (float64, error) {
	if s.err != nil { var zero float64; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.port", reqArgs)
	if err != nil {
		var zero float64
		return zero, err
	}
	return decodeAs[float64](result)
}

// Protocol gets the Protocol property
func (s *endpointUpdateContext) Protocol() (ProtocolType, error) {
	if s.err != nil { var zero ProtocolType; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.protocol", reqArgs)
	if err != nil {
		var zero ProtocolType
		return zero, err
	}
	return decodeAs[ProtocolType](result)
}

// SetExcludeReferenceEndpoint sets the ExcludeReferenceEndpoint property
func (s *endpointUpdateContext) SetExcludeReferenceEndpoint(value bool) EndpointUpdateContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setExcludeReferenceEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetIsExternal sets the IsExternal property
func (s *endpointUpdateContext) SetIsExternal(value bool) EndpointUpdateContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setIsExternal", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetIsProxied sets the IsProxied property
func (s *endpointUpdateContext) SetIsProxied(value bool) EndpointUpdateContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setIsProxied", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetPort sets the Port property
func (s *endpointUpdateContext) SetPort(value float64) EndpointUpdateContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setPort", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetProtocol sets the Protocol property
func (s *endpointUpdateContext) SetProtocol(value ProtocolType) EndpointUpdateContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setProtocol", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetTargetHost sets the TargetHost property
func (s *endpointUpdateContext) SetTargetHost(value string) EndpointUpdateContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setTargetHost", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetTargetPort sets the TargetPort property
func (s *endpointUpdateContext) SetTargetPort(value float64) EndpointUpdateContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setTargetPort", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetTlsEnabled sets the TlsEnabled property
func (s *endpointUpdateContext) SetTlsEnabled(value bool) EndpointUpdateContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setTlsEnabled", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetTransport sets the Transport property
func (s *endpointUpdateContext) SetTransport(value string) EndpointUpdateContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setTransport", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetUriScheme sets the UriScheme property
func (s *endpointUpdateContext) SetUriScheme(value string) EndpointUpdateContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.setUriScheme", reqArgs); err != nil { s.setErr(err) }
	return s
}

// TargetHost gets the TargetHost property
func (s *endpointUpdateContext) TargetHost() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.targetHost", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// TargetPort gets the TargetPort property
func (s *endpointUpdateContext) TargetPort() (float64, error) {
	if s.err != nil { var zero float64; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.targetPort", reqArgs)
	if err != nil {
		var zero float64
		return zero, err
	}
	return decodeAs[float64](result)
}

// TlsEnabled gets the TlsEnabled property
func (s *endpointUpdateContext) TlsEnabled() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.tlsEnabled", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// Transport gets the Transport property
func (s *endpointUpdateContext) Transport() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.transport", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// UriScheme gets the UriScheme property
func (s *endpointUpdateContext) UriScheme() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EndpointUpdateContext.uriScheme", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// EnvironmentCallbackContext is the public interface for handle type EnvironmentCallbackContext.
type EnvironmentCallbackContext interface {
	handleReference
	Environment() EnvironmentEditor
	ExecutionContext() DistributedApplicationExecutionContext
	Log() LogFacade
	Resource() Resource
	Err() error
}

// environmentCallbackContext is the unexported impl of EnvironmentCallbackContext.
type environmentCallbackContext struct {
	*resourceBuilderBase
}

// newEnvironmentCallbackContextFromHandle wraps an existing handle as EnvironmentCallbackContext.
func newEnvironmentCallbackContextFromHandle(h *handle, c *client) EnvironmentCallbackContext {
	return &environmentCallbackContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Environment gets the environment variable editor
func (s *environmentCallbackContext) Environment() EnvironmentEditor {
	if s.err != nil { return &environmentEditor{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.environment", reqArgs)
	if err != nil {
		return &environmentEditor{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.environment returned unexpected type %T", result)
		return &environmentEditor{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &environmentEditor{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ExecutionContext gets the execution context for this callback invocation
func (s *environmentCallbackContext) ExecutionContext() DistributedApplicationExecutionContext {
	if s.err != nil { return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.executionContext", reqArgs)
	if err != nil {
		return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.executionContext returned unexpected type %T", result)
		return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationExecutionContext{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Log gets the callback logger facade
func (s *environmentCallbackContext) Log() LogFacade {
	if s.err != nil { return &logFacade{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.log", reqArgs)
	if err != nil {
		return &logFacade{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.log returned unexpected type %T", result)
		return &logFacade{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &logFacade{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Resource gets the resource associated with this callback
func (s *environmentCallbackContext) Resource() Resource {
	if s.err != nil { return nil }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.resource", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(Resource)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.resource returned unexpected type %T", result))
		return nil
	}
	return typed
}

// EnvironmentEditor is the public interface for handle type EnvironmentEditor.
type EnvironmentEditor interface {
	handleReference
	Set(name string, value any) error
	Err() error
}

// environmentEditor is the unexported impl of EnvironmentEditor.
type environmentEditor struct {
	*resourceBuilderBase
}

// newEnvironmentEditorFromHandle wraps an existing handle as EnvironmentEditor.
func newEnvironmentEditorFromHandle(h *handle, c *client) EnvironmentEditor {
	return &environmentEditor{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Set sets an environment variable
// Allowed types for parameter value: string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue.
func (s *environmentEditor) Set(name string, value any) error {
	if s.err != nil { return s.err }
	switch value.(type) {
	case string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue:
	default:
		err := fmt.Errorf("aspire: Set: parameter %q must be one of [string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue], got %T", "value", value)
		return err
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if value != nil { reqArgs["value"] = serializeValue(value) }
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/set", reqArgs)
	return err
}

// EventingSubscriberRegistrationContext is the public interface for handle type EventingSubscriberRegistrationContext.
type EventingSubscriberRegistrationContext interface {
	handleReference
	CancellationToken() (*CancellationToken, error)
	ExecutionContext() DistributedApplicationExecutionContext
	OnAfterResourcesCreated(callback func(arg AfterResourcesCreatedEvent)) DistributedApplicationEventSubscription
	OnBeforeStart(callback func(arg BeforeStartEvent)) DistributedApplicationEventSubscription
	Err() error
}

// eventingSubscriberRegistrationContext is the unexported impl of EventingSubscriberRegistrationContext.
type eventingSubscriberRegistrationContext struct {
	*resourceBuilderBase
}

// newEventingSubscriberRegistrationContextFromHandle wraps an existing handle as EventingSubscriberRegistrationContext.
func newEventingSubscriberRegistrationContextFromHandle(h *handle, c *client) EventingSubscriberRegistrationContext {
	return &eventingSubscriberRegistrationContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// CancellationToken gets the CancellationToken property
func (s *eventingSubscriberRegistrationContext) CancellationToken() (*CancellationToken, error) {
	if s.err != nil { var zero *CancellationToken; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Ats/EventingSubscriberRegistrationContext.cancellationToken", reqArgs)
	if err != nil {
		var zero *CancellationToken
		return zero, err
	}
	return decodeAs[*CancellationToken](result)
}

// ExecutionContext gets the ExecutionContext property
func (s *eventingSubscriberRegistrationContext) ExecutionContext() DistributedApplicationExecutionContext {
	if s.err != nil { return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Ats/EventingSubscriberRegistrationContext.executionContext", reqArgs)
	if err != nil {
		return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.Ats/EventingSubscriberRegistrationContext.executionContext returned unexpected type %T", result)
		return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationExecutionContext{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// OnAfterResourcesCreated subscribes an eventing subscriber to the AfterResourcesCreated event
func (s *eventingSubscriberRegistrationContext) OnAfterResourcesCreated(callback func(arg AfterResourcesCreatedEvent)) DistributedApplicationEventSubscription {
	if s.err != nil { return &distributedApplicationEventSubscription{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[AfterResourcesCreatedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/eventingSubscriberOnAfterResourcesCreated", reqArgs)
	if err != nil {
		return &distributedApplicationEventSubscription{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/eventingSubscriberOnAfterResourcesCreated returned unexpected type %T", result)
		return &distributedApplicationEventSubscription{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationEventSubscription{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// OnBeforeStart subscribes an eventing subscriber to the BeforeStart event
func (s *eventingSubscriberRegistrationContext) OnBeforeStart(callback func(arg BeforeStartEvent)) DistributedApplicationEventSubscription {
	if s.err != nil { return &distributedApplicationEventSubscription{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[BeforeStartEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/eventingSubscriberOnBeforeStart", reqArgs)
	if err != nil {
		return &distributedApplicationEventSubscription{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/eventingSubscriberOnBeforeStart returned unexpected type %T", result)
		return &distributedApplicationEventSubscription{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationEventSubscription{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ExecutableResource is the public interface for handle type ExecutableResource.
type ExecutableResource interface {
	handleReference
	AsHttp2Service() ExecutableResource
	CreateExecutionConfiguration() ExecutionConfigurationBuilder
	ExcludeFromManifest() ExecutableResource
	ExcludeFromMcp() ExecutableResource
	GetEndpoint(name string) EndpointReference
	GetResourceName() (string, error)
	OnBeforeResourceStarted(callback func(arg BeforeResourceStartedEvent)) ExecutableResource
	OnInitializeResource(callback func(arg InitializeResourceEvent)) ExecutableResource
	OnResourceEndpointsAllocated(callback func(arg ResourceEndpointsAllocatedEvent)) ExecutableResource
	OnResourceReady(callback func(arg ResourceReadyEvent)) ExecutableResource
	OnResourceStopped(callback func(arg ResourceStoppedEvent)) ExecutableResource
	PublishAsDockerFile(configure func(obj ContainerResource)) ExecutableResource
	WaitFor(dependency Resource, options ...*WaitForOptions) ExecutableResource
	WaitForCompletion(dependency Resource, options ...*WaitForCompletionOptions) ExecutableResource
	WaitForStart(dependency Resource, options ...*WaitForStartOptions) ExecutableResource
	WithArgs(args []string) ExecutableResource
	WithArgsCallback(callback func(obj CommandLineArgsCallbackContext)) ExecutableResource
	WithCertificateTrustScope(scope CertificateTrustScope) ExecutableResource
	WithChildRelationship(child Resource) ExecutableResource
	WithCommand(name string, displayName string, executeCommand func(arg ExecuteCommandContext) *ExecuteCommandResult, options ...*WithCommandOptions) ExecutableResource
	WithContainerRegistry(registry Resource) ExecutableResource
	WithDeveloperCertificateTrust(trust bool) ExecutableResource
	WithDockerfileBaseImage(options ...*WithDockerfileBaseImageOptions) ExecutableResource
	WithEndpoint(options ...*WithEndpointOptions) ExecutableResource
	WithEndpointCallback(endpointName string, callback func(obj EndpointUpdateContext), options ...*WithEndpointCallbackOptions) ExecutableResource
	WithEnvironment(name string, value any) ExecutableResource
	WithEnvironmentCallback(callback func(arg EnvironmentCallbackContext)) ExecutableResource
	WithExecutableCommand(command string) ExecutableResource
	WithExplicitStart() ExecutableResource
	WithExternalHttpEndpoints() ExecutableResource
	WithHealthCheck(key string) ExecutableResource
	WithHttpCommand(path string, displayName string, options ...*WithHttpCommandOptions) ExecutableResource
	WithHttpEndpoint(options ...*WithHttpEndpointOptions) ExecutableResource
	WithHttpEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpEndpointCallbackOptions) ExecutableResource
	WithHttpHealthCheck(options ...*WithHttpHealthCheckOptions) ExecutableResource
	WithHttpProbe(probeType ProbeType, options ...*WithHttpProbeOptions) ExecutableResource
	WithHttpsDeveloperCertificate(options ...*WithHttpsDeveloperCertificateOptions) ExecutableResource
	WithHttpsEndpoint(options ...*WithHttpsEndpointOptions) ExecutableResource
	WithHttpsEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpsEndpointCallbackOptions) ExecutableResource
	WithIconName(iconName string, options ...*WithIconNameOptions) ExecutableResource
	WithImagePushOptions(callback func(arg ContainerImagePushOptionsCallbackContext)) ExecutableResource
	WithMcpServer(options ...*WithMcpServerOptions) ExecutableResource
	WithOtlpExporter(options ...*WithOtlpExporterOptions) ExecutableResource
	WithParentRelationship(parent Resource) ExecutableResource
	WithPipelineConfiguration(callback func(obj PipelineConfigurationContext)) ExecutableResource
	WithPipelineStepFactory(stepName string, callback func(arg PipelineStepContext), options ...*WithPipelineStepFactoryOptions) ExecutableResource
	WithReference(source any, options ...*WithReferenceOptions) ExecutableResource
	WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) ExecutableResource
	WithRelationship(resourceBuilder Resource, type_ string) ExecutableResource
	WithRemoteImageName(remoteImageName string) ExecutableResource
	WithRemoteImageTag(remoteImageTag string) ExecutableResource
	WithRequiredCommand(command string, options ...*WithRequiredCommandOptions) ExecutableResource
	WithUrl(url any, options ...*WithUrlOptions) ExecutableResource
	WithUrlForEndpoint(endpointName string, callback func(obj *ResourceUrlAnnotation)) ExecutableResource
	WithUrls(callback func(obj ResourceUrlsCallbackContext)) ExecutableResource
	WithWorkingDirectory(workingDirectory string) ExecutableResource
	WithoutHttpsCertificate() ExecutableResource
	Err() error
}

// executableResource is the unexported impl of ExecutableResource.
type executableResource struct {
	*resourceBuilderBase
}

// newExecutableResourceFromHandle wraps an existing handle as ExecutableResource.
func newExecutableResourceFromHandle(h *handle, c *client) ExecutableResource {
	return &executableResource{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// AsHttp2Service configures resource for HTTP/2
func (s *executableResource) AsHttp2Service() ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/asHttp2Service", reqArgs); err != nil { s.setErr(err) }
	return s
}

// CreateExecutionConfiguration creates an execution configuration builder
func (s *executableResource) CreateExecutionConfiguration() ExecutionConfigurationBuilder {
	if s.err != nil { return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/createExecutionConfiguration returned unexpected type %T", result)
		return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &executionConfigurationBuilder{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *executableResource) ExcludeFromManifest() ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/excludeFromManifest", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *executableResource) ExcludeFromMcp() ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/excludeFromMcp", reqArgs); err != nil { s.setErr(err) }
	return s
}

// GetEndpoint gets an endpoint reference
func (s *executableResource) GetEndpoint(name string) EndpointReference {
	if s.err != nil { return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getEndpoint", reqArgs)
	if err != nil {
		return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/getEndpoint returned unexpected type %T", result)
		return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &endpointReference{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// GetResourceName gets the resource name
func (s *executableResource) GetResourceName() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *executableResource) OnBeforeResourceStarted(callback func(arg BeforeResourceStartedEvent)) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[BeforeResourceStartedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onBeforeResourceStarted", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *executableResource) OnInitializeResource(callback func(arg InitializeResourceEvent)) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[InitializeResourceEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onInitializeResource", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceEndpointsAllocated subscribes to the ResourceEndpointsAllocated event
func (s *executableResource) OnResourceEndpointsAllocated(callback func(arg ResourceEndpointsAllocatedEvent)) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceEndpointsAllocatedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceEndpointsAllocated", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceReady subscribes to the ResourceReady event
func (s *executableResource) OnResourceReady(callback func(arg ResourceReadyEvent)) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceReadyEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceReady", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *executableResource) OnResourceStopped(callback func(arg ResourceStoppedEvent)) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceStoppedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceStopped", reqArgs); err != nil { s.setErr(err) }
	return s
}

// PublishAsDockerFile publishes an executable as a Docker file
func (s *executableResource) PublishAsDockerFile(configure func(obj ContainerResource)) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if configure != nil {
		cb := configure
		shim := func(args ...any) any {
			cb(callbackArg[ContainerResource](args, 0))
			return nil
		}
		reqArgs["configure"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/publishAsDockerFile", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WaitFor waits for another resource to be ready
func (s *executableResource) WaitFor(dependency Resource, options ...*WaitForOptions) ExecutableResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if len(options) > 0 {
		merged := &WaitForOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitFor", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WaitForCompletion waits for resource completion
func (s *executableResource) WaitForCompletion(dependency Resource, options ...*WaitForCompletionOptions) ExecutableResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if len(options) > 0 {
		merged := &WaitForCompletionOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitForResourceCompletion", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WaitForStart waits for another resource to start
func (s *executableResource) WaitForStart(dependency Resource, options ...*WaitForStartOptions) ExecutableResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if len(options) > 0 {
		merged := &WaitForStartOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitForStart", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithArgs adds arguments
func (s *executableResource) WithArgs(args []string) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if args != nil { reqArgs["args"] = serializeValue(args) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withArgs", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithArgsCallback sets command-line arguments via callback
func (s *executableResource) WithArgsCallback(callback func(obj CommandLineArgsCallbackContext)) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[CommandLineArgsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withArgsCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCertificateTrustScope sets the certificate trust scope
func (s *executableResource) WithCertificateTrustScope(scope CertificateTrustScope) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["scope"] = serializeValue(scope)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withCertificateTrustScope", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithChildRelationship sets a child relationship
func (s *executableResource) WithChildRelationship(child Resource) ExecutableResource {
	if s.err != nil { return s }
	if child != nil { if err := child.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["child"] = serializeValue(child)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderChildRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCommand adds a resource command
func (s *executableResource) WithCommand(name string, displayName string, executeCommand func(arg ExecuteCommandContext) *ExecuteCommandResult, options ...*WithCommandOptions) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["displayName"] = serializeValue(displayName)
	if executeCommand != nil {
		cb := executeCommand
		shim := func(args ...any) any {
			return cb(callbackArg[ExecuteCommandContext](args, 0))
		}
		reqArgs["executeCommand"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithContainerRegistry configures a resource to use a container registry
func (s *executableResource) WithContainerRegistry(registry Resource) ExecutableResource {
	if s.err != nil { return s }
	if registry != nil { if err := registry.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["registry"] = serializeValue(registry)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withContainerRegistry", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDeveloperCertificateTrust configures developer certificate trust
func (s *executableResource) WithDeveloperCertificateTrust(trust bool) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["trust"] = serializeValue(trust)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withDeveloperCertificateTrust", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *executableResource) WithDockerfileBaseImage(options ...*WithDockerfileBaseImageOptions) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithDockerfileBaseImageOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withDockerfileBaseImage", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEndpoint adds a network endpoint
func (s *executableResource) WithEndpoint(options ...*WithEndpointOptions) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithEndpointOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEndpointCallback updates a named endpoint via callback
func (s *executableResource) WithEndpointCallback(endpointName string, callback func(obj EndpointUpdateContext), options ...*WithEndpointCallbackOptions) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EndpointUpdateContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithEndpointCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEndpointCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEnvironment sets an environment variable
// Allowed types for parameter value: string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue.
func (s *executableResource) WithEnvironment(name string, value any) ExecutableResource {
	if s.err != nil { return s }
	switch value.(type) {
	case string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue:
	default:
		err := fmt.Errorf("aspire: WithEnvironment: parameter %q must be one of [string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue], got %T", "value", value)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if value != nil { reqArgs["value"] = serializeValue(value) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEnvironment", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEnvironmentCallback sets environment variables via callback
func (s *executableResource) WithEnvironmentCallback(callback func(arg EnvironmentCallbackContext)) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EnvironmentCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEnvironmentCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithExecutableCommand sets the executable command
func (s *executableResource) WithExecutableCommand(command string) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["command"] = serializeValue(command)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withExecutableCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithExplicitStart prevents resource from starting automatically
func (s *executableResource) WithExplicitStart() ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withExplicitStart", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithExternalHttpEndpoints makes HTTP endpoints externally accessible
func (s *executableResource) WithExternalHttpEndpoints() ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withExternalHttpEndpoints", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHealthCheck adds a health check by key
func (s *executableResource) WithHealthCheck(key string) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["key"] = serializeValue(key)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHealthCheck", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpCommand adds an HTTP resource command
func (s *executableResource) WithHttpCommand(path string, displayName string, options ...*WithHttpCommandOptions) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["path"] = serializeValue(path)
	reqArgs["displayName"] = serializeValue(displayName)
	if len(options) > 0 {
		merged := &WithHttpCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpEndpoint adds an HTTP endpoint
func (s *executableResource) WithHttpEndpoint(options ...*WithHttpEndpointOptions) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpEndpointOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpEndpointCallback updates an HTTP endpoint via callback
func (s *executableResource) WithHttpEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpEndpointCallbackOptions) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EndpointUpdateContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithHttpEndpointCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpEndpointCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpHealthCheck adds an HTTP health check
func (s *executableResource) WithHttpHealthCheck(options ...*WithHttpHealthCheckOptions) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpHealthCheckOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpHealthCheck", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpProbe adds an HTTP health probe to the resource
func (s *executableResource) WithHttpProbe(probeType ProbeType, options ...*WithHttpProbeOptions) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["probeType"] = serializeValue(probeType)
	if len(options) > 0 {
		merged := &WithHttpProbeOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpProbe", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpsDeveloperCertificate configures HTTPS with a developer certificate
func (s *executableResource) WithHttpsDeveloperCertificate(options ...*WithHttpsDeveloperCertificateOptions) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpsDeveloperCertificateOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withParameterHttpsDeveloperCertificate", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpsEndpoint adds an HTTPS endpoint
func (s *executableResource) WithHttpsEndpoint(options ...*WithHttpsEndpointOptions) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpsEndpointOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpsEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpsEndpointCallback updates an HTTPS endpoint via callback
func (s *executableResource) WithHttpsEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpsEndpointCallbackOptions) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EndpointUpdateContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithHttpsEndpointCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpsEndpointCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithIconName sets the icon for the resource
func (s *executableResource) WithIconName(iconName string, options ...*WithIconNameOptions) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["iconName"] = serializeValue(iconName)
	if len(options) > 0 {
		merged := &WithIconNameOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withIconName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithImagePushOptions sets image push options via callback
func (s *executableResource) WithImagePushOptions(callback func(arg ContainerImagePushOptionsCallbackContext)) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ContainerImagePushOptionsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withImagePushOptions", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMcpServer configures an MCP server endpoint on the resource
func (s *executableResource) WithMcpServer(options ...*WithMcpServerOptions) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithMcpServerOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withMcpServer", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithOtlpExporter configures OTLP telemetry export
func (s *executableResource) WithOtlpExporter(options ...*WithOtlpExporterOptions) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithOtlpExporterOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withOtlpExporter", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithParentRelationship sets the parent relationship
func (s *executableResource) WithParentRelationship(parent Resource) ExecutableResource {
	if s.err != nil { return s }
	if parent != nil { if err := parent.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["parent"] = serializeValue(parent)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderParentRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *executableResource) WithPipelineConfiguration(callback func(obj PipelineConfigurationContext)) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineConfigurationContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withPipelineConfiguration", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *executableResource) WithPipelineStepFactory(stepName string, callback func(arg PipelineStepContext), options ...*WithPipelineStepFactoryOptions) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["stepName"] = serializeValue(stepName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineStepContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithPipelineStepFactoryOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withPipelineStepFactory", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithReference adds a reference to another resource
// Allowed types for parameter source: Resource, EndpointReference, string.
func (s *executableResource) WithReference(source any, options ...*WithReferenceOptions) ExecutableResource {
	if s.err != nil { return s }
	switch source.(type) {
	case Resource, EndpointReference, string:
	default:
		err := fmt.Errorf("aspire: WithReference: parameter %q must be one of [Resource, EndpointReference, string], got %T", "source", source)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if source != nil { reqArgs["source"] = serializeValue(source) }
	if len(options) > 0 {
		merged := &WithReferenceOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withReference", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithReferenceEnvironment configures which reference values are injected into environment variables
func (s *executableResource) WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if options != nil { reqArgs["options"] = serializeValue(options) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withReferenceEnvironment", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRelationship adds a relationship to another resource
func (s *executableResource) WithRelationship(resourceBuilder Resource, type_ string) ExecutableResource {
	if s.err != nil { return s }
	if resourceBuilder != nil { if err := resourceBuilder.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["resourceBuilder"] = serializeValue(resourceBuilder)
	reqArgs["type"] = serializeValue(type_)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRemoteImageName sets the remote image name for publishing
func (s *executableResource) WithRemoteImageName(remoteImageName string) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["remoteImageName"] = serializeValue(remoteImageName)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRemoteImageName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRemoteImageTag sets the remote image tag for publishing
func (s *executableResource) WithRemoteImageTag(remoteImageTag string) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["remoteImageTag"] = serializeValue(remoteImageTag)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRemoteImageTag", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRequiredCommand adds a required command dependency
func (s *executableResource) WithRequiredCommand(command string, options ...*WithRequiredCommandOptions) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["command"] = serializeValue(command)
	if len(options) > 0 {
		merged := &WithRequiredCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRequiredCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrl adds or modifies displayed URLs
// Allowed types for parameter url: string, *ReferenceExpression.
func (s *executableResource) WithUrl(url any, options ...*WithUrlOptions) ExecutableResource {
	if s.err != nil { return s }
	switch url.(type) {
	case string, *ReferenceExpression:
	default:
		err := fmt.Errorf("aspire: WithUrl: parameter %q must be one of [string, *ReferenceExpression], got %T", "url", url)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if url != nil { reqArgs["url"] = serializeValue(url) }
	if len(options) > 0 {
		merged := &WithUrlOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrl", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *executableResource) WithUrlForEndpoint(endpointName string, callback func(obj *ResourceUrlAnnotation)) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			arg0 := callbackArg[*ResourceUrlAnnotation](args, 0)
			cb(arg0)
			return map[string]any{
				"p0": serializeValue(arg0),
			}
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrlForEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrls customizes displayed URLs via callback
func (s *executableResource) WithUrls(callback func(obj ResourceUrlsCallbackContext)) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceUrlsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrls", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithWorkingDirectory sets the executable working directory
func (s *executableResource) WithWorkingDirectory(workingDirectory string) ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["workingDirectory"] = serializeValue(workingDirectory)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withWorkingDirectory", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithoutHttpsCertificate removes HTTPS certificate configuration
func (s *executableResource) WithoutHttpsCertificate() ExecutableResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withoutHttpsCertificate", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ExecuteCommandContext is the public interface for handle type ExecuteCommandContext.
type ExecuteCommandContext interface {
	handleReference
	CancellationToken() (*CancellationToken, error)
	Logger() Logger
	ResourceName() (string, error)
	ServiceProvider() ServiceProvider
	SetCancellationToken(options ...*SetCancellationTokenOptions) ExecuteCommandContext
	SetLogger(value Logger) ExecuteCommandContext
	SetResourceName(value string) ExecuteCommandContext
	SetServiceProvider(value ServiceProvider) ExecuteCommandContext
	Err() error
}

// executeCommandContext is the unexported impl of ExecuteCommandContext.
type executeCommandContext struct {
	*resourceBuilderBase
}

// newExecuteCommandContextFromHandle wraps an existing handle as ExecuteCommandContext.
func newExecuteCommandContextFromHandle(h *handle, c *client) ExecuteCommandContext {
	return &executeCommandContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// CancellationToken gets the CancellationToken property
func (s *executeCommandContext) CancellationToken() (*CancellationToken, error) {
	if s.err != nil { var zero *CancellationToken; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ExecuteCommandContext.cancellationToken", reqArgs)
	if err != nil {
		var zero *CancellationToken
		return zero, err
	}
	return decodeAs[*CancellationToken](result)
}

// Logger gets the Logger property
func (s *executeCommandContext) Logger() Logger {
	if s.err != nil { return &logger{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ExecuteCommandContext.logger", reqArgs)
	if err != nil {
		return &logger{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/ExecuteCommandContext.logger returned unexpected type %T", result)
		return &logger{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &logger{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ResourceName gets the ResourceName property
func (s *executeCommandContext) ResourceName() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ExecuteCommandContext.resourceName", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// ServiceProvider gets the ServiceProvider property
func (s *executeCommandContext) ServiceProvider() ServiceProvider {
	if s.err != nil { return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ExecuteCommandContext.serviceProvider", reqArgs)
	if err != nil {
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/ExecuteCommandContext.serviceProvider returned unexpected type %T", result)
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &serviceProvider{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// SetCancellationToken sets the CancellationToken property
func (s *executeCommandContext) SetCancellationToken(options ...*SetCancellationTokenOptions) ExecuteCommandContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &SetCancellationTokenOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.Value != nil {
			ctx = merged.Value.Context()
			if id := s.client.registerCancellation(merged.Value); id != "" {
				reqArgs["value"] = id
			}
		}
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ExecuteCommandContext.setCancellationToken", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetLogger sets the Logger property
func (s *executeCommandContext) SetLogger(value Logger) ExecuteCommandContext {
	if s.err != nil { return s }
	if value != nil { if err := value.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ExecuteCommandContext.setLogger", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetResourceName sets the ResourceName property
func (s *executeCommandContext) SetResourceName(value string) ExecuteCommandContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ExecuteCommandContext.setResourceName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetServiceProvider sets the ServiceProvider property
func (s *executeCommandContext) SetServiceProvider(value ServiceProvider) ExecuteCommandContext {
	if s.err != nil { return s }
	if value != nil { if err := value.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ExecuteCommandContext.setServiceProvider", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ExecutionConfigurationBuilder is the public interface for handle type ExecutionConfigurationBuilder.
type ExecutionConfigurationBuilder interface {
	handleReference
	Build(executionContext DistributedApplicationExecutionContext, options ...*BuildOptions) ExecutionConfigurationResult
	WithArgumentsConfig() ExecutionConfigurationBuilder
	WithCertificateTrustConfig(configContextFactory func(arg CertificateTrustScope) *CertificateTrustExecutionConfigurationContext) ExecutionConfigurationBuilder
	WithEnvironmentVariablesConfig() ExecutionConfigurationBuilder
	WithHttpsCertificateConfig(configContextFactory func(arg *HttpsCertificateInfo) *HttpsCertificateExecutionConfigurationContext) ExecutionConfigurationBuilder
	Err() error
}

// executionConfigurationBuilder is the unexported impl of ExecutionConfigurationBuilder.
type executionConfigurationBuilder struct {
	*resourceBuilderBase
}

// newExecutionConfigurationBuilderFromHandle wraps an existing handle as ExecutionConfigurationBuilder.
func newExecutionConfigurationBuilderFromHandle(h *handle, c *client) ExecutionConfigurationBuilder {
	return &executionConfigurationBuilder{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Build builds the execution configuration
func (s *executionConfigurationBuilder) Build(executionContext DistributedApplicationExecutionContext, options ...*BuildOptions) ExecutionConfigurationResult {
	if s.err != nil { return &executionConfigurationResult{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	if executionContext != nil { if err := executionContext.Err(); err != nil { return &executionConfigurationResult{resourceBuilderBase: newErroredResourceBuilder(err, s.client)} } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["executionContext"] = serializeValue(executionContext)
	if len(options) > 0 {
		merged := &BuildOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.CancellationToken != nil {
			ctx = merged.CancellationToken.Context()
			if id := s.client.registerCancellation(merged.CancellationToken); id != "" {
				reqArgs["cancellationToken"] = id
			}
		}
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/buildExecutionConfiguration", reqArgs)
	if err != nil {
		return &executionConfigurationResult{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/buildExecutionConfiguration returned unexpected type %T", result)
		return &executionConfigurationResult{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &executionConfigurationResult{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// WithArgumentsConfig adds an arguments configuration gatherer
func (s *executionConfigurationBuilder) WithArgumentsConfig() ExecutionConfigurationBuilder {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withArgumentsConfig", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCertificateTrustConfig adds a certificate trust configuration gatherer
func (s *executionConfigurationBuilder) WithCertificateTrustConfig(configContextFactory func(arg CertificateTrustScope) *CertificateTrustExecutionConfigurationContext) ExecutionConfigurationBuilder {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if configContextFactory != nil {
		cb := configContextFactory
		shim := func(args ...any) any {
			return cb(callbackArg[CertificateTrustScope](args, 0))
		}
		reqArgs["configContextFactory"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withCertificateTrustConfig", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEnvironmentVariablesConfig adds an environment variables configuration gatherer
func (s *executionConfigurationBuilder) WithEnvironmentVariablesConfig() ExecutionConfigurationBuilder {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEnvironmentVariablesConfig", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpsCertificateConfig adds an HTTPS certificate configuration gatherer
func (s *executionConfigurationBuilder) WithHttpsCertificateConfig(configContextFactory func(arg *HttpsCertificateInfo) *HttpsCertificateExecutionConfigurationContext) ExecutionConfigurationBuilder {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if configContextFactory != nil {
		cb := configContextFactory
		shim := func(args ...any) any {
			return cb(callbackArg[*HttpsCertificateInfo](args, 0))
		}
		reqArgs["configContextFactory"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpsCertificateConfigExport", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ExecutionConfigurationResult is the public interface for handle type ExecutionConfigurationResult.
type ExecutionConfigurationResult interface {
	handleReference
	GetCertificateTrustData() (*CertificateTrustExecutionConfigurationExportData, error)
	GetHttpsCertificateData() (*HttpsCertificateExecutionConfigurationExportData, error)
	Err() error
}

// executionConfigurationResult is the unexported impl of ExecutionConfigurationResult.
type executionConfigurationResult struct {
	*resourceBuilderBase
}

// newExecutionConfigurationResultFromHandle wraps an existing handle as ExecutionConfigurationResult.
func newExecutionConfigurationResultFromHandle(h *handle, c *client) ExecutionConfigurationResult {
	return &executionConfigurationResult{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// GetCertificateTrustData gets certificate trust execution-configuration data
func (s *executionConfigurationResult) GetCertificateTrustData() (*CertificateTrustExecutionConfigurationExportData, error) {
	if s.err != nil { var zero *CertificateTrustExecutionConfigurationExportData; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"configuration": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getCertificateTrustData", reqArgs)
	if err != nil {
		var zero *CertificateTrustExecutionConfigurationExportData
		return zero, err
	}
	return decodeAs[*CertificateTrustExecutionConfigurationExportData](result)
}

// GetHttpsCertificateData gets HTTPS certificate execution-configuration data
func (s *executionConfigurationResult) GetHttpsCertificateData() (*HttpsCertificateExecutionConfigurationExportData, error) {
	if s.err != nil { var zero *HttpsCertificateExecutionConfigurationExportData; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"configuration": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getHttpsCertificateData", reqArgs)
	if err != nil {
		var zero *HttpsCertificateExecutionConfigurationExportData
		return zero, err
	}
	return decodeAs[*HttpsCertificateExecutionConfigurationExportData](result)
}

// ExternalServiceResource is the public interface for handle type ExternalServiceResource.
type ExternalServiceResource interface {
	handleReference
	CreateExecutionConfiguration() ExecutionConfigurationBuilder
	ExcludeFromManifest() ExternalServiceResource
	ExcludeFromMcp() ExternalServiceResource
	GetResourceName() (string, error)
	OnBeforeResourceStarted(callback func(arg BeforeResourceStartedEvent)) ExternalServiceResource
	OnInitializeResource(callback func(arg InitializeResourceEvent)) ExternalServiceResource
	OnResourceReady(callback func(arg ResourceReadyEvent)) ExternalServiceResource
	OnResourceStopped(callback func(arg ResourceStoppedEvent)) ExternalServiceResource
	WithChildRelationship(child Resource) ExternalServiceResource
	WithCommand(name string, displayName string, executeCommand func(arg ExecuteCommandContext) *ExecuteCommandResult, options ...*WithCommandOptions) ExternalServiceResource
	WithContainerRegistry(registry Resource) ExternalServiceResource
	WithDockerfileBaseImage(options ...*WithDockerfileBaseImageOptions) ExternalServiceResource
	WithExplicitStart() ExternalServiceResource
	WithHealthCheck(key string) ExternalServiceResource
	WithHttpHealthCheck(options ...*WithHttpHealthCheckOptions) ExternalServiceResource
	WithIconName(iconName string, options ...*WithIconNameOptions) ExternalServiceResource
	WithParentRelationship(parent Resource) ExternalServiceResource
	WithPipelineConfiguration(callback func(obj PipelineConfigurationContext)) ExternalServiceResource
	WithPipelineStepFactory(stepName string, callback func(arg PipelineStepContext), options ...*WithPipelineStepFactoryOptions) ExternalServiceResource
	WithRelationship(resourceBuilder Resource, type_ string) ExternalServiceResource
	WithRequiredCommand(command string, options ...*WithRequiredCommandOptions) ExternalServiceResource
	WithUrl(url any, options ...*WithUrlOptions) ExternalServiceResource
	WithUrlForEndpoint(endpointName string, callback func(obj *ResourceUrlAnnotation)) ExternalServiceResource
	WithUrls(callback func(obj ResourceUrlsCallbackContext)) ExternalServiceResource
	Err() error
}

// externalServiceResource is the unexported impl of ExternalServiceResource.
type externalServiceResource struct {
	*resourceBuilderBase
}

// newExternalServiceResourceFromHandle wraps an existing handle as ExternalServiceResource.
func newExternalServiceResourceFromHandle(h *handle, c *client) ExternalServiceResource {
	return &externalServiceResource{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// CreateExecutionConfiguration creates an execution configuration builder
func (s *externalServiceResource) CreateExecutionConfiguration() ExecutionConfigurationBuilder {
	if s.err != nil { return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/createExecutionConfiguration returned unexpected type %T", result)
		return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &executionConfigurationBuilder{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *externalServiceResource) ExcludeFromManifest() ExternalServiceResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/excludeFromManifest", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *externalServiceResource) ExcludeFromMcp() ExternalServiceResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/excludeFromMcp", reqArgs); err != nil { s.setErr(err) }
	return s
}

// GetResourceName gets the resource name
func (s *externalServiceResource) GetResourceName() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *externalServiceResource) OnBeforeResourceStarted(callback func(arg BeforeResourceStartedEvent)) ExternalServiceResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[BeforeResourceStartedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onBeforeResourceStarted", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *externalServiceResource) OnInitializeResource(callback func(arg InitializeResourceEvent)) ExternalServiceResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[InitializeResourceEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onInitializeResource", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceReady subscribes to the ResourceReady event
func (s *externalServiceResource) OnResourceReady(callback func(arg ResourceReadyEvent)) ExternalServiceResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceReadyEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceReady", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *externalServiceResource) OnResourceStopped(callback func(arg ResourceStoppedEvent)) ExternalServiceResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceStoppedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceStopped", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithChildRelationship sets a child relationship
func (s *externalServiceResource) WithChildRelationship(child Resource) ExternalServiceResource {
	if s.err != nil { return s }
	if child != nil { if err := child.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["child"] = serializeValue(child)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderChildRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCommand adds a resource command
func (s *externalServiceResource) WithCommand(name string, displayName string, executeCommand func(arg ExecuteCommandContext) *ExecuteCommandResult, options ...*WithCommandOptions) ExternalServiceResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["displayName"] = serializeValue(displayName)
	if executeCommand != nil {
		cb := executeCommand
		shim := func(args ...any) any {
			return cb(callbackArg[ExecuteCommandContext](args, 0))
		}
		reqArgs["executeCommand"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithContainerRegistry configures a resource to use a container registry
func (s *externalServiceResource) WithContainerRegistry(registry Resource) ExternalServiceResource {
	if s.err != nil { return s }
	if registry != nil { if err := registry.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["registry"] = serializeValue(registry)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withContainerRegistry", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *externalServiceResource) WithDockerfileBaseImage(options ...*WithDockerfileBaseImageOptions) ExternalServiceResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithDockerfileBaseImageOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withDockerfileBaseImage", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithExplicitStart prevents resource from starting automatically
func (s *externalServiceResource) WithExplicitStart() ExternalServiceResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withExplicitStart", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHealthCheck adds a health check by key
func (s *externalServiceResource) WithHealthCheck(key string) ExternalServiceResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["key"] = serializeValue(key)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHealthCheck", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpHealthCheck adds an HTTP health check to the external service
func (s *externalServiceResource) WithHttpHealthCheck(options ...*WithHttpHealthCheckOptions) ExternalServiceResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpHealthCheckOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withExternalServiceHttpHealthCheck", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithIconName sets the icon for the resource
func (s *externalServiceResource) WithIconName(iconName string, options ...*WithIconNameOptions) ExternalServiceResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["iconName"] = serializeValue(iconName)
	if len(options) > 0 {
		merged := &WithIconNameOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withIconName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithParentRelationship sets the parent relationship
func (s *externalServiceResource) WithParentRelationship(parent Resource) ExternalServiceResource {
	if s.err != nil { return s }
	if parent != nil { if err := parent.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["parent"] = serializeValue(parent)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderParentRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *externalServiceResource) WithPipelineConfiguration(callback func(obj PipelineConfigurationContext)) ExternalServiceResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineConfigurationContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withPipelineConfiguration", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *externalServiceResource) WithPipelineStepFactory(stepName string, callback func(arg PipelineStepContext), options ...*WithPipelineStepFactoryOptions) ExternalServiceResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["stepName"] = serializeValue(stepName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineStepContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithPipelineStepFactoryOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withPipelineStepFactory", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRelationship adds a relationship to another resource
func (s *externalServiceResource) WithRelationship(resourceBuilder Resource, type_ string) ExternalServiceResource {
	if s.err != nil { return s }
	if resourceBuilder != nil { if err := resourceBuilder.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["resourceBuilder"] = serializeValue(resourceBuilder)
	reqArgs["type"] = serializeValue(type_)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRequiredCommand adds a required command dependency
func (s *externalServiceResource) WithRequiredCommand(command string, options ...*WithRequiredCommandOptions) ExternalServiceResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["command"] = serializeValue(command)
	if len(options) > 0 {
		merged := &WithRequiredCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRequiredCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrl adds or modifies displayed URLs
// Allowed types for parameter url: string, *ReferenceExpression.
func (s *externalServiceResource) WithUrl(url any, options ...*WithUrlOptions) ExternalServiceResource {
	if s.err != nil { return s }
	switch url.(type) {
	case string, *ReferenceExpression:
	default:
		err := fmt.Errorf("aspire: WithUrl: parameter %q must be one of [string, *ReferenceExpression], got %T", "url", url)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if url != nil { reqArgs["url"] = serializeValue(url) }
	if len(options) > 0 {
		merged := &WithUrlOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrl", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *externalServiceResource) WithUrlForEndpoint(endpointName string, callback func(obj *ResourceUrlAnnotation)) ExternalServiceResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			arg0 := callbackArg[*ResourceUrlAnnotation](args, 0)
			cb(arg0)
			return map[string]any{
				"p0": serializeValue(arg0),
			}
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrlForEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrls customizes displayed URLs via callback
func (s *externalServiceResource) WithUrls(callback func(obj ResourceUrlsCallbackContext)) ExternalServiceResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceUrlsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrls", reqArgs); err != nil { s.setErr(err) }
	return s
}

// GoAppResource is the public interface for handle type GoAppResource.
type GoAppResource interface {
	handleReference
	AsHttp2Service() GoAppResource
	Command() (string, error)
	CreateExecutionConfiguration() ExecutionConfigurationBuilder
	ExcludeFromManifest() GoAppResource
	ExcludeFromMcp() GoAppResource
	GetEndpoint(name string) EndpointReference
	GetResourceName() (string, error)
	Name() (string, error)
	OnBeforeResourceStarted(callback func(arg BeforeResourceStartedEvent)) GoAppResource
	OnInitializeResource(callback func(arg InitializeResourceEvent)) GoAppResource
	OnResourceEndpointsAllocated(callback func(arg ResourceEndpointsAllocatedEvent)) GoAppResource
	OnResourceReady(callback func(arg ResourceReadyEvent)) GoAppResource
	OnResourceStopped(callback func(arg ResourceStoppedEvent)) GoAppResource
	PublishAsDockerFile(configure func(obj ContainerResource)) GoAppResource
	PublishWithContainerFiles(source ResourceWithContainerFiles, destinationPath string) GoAppResource
	WaitFor(dependency Resource, options ...*WaitForOptions) GoAppResource
	WaitForCompletion(dependency Resource, options ...*WaitForCompletionOptions) GoAppResource
	WaitForStart(dependency Resource, options ...*WaitForStartOptions) GoAppResource
	WithAppArgs(args []string) GoAppResource
	WithArgs(args []string) GoAppResource
	WithArgsCallback(callback func(obj CommandLineArgsCallbackContext)) GoAppResource
	WithCertificateTrustScope(scope CertificateTrustScope) GoAppResource
	WithChildRelationship(child Resource) GoAppResource
	WithCommand(name string, displayName string, executeCommand func(arg ExecuteCommandContext) *ExecuteCommandResult, options ...*WithCommandOptions) GoAppResource
	WithContainerRegistry(registry Resource) GoAppResource
	WithDelveServer(options ...*WithDelveServerOptions) GoAppResource
	WithDeveloperCertificateTrust(trust bool) GoAppResource
	WithDockerfileBaseImage(options ...*WithDockerfileBaseImageOptions) GoAppResource
	WithEndpoint(options ...*WithEndpointOptions) GoAppResource
	WithEndpointCallback(endpointName string, callback func(obj EndpointUpdateContext), options ...*WithEndpointCallbackOptions) GoAppResource
	WithEnvironment(name string, value any) GoAppResource
	WithEnvironmentCallback(callback func(arg EnvironmentCallbackContext)) GoAppResource
	WithExecutableCommand(command string) GoAppResource
	WithExplicitStart() GoAppResource
	WithExternalHttpEndpoints() GoAppResource
	WithHealthCheck(key string) GoAppResource
	WithHttpCommand(path string, displayName string, options ...*WithHttpCommandOptions) GoAppResource
	WithHttpEndpoint(options ...*WithHttpEndpointOptions) GoAppResource
	WithHttpEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpEndpointCallbackOptions) GoAppResource
	WithHttpHealthCheck(options ...*WithHttpHealthCheckOptions) GoAppResource
	WithHttpProbe(probeType ProbeType, options ...*WithHttpProbeOptions) GoAppResource
	WithHttpsDeveloperCertificate(options ...*WithHttpsDeveloperCertificateOptions) GoAppResource
	WithHttpsEndpoint(options ...*WithHttpsEndpointOptions) GoAppResource
	WithHttpsEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpsEndpointCallbackOptions) GoAppResource
	WithIconName(iconName string, options ...*WithIconNameOptions) GoAppResource
	WithImagePushOptions(callback func(arg ContainerImagePushOptionsCallbackContext)) GoAppResource
	WithMcpServer(options ...*WithMcpServerOptions) GoAppResource
	WithModDownload() GoAppResource
	WithModTidy() GoAppResource
	WithModVendor() GoAppResource
	WithOtlpExporter(options ...*WithOtlpExporterOptions) GoAppResource
	WithParentRelationship(parent Resource) GoAppResource
	WithPipelineConfiguration(callback func(obj PipelineConfigurationContext)) GoAppResource
	WithPipelineStepFactory(stepName string, callback func(arg PipelineStepContext), options ...*WithPipelineStepFactoryOptions) GoAppResource
	WithReference(source any, options ...*WithReferenceOptions) GoAppResource
	WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) GoAppResource
	WithRelationship(resourceBuilder Resource, type_ string) GoAppResource
	WithRemoteImageName(remoteImageName string) GoAppResource
	WithRemoteImageTag(remoteImageTag string) GoAppResource
	WithRequiredCommand(command string, options ...*WithRequiredCommandOptions) GoAppResource
	WithUrl(url any, options ...*WithUrlOptions) GoAppResource
	WithUrlForEndpoint(endpointName string, callback func(obj *ResourceUrlAnnotation)) GoAppResource
	WithUrls(callback func(obj ResourceUrlsCallbackContext)) GoAppResource
	WithVetTool() GoAppResource
	WithWorkingDirectory(workingDirectory string) GoAppResource
	WithoutHttpsCertificate() GoAppResource
	WorkingDirectory() (string, error)
	Err() error
}

// goAppResource is the unexported impl of GoAppResource.
type goAppResource struct {
	*resourceBuilderBase
}

// newGoAppResourceFromHandle wraps an existing handle as GoAppResource.
func newGoAppResourceFromHandle(h *handle, c *client) GoAppResource {
	return &goAppResource{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// AsHttp2Service configures resource for HTTP/2
func (s *goAppResource) AsHttp2Service() GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/asHttp2Service", reqArgs); err != nil { s.setErr(err) }
	return s
}

// Command gets the Command property
func (s *goAppResource) Command() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Go/GoAppResource.command", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// CreateExecutionConfiguration creates an execution configuration builder
func (s *goAppResource) CreateExecutionConfiguration() ExecutionConfigurationBuilder {
	if s.err != nil { return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/createExecutionConfiguration returned unexpected type %T", result)
		return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &executionConfigurationBuilder{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *goAppResource) ExcludeFromManifest() GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/excludeFromManifest", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *goAppResource) ExcludeFromMcp() GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/excludeFromMcp", reqArgs); err != nil { s.setErr(err) }
	return s
}

// GetEndpoint gets an endpoint reference
func (s *goAppResource) GetEndpoint(name string) EndpointReference {
	if s.err != nil { return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getEndpoint", reqArgs)
	if err != nil {
		return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/getEndpoint returned unexpected type %T", result)
		return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &endpointReference{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// GetResourceName gets the resource name
func (s *goAppResource) GetResourceName() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// Name gets the Name property
func (s *goAppResource) Name() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Go/GoAppResource.name", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *goAppResource) OnBeforeResourceStarted(callback func(arg BeforeResourceStartedEvent)) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[BeforeResourceStartedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onBeforeResourceStarted", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *goAppResource) OnInitializeResource(callback func(arg InitializeResourceEvent)) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[InitializeResourceEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onInitializeResource", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceEndpointsAllocated subscribes to the ResourceEndpointsAllocated event
func (s *goAppResource) OnResourceEndpointsAllocated(callback func(arg ResourceEndpointsAllocatedEvent)) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceEndpointsAllocatedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceEndpointsAllocated", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceReady subscribes to the ResourceReady event
func (s *goAppResource) OnResourceReady(callback func(arg ResourceReadyEvent)) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceReadyEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceReady", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *goAppResource) OnResourceStopped(callback func(arg ResourceStoppedEvent)) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceStoppedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceStopped", reqArgs); err != nil { s.setErr(err) }
	return s
}

// PublishAsDockerFile publishes an executable as a Docker file
func (s *goAppResource) PublishAsDockerFile(configure func(obj ContainerResource)) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if configure != nil {
		cb := configure
		shim := func(args ...any) any {
			cb(callbackArg[ContainerResource](args, 0))
			return nil
		}
		reqArgs["configure"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/publishAsDockerFile", reqArgs); err != nil { s.setErr(err) }
	return s
}

// PublishWithContainerFiles configures the resource to copy container files from the specified source during publishing
func (s *goAppResource) PublishWithContainerFiles(source ResourceWithContainerFiles, destinationPath string) GoAppResource {
	if s.err != nil { return s }
	if source != nil { if err := source.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["source"] = serializeValue(source)
	reqArgs["destinationPath"] = serializeValue(destinationPath)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/publishWithContainerFilesFromResource", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WaitFor waits for another resource to be ready
func (s *goAppResource) WaitFor(dependency Resource, options ...*WaitForOptions) GoAppResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if len(options) > 0 {
		merged := &WaitForOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitFor", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WaitForCompletion waits for resource completion
func (s *goAppResource) WaitForCompletion(dependency Resource, options ...*WaitForCompletionOptions) GoAppResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if len(options) > 0 {
		merged := &WaitForCompletionOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitForResourceCompletion", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WaitForStart waits for another resource to start
func (s *goAppResource) WaitForStart(dependency Resource, options ...*WaitForStartOptions) GoAppResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if len(options) > 0 {
		merged := &WaitForStartOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitForStart", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithAppArgs passes extra arguments to the Go program at runtime (after go run . in normal mode, or after -- in Delve mode)
func (s *goAppResource) WithAppArgs(args []string) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if args != nil { reqArgs["args"] = serializeValue(args) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Go/withAppArgs", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithArgs adds arguments
func (s *goAppResource) WithArgs(args []string) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if args != nil { reqArgs["args"] = serializeValue(args) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withArgs", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithArgsCallback sets command-line arguments via callback
func (s *goAppResource) WithArgsCallback(callback func(obj CommandLineArgsCallbackContext)) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[CommandLineArgsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withArgsCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCertificateTrustScope sets the certificate trust scope
func (s *goAppResource) WithCertificateTrustScope(scope CertificateTrustScope) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["scope"] = serializeValue(scope)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withCertificateTrustScope", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithChildRelationship sets a child relationship
func (s *goAppResource) WithChildRelationship(child Resource) GoAppResource {
	if s.err != nil { return s }
	if child != nil { if err := child.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["child"] = serializeValue(child)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderChildRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCommand adds a resource command
func (s *goAppResource) WithCommand(name string, displayName string, executeCommand func(arg ExecuteCommandContext) *ExecuteCommandResult, options ...*WithCommandOptions) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["displayName"] = serializeValue(displayName)
	if executeCommand != nil {
		cb := executeCommand
		shim := func(args ...any) any {
			return cb(callbackArg[ExecuteCommandContext](args, 0))
		}
		reqArgs["executeCommand"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithContainerRegistry configures a resource to use a container registry
func (s *goAppResource) WithContainerRegistry(registry Resource) GoAppResource {
	if s.err != nil { return s }
	if registry != nil { if err := registry.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["registry"] = serializeValue(registry)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withContainerRegistry", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDelveServer starts a headless Delve server for remote debugging (GoLand, VS Code attach, any DAP client)
func (s *goAppResource) WithDelveServer(options ...*WithDelveServerOptions) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithDelveServerOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Go/withDelveServer", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDeveloperCertificateTrust configures developer certificate trust
func (s *goAppResource) WithDeveloperCertificateTrust(trust bool) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["trust"] = serializeValue(trust)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withDeveloperCertificateTrust", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *goAppResource) WithDockerfileBaseImage(options ...*WithDockerfileBaseImageOptions) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithDockerfileBaseImageOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withDockerfileBaseImage", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEndpoint adds a network endpoint
func (s *goAppResource) WithEndpoint(options ...*WithEndpointOptions) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithEndpointOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEndpointCallback updates a named endpoint via callback
func (s *goAppResource) WithEndpointCallback(endpointName string, callback func(obj EndpointUpdateContext), options ...*WithEndpointCallbackOptions) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EndpointUpdateContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithEndpointCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEndpointCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEnvironment sets an environment variable
// Allowed types for parameter value: string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue.
func (s *goAppResource) WithEnvironment(name string, value any) GoAppResource {
	if s.err != nil { return s }
	switch value.(type) {
	case string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue:
	default:
		err := fmt.Errorf("aspire: WithEnvironment: parameter %q must be one of [string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue], got %T", "value", value)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if value != nil { reqArgs["value"] = serializeValue(value) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEnvironment", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEnvironmentCallback sets environment variables via callback
func (s *goAppResource) WithEnvironmentCallback(callback func(arg EnvironmentCallbackContext)) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EnvironmentCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEnvironmentCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithExecutableCommand sets the executable command
func (s *goAppResource) WithExecutableCommand(command string) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["command"] = serializeValue(command)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withExecutableCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithExplicitStart prevents resource from starting automatically
func (s *goAppResource) WithExplicitStart() GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withExplicitStart", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithExternalHttpEndpoints makes HTTP endpoints externally accessible
func (s *goAppResource) WithExternalHttpEndpoints() GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withExternalHttpEndpoints", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHealthCheck adds a health check by key
func (s *goAppResource) WithHealthCheck(key string) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["key"] = serializeValue(key)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHealthCheck", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpCommand adds an HTTP resource command
func (s *goAppResource) WithHttpCommand(path string, displayName string, options ...*WithHttpCommandOptions) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["path"] = serializeValue(path)
	reqArgs["displayName"] = serializeValue(displayName)
	if len(options) > 0 {
		merged := &WithHttpCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpEndpoint adds an HTTP endpoint
func (s *goAppResource) WithHttpEndpoint(options ...*WithHttpEndpointOptions) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpEndpointOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpEndpointCallback updates an HTTP endpoint via callback
func (s *goAppResource) WithHttpEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpEndpointCallbackOptions) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EndpointUpdateContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithHttpEndpointCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpEndpointCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpHealthCheck adds an HTTP health check
func (s *goAppResource) WithHttpHealthCheck(options ...*WithHttpHealthCheckOptions) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpHealthCheckOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpHealthCheck", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpProbe adds an HTTP health probe to the resource
func (s *goAppResource) WithHttpProbe(probeType ProbeType, options ...*WithHttpProbeOptions) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["probeType"] = serializeValue(probeType)
	if len(options) > 0 {
		merged := &WithHttpProbeOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpProbe", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpsDeveloperCertificate configures HTTPS with a developer certificate
func (s *goAppResource) WithHttpsDeveloperCertificate(options ...*WithHttpsDeveloperCertificateOptions) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpsDeveloperCertificateOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withParameterHttpsDeveloperCertificate", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpsEndpoint adds an HTTPS endpoint
func (s *goAppResource) WithHttpsEndpoint(options ...*WithHttpsEndpointOptions) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpsEndpointOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpsEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpsEndpointCallback updates an HTTPS endpoint via callback
func (s *goAppResource) WithHttpsEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpsEndpointCallbackOptions) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EndpointUpdateContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithHttpsEndpointCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpsEndpointCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithIconName sets the icon for the resource
func (s *goAppResource) WithIconName(iconName string, options ...*WithIconNameOptions) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["iconName"] = serializeValue(iconName)
	if len(options) > 0 {
		merged := &WithIconNameOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withIconName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithImagePushOptions sets image push options via callback
func (s *goAppResource) WithImagePushOptions(callback func(arg ContainerImagePushOptionsCallbackContext)) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ContainerImagePushOptionsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withImagePushOptions", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMcpServer configures an MCP server endpoint on the resource
func (s *goAppResource) WithMcpServer(options ...*WithMcpServerOptions) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithMcpServerOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withMcpServer", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithModDownload runs go mod download before starting the application to pre-fetch module dependencies into the local cache
func (s *goAppResource) WithModDownload() GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Go/withModDownload", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithModTidy runs go mod tidy before starting the application to ensure go.sum is up to date
func (s *goAppResource) WithModTidy() GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Go/withModTidy", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithModVendor runs go mod vendor before starting the application to cache module dependencies locally
func (s *goAppResource) WithModVendor() GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Go/withModVendor", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithOtlpExporter configures OTLP telemetry export
func (s *goAppResource) WithOtlpExporter(options ...*WithOtlpExporterOptions) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithOtlpExporterOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withOtlpExporter", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithParentRelationship sets the parent relationship
func (s *goAppResource) WithParentRelationship(parent Resource) GoAppResource {
	if s.err != nil { return s }
	if parent != nil { if err := parent.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["parent"] = serializeValue(parent)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderParentRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *goAppResource) WithPipelineConfiguration(callback func(obj PipelineConfigurationContext)) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineConfigurationContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withPipelineConfiguration", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *goAppResource) WithPipelineStepFactory(stepName string, callback func(arg PipelineStepContext), options ...*WithPipelineStepFactoryOptions) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["stepName"] = serializeValue(stepName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineStepContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithPipelineStepFactoryOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withPipelineStepFactory", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithReference adds a reference to another resource
// Allowed types for parameter source: Resource, EndpointReference, string.
func (s *goAppResource) WithReference(source any, options ...*WithReferenceOptions) GoAppResource {
	if s.err != nil { return s }
	switch source.(type) {
	case Resource, EndpointReference, string:
	default:
		err := fmt.Errorf("aspire: WithReference: parameter %q must be one of [Resource, EndpointReference, string], got %T", "source", source)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if source != nil { reqArgs["source"] = serializeValue(source) }
	if len(options) > 0 {
		merged := &WithReferenceOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withReference", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithReferenceEnvironment configures which reference values are injected into environment variables
func (s *goAppResource) WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if options != nil { reqArgs["options"] = serializeValue(options) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withReferenceEnvironment", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRelationship adds a relationship to another resource
func (s *goAppResource) WithRelationship(resourceBuilder Resource, type_ string) GoAppResource {
	if s.err != nil { return s }
	if resourceBuilder != nil { if err := resourceBuilder.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["resourceBuilder"] = serializeValue(resourceBuilder)
	reqArgs["type"] = serializeValue(type_)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRemoteImageName sets the remote image name for publishing
func (s *goAppResource) WithRemoteImageName(remoteImageName string) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["remoteImageName"] = serializeValue(remoteImageName)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRemoteImageName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRemoteImageTag sets the remote image tag for publishing
func (s *goAppResource) WithRemoteImageTag(remoteImageTag string) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["remoteImageTag"] = serializeValue(remoteImageTag)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRemoteImageTag", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRequiredCommand adds a required command dependency
func (s *goAppResource) WithRequiredCommand(command string, options ...*WithRequiredCommandOptions) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["command"] = serializeValue(command)
	if len(options) > 0 {
		merged := &WithRequiredCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRequiredCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrl adds or modifies displayed URLs
// Allowed types for parameter url: string, *ReferenceExpression.
func (s *goAppResource) WithUrl(url any, options ...*WithUrlOptions) GoAppResource {
	if s.err != nil { return s }
	switch url.(type) {
	case string, *ReferenceExpression:
	default:
		err := fmt.Errorf("aspire: WithUrl: parameter %q must be one of [string, *ReferenceExpression], got %T", "url", url)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if url != nil { reqArgs["url"] = serializeValue(url) }
	if len(options) > 0 {
		merged := &WithUrlOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrl", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *goAppResource) WithUrlForEndpoint(endpointName string, callback func(obj *ResourceUrlAnnotation)) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			arg0 := callbackArg[*ResourceUrlAnnotation](args, 0)
			cb(arg0)
			return map[string]any{
				"p0": serializeValue(arg0),
			}
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrlForEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrls customizes displayed URLs via callback
func (s *goAppResource) WithUrls(callback func(obj ResourceUrlsCallbackContext)) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceUrlsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrls", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithVetTool runs go vet ./... before starting the application to catch static analysis issues
func (s *goAppResource) WithVetTool() GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Go/withVetTool", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithWorkingDirectory sets the executable working directory
func (s *goAppResource) WithWorkingDirectory(workingDirectory string) GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["workingDirectory"] = serializeValue(workingDirectory)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withWorkingDirectory", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithoutHttpsCertificate removes HTTPS certificate configuration
func (s *goAppResource) WithoutHttpsCertificate() GoAppResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withoutHttpsCertificate", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WorkingDirectory gets the WorkingDirectory property
func (s *goAppResource) WorkingDirectory() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Go/GoAppResource.workingDirectory", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// HostEnvironment is the public interface for handle type HostEnvironment.
type HostEnvironment interface {
	handleReference
	IsDevelopment() (bool, error)
	IsEnvironment(environmentName string) (bool, error)
	IsProduction() (bool, error)
	IsStaging() (bool, error)
	Err() error
}

// hostEnvironment is the unexported impl of HostEnvironment.
type hostEnvironment struct {
	*resourceBuilderBase
}

// newHostEnvironmentFromHandle wraps an existing handle as HostEnvironment.
func newHostEnvironmentFromHandle(h *handle, c *client) HostEnvironment {
	return &hostEnvironment{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// IsDevelopment checks if running in Development environment
func (s *hostEnvironment) IsDevelopment() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"environment": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/isDevelopment", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// IsEnvironment checks if the environment matches the specified name
func (s *hostEnvironment) IsEnvironment(environmentName string) (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"environment": s.handle.ToJSON(),
	}
	reqArgs["environmentName"] = serializeValue(environmentName)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/isEnvironment", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// IsProduction checks if running in Production environment
func (s *hostEnvironment) IsProduction() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"environment": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/isProduction", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// IsStaging checks if running in Staging environment
func (s *hostEnvironment) IsStaging() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"environment": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/isStaging", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// IComputeResource is the public interface for handle type IComputeResource.
type IComputeResource interface {
	handleReference
	Err() error
}

// iComputeResource is the unexported impl of IComputeResource.
type iComputeResource struct {
	*resourceBuilderBase
}

// newIComputeResourceFromHandle wraps an existing handle as IComputeResource.
func newIComputeResourceFromHandle(h *handle, c *client) IComputeResource {
	return &iComputeResource{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// IContainerFilesDestinationResource is the public interface for handle type IContainerFilesDestinationResource.
type IContainerFilesDestinationResource interface {
	handleReference
	Err() error
}

// iContainerFilesDestinationResource is the unexported impl of IContainerFilesDestinationResource.
type iContainerFilesDestinationResource struct {
	*resourceBuilderBase
}

// newIContainerFilesDestinationResourceFromHandle wraps an existing handle as IContainerFilesDestinationResource.
func newIContainerFilesDestinationResourceFromHandle(h *handle, c *client) IContainerFilesDestinationResource {
	return &iContainerFilesDestinationResource{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// InitializeResourceEvent is the public interface for handle type InitializeResourceEvent.
type InitializeResourceEvent interface {
	handleReference
	Eventing() DistributedApplicationEventing
	Logger() Logger
	Notifications() ResourceNotificationService
	Resource() Resource
	Services() ServiceProvider
	Err() error
}

// initializeResourceEvent is the unexported impl of InitializeResourceEvent.
type initializeResourceEvent struct {
	*resourceBuilderBase
}

// newInitializeResourceEventFromHandle wraps an existing handle as InitializeResourceEvent.
func newInitializeResourceEventFromHandle(h *handle, c *client) InitializeResourceEvent {
	return &initializeResourceEvent{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Eventing gets the Eventing property
func (s *initializeResourceEvent) Eventing() DistributedApplicationEventing {
	if s.err != nil { return &distributedApplicationEventing{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/InitializeResourceEvent.eventing", reqArgs)
	if err != nil {
		return &distributedApplicationEventing{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/InitializeResourceEvent.eventing returned unexpected type %T", result)
		return &distributedApplicationEventing{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationEventing{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Logger gets the Logger property
func (s *initializeResourceEvent) Logger() Logger {
	if s.err != nil { return &logger{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/InitializeResourceEvent.logger", reqArgs)
	if err != nil {
		return &logger{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/InitializeResourceEvent.logger returned unexpected type %T", result)
		return &logger{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &logger{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Notifications gets the Notifications property
func (s *initializeResourceEvent) Notifications() ResourceNotificationService {
	if s.err != nil { return &resourceNotificationService{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/InitializeResourceEvent.notifications", reqArgs)
	if err != nil {
		return &resourceNotificationService{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/InitializeResourceEvent.notifications returned unexpected type %T", result)
		return &resourceNotificationService{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &resourceNotificationService{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Resource gets the Resource property
func (s *initializeResourceEvent) Resource() Resource {
	if s.err != nil { return nil }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/InitializeResourceEvent.resource", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(Resource)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/InitializeResourceEvent.resource returned unexpected type %T", result))
		return nil
	}
	return typed
}

// Services gets the Services property
func (s *initializeResourceEvent) Services() ServiceProvider {
	if s.err != nil { return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/InitializeResourceEvent.services", reqArgs)
	if err != nil {
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/InitializeResourceEvent.services returned unexpected type %T", result)
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &serviceProvider{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// LogFacade is the public interface for handle type LogFacade.
type LogFacade interface {
	handleReference
	Debug(message string) error
	Error(message string) error
	Info(message string) error
	Warning(message string) error
	Err() error
}

// logFacade is the unexported impl of LogFacade.
type logFacade struct {
	*resourceBuilderBase
}

// newLogFacadeFromHandle wraps an existing handle as LogFacade.
func newLogFacadeFromHandle(h *handle, c *client) LogFacade {
	return &logFacade{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Debug writes a debug log message
func (s *logFacade) Debug(message string) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["message"] = serializeValue(message)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/debug", reqArgs)
	return err
}

// Error writes an error log message
func (s *logFacade) Error(message string) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["message"] = serializeValue(message)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/error", reqArgs)
	return err
}

// Info writes an informational log message
func (s *logFacade) Info(message string) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["message"] = serializeValue(message)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/info", reqArgs)
	return err
}

// Warning writes a warning log message
func (s *logFacade) Warning(message string) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["message"] = serializeValue(message)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/warning", reqArgs)
	return err
}

// Logger is the public interface for handle type Logger.
type Logger interface {
	handleReference
	Log(level string, message string) error
	LogDebug(message string) error
	LogError(message string) error
	LogInformation(message string) error
	LogWarning(message string) error
	Err() error
}

// logger is the unexported impl of Logger.
type logger struct {
	*resourceBuilderBase
}

// newLoggerFromHandle wraps an existing handle as Logger.
func newLoggerFromHandle(h *handle, c *client) Logger {
	return &logger{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Log logs a message with specified level
func (s *logger) Log(level string, message string) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"logger": s.handle.ToJSON(),
	}
	reqArgs["level"] = serializeValue(level)
	reqArgs["message"] = serializeValue(message)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/log", reqArgs)
	return err
}

// LogDebug logs a debug message
func (s *logger) LogDebug(message string) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"logger": s.handle.ToJSON(),
	}
	reqArgs["message"] = serializeValue(message)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/logDebug", reqArgs)
	return err
}

// LogError logs an error message
func (s *logger) LogError(message string) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"logger": s.handle.ToJSON(),
	}
	reqArgs["message"] = serializeValue(message)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/logError", reqArgs)
	return err
}

// LogInformation logs an information message
func (s *logger) LogInformation(message string) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"logger": s.handle.ToJSON(),
	}
	reqArgs["message"] = serializeValue(message)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/logInformation", reqArgs)
	return err
}

// LogWarning logs a warning message
func (s *logger) LogWarning(message string) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"logger": s.handle.ToJSON(),
	}
	reqArgs["message"] = serializeValue(message)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/logWarning", reqArgs)
	return err
}

// LoggerFactory is the public interface for handle type LoggerFactory.
type LoggerFactory interface {
	handleReference
	CreateLogger(categoryName string) Logger
	Err() error
}

// loggerFactory is the unexported impl of LoggerFactory.
type loggerFactory struct {
	*resourceBuilderBase
}

// newLoggerFactoryFromHandle wraps an existing handle as LoggerFactory.
func newLoggerFactoryFromHandle(h *handle, c *client) LoggerFactory {
	return &loggerFactory{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// CreateLogger creates a logger for a category
func (s *loggerFactory) CreateLogger(categoryName string) Logger {
	if s.err != nil { return &logger{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"loggerFactory": s.handle.ToJSON(),
	}
	reqArgs["categoryName"] = serializeValue(categoryName)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/createLogger", reqArgs)
	if err != nil {
		return &logger{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/createLogger returned unexpected type %T", result)
		return &logger{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &logger{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ParameterResource is the public interface for handle type ParameterResource.
type ParameterResource interface {
	handleReference
	CreateExecutionConfiguration() ExecutionConfigurationBuilder
	ExcludeFromManifest() ParameterResource
	ExcludeFromMcp() ParameterResource
	GetResourceName() (string, error)
	OnBeforeResourceStarted(callback func(arg BeforeResourceStartedEvent)) ParameterResource
	OnInitializeResource(callback func(arg InitializeResourceEvent)) ParameterResource
	OnResourceReady(callback func(arg ResourceReadyEvent)) ParameterResource
	OnResourceStopped(callback func(arg ResourceStoppedEvent)) ParameterResource
	WithChildRelationship(child Resource) ParameterResource
	WithCommand(name string, displayName string, executeCommand func(arg ExecuteCommandContext) *ExecuteCommandResult, options ...*WithCommandOptions) ParameterResource
	WithContainerRegistry(registry Resource) ParameterResource
	WithDescription(description string, options ...*WithDescriptionOptions) ParameterResource
	WithDockerfileBaseImage(options ...*WithDockerfileBaseImageOptions) ParameterResource
	WithExplicitStart() ParameterResource
	WithHealthCheck(key string) ParameterResource
	WithIconName(iconName string, options ...*WithIconNameOptions) ParameterResource
	WithParentRelationship(parent Resource) ParameterResource
	WithPipelineConfiguration(callback func(obj PipelineConfigurationContext)) ParameterResource
	WithPipelineStepFactory(stepName string, callback func(arg PipelineStepContext), options ...*WithPipelineStepFactoryOptions) ParameterResource
	WithRelationship(resourceBuilder Resource, type_ string) ParameterResource
	WithRequiredCommand(command string, options ...*WithRequiredCommandOptions) ParameterResource
	WithUrl(url any, options ...*WithUrlOptions) ParameterResource
	WithUrlForEndpoint(endpointName string, callback func(obj *ResourceUrlAnnotation)) ParameterResource
	WithUrls(callback func(obj ResourceUrlsCallbackContext)) ParameterResource
	Err() error
}

// parameterResource is the unexported impl of ParameterResource.
type parameterResource struct {
	*resourceBuilderBase
}

// newParameterResourceFromHandle wraps an existing handle as ParameterResource.
func newParameterResourceFromHandle(h *handle, c *client) ParameterResource {
	return &parameterResource{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// CreateExecutionConfiguration creates an execution configuration builder
func (s *parameterResource) CreateExecutionConfiguration() ExecutionConfigurationBuilder {
	if s.err != nil { return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/createExecutionConfiguration returned unexpected type %T", result)
		return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &executionConfigurationBuilder{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *parameterResource) ExcludeFromManifest() ParameterResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/excludeFromManifest", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *parameterResource) ExcludeFromMcp() ParameterResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/excludeFromMcp", reqArgs); err != nil { s.setErr(err) }
	return s
}

// GetResourceName gets the resource name
func (s *parameterResource) GetResourceName() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *parameterResource) OnBeforeResourceStarted(callback func(arg BeforeResourceStartedEvent)) ParameterResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[BeforeResourceStartedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onBeforeResourceStarted", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *parameterResource) OnInitializeResource(callback func(arg InitializeResourceEvent)) ParameterResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[InitializeResourceEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onInitializeResource", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceReady subscribes to the ResourceReady event
func (s *parameterResource) OnResourceReady(callback func(arg ResourceReadyEvent)) ParameterResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceReadyEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceReady", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *parameterResource) OnResourceStopped(callback func(arg ResourceStoppedEvent)) ParameterResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceStoppedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceStopped", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithChildRelationship sets a child relationship
func (s *parameterResource) WithChildRelationship(child Resource) ParameterResource {
	if s.err != nil { return s }
	if child != nil { if err := child.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["child"] = serializeValue(child)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderChildRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCommand adds a resource command
func (s *parameterResource) WithCommand(name string, displayName string, executeCommand func(arg ExecuteCommandContext) *ExecuteCommandResult, options ...*WithCommandOptions) ParameterResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["displayName"] = serializeValue(displayName)
	if executeCommand != nil {
		cb := executeCommand
		shim := func(args ...any) any {
			return cb(callbackArg[ExecuteCommandContext](args, 0))
		}
		reqArgs["executeCommand"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithContainerRegistry configures a resource to use a container registry
func (s *parameterResource) WithContainerRegistry(registry Resource) ParameterResource {
	if s.err != nil { return s }
	if registry != nil { if err := registry.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["registry"] = serializeValue(registry)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withContainerRegistry", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDescription sets a parameter description
func (s *parameterResource) WithDescription(description string, options ...*WithDescriptionOptions) ParameterResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["description"] = serializeValue(description)
	if len(options) > 0 {
		merged := &WithDescriptionOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withDescription", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *parameterResource) WithDockerfileBaseImage(options ...*WithDockerfileBaseImageOptions) ParameterResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithDockerfileBaseImageOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withDockerfileBaseImage", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithExplicitStart prevents resource from starting automatically
func (s *parameterResource) WithExplicitStart() ParameterResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withExplicitStart", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHealthCheck adds a health check by key
func (s *parameterResource) WithHealthCheck(key string) ParameterResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["key"] = serializeValue(key)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHealthCheck", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithIconName sets the icon for the resource
func (s *parameterResource) WithIconName(iconName string, options ...*WithIconNameOptions) ParameterResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["iconName"] = serializeValue(iconName)
	if len(options) > 0 {
		merged := &WithIconNameOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withIconName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithParentRelationship sets the parent relationship
func (s *parameterResource) WithParentRelationship(parent Resource) ParameterResource {
	if s.err != nil { return s }
	if parent != nil { if err := parent.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["parent"] = serializeValue(parent)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderParentRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *parameterResource) WithPipelineConfiguration(callback func(obj PipelineConfigurationContext)) ParameterResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineConfigurationContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withPipelineConfiguration", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *parameterResource) WithPipelineStepFactory(stepName string, callback func(arg PipelineStepContext), options ...*WithPipelineStepFactoryOptions) ParameterResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["stepName"] = serializeValue(stepName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineStepContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithPipelineStepFactoryOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withPipelineStepFactory", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRelationship adds a relationship to another resource
func (s *parameterResource) WithRelationship(resourceBuilder Resource, type_ string) ParameterResource {
	if s.err != nil { return s }
	if resourceBuilder != nil { if err := resourceBuilder.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["resourceBuilder"] = serializeValue(resourceBuilder)
	reqArgs["type"] = serializeValue(type_)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRequiredCommand adds a required command dependency
func (s *parameterResource) WithRequiredCommand(command string, options ...*WithRequiredCommandOptions) ParameterResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["command"] = serializeValue(command)
	if len(options) > 0 {
		merged := &WithRequiredCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRequiredCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrl adds or modifies displayed URLs
// Allowed types for parameter url: string, *ReferenceExpression.
func (s *parameterResource) WithUrl(url any, options ...*WithUrlOptions) ParameterResource {
	if s.err != nil { return s }
	switch url.(type) {
	case string, *ReferenceExpression:
	default:
		err := fmt.Errorf("aspire: WithUrl: parameter %q must be one of [string, *ReferenceExpression], got %T", "url", url)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if url != nil { reqArgs["url"] = serializeValue(url) }
	if len(options) > 0 {
		merged := &WithUrlOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrl", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *parameterResource) WithUrlForEndpoint(endpointName string, callback func(obj *ResourceUrlAnnotation)) ParameterResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			arg0 := callbackArg[*ResourceUrlAnnotation](args, 0)
			cb(arg0)
			return map[string]any{
				"p0": serializeValue(arg0),
			}
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrlForEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrls customizes displayed URLs via callback
func (s *parameterResource) WithUrls(callback func(obj ResourceUrlsCallbackContext)) ParameterResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceUrlsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrls", reqArgs); err != nil { s.setErr(err) }
	return s
}

// PipelineConfigurationContext is the public interface for handle type PipelineConfigurationContext.
type PipelineConfigurationContext interface {
	handleReference
	GetSteps(tag string) ([]PipelineStep, error)
	Log() LogFacade
	Pipeline() PipelineEditor
	Err() error
}

// pipelineConfigurationContext is the unexported impl of PipelineConfigurationContext.
type pipelineConfigurationContext struct {
	*resourceBuilderBase
}

// newPipelineConfigurationContextFromHandle wraps an existing handle as PipelineConfigurationContext.
func newPipelineConfigurationContextFromHandle(h *handle, c *client) PipelineConfigurationContext {
	return &pipelineConfigurationContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// GetSteps gets pipeline steps with the specified tag
func (s *pipelineConfigurationContext) GetSteps(tag string) ([]PipelineStep, error) {
	if s.err != nil { var zero []PipelineStep; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["tag"] = serializeValue(tag)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/getSteps", reqArgs)
	if err != nil {
		var zero []PipelineStep
		return zero, err
	}
	return decodeAs[[]PipelineStep](result)
}

// Log gets the callback logger facade
func (s *pipelineConfigurationContext) Log() LogFacade {
	if s.err != nil { return &logFacade{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineConfigurationContext.log", reqArgs)
	if err != nil {
		return &logFacade{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.Pipelines/PipelineConfigurationContext.log returned unexpected type %T", result)
		return &logFacade{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &logFacade{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Pipeline gets the pipeline editor
func (s *pipelineConfigurationContext) Pipeline() PipelineEditor {
	if s.err != nil { return &pipelineEditor{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineConfigurationContext.pipeline", reqArgs)
	if err != nil {
		return &pipelineEditor{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.Pipelines/PipelineConfigurationContext.pipeline returned unexpected type %T", result)
		return &pipelineEditor{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &pipelineEditor{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// PipelineContext is the public interface for handle type PipelineContext.
type PipelineContext interface {
	handleReference
	CancellationToken() (*CancellationToken, error)
	ExecutionContext() DistributedApplicationExecutionContext
	Logger() Logger
	Model() DistributedApplicationModel
	Services() ServiceProvider
	SetCancellationToken(options ...*SetCancellationTokenOptions) PipelineContext
	Summary() PipelineSummary
	Err() error
}

// pipelineContext is the unexported impl of PipelineContext.
type pipelineContext struct {
	*resourceBuilderBase
}

// newPipelineContextFromHandle wraps an existing handle as PipelineContext.
func newPipelineContextFromHandle(h *handle, c *client) PipelineContext {
	return &pipelineContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// CancellationToken gets the CancellationToken property
func (s *pipelineContext) CancellationToken() (*CancellationToken, error) {
	if s.err != nil { var zero *CancellationToken; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineContext.cancellationToken", reqArgs)
	if err != nil {
		var zero *CancellationToken
		return zero, err
	}
	return decodeAs[*CancellationToken](result)
}

// ExecutionContext gets the ExecutionContext property
func (s *pipelineContext) ExecutionContext() DistributedApplicationExecutionContext {
	if s.err != nil { return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineContext.executionContext", reqArgs)
	if err != nil {
		return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.Pipelines/PipelineContext.executionContext returned unexpected type %T", result)
		return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationExecutionContext{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Logger gets the Logger property
func (s *pipelineContext) Logger() Logger {
	if s.err != nil { return &logger{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineContext.logger", reqArgs)
	if err != nil {
		return &logger{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.Pipelines/PipelineContext.logger returned unexpected type %T", result)
		return &logger{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &logger{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Model gets the Model property
func (s *pipelineContext) Model() DistributedApplicationModel {
	if s.err != nil { return &distributedApplicationModel{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineContext.model", reqArgs)
	if err != nil {
		return &distributedApplicationModel{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.Pipelines/PipelineContext.model returned unexpected type %T", result)
		return &distributedApplicationModel{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationModel{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Services gets the Services property
func (s *pipelineContext) Services() ServiceProvider {
	if s.err != nil { return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineContext.services", reqArgs)
	if err != nil {
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.Pipelines/PipelineContext.services returned unexpected type %T", result)
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &serviceProvider{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// SetCancellationToken sets the CancellationToken property
func (s *pipelineContext) SetCancellationToken(options ...*SetCancellationTokenOptions) PipelineContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &SetCancellationTokenOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.Value != nil {
			ctx = merged.Value.Context()
			if id := s.client.registerCancellation(merged.Value); id != "" {
				reqArgs["value"] = id
			}
		}
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineContext.setCancellationToken", reqArgs); err != nil { s.setErr(err) }
	return s
}

// Summary gets the Summary property
func (s *pipelineContext) Summary() PipelineSummary {
	if s.err != nil { return &pipelineSummary{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineContext.summary", reqArgs)
	if err != nil {
		return &pipelineSummary{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.Pipelines/PipelineContext.summary returned unexpected type %T", result)
		return &pipelineSummary{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &pipelineSummary{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// PipelineEditor is the public interface for handle type PipelineEditor.
type PipelineEditor interface {
	handleReference
	Steps() ([]PipelineStep, error)
	StepsByTag(tag string) ([]PipelineStep, error)
	Err() error
}

// pipelineEditor is the unexported impl of PipelineEditor.
type pipelineEditor struct {
	*resourceBuilderBase
}

// newPipelineEditorFromHandle wraps an existing handle as PipelineEditor.
func newPipelineEditorFromHandle(h *handle, c *client) PipelineEditor {
	return &pipelineEditor{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Steps gets all configured pipeline steps
func (s *pipelineEditor) Steps() ([]PipelineStep, error) {
	if s.err != nil { var zero []PipelineStep; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/steps", reqArgs)
	if err != nil {
		var zero []PipelineStep
		return zero, err
	}
	return decodeAs[[]PipelineStep](result)
}

// StepsByTag gets pipeline steps with the specified tag
func (s *pipelineEditor) StepsByTag(tag string) ([]PipelineStep, error) {
	if s.err != nil { var zero []PipelineStep; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["tag"] = serializeValue(tag)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/stepsByTag", reqArgs)
	if err != nil {
		var zero []PipelineStep
		return zero, err
	}
	return decodeAs[[]PipelineStep](result)
}

// PipelineStep is the public interface for handle type PipelineStep.
type PipelineStep interface {
	handleReference
	AddTag(tag string) error
	DependsOn(stepName string) error
	Description() (string, error)
	Name() (string, error)
	RequiredBy(stepName string) error
	Err() error
}

// pipelineStep is the unexported impl of PipelineStep.
type pipelineStep struct {
	*resourceBuilderBase
}

// newPipelineStepFromHandle wraps an existing handle as PipelineStep.
func newPipelineStepFromHandle(h *handle, c *client) PipelineStep {
	return &pipelineStep{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// AddTag adds a tag to the step
func (s *pipelineStep) AddTag(tag string) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["tag"] = serializeValue(tag)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/addTag", reqArgs)
	return err
}

// DependsOn adds a dependency on another step by name
func (s *pipelineStep) DependsOn(stepName string) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["stepName"] = serializeValue(stepName)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/dependsOn", reqArgs)
	return err
}

// Description gets the human-readable description of the step
func (s *pipelineStep) Description() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineStep.description", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// Name gets the unique name of the step
func (s *pipelineStep) Name() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineStep.name", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// RequiredBy specifies that another step requires this step by name
func (s *pipelineStep) RequiredBy(stepName string) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["stepName"] = serializeValue(stepName)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/requiredBy", reqArgs)
	return err
}

// PipelineStepContext is the public interface for handle type PipelineStepContext.
type PipelineStepContext interface {
	handleReference
	CancellationToken() (*CancellationToken, error)
	ExecutionContext() DistributedApplicationExecutionContext
	Logger() Logger
	Model() DistributedApplicationModel
	PipelineContext() PipelineContext
	ReportingStep() ReportingStep
	Services() ServiceProvider
	SetPipelineContext(value PipelineContext) PipelineStepContext
	SetReportingStep(value ReportingStep) PipelineStepContext
	Summary() PipelineSummary
	Err() error
}

// pipelineStepContext is the unexported impl of PipelineStepContext.
type pipelineStepContext struct {
	*resourceBuilderBase
}

// newPipelineStepContextFromHandle wraps an existing handle as PipelineStepContext.
func newPipelineStepContextFromHandle(h *handle, c *client) PipelineStepContext {
	return &pipelineStepContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// CancellationToken gets the CancellationToken property
func (s *pipelineStepContext) CancellationToken() (*CancellationToken, error) {
	if s.err != nil { var zero *CancellationToken; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineStepContext.cancellationToken", reqArgs)
	if err != nil {
		var zero *CancellationToken
		return zero, err
	}
	return decodeAs[*CancellationToken](result)
}

// ExecutionContext gets the ExecutionContext property
func (s *pipelineStepContext) ExecutionContext() DistributedApplicationExecutionContext {
	if s.err != nil { return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineStepContext.executionContext", reqArgs)
	if err != nil {
		return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.Pipelines/PipelineStepContext.executionContext returned unexpected type %T", result)
		return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationExecutionContext{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Logger gets the Logger property
func (s *pipelineStepContext) Logger() Logger {
	if s.err != nil { return &logger{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineStepContext.logger", reqArgs)
	if err != nil {
		return &logger{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.Pipelines/PipelineStepContext.logger returned unexpected type %T", result)
		return &logger{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &logger{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Model gets the Model property
func (s *pipelineStepContext) Model() DistributedApplicationModel {
	if s.err != nil { return &distributedApplicationModel{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineStepContext.model", reqArgs)
	if err != nil {
		return &distributedApplicationModel{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.Pipelines/PipelineStepContext.model returned unexpected type %T", result)
		return &distributedApplicationModel{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationModel{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// PipelineContext gets the PipelineContext property
func (s *pipelineStepContext) PipelineContext() PipelineContext {
	if s.err != nil { return &pipelineContext{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineStepContext.pipelineContext", reqArgs)
	if err != nil {
		return &pipelineContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.Pipelines/PipelineStepContext.pipelineContext returned unexpected type %T", result)
		return &pipelineContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &pipelineContext{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ReportingStep gets the ReportingStep property
func (s *pipelineStepContext) ReportingStep() ReportingStep {
	if s.err != nil { return &reportingStep{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineStepContext.reportingStep", reqArgs)
	if err != nil {
		return &reportingStep{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.Pipelines/PipelineStepContext.reportingStep returned unexpected type %T", result)
		return &reportingStep{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &reportingStep{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Services gets the Services property
func (s *pipelineStepContext) Services() ServiceProvider {
	if s.err != nil { return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineStepContext.services", reqArgs)
	if err != nil {
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.Pipelines/PipelineStepContext.services returned unexpected type %T", result)
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &serviceProvider{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// SetPipelineContext sets the PipelineContext property
func (s *pipelineStepContext) SetPipelineContext(value PipelineContext) PipelineStepContext {
	if s.err != nil { return s }
	if value != nil { if err := value.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineStepContext.setPipelineContext", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetReportingStep sets the ReportingStep property
func (s *pipelineStepContext) SetReportingStep(value ReportingStep) PipelineStepContext {
	if s.err != nil { return s }
	if value != nil { if err := value.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineStepContext.setReportingStep", reqArgs); err != nil { s.setErr(err) }
	return s
}

// Summary gets the Summary property
func (s *pipelineStepContext) Summary() PipelineSummary {
	if s.err != nil { return &pipelineSummary{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineStepContext.summary", reqArgs)
	if err != nil {
		return &pipelineSummary{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.Pipelines/PipelineStepContext.summary returned unexpected type %T", result)
		return &pipelineSummary{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &pipelineSummary{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// PipelineStepFactoryContext is the public interface for handle type PipelineStepFactoryContext.
type PipelineStepFactoryContext interface {
	handleReference
	PipelineContext() PipelineContext
	Resource() Resource
	SetPipelineContext(value PipelineContext) PipelineStepFactoryContext
	SetResource(value Resource) PipelineStepFactoryContext
	Err() error
}

// pipelineStepFactoryContext is the unexported impl of PipelineStepFactoryContext.
type pipelineStepFactoryContext struct {
	*resourceBuilderBase
}

// newPipelineStepFactoryContextFromHandle wraps an existing handle as PipelineStepFactoryContext.
func newPipelineStepFactoryContextFromHandle(h *handle, c *client) PipelineStepFactoryContext {
	return &pipelineStepFactoryContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// PipelineContext gets the PipelineContext property
func (s *pipelineStepFactoryContext) PipelineContext() PipelineContext {
	if s.err != nil { return &pipelineContext{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineStepFactoryContext.pipelineContext", reqArgs)
	if err != nil {
		return &pipelineContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.Pipelines/PipelineStepFactoryContext.pipelineContext returned unexpected type %T", result)
		return &pipelineContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &pipelineContext{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Resource gets the Resource property
func (s *pipelineStepFactoryContext) Resource() Resource {
	if s.err != nil { return nil }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineStepFactoryContext.resource", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(Resource)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Aspire.Hosting.Pipelines/PipelineStepFactoryContext.resource returned unexpected type %T", result))
		return nil
	}
	return typed
}

// SetPipelineContext sets the PipelineContext property
func (s *pipelineStepFactoryContext) SetPipelineContext(value PipelineContext) PipelineStepFactoryContext {
	if s.err != nil { return s }
	if value != nil { if err := value.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineStepFactoryContext.setPipelineContext", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetResource sets the Resource property
func (s *pipelineStepFactoryContext) SetResource(value Resource) PipelineStepFactoryContext {
	if s.err != nil { return s }
	if value != nil { if err := value.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineStepFactoryContext.setResource", reqArgs); err != nil { s.setErr(err) }
	return s
}

// PipelineSummary is the public interface for handle type PipelineSummary.
type PipelineSummary interface {
	handleReference
	Add(key string, value string) error
	AddMarkdown(key string, markdownString string) error
	Err() error
}

// pipelineSummary is the unexported impl of PipelineSummary.
type pipelineSummary struct {
	*resourceBuilderBase
}

// newPipelineSummaryFromHandle wraps an existing handle as PipelineSummary.
func newPipelineSummaryFromHandle(h *handle, c *client) PipelineSummary {
	return &pipelineSummary{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Add invokes the Add method
func (s *pipelineSummary) Add(key string, value string) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["key"] = serializeValue(key)
	reqArgs["value"] = serializeValue(value)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Pipelines/PipelineSummary.add", reqArgs)
	return err
}

// AddMarkdown adds a Markdown-formatted value to the pipeline summary
func (s *pipelineSummary) AddMarkdown(key string, markdownString string) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"summary": s.handle.ToJSON(),
	}
	reqArgs["key"] = serializeValue(key)
	reqArgs["markdownString"] = serializeValue(markdownString)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/addMarkdown", reqArgs)
	return err
}

// ProjectResource is the public interface for handle type ProjectResource.
type ProjectResource interface {
	handleReference
	AsHttp2Service() ProjectResource
	CreateExecutionConfiguration() ExecutionConfigurationBuilder
	DisableForwardedHeaders() ProjectResource
	ExcludeFromManifest() ProjectResource
	ExcludeFromMcp() ProjectResource
	GetEndpoint(name string) EndpointReference
	GetResourceName() (string, error)
	OnBeforeResourceStarted(callback func(arg BeforeResourceStartedEvent)) ProjectResource
	OnInitializeResource(callback func(arg InitializeResourceEvent)) ProjectResource
	OnResourceEndpointsAllocated(callback func(arg ResourceEndpointsAllocatedEvent)) ProjectResource
	OnResourceReady(callback func(arg ResourceReadyEvent)) ProjectResource
	OnResourceStopped(callback func(arg ResourceStoppedEvent)) ProjectResource
	PublishAsDockerFile(options ...*PublishAsDockerFileOptions) ProjectResource
	PublishWithContainerFiles(source ResourceWithContainerFiles, destinationPath string) ProjectResource
	WaitFor(dependency Resource, options ...*WaitForOptions) ProjectResource
	WaitForCompletion(dependency Resource, options ...*WaitForCompletionOptions) ProjectResource
	WaitForStart(dependency Resource, options ...*WaitForStartOptions) ProjectResource
	WithArgs(args []string) ProjectResource
	WithArgsCallback(callback func(obj CommandLineArgsCallbackContext)) ProjectResource
	WithCertificateTrustScope(scope CertificateTrustScope) ProjectResource
	WithChildRelationship(child Resource) ProjectResource
	WithCommand(name string, displayName string, executeCommand func(arg ExecuteCommandContext) *ExecuteCommandResult, options ...*WithCommandOptions) ProjectResource
	WithContainerRegistry(registry Resource) ProjectResource
	WithDeveloperCertificateTrust(trust bool) ProjectResource
	WithDockerfileBaseImage(options ...*WithDockerfileBaseImageOptions) ProjectResource
	WithEndpoint(options ...*WithEndpointOptions) ProjectResource
	WithEndpointCallback(endpointName string, callback func(obj EndpointUpdateContext), options ...*WithEndpointCallbackOptions) ProjectResource
	WithEnvironment(name string, value any) ProjectResource
	WithEnvironmentCallback(callback func(arg EnvironmentCallbackContext)) ProjectResource
	WithExplicitStart() ProjectResource
	WithExternalHttpEndpoints() ProjectResource
	WithHealthCheck(key string) ProjectResource
	WithHttpCommand(path string, displayName string, options ...*WithHttpCommandOptions) ProjectResource
	WithHttpEndpoint(options ...*WithHttpEndpointOptions) ProjectResource
	WithHttpEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpEndpointCallbackOptions) ProjectResource
	WithHttpHealthCheck(options ...*WithHttpHealthCheckOptions) ProjectResource
	WithHttpProbe(probeType ProbeType, options ...*WithHttpProbeOptions) ProjectResource
	WithHttpsDeveloperCertificate(options ...*WithHttpsDeveloperCertificateOptions) ProjectResource
	WithHttpsEndpoint(options ...*WithHttpsEndpointOptions) ProjectResource
	WithHttpsEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpsEndpointCallbackOptions) ProjectResource
	WithIconName(iconName string, options ...*WithIconNameOptions) ProjectResource
	WithImagePushOptions(callback func(arg ContainerImagePushOptionsCallbackContext)) ProjectResource
	WithMcpServer(options ...*WithMcpServerOptions) ProjectResource
	WithOtlpExporter(options ...*WithOtlpExporterOptions) ProjectResource
	WithParentRelationship(parent Resource) ProjectResource
	WithPipelineConfiguration(callback func(obj PipelineConfigurationContext)) ProjectResource
	WithPipelineStepFactory(stepName string, callback func(arg PipelineStepContext), options ...*WithPipelineStepFactoryOptions) ProjectResource
	WithReference(source any, options ...*WithReferenceOptions) ProjectResource
	WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) ProjectResource
	WithRelationship(resourceBuilder Resource, type_ string) ProjectResource
	WithRemoteImageName(remoteImageName string) ProjectResource
	WithRemoteImageTag(remoteImageTag string) ProjectResource
	WithReplicas(replicas float64) ProjectResource
	WithRequiredCommand(command string, options ...*WithRequiredCommandOptions) ProjectResource
	WithUrl(url any, options ...*WithUrlOptions) ProjectResource
	WithUrlForEndpoint(endpointName string, callback func(obj *ResourceUrlAnnotation)) ProjectResource
	WithUrls(callback func(obj ResourceUrlsCallbackContext)) ProjectResource
	WithoutHttpsCertificate() ProjectResource
	Err() error
}

// projectResource is the unexported impl of ProjectResource.
type projectResource struct {
	*resourceBuilderBase
}

// newProjectResourceFromHandle wraps an existing handle as ProjectResource.
func newProjectResourceFromHandle(h *handle, c *client) ProjectResource {
	return &projectResource{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// AsHttp2Service configures resource for HTTP/2
func (s *projectResource) AsHttp2Service() ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/asHttp2Service", reqArgs); err != nil { s.setErr(err) }
	return s
}

// CreateExecutionConfiguration creates an execution configuration builder
func (s *projectResource) CreateExecutionConfiguration() ExecutionConfigurationBuilder {
	if s.err != nil { return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/createExecutionConfiguration", reqArgs)
	if err != nil {
		return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/createExecutionConfiguration returned unexpected type %T", result)
		return &executionConfigurationBuilder{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &executionConfigurationBuilder{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// DisableForwardedHeaders disables forwarded headers for the project
func (s *projectResource) DisableForwardedHeaders() ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/disableForwardedHeaders", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ExcludeFromManifest excludes the resource from the deployment manifest
func (s *projectResource) ExcludeFromManifest() ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/excludeFromManifest", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ExcludeFromMcp excludes the resource from MCP server exposure
func (s *projectResource) ExcludeFromMcp() ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/excludeFromMcp", reqArgs); err != nil { s.setErr(err) }
	return s
}

// GetEndpoint gets an endpoint reference
func (s *projectResource) GetEndpoint(name string) EndpointReference {
	if s.err != nil { return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getEndpoint", reqArgs)
	if err != nil {
		return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/getEndpoint returned unexpected type %T", result)
		return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &endpointReference{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// GetResourceName gets the resource name
func (s *projectResource) GetResourceName() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getResourceName", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// OnBeforeResourceStarted subscribes to the BeforeResourceStarted event
func (s *projectResource) OnBeforeResourceStarted(callback func(arg BeforeResourceStartedEvent)) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[BeforeResourceStartedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onBeforeResourceStarted", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnInitializeResource subscribes to the InitializeResource event
func (s *projectResource) OnInitializeResource(callback func(arg InitializeResourceEvent)) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[InitializeResourceEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onInitializeResource", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceEndpointsAllocated subscribes to the ResourceEndpointsAllocated event
func (s *projectResource) OnResourceEndpointsAllocated(callback func(arg ResourceEndpointsAllocatedEvent)) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceEndpointsAllocatedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceEndpointsAllocated", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceReady subscribes to the ResourceReady event
func (s *projectResource) OnResourceReady(callback func(arg ResourceReadyEvent)) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceReadyEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceReady", reqArgs); err != nil { s.setErr(err) }
	return s
}

// OnResourceStopped subscribes to the ResourceStopped event
func (s *projectResource) OnResourceStopped(callback func(arg ResourceStoppedEvent)) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceStoppedEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onResourceStopped", reqArgs); err != nil { s.setErr(err) }
	return s
}

// PublishAsDockerFile publishes a project as a Docker file with optional container configuration
func (s *projectResource) PublishAsDockerFile(options ...*PublishAsDockerFileOptions) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &PublishAsDockerFileOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.Configure != nil {
			cb := merged.Configure
			shim := func(args ...any) any {
				cb(callbackArg[ContainerResource](args, 0))
				return nil
			}
			reqArgs["configure"] = s.client.registerCallback(shim)
		}
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/publishProjectAsDockerFileWithConfigure", reqArgs); err != nil { s.setErr(err) }
	return s
}

// PublishWithContainerFiles configures the resource to copy container files from the specified source during publishing
func (s *projectResource) PublishWithContainerFiles(source ResourceWithContainerFiles, destinationPath string) ProjectResource {
	if s.err != nil { return s }
	if source != nil { if err := source.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["source"] = serializeValue(source)
	reqArgs["destinationPath"] = serializeValue(destinationPath)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/publishWithContainerFilesFromResource", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WaitFor waits for another resource to be ready
func (s *projectResource) WaitFor(dependency Resource, options ...*WaitForOptions) ProjectResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if len(options) > 0 {
		merged := &WaitForOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitFor", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WaitForCompletion waits for resource completion
func (s *projectResource) WaitForCompletion(dependency Resource, options ...*WaitForCompletionOptions) ProjectResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if len(options) > 0 {
		merged := &WaitForCompletionOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitForResourceCompletion", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WaitForStart waits for another resource to start
func (s *projectResource) WaitForStart(dependency Resource, options ...*WaitForStartOptions) ProjectResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if len(options) > 0 {
		merged := &WaitForStartOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitForStart", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithArgs adds arguments
func (s *projectResource) WithArgs(args []string) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if args != nil { reqArgs["args"] = serializeValue(args) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withArgs", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithArgsCallback sets command-line arguments via callback
func (s *projectResource) WithArgsCallback(callback func(obj CommandLineArgsCallbackContext)) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[CommandLineArgsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withArgsCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCertificateTrustScope sets the certificate trust scope
func (s *projectResource) WithCertificateTrustScope(scope CertificateTrustScope) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["scope"] = serializeValue(scope)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withCertificateTrustScope", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithChildRelationship sets a child relationship
func (s *projectResource) WithChildRelationship(child Resource) ProjectResource {
	if s.err != nil { return s }
	if child != nil { if err := child.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["child"] = serializeValue(child)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderChildRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCommand adds a resource command
func (s *projectResource) WithCommand(name string, displayName string, executeCommand func(arg ExecuteCommandContext) *ExecuteCommandResult, options ...*WithCommandOptions) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["displayName"] = serializeValue(displayName)
	if executeCommand != nil {
		cb := executeCommand
		shim := func(args ...any) any {
			return cb(callbackArg[ExecuteCommandContext](args, 0))
		}
		reqArgs["executeCommand"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithContainerRegistry configures a resource to use a container registry
func (s *projectResource) WithContainerRegistry(registry Resource) ProjectResource {
	if s.err != nil { return s }
	if registry != nil { if err := registry.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["registry"] = serializeValue(registry)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withContainerRegistry", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDeveloperCertificateTrust configures developer certificate trust
func (s *projectResource) WithDeveloperCertificateTrust(trust bool) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["trust"] = serializeValue(trust)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withDeveloperCertificateTrust", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDockerfileBaseImage sets the base image for a Dockerfile build
func (s *projectResource) WithDockerfileBaseImage(options ...*WithDockerfileBaseImageOptions) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithDockerfileBaseImageOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withDockerfileBaseImage", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEndpoint adds a network endpoint
func (s *projectResource) WithEndpoint(options ...*WithEndpointOptions) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithEndpointOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEndpointCallback updates a named endpoint via callback
func (s *projectResource) WithEndpointCallback(endpointName string, callback func(obj EndpointUpdateContext), options ...*WithEndpointCallbackOptions) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EndpointUpdateContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithEndpointCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEndpointCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEnvironment sets an environment variable
// Allowed types for parameter value: string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue.
func (s *projectResource) WithEnvironment(name string, value any) ProjectResource {
	if s.err != nil { return s }
	switch value.(type) {
	case string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue:
	default:
		err := fmt.Errorf("aspire: WithEnvironment: parameter %q must be one of [string, *ReferenceExpression, EndpointReference, ParameterResource, ResourceWithConnectionString, ExpressionValue], got %T", "value", value)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if value != nil { reqArgs["value"] = serializeValue(value) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEnvironment", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEnvironmentCallback sets environment variables via callback
func (s *projectResource) WithEnvironmentCallback(callback func(arg EnvironmentCallbackContext)) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EnvironmentCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withEnvironmentCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithExplicitStart prevents resource from starting automatically
func (s *projectResource) WithExplicitStart() ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withExplicitStart", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithExternalHttpEndpoints makes HTTP endpoints externally accessible
func (s *projectResource) WithExternalHttpEndpoints() ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withExternalHttpEndpoints", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHealthCheck adds a health check by key
func (s *projectResource) WithHealthCheck(key string) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["key"] = serializeValue(key)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHealthCheck", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpCommand adds an HTTP resource command
func (s *projectResource) WithHttpCommand(path string, displayName string, options ...*WithHttpCommandOptions) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["path"] = serializeValue(path)
	reqArgs["displayName"] = serializeValue(displayName)
	if len(options) > 0 {
		merged := &WithHttpCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpEndpoint adds an HTTP endpoint
func (s *projectResource) WithHttpEndpoint(options ...*WithHttpEndpointOptions) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpEndpointOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpEndpointCallback updates an HTTP endpoint via callback
func (s *projectResource) WithHttpEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpEndpointCallbackOptions) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EndpointUpdateContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithHttpEndpointCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpEndpointCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpHealthCheck adds an HTTP health check
func (s *projectResource) WithHttpHealthCheck(options ...*WithHttpHealthCheckOptions) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpHealthCheckOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpHealthCheck", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpProbe adds an HTTP health probe to the resource
func (s *projectResource) WithHttpProbe(probeType ProbeType, options ...*WithHttpProbeOptions) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["probeType"] = serializeValue(probeType)
	if len(options) > 0 {
		merged := &WithHttpProbeOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpProbe", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpsDeveloperCertificate configures HTTPS with a developer certificate
func (s *projectResource) WithHttpsDeveloperCertificate(options ...*WithHttpsDeveloperCertificateOptions) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpsDeveloperCertificateOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withParameterHttpsDeveloperCertificate", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpsEndpoint adds an HTTPS endpoint
func (s *projectResource) WithHttpsEndpoint(options ...*WithHttpsEndpointOptions) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithHttpsEndpointOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpsEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHttpsEndpointCallback updates an HTTPS endpoint via callback
func (s *projectResource) WithHttpsEndpointCallback(callback func(obj EndpointUpdateContext), options ...*WithHttpsEndpointCallbackOptions) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[EndpointUpdateContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithHttpsEndpointCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withHttpsEndpointCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithIconName sets the icon for the resource
func (s *projectResource) WithIconName(iconName string, options ...*WithIconNameOptions) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["iconName"] = serializeValue(iconName)
	if len(options) > 0 {
		merged := &WithIconNameOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withIconName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithImagePushOptions sets image push options via callback
func (s *projectResource) WithImagePushOptions(callback func(arg ContainerImagePushOptionsCallbackContext)) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ContainerImagePushOptionsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withImagePushOptions", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMcpServer configures an MCP server endpoint on the resource
func (s *projectResource) WithMcpServer(options ...*WithMcpServerOptions) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithMcpServerOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withMcpServer", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithOtlpExporter configures OTLP telemetry export
func (s *projectResource) WithOtlpExporter(options ...*WithOtlpExporterOptions) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithOtlpExporterOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withOtlpExporter", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithParentRelationship sets the parent relationship
func (s *projectResource) WithParentRelationship(parent Resource) ProjectResource {
	if s.err != nil { return s }
	if parent != nil { if err := parent.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["parent"] = serializeValue(parent)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderParentRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithPipelineConfiguration configures pipeline step dependencies via a callback
func (s *projectResource) WithPipelineConfiguration(callback func(obj PipelineConfigurationContext)) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineConfigurationContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withPipelineConfiguration", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithPipelineStepFactory adds a pipeline step to the resource
func (s *projectResource) WithPipelineStepFactory(stepName string, callback func(arg PipelineStepContext), options ...*WithPipelineStepFactoryOptions) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["stepName"] = serializeValue(stepName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[PipelineStepContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if len(options) > 0 {
		merged := &WithPipelineStepFactoryOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withPipelineStepFactory", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithReference adds a reference to another resource
// Allowed types for parameter source: Resource, EndpointReference, string.
func (s *projectResource) WithReference(source any, options ...*WithReferenceOptions) ProjectResource {
	if s.err != nil { return s }
	switch source.(type) {
	case Resource, EndpointReference, string:
	default:
		err := fmt.Errorf("aspire: WithReference: parameter %q must be one of [Resource, EndpointReference, string], got %T", "source", source)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if source != nil { reqArgs["source"] = serializeValue(source) }
	if len(options) > 0 {
		merged := &WithReferenceOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withReference", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithReferenceEnvironment configures which reference values are injected into environment variables
func (s *projectResource) WithReferenceEnvironment(options *ReferenceEnvironmentInjectionOptions) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if options != nil { reqArgs["options"] = serializeValue(options) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withReferenceEnvironment", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRelationship adds a relationship to another resource
func (s *projectResource) WithRelationship(resourceBuilder Resource, type_ string) ProjectResource {
	if s.err != nil { return s }
	if resourceBuilder != nil { if err := resourceBuilder.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["resourceBuilder"] = serializeValue(resourceBuilder)
	reqArgs["type"] = serializeValue(type_)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withBuilderRelationship", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRemoteImageName sets the remote image name for publishing
func (s *projectResource) WithRemoteImageName(remoteImageName string) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["remoteImageName"] = serializeValue(remoteImageName)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRemoteImageName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRemoteImageTag sets the remote image tag for publishing
func (s *projectResource) WithRemoteImageTag(remoteImageTag string) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["remoteImageTag"] = serializeValue(remoteImageTag)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRemoteImageTag", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithReplicas sets the number of replicas
func (s *projectResource) WithReplicas(replicas float64) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["replicas"] = serializeValue(replicas)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withReplicas", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRequiredCommand adds a required command dependency
func (s *projectResource) WithRequiredCommand(command string, options ...*WithRequiredCommandOptions) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["command"] = serializeValue(command)
	if len(options) > 0 {
		merged := &WithRequiredCommandOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withRequiredCommand", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrl adds or modifies displayed URLs
// Allowed types for parameter url: string, *ReferenceExpression.
func (s *projectResource) WithUrl(url any, options ...*WithUrlOptions) ProjectResource {
	if s.err != nil { return s }
	switch url.(type) {
	case string, *ReferenceExpression:
	default:
		err := fmt.Errorf("aspire: WithUrl: parameter %q must be one of [string, *ReferenceExpression], got %T", "url", url)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if url != nil { reqArgs["url"] = serializeValue(url) }
	if len(options) > 0 {
		merged := &WithUrlOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrl", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrlForEndpoint customizes the URL for a specific endpoint via callback
func (s *projectResource) WithUrlForEndpoint(endpointName string, callback func(obj *ResourceUrlAnnotation)) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			arg0 := callbackArg[*ResourceUrlAnnotation](args, 0)
			cb(arg0)
			return map[string]any{
				"p0": serializeValue(arg0),
			}
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrlForEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUrls customizes displayed URLs via callback
func (s *projectResource) WithUrls(callback func(obj ResourceUrlsCallbackContext)) ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ResourceUrlsCallbackContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withUrls", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithoutHttpsCertificate removes HTTPS certificate configuration
func (s *projectResource) WithoutHttpsCertificate() ProjectResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withoutHttpsCertificate", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ProjectResourceOptions is the public interface for handle type ProjectResourceOptions.
type ProjectResourceOptions interface {
	handleReference
	ExcludeKestrelEndpoints() (bool, error)
	ExcludeLaunchProfile() (bool, error)
	LaunchProfileName() (string, error)
	SetExcludeKestrelEndpoints(value bool) ProjectResourceOptions
	SetExcludeLaunchProfile(value bool) ProjectResourceOptions
	SetLaunchProfileName(value string) ProjectResourceOptions
	Err() error
}

// projectResourceOptions is the unexported impl of ProjectResourceOptions.
type projectResourceOptions struct {
	*resourceBuilderBase
}

// newProjectResourceOptionsFromHandle wraps an existing handle as ProjectResourceOptions.
func newProjectResourceOptionsFromHandle(h *handle, c *client) ProjectResourceOptions {
	return &projectResourceOptions{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// ExcludeKestrelEndpoints gets the ExcludeKestrelEndpoints property
func (s *projectResourceOptions) ExcludeKestrelEndpoints() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/ProjectResourceOptions.excludeKestrelEndpoints", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// ExcludeLaunchProfile gets the ExcludeLaunchProfile property
func (s *projectResourceOptions) ExcludeLaunchProfile() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/ProjectResourceOptions.excludeLaunchProfile", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// LaunchProfileName gets the LaunchProfileName property
func (s *projectResourceOptions) LaunchProfileName() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/ProjectResourceOptions.launchProfileName", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// SetExcludeKestrelEndpoints sets the ExcludeKestrelEndpoints property
func (s *projectResourceOptions) SetExcludeKestrelEndpoints(value bool) ProjectResourceOptions {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/ProjectResourceOptions.setExcludeKestrelEndpoints", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetExcludeLaunchProfile sets the ExcludeLaunchProfile property
func (s *projectResourceOptions) SetExcludeLaunchProfile(value bool) ProjectResourceOptions {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/ProjectResourceOptions.setExcludeLaunchProfile", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetLaunchProfileName sets the LaunchProfileName property
func (s *projectResourceOptions) SetLaunchProfileName(value string) ProjectResourceOptions {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/ProjectResourceOptions.setLaunchProfileName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ReferenceExpressionBuilder is the public interface for handle type ReferenceExpressionBuilder.
type ReferenceExpressionBuilder interface {
	handleReference
	AppendFormatted(value string, options ...*AppendFormattedOptions) error
	AppendLiteral(value string) error
	AppendValueProvider(valueProvider any, options ...*AppendValueProviderOptions) error
	Build() *ReferenceExpression
	IsEmpty() (bool, error)
	Err() error
}

// referenceExpressionBuilder is the unexported impl of ReferenceExpressionBuilder.
type referenceExpressionBuilder struct {
	*resourceBuilderBase
}

// newReferenceExpressionBuilderFromHandle wraps an existing handle as ReferenceExpressionBuilder.
func newReferenceExpressionBuilderFromHandle(h *handle, c *client) ReferenceExpressionBuilder {
	return &referenceExpressionBuilder{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// AppendFormatted appends a formatted string value to the reference expression
func (s *referenceExpressionBuilder) AppendFormatted(value string, options ...*AppendFormattedOptions) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if len(options) > 0 {
		merged := &AppendFormattedOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/appendFormatted", reqArgs)
	return err
}

// AppendLiteral appends a literal string to the reference expression
func (s *referenceExpressionBuilder) AppendLiteral(value string) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/appendLiteral", reqArgs)
	return err
}

// AppendValueProvider appends a value provider to the reference expression
func (s *referenceExpressionBuilder) AppendValueProvider(valueProvider any, options ...*AppendValueProviderOptions) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	if valueProvider != nil { reqArgs["valueProvider"] = serializeValue(valueProvider) }
	if len(options) > 0 {
		merged := &AppendValueProviderOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/appendValueProvider", reqArgs)
	return err
}

// Build builds the reference expression
func (s *referenceExpressionBuilder) Build() *ReferenceExpression {
	if s.err != nil { return nil }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/build", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(*ReferenceExpression)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/build returned unexpected type %T", result))
		return nil
	}
	return typed
}

// IsEmpty gets the IsEmpty property
func (s *referenceExpressionBuilder) IsEmpty() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ReferenceExpressionBuilder.isEmpty", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// ReportingStep is the public interface for handle type ReportingStep.
type ReportingStep interface {
	handleReference
	CompleteStep(completionText string, options ...*CompleteStepOptions) error
	CompleteStepMarkdown(markdownString string, options ...*CompleteStepMarkdownOptions) error
	CreateMarkdownTask(markdownString string, options ...*CreateMarkdownTaskOptions) ReportingTask
	CreateTask(statusText string, options ...*CreateTaskOptions) ReportingTask
	LogStep(level string, message string) error
	LogStepMarkdown(level string, markdownString string) error
	Err() error
}

// reportingStep is the unexported impl of ReportingStep.
type reportingStep struct {
	*resourceBuilderBase
}

// newReportingStepFromHandle wraps an existing handle as ReportingStep.
func newReportingStepFromHandle(h *handle, c *client) ReportingStep {
	return &reportingStep{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// CompleteStep completes the reporting step with plain-text completion text
func (s *reportingStep) CompleteStep(completionText string, options ...*CompleteStepOptions) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"reportingStep": s.handle.ToJSON(),
	}
	reqArgs["completionText"] = serializeValue(completionText)
	if len(options) > 0 {
		merged := &CompleteStepOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.CancellationToken != nil {
			ctx = merged.CancellationToken.Context()
			if id := s.client.registerCancellation(merged.CancellationToken); id != "" {
				reqArgs["cancellationToken"] = id
			}
		}
	}
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/completeStep", reqArgs)
	return err
}

// CompleteStepMarkdown completes the reporting step with Markdown-formatted completion text
func (s *reportingStep) CompleteStepMarkdown(markdownString string, options ...*CompleteStepMarkdownOptions) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"reportingStep": s.handle.ToJSON(),
	}
	reqArgs["markdownString"] = serializeValue(markdownString)
	if len(options) > 0 {
		merged := &CompleteStepMarkdownOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.CancellationToken != nil {
			ctx = merged.CancellationToken.Context()
			if id := s.client.registerCancellation(merged.CancellationToken); id != "" {
				reqArgs["cancellationToken"] = id
			}
		}
	}
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/completeStepMarkdown", reqArgs)
	return err
}

// CreateMarkdownTask creates a reporting task with Markdown-formatted status text
func (s *reportingStep) CreateMarkdownTask(markdownString string, options ...*CreateMarkdownTaskOptions) ReportingTask {
	if s.err != nil { return &reportingTask{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"reportingStep": s.handle.ToJSON(),
	}
	reqArgs["markdownString"] = serializeValue(markdownString)
	if len(options) > 0 {
		merged := &CreateMarkdownTaskOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.CancellationToken != nil {
			ctx = merged.CancellationToken.Context()
			if id := s.client.registerCancellation(merged.CancellationToken); id != "" {
				reqArgs["cancellationToken"] = id
			}
		}
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/createMarkdownTask", reqArgs)
	if err != nil {
		return &reportingTask{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/createMarkdownTask returned unexpected type %T", result)
		return &reportingTask{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &reportingTask{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// CreateTask creates a reporting task with plain-text status text
func (s *reportingStep) CreateTask(statusText string, options ...*CreateTaskOptions) ReportingTask {
	if s.err != nil { return &reportingTask{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"reportingStep": s.handle.ToJSON(),
	}
	reqArgs["statusText"] = serializeValue(statusText)
	if len(options) > 0 {
		merged := &CreateTaskOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.CancellationToken != nil {
			ctx = merged.CancellationToken.Context()
			if id := s.client.registerCancellation(merged.CancellationToken); id != "" {
				reqArgs["cancellationToken"] = id
			}
		}
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/createTask", reqArgs)
	if err != nil {
		return &reportingTask{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/createTask returned unexpected type %T", result)
		return &reportingTask{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &reportingTask{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// LogStep logs a plain-text message for the reporting step
func (s *reportingStep) LogStep(level string, message string) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"reportingStep": s.handle.ToJSON(),
	}
	reqArgs["level"] = serializeValue(level)
	reqArgs["message"] = serializeValue(message)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/logStep", reqArgs)
	return err
}

// LogStepMarkdown logs a Markdown-formatted message for the reporting step
func (s *reportingStep) LogStepMarkdown(level string, markdownString string) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"reportingStep": s.handle.ToJSON(),
	}
	reqArgs["level"] = serializeValue(level)
	reqArgs["markdownString"] = serializeValue(markdownString)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/logStepMarkdown", reqArgs)
	return err
}

// ReportingTask is the public interface for handle type ReportingTask.
type ReportingTask interface {
	handleReference
	CompleteTask(options ...*CompleteTaskOptions) error
	CompleteTaskMarkdown(markdownString string, options ...*CompleteTaskMarkdownOptions) error
	UpdateTask(statusText string, options ...*UpdateTaskOptions) error
	UpdateTaskMarkdown(markdownString string, options ...*UpdateTaskMarkdownOptions) error
	Err() error
}

// reportingTask is the unexported impl of ReportingTask.
type reportingTask struct {
	*resourceBuilderBase
}

// newReportingTaskFromHandle wraps an existing handle as ReportingTask.
func newReportingTaskFromHandle(h *handle, c *client) ReportingTask {
	return &reportingTask{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// CompleteTask completes the reporting task with plain-text completion text
func (s *reportingTask) CompleteTask(options ...*CompleteTaskOptions) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"reportingTask": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &CompleteTaskOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.CancellationToken != nil {
			ctx = merged.CancellationToken.Context()
			if id := s.client.registerCancellation(merged.CancellationToken); id != "" {
				reqArgs["cancellationToken"] = id
			}
		}
	}
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/completeTask", reqArgs)
	return err
}

// CompleteTaskMarkdown completes the reporting task with Markdown-formatted completion text
func (s *reportingTask) CompleteTaskMarkdown(markdownString string, options ...*CompleteTaskMarkdownOptions) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"reportingTask": s.handle.ToJSON(),
	}
	reqArgs["markdownString"] = serializeValue(markdownString)
	if len(options) > 0 {
		merged := &CompleteTaskMarkdownOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.CancellationToken != nil {
			ctx = merged.CancellationToken.Context()
			if id := s.client.registerCancellation(merged.CancellationToken); id != "" {
				reqArgs["cancellationToken"] = id
			}
		}
	}
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/completeTaskMarkdown", reqArgs)
	return err
}

// UpdateTask updates the reporting task with plain-text status text
func (s *reportingTask) UpdateTask(statusText string, options ...*UpdateTaskOptions) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"reportingTask": s.handle.ToJSON(),
	}
	reqArgs["statusText"] = serializeValue(statusText)
	if len(options) > 0 {
		merged := &UpdateTaskOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.CancellationToken != nil {
			ctx = merged.CancellationToken.Context()
			if id := s.client.registerCancellation(merged.CancellationToken); id != "" {
				reqArgs["cancellationToken"] = id
			}
		}
	}
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/updateTask", reqArgs)
	return err
}

// UpdateTaskMarkdown updates the reporting task with Markdown-formatted status text
func (s *reportingTask) UpdateTaskMarkdown(markdownString string, options ...*UpdateTaskMarkdownOptions) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"reportingTask": s.handle.ToJSON(),
	}
	reqArgs["markdownString"] = serializeValue(markdownString)
	if len(options) > 0 {
		merged := &UpdateTaskMarkdownOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.CancellationToken != nil {
			ctx = merged.CancellationToken.Context()
			if id := s.client.registerCancellation(merged.CancellationToken); id != "" {
				reqArgs["cancellationToken"] = id
			}
		}
	}
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/updateTaskMarkdown", reqArgs)
	return err
}

// ResourceEndpointsAllocatedEvent is the public interface for handle type ResourceEndpointsAllocatedEvent.
type ResourceEndpointsAllocatedEvent interface {
	handleReference
	Resource() Resource
	Services() ServiceProvider
	Err() error
}

// resourceEndpointsAllocatedEvent is the unexported impl of ResourceEndpointsAllocatedEvent.
type resourceEndpointsAllocatedEvent struct {
	*resourceBuilderBase
}

// newResourceEndpointsAllocatedEventFromHandle wraps an existing handle as ResourceEndpointsAllocatedEvent.
func newResourceEndpointsAllocatedEventFromHandle(h *handle, c *client) ResourceEndpointsAllocatedEvent {
	return &resourceEndpointsAllocatedEvent{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Resource gets the Resource property
func (s *resourceEndpointsAllocatedEvent) Resource() Resource {
	if s.err != nil { return nil }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ResourceEndpointsAllocatedEvent.resource", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(Resource)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/ResourceEndpointsAllocatedEvent.resource returned unexpected type %T", result))
		return nil
	}
	return typed
}

// Services gets the Services property
func (s *resourceEndpointsAllocatedEvent) Services() ServiceProvider {
	if s.err != nil { return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ResourceEndpointsAllocatedEvent.services", reqArgs)
	if err != nil {
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/ResourceEndpointsAllocatedEvent.services returned unexpected type %T", result)
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &serviceProvider{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ResourceLoggerService is the public interface for handle type ResourceLoggerService.
type ResourceLoggerService interface {
	handleReference
	CompleteLog(resource Resource) error
	CompleteLogByName(resourceName string) error
	Err() error
}

// resourceLoggerService is the unexported impl of ResourceLoggerService.
type resourceLoggerService struct {
	*resourceBuilderBase
}

// newResourceLoggerServiceFromHandle wraps an existing handle as ResourceLoggerService.
func newResourceLoggerServiceFromHandle(h *handle, c *client) ResourceLoggerService {
	return &resourceLoggerService{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// CompleteLog completes the log stream for a resource
func (s *resourceLoggerService) CompleteLog(resource Resource) error {
	if s.err != nil { return s.err }
	if resource != nil { if err := resource.Err(); err != nil { return err } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"loggerService": s.handle.ToJSON(),
	}
	reqArgs["resource"] = serializeValue(resource)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/completeLog", reqArgs)
	return err
}

// CompleteLogByName completes the log stream by resource name
func (s *resourceLoggerService) CompleteLogByName(resourceName string) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"loggerService": s.handle.ToJSON(),
	}
	reqArgs["resourceName"] = serializeValue(resourceName)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/completeLogByName", reqArgs)
	return err
}

// ResourceNotificationService is the public interface for handle type ResourceNotificationService.
type ResourceNotificationService interface {
	handleReference
	PublishResourceUpdate(resource Resource, options ...*PublishResourceUpdateOptions) error
	TryGetResourceState(resourceName string) (*ResourceEventDto, error)
	WaitForDependencies(resource Resource) error
	WaitForResourceHealthy(resourceName string) (*ResourceEventDto, error)
	WaitForResourceState(resourceName string, options ...*WaitForResourceStateOptions) error
	WaitForResourceStates(resourceName string, targetStates []string) (string, error)
	Err() error
}

// resourceNotificationService is the unexported impl of ResourceNotificationService.
type resourceNotificationService struct {
	*resourceBuilderBase
}

// newResourceNotificationServiceFromHandle wraps an existing handle as ResourceNotificationService.
func newResourceNotificationServiceFromHandle(h *handle, c *client) ResourceNotificationService {
	return &resourceNotificationService{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// PublishResourceUpdate publishes an update for a resource's state
func (s *resourceNotificationService) PublishResourceUpdate(resource Resource, options ...*PublishResourceUpdateOptions) error {
	if s.err != nil { return s.err }
	if resource != nil { if err := resource.Err(); err != nil { return err } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"notificationService": s.handle.ToJSON(),
	}
	reqArgs["resource"] = serializeValue(resource)
	if len(options) > 0 {
		merged := &PublishResourceUpdateOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/publishResourceUpdate", reqArgs)
	return err
}

// TryGetResourceState tries to get the current state of a resource
func (s *resourceNotificationService) TryGetResourceState(resourceName string) (*ResourceEventDto, error) {
	if s.err != nil { var zero *ResourceEventDto; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"notificationService": s.handle.ToJSON(),
	}
	reqArgs["resourceName"] = serializeValue(resourceName)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/tryGetResourceState", reqArgs)
	if err != nil {
		var zero *ResourceEventDto
		return zero, err
	}
	return decodeAs[*ResourceEventDto](result)
}

// WaitForDependencies waits for all dependencies of a resource to be ready
func (s *resourceNotificationService) WaitForDependencies(resource Resource) error {
	if s.err != nil { return s.err }
	if resource != nil { if err := resource.Err(); err != nil { return err } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"notificationService": s.handle.ToJSON(),
	}
	reqArgs["resource"] = serializeValue(resource)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitForDependencies", reqArgs)
	return err
}

// WaitForResourceHealthy waits for a resource to become healthy
func (s *resourceNotificationService) WaitForResourceHealthy(resourceName string) (*ResourceEventDto, error) {
	if s.err != nil { var zero *ResourceEventDto; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"notificationService": s.handle.ToJSON(),
	}
	reqArgs["resourceName"] = serializeValue(resourceName)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitForResourceHealthy", reqArgs)
	if err != nil {
		var zero *ResourceEventDto
		return zero, err
	}
	return decodeAs[*ResourceEventDto](result)
}

// WaitForResourceState waits for a resource to reach a specified state
func (s *resourceNotificationService) WaitForResourceState(resourceName string, options ...*WaitForResourceStateOptions) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"notificationService": s.handle.ToJSON(),
	}
	reqArgs["resourceName"] = serializeValue(resourceName)
	if len(options) > 0 {
		merged := &WaitForResourceStateOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitForResourceState", reqArgs)
	return err
}

// WaitForResourceStates waits for a resource to reach one of the specified states
func (s *resourceNotificationService) WaitForResourceStates(resourceName string, targetStates []string) (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"notificationService": s.handle.ToJSON(),
	}
	reqArgs["resourceName"] = serializeValue(resourceName)
	if targetStates != nil { reqArgs["targetStates"] = serializeValue(targetStates) }
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/waitForResourceStates", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// ResourceReadyEvent is the public interface for handle type ResourceReadyEvent.
type ResourceReadyEvent interface {
	handleReference
	Resource() Resource
	Services() ServiceProvider
	Err() error
}

// resourceReadyEvent is the unexported impl of ResourceReadyEvent.
type resourceReadyEvent struct {
	*resourceBuilderBase
}

// newResourceReadyEventFromHandle wraps an existing handle as ResourceReadyEvent.
func newResourceReadyEventFromHandle(h *handle, c *client) ResourceReadyEvent {
	return &resourceReadyEvent{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Resource gets the Resource property
func (s *resourceReadyEvent) Resource() Resource {
	if s.err != nil { return nil }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ResourceReadyEvent.resource", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(Resource)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/ResourceReadyEvent.resource returned unexpected type %T", result))
		return nil
	}
	return typed
}

// Services gets the Services property
func (s *resourceReadyEvent) Services() ServiceProvider {
	if s.err != nil { return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ResourceReadyEvent.services", reqArgs)
	if err != nil {
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/ResourceReadyEvent.services returned unexpected type %T", result)
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &serviceProvider{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ResourceStoppedEvent is the public interface for handle type ResourceStoppedEvent.
type ResourceStoppedEvent interface {
	handleReference
	Resource() Resource
	Services() ServiceProvider
	Err() error
}

// resourceStoppedEvent is the unexported impl of ResourceStoppedEvent.
type resourceStoppedEvent struct {
	*resourceBuilderBase
}

// newResourceStoppedEventFromHandle wraps an existing handle as ResourceStoppedEvent.
func newResourceStoppedEventFromHandle(h *handle, c *client) ResourceStoppedEvent {
	return &resourceStoppedEvent{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Resource gets the Resource property
func (s *resourceStoppedEvent) Resource() Resource {
	if s.err != nil { return nil }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ResourceStoppedEvent.resource", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(Resource)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/ResourceStoppedEvent.resource returned unexpected type %T", result))
		return nil
	}
	return typed
}

// Services gets the Services property
func (s *resourceStoppedEvent) Services() ServiceProvider {
	if s.err != nil { return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ResourceStoppedEvent.services", reqArgs)
	if err != nil {
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/ResourceStoppedEvent.services returned unexpected type %T", result)
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &serviceProvider{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ResourceUrlsCallbackContext is the public interface for handle type ResourceUrlsCallbackContext.
type ResourceUrlsCallbackContext interface {
	handleReference
	ExecutionContext() DistributedApplicationExecutionContext
	GetEndpoint(name string) EndpointReference
	Log() LogFacade
	Resource() Resource
	Urls() ResourceUrlsEditor
	Err() error
}

// resourceUrlsCallbackContext is the unexported impl of ResourceUrlsCallbackContext.
type resourceUrlsCallbackContext struct {
	*resourceBuilderBase
}

// newResourceUrlsCallbackContextFromHandle wraps an existing handle as ResourceUrlsCallbackContext.
func newResourceUrlsCallbackContextFromHandle(h *handle, c *client) ResourceUrlsCallbackContext {
	return &resourceUrlsCallbackContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// ExecutionContext gets the execution context for this callback invocation
func (s *resourceUrlsCallbackContext) ExecutionContext() DistributedApplicationExecutionContext {
	if s.err != nil { return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.executionContext", reqArgs)
	if err != nil {
		return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.executionContext returned unexpected type %T", result)
		return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationExecutionContext{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// GetEndpoint gets an endpoint reference from the associated resource
func (s *resourceUrlsCallbackContext) GetEndpoint(name string) EndpointReference {
	if s.err != nil { return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/getEndpoint", reqArgs)
	if err != nil {
		return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/getEndpoint returned unexpected type %T", result)
		return &endpointReference{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &endpointReference{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Log gets the callback logger facade
func (s *resourceUrlsCallbackContext) Log() LogFacade {
	if s.err != nil { return &logFacade{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.log", reqArgs)
	if err != nil {
		return &logFacade{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.log returned unexpected type %T", result)
		return &logFacade{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &logFacade{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// Resource gets the resource associated with these URLs
func (s *resourceUrlsCallbackContext) Resource() Resource {
	if s.err != nil { return nil }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.resource", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(Resource)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.resource returned unexpected type %T", result))
		return nil
	}
	return typed
}

// Urls gets the URL editor
func (s *resourceUrlsCallbackContext) Urls() ResourceUrlsEditor {
	if s.err != nil { return &resourceUrlsEditor{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.urls", reqArgs)
	if err != nil {
		return &resourceUrlsEditor{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.urls returned unexpected type %T", result)
		return &resourceUrlsEditor{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &resourceUrlsEditor{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ResourceUrlsEditor is the public interface for handle type ResourceUrlsEditor.
type ResourceUrlsEditor interface {
	handleReference
	Add(url any, options ...*AddOptions) error
	AddForEndpoint(endpoint EndpointReference, url any, options ...*AddForEndpointOptions) error
	ExecutionContext() DistributedApplicationExecutionContext
	Err() error
}

// resourceUrlsEditor is the unexported impl of ResourceUrlsEditor.
type resourceUrlsEditor struct {
	*resourceBuilderBase
}

// newResourceUrlsEditorFromHandle wraps an existing handle as ResourceUrlsEditor.
func newResourceUrlsEditorFromHandle(h *handle, c *client) ResourceUrlsEditor {
	return &resourceUrlsEditor{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Add adds a displayed URL
// Allowed types for parameter url: string, *ReferenceExpression.
func (s *resourceUrlsEditor) Add(url any, options ...*AddOptions) error {
	if s.err != nil { return s.err }
	switch url.(type) {
	case string, *ReferenceExpression:
	default:
		err := fmt.Errorf("aspire: Add: parameter %q must be one of [string, *ReferenceExpression], got %T", "url", url)
		return err
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	if url != nil { reqArgs["url"] = serializeValue(url) }
	if len(options) > 0 {
		merged := &AddOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ResourceUrlsEditor.add", reqArgs)
	return err
}

// AddForEndpoint adds a displayed URL for a specific endpoint
// Allowed types for parameter url: string, *ReferenceExpression.
func (s *resourceUrlsEditor) AddForEndpoint(endpoint EndpointReference, url any, options ...*AddForEndpointOptions) error {
	if s.err != nil { return s.err }
	if endpoint != nil { if err := endpoint.Err(); err != nil { return err } }
	switch url.(type) {
	case string, *ReferenceExpression:
	default:
		err := fmt.Errorf("aspire: AddForEndpoint: parameter %q must be one of [string, *ReferenceExpression], got %T", "url", url)
		return err
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["endpoint"] = serializeValue(endpoint)
	if url != nil { reqArgs["url"] = serializeValue(url) }
	if len(options) > 0 {
		merged := &AddForEndpointOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ResourceUrlsEditor.addForEndpoint", reqArgs)
	return err
}

// ExecutionContext gets the execution context for this URL editor
func (s *resourceUrlsEditor) ExecutionContext() DistributedApplicationExecutionContext {
	if s.err != nil { return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/ResourceUrlsEditor.executionContext", reqArgs)
	if err != nil {
		return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/ResourceUrlsEditor.executionContext returned unexpected type %T", result)
		return &distributedApplicationExecutionContext{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationExecutionContext{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// ResourceWithConnectionString is the public interface for handle type ResourceWithConnectionString.
type ResourceWithConnectionString interface {
	handleReference
	GetConnectionProperty(key string) *ReferenceExpression
	OnConnectionStringAvailable(callback func(arg ConnectionStringAvailableEvent)) ResourceWithConnectionString
	WithConnectionProperty(name string, value any) ResourceWithConnectionString
	Err() error
}

// resourceWithConnectionString is the unexported impl of ResourceWithConnectionString.
type resourceWithConnectionString struct {
	*resourceBuilderBase
}

// newResourceWithConnectionStringFromHandle wraps an existing handle as ResourceWithConnectionString.
func newResourceWithConnectionStringFromHandle(h *handle, c *client) ResourceWithConnectionString {
	return &resourceWithConnectionString{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// GetConnectionProperty gets a connection property by key
func (s *resourceWithConnectionString) GetConnectionProperty(key string) *ReferenceExpression {
	if s.err != nil { return nil }
	ctx := context.Background()
	reqArgs := map[string]any{
		"resource": s.handle.ToJSON(),
	}
	reqArgs["key"] = serializeValue(key)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getConnectionProperty", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(*ReferenceExpression)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Aspire.Hosting/getConnectionProperty returned unexpected type %T", result))
		return nil
	}
	return typed
}

// OnConnectionStringAvailable subscribes to the ConnectionStringAvailable event
func (s *resourceWithConnectionString) OnConnectionStringAvailable(callback func(arg ConnectionStringAvailableEvent)) ResourceWithConnectionString {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[ConnectionStringAvailableEvent](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/onConnectionStringAvailable", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithConnectionProperty adds a connection property with a string or reference expression value
// Allowed types for parameter value: string, *ReferenceExpression.
func (s *resourceWithConnectionString) WithConnectionProperty(name string, value any) ResourceWithConnectionString {
	if s.err != nil { return s }
	switch value.(type) {
	case string, *ReferenceExpression:
	default:
		err := fmt.Errorf("aspire: WithConnectionProperty: parameter %q must be one of [string, *ReferenceExpression], got %T", "value", value)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if value != nil { reqArgs["value"] = serializeValue(value) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withConnectionProperty", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ResourceWithContainerFiles is the public interface for handle type ResourceWithContainerFiles.
type ResourceWithContainerFiles interface {
	handleReference
	ClearContainerFilesSources() ResourceWithContainerFiles
	WithContainerFilesSource(sourcePath string) ResourceWithContainerFiles
	Err() error
}

// resourceWithContainerFiles is the unexported impl of ResourceWithContainerFiles.
type resourceWithContainerFiles struct {
	*resourceBuilderBase
}

// newResourceWithContainerFilesFromHandle wraps an existing handle as ResourceWithContainerFiles.
func newResourceWithContainerFilesFromHandle(h *handle, c *client) ResourceWithContainerFiles {
	return &resourceWithContainerFiles{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// ClearContainerFilesSources clears all container file sources
func (s *resourceWithContainerFiles) ClearContainerFilesSources() ResourceWithContainerFiles {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/clearContainerFilesSources", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithContainerFilesSource sets the source directory for container files
func (s *resourceWithContainerFiles) WithContainerFilesSource(sourcePath string) ResourceWithContainerFiles {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["sourcePath"] = serializeValue(sourcePath)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting/withContainerFilesSource", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ServiceProvider is the public interface for handle type ServiceProvider.
type ServiceProvider interface {
	handleReference
	GetAspireStore() AspireStore
	GetDistributedApplicationModel() DistributedApplicationModel
	GetEventing() DistributedApplicationEventing
	GetLoggerFactory() LoggerFactory
	GetResourceLoggerService() ResourceLoggerService
	GetResourceNotificationService() ResourceNotificationService
	GetUserSecretsManager() UserSecretsManager
	Err() error
}

// serviceProvider is the unexported impl of ServiceProvider.
type serviceProvider struct {
	*resourceBuilderBase
}

// newServiceProviderFromHandle wraps an existing handle as ServiceProvider.
func newServiceProviderFromHandle(h *handle, c *client) ServiceProvider {
	return &serviceProvider{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// GetAspireStore gets the Aspire store from the service provider
func (s *serviceProvider) GetAspireStore() AspireStore {
	if s.err != nil { return &aspireStore{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"serviceProvider": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getAspireStore", reqArgs)
	if err != nil {
		return &aspireStore{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/getAspireStore returned unexpected type %T", result)
		return &aspireStore{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &aspireStore{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// GetDistributedApplicationModel gets the distributed application model from the service provider
func (s *serviceProvider) GetDistributedApplicationModel() DistributedApplicationModel {
	if s.err != nil { return &distributedApplicationModel{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"serviceProvider": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getDistributedApplicationModel", reqArgs)
	if err != nil {
		return &distributedApplicationModel{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/getDistributedApplicationModel returned unexpected type %T", result)
		return &distributedApplicationModel{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationModel{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// GetEventing gets the distributed application eventing service from the service provider
func (s *serviceProvider) GetEventing() DistributedApplicationEventing {
	if s.err != nil { return &distributedApplicationEventing{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"serviceProvider": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getEventing", reqArgs)
	if err != nil {
		return &distributedApplicationEventing{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/getEventing returned unexpected type %T", result)
		return &distributedApplicationEventing{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &distributedApplicationEventing{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// GetLoggerFactory gets the logger factory from the service provider
func (s *serviceProvider) GetLoggerFactory() LoggerFactory {
	if s.err != nil { return &loggerFactory{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"serviceProvider": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getLoggerFactory", reqArgs)
	if err != nil {
		return &loggerFactory{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/getLoggerFactory returned unexpected type %T", result)
		return &loggerFactory{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &loggerFactory{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// GetResourceLoggerService gets the resource logger service from the service provider
func (s *serviceProvider) GetResourceLoggerService() ResourceLoggerService {
	if s.err != nil { return &resourceLoggerService{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"serviceProvider": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getResourceLoggerService", reqArgs)
	if err != nil {
		return &resourceLoggerService{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/getResourceLoggerService returned unexpected type %T", result)
		return &resourceLoggerService{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &resourceLoggerService{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// GetResourceNotificationService gets the resource notification service from the service provider
func (s *serviceProvider) GetResourceNotificationService() ResourceNotificationService {
	if s.err != nil { return &resourceNotificationService{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"serviceProvider": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getResourceNotificationService", reqArgs)
	if err != nil {
		return &resourceNotificationService{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/getResourceNotificationService returned unexpected type %T", result)
		return &resourceNotificationService{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &resourceNotificationService{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// GetUserSecretsManager gets the user secrets manager from the service provider
func (s *serviceProvider) GetUserSecretsManager() UserSecretsManager {
	if s.err != nil { return &userSecretsManager{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"serviceProvider": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getUserSecretsManager", reqArgs)
	if err != nil {
		return &userSecretsManager{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting/getUserSecretsManager returned unexpected type %T", result)
		return &userSecretsManager{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &userSecretsManager{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// UpdateCommandStateContext is the public interface for handle type UpdateCommandStateContext.
type UpdateCommandStateContext interface {
	handleReference
	ServiceProvider() ServiceProvider
	SetServiceProvider(value ServiceProvider) UpdateCommandStateContext
	Err() error
}

// updateCommandStateContext is the unexported impl of UpdateCommandStateContext.
type updateCommandStateContext struct {
	*resourceBuilderBase
}

// newUpdateCommandStateContextFromHandle wraps an existing handle as UpdateCommandStateContext.
func newUpdateCommandStateContextFromHandle(h *handle, c *client) UpdateCommandStateContext {
	return &updateCommandStateContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// ServiceProvider gets the ServiceProvider property
func (s *updateCommandStateContext) ServiceProvider() ServiceProvider {
	if s.err != nil { return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/UpdateCommandStateContext.serviceProvider", reqArgs)
	if err != nil {
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.ApplicationModel/UpdateCommandStateContext.serviceProvider returned unexpected type %T", result)
		return &serviceProvider{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &serviceProvider{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// SetServiceProvider sets the ServiceProvider property
func (s *updateCommandStateContext) SetServiceProvider(value ServiceProvider) UpdateCommandStateContext {
	if s.err != nil { return s }
	if value != nil { if err := value.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/UpdateCommandStateContext.setServiceProvider", reqArgs); err != nil { s.setErr(err) }
	return s
}

// UserSecretsManager is the public interface for handle type UserSecretsManager.
type UserSecretsManager interface {
	handleReference
	FilePath() (string, error)
	GetOrSetSecret(resourceBuilder Resource, name string, value string) error
	IsAvailable() (bool, error)
	SaveStateJson(json string, options ...*SaveStateJsonOptions) error
	TryDeleteSecret(name string) (bool, error)
	TrySetSecret(name string, value string) (bool, error)
	Err() error
}

// userSecretsManager is the unexported impl of UserSecretsManager.
type userSecretsManager struct {
	*resourceBuilderBase
}

// newUserSecretsManagerFromHandle wraps an existing handle as UserSecretsManager.
func newUserSecretsManagerFromHandle(h *handle, c *client) UserSecretsManager {
	return &userSecretsManager{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// FilePath gets the FilePath property
func (s *userSecretsManager) FilePath() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/IUserSecretsManager.filePath", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// GetOrSetSecret gets a secret value if it exists, or sets it to the provided value if it does not
func (s *userSecretsManager) GetOrSetSecret(resourceBuilder Resource, name string, value string) error {
	if s.err != nil { return s.err }
	if resourceBuilder != nil { if err := resourceBuilder.Err(); err != nil { return err } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"userSecretsManager": s.handle.ToJSON(),
	}
	reqArgs["resourceBuilder"] = serializeValue(resourceBuilder)
	reqArgs["name"] = serializeValue(name)
	reqArgs["value"] = serializeValue(value)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/getOrSetSecret", reqArgs)
	return err
}

// IsAvailable gets the IsAvailable property
func (s *userSecretsManager) IsAvailable() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/IUserSecretsManager.isAvailable", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// SaveStateJson saves state to user secrets from a JSON string
func (s *userSecretsManager) SaveStateJson(json string, options ...*SaveStateJsonOptions) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"userSecretsManager": s.handle.ToJSON(),
	}
	reqArgs["json"] = serializeValue(json)
	if len(options) > 0 {
		merged := &SaveStateJsonOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.CancellationToken != nil {
			ctx = merged.CancellationToken.Context()
			if id := s.client.registerCancellation(merged.CancellationToken); id != "" {
				reqArgs["cancellationToken"] = id
			}
		}
	}
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting/saveStateJson", reqArgs)
	return err
}

// TryDeleteSecret attempts to delete a user secret value
func (s *userSecretsManager) TryDeleteSecret(name string) (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/IUserSecretsManager.tryDeleteSecret", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// TrySetSecret attempts to set a user secret value
func (s *userSecretsManager) TrySetSecret(name string, value string) (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	reqArgs["value"] = serializeValue(value)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting/IUserSecretsManager.trySetSecret", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// ============================================================================
// Options structs
// ============================================================================

// AddContainerRegistryOptions carries optional parameters for AddContainerRegistry.
type AddContainerRegistryOptions struct {
	Repository any `json:"repository,omitempty"`
}

func (o *AddContainerRegistryOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Repository != nil { m["repository"] = serializeValue(o.Repository) }
	return m
}

// WithBindMountOptions carries optional parameters for WithBindMount.
type WithBindMountOptions struct {
	IsReadOnly *bool `json:"isReadOnly,omitempty"`
}

func (o *WithBindMountOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.IsReadOnly != nil { m["isReadOnly"] = serializeValue(o.IsReadOnly) }
	return m
}

// WithImageOptions carries optional parameters for WithImage.
type WithImageOptions struct {
	Tag *string `json:"tag,omitempty"`
}

func (o *WithImageOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Tag != nil { m["tag"] = serializeValue(o.Tag) }
	return m
}

// WithDockerfileOptions carries optional parameters for WithDockerfile.
type WithDockerfileOptions struct {
	DockerfilePath *string `json:"dockerfilePath,omitempty"`
	Stage *string `json:"stage,omitempty"`
}

func (o *WithDockerfileOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.DockerfilePath != nil { m["dockerfilePath"] = serializeValue(o.DockerfilePath) }
	if o.Stage != nil { m["stage"] = serializeValue(o.Stage) }
	return m
}

// AddDockerfileOptions carries optional parameters for AddDockerfile.
type AddDockerfileOptions struct {
	DockerfilePath *string `json:"dockerfilePath,omitempty"`
	Stage *string `json:"stage,omitempty"`
}

func (o *AddDockerfileOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.DockerfilePath != nil { m["dockerfilePath"] = serializeValue(o.DockerfilePath) }
	if o.Stage != nil { m["stage"] = serializeValue(o.Stage) }
	return m
}

// AddDockerfileBuilderOptions carries optional parameters for AddDockerfileBuilder.
type AddDockerfileBuilderOptions struct {
	Stage *string `json:"stage,omitempty"`
}

func (o *AddDockerfileBuilderOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Stage != nil { m["stage"] = serializeValue(o.Stage) }
	return m
}

// WithContainerCertificatePathsOptions carries optional parameters for WithContainerCertificatePaths.
type WithContainerCertificatePathsOptions struct {
	CustomCertificatesDestination *string `json:"customCertificatesDestination,omitempty"`
	DefaultCertificateBundlePaths []string `json:"defaultCertificateBundlePaths,omitempty"`
	DefaultCertificateDirectoryPaths []string `json:"defaultCertificateDirectoryPaths,omitempty"`
}

func (o *WithContainerCertificatePathsOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.CustomCertificatesDestination != nil { m["customCertificatesDestination"] = serializeValue(o.CustomCertificatesDestination) }
	if o.DefaultCertificateBundlePaths != nil { m["defaultCertificateBundlePaths"] = serializeValue(o.DefaultCertificateBundlePaths) }
	if o.DefaultCertificateDirectoryPaths != nil { m["defaultCertificateDirectoryPaths"] = serializeValue(o.DefaultCertificateDirectoryPaths) }
	return m
}

// WithDockerfileBuilderOptions carries optional parameters for WithDockerfileBuilder.
type WithDockerfileBuilderOptions struct {
	Stage *string `json:"stage,omitempty"`
}

func (o *WithDockerfileBuilderOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Stage != nil { m["stage"] = serializeValue(o.Stage) }
	return m
}

// WithDockerfileBaseImageOptions carries optional parameters for WithDockerfileBaseImage.
type WithDockerfileBaseImageOptions struct {
	BuildImage *string `json:"buildImage,omitempty"`
	RuntimeImage *string `json:"runtimeImage,omitempty"`
}

func (o *WithDockerfileBaseImageOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.BuildImage != nil { m["buildImage"] = serializeValue(o.BuildImage) }
	if o.RuntimeImage != nil { m["runtimeImage"] = serializeValue(o.RuntimeImage) }
	return m
}

// RunOptions carries optional parameters for Run.
type RunOptions struct {
	CancellationToken *CancellationToken `json:"-"`
}

func (o *RunOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	return m
}

// WithHttpHealthCheckOptions carries optional parameters for WithHttpHealthCheck.
type WithHttpHealthCheckOptions struct {
	Path *string `json:"path,omitempty"`
	StatusCode *float64 `json:"statusCode,omitempty"`
	EndpointName *string `json:"endpointName,omitempty"`
}

func (o *WithHttpHealthCheckOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Path != nil { m["path"] = serializeValue(o.Path) }
	if o.StatusCode != nil { m["statusCode"] = serializeValue(o.StatusCode) }
	if o.EndpointName != nil { m["endpointName"] = serializeValue(o.EndpointName) }
	return m
}

// WithMcpServerOptions carries optional parameters for WithMcpServer.
type WithMcpServerOptions struct {
	Path *string `json:"path,omitempty"`
	EndpointName *string `json:"endpointName,omitempty"`
}

func (o *WithMcpServerOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Path != nil { m["path"] = serializeValue(o.Path) }
	if o.EndpointName != nil { m["endpointName"] = serializeValue(o.EndpointName) }
	return m
}

// WithOtlpExporterOptions carries optional parameters for WithOtlpExporter.
type WithOtlpExporterOptions struct {
	Protocol *OtlpProtocol `json:"protocol,omitempty"`
}

func (o *WithOtlpExporterOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Protocol != nil { m["protocol"] = serializeValue(o.Protocol) }
	return m
}

// AddParameterOptions carries optional parameters for AddParameter.
type AddParameterOptions struct {
	Value *string `json:"value,omitempty"`
	PublishValueAsDefault *bool `json:"publishValueAsDefault,omitempty"`
	Secret *bool `json:"secret,omitempty"`
}

func (o *AddParameterOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Value != nil { m["value"] = serializeValue(o.Value) }
	if o.PublishValueAsDefault != nil { m["publishValueAsDefault"] = serializeValue(o.PublishValueAsDefault) }
	if o.Secret != nil { m["secret"] = serializeValue(o.Secret) }
	return m
}

// AddParameterFromConfigurationOptions carries optional parameters for AddParameterFromConfiguration.
type AddParameterFromConfigurationOptions struct {
	Secret *bool `json:"secret,omitempty"`
}

func (o *AddParameterFromConfigurationOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Secret != nil { m["secret"] = serializeValue(o.Secret) }
	return m
}

// AddParameterWithGeneratedValueOptions carries optional parameters for AddParameterWithGeneratedValue.
type AddParameterWithGeneratedValueOptions struct {
	Secret *bool `json:"secret,omitempty"`
	Persist *bool `json:"persist,omitempty"`
}

func (o *AddParameterWithGeneratedValueOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Secret != nil { m["secret"] = serializeValue(o.Secret) }
	if o.Persist != nil { m["persist"] = serializeValue(o.Persist) }
	return m
}

// WithDescriptionOptions carries optional parameters for WithDescription.
type WithDescriptionOptions struct {
	EnableMarkdown *bool `json:"enableMarkdown,omitempty"`
}

func (o *WithDescriptionOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.EnableMarkdown != nil { m["enableMarkdown"] = serializeValue(o.EnableMarkdown) }
	return m
}

// AddConnectionStringOptions carries optional parameters for AddConnectionString.
type AddConnectionStringOptions struct {
	EnvironmentVariableNameOrExpression any `json:"environmentVariableNameOrExpression,omitempty"`
}

func (o *AddConnectionStringOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.EnvironmentVariableNameOrExpression != nil { m["environmentVariableNameOrExpression"] = serializeValue(o.EnvironmentVariableNameOrExpression) }
	return m
}

// AddProjectOptions carries optional parameters for AddProject.
type AddProjectOptions struct {
	LaunchProfileOrOptions any `json:"launchProfileOrOptions,omitempty"`
}

func (o *AddProjectOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.LaunchProfileOrOptions != nil { m["launchProfileOrOptions"] = serializeValue(o.LaunchProfileOrOptions) }
	return m
}

// AddCSharpAppOptions carries optional parameters for AddCSharpApp.
type AddCSharpAppOptions struct {
	Options *ProjectResourceOptions `json:"options,omitempty"`
}

func (o *AddCSharpAppOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Options != nil { m["options"] = serializeValue(o.Options) }
	return m
}

// PublishAsDockerFileOptions carries optional parameters for PublishAsDockerFile.
type PublishAsDockerFileOptions struct {
	Configure func(obj ContainerResource) `json:"-"`
}

func (o *PublishAsDockerFileOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	return m
}

// WithRequiredCommandOptions carries optional parameters for WithRequiredCommand.
type WithRequiredCommandOptions struct {
	HelpLink *string `json:"helpLink,omitempty"`
}

func (o *WithRequiredCommandOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.HelpLink != nil { m["helpLink"] = serializeValue(o.HelpLink) }
	return m
}

// WithReferenceOptions carries optional parameters for WithReference.
type WithReferenceOptions struct {
	ConnectionName *string `json:"connectionName,omitempty"`
	Optional *bool `json:"optional,omitempty"`
	Name *string `json:"name,omitempty"`
}

func (o *WithReferenceOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.ConnectionName != nil { m["connectionName"] = serializeValue(o.ConnectionName) }
	if o.Optional != nil { m["optional"] = serializeValue(o.Optional) }
	if o.Name != nil { m["name"] = serializeValue(o.Name) }
	return m
}

// WithEndpointCallbackOptions carries optional parameters for WithEndpointCallback.
type WithEndpointCallbackOptions struct {
	CreateIfNotExists *bool `json:"createIfNotExists,omitempty"`
}

func (o *WithEndpointCallbackOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.CreateIfNotExists != nil { m["createIfNotExists"] = serializeValue(o.CreateIfNotExists) }
	return m
}

// WithHttpEndpointCallbackOptions carries optional parameters for WithHttpEndpointCallback.
type WithHttpEndpointCallbackOptions struct {
	Name *string `json:"name,omitempty"`
	CreateIfNotExists *bool `json:"createIfNotExists,omitempty"`
}

func (o *WithHttpEndpointCallbackOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Name != nil { m["name"] = serializeValue(o.Name) }
	if o.CreateIfNotExists != nil { m["createIfNotExists"] = serializeValue(o.CreateIfNotExists) }
	return m
}

// WithHttpsEndpointCallbackOptions carries optional parameters for WithHttpsEndpointCallback.
type WithHttpsEndpointCallbackOptions struct {
	Name *string `json:"name,omitempty"`
	CreateIfNotExists *bool `json:"createIfNotExists,omitempty"`
}

func (o *WithHttpsEndpointCallbackOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Name != nil { m["name"] = serializeValue(o.Name) }
	if o.CreateIfNotExists != nil { m["createIfNotExists"] = serializeValue(o.CreateIfNotExists) }
	return m
}

// WithEndpointOptions carries optional parameters for WithEndpoint.
type WithEndpointOptions struct {
	Port *float64 `json:"port,omitempty"`
	TargetPort *float64 `json:"targetPort,omitempty"`
	Scheme *string `json:"scheme,omitempty"`
	Name *string `json:"name,omitempty"`
	Env *string `json:"env,omitempty"`
	IsProxied *bool `json:"isProxied,omitempty"`
	IsExternal *bool `json:"isExternal,omitempty"`
	Protocol *ProtocolType `json:"protocol,omitempty"`
}

func (o *WithEndpointOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Port != nil { m["port"] = serializeValue(o.Port) }
	if o.TargetPort != nil { m["targetPort"] = serializeValue(o.TargetPort) }
	if o.Scheme != nil { m["scheme"] = serializeValue(o.Scheme) }
	if o.Name != nil { m["name"] = serializeValue(o.Name) }
	if o.Env != nil { m["env"] = serializeValue(o.Env) }
	if o.IsProxied != nil { m["isProxied"] = serializeValue(o.IsProxied) }
	if o.IsExternal != nil { m["isExternal"] = serializeValue(o.IsExternal) }
	if o.Protocol != nil { m["protocol"] = serializeValue(o.Protocol) }
	return m
}

// WithHttpEndpointOptions carries optional parameters for WithHttpEndpoint.
type WithHttpEndpointOptions struct {
	Port *float64 `json:"port,omitempty"`
	TargetPort *float64 `json:"targetPort,omitempty"`
	Name *string `json:"name,omitempty"`
	Env *string `json:"env,omitempty"`
	IsProxied *bool `json:"isProxied,omitempty"`
}

func (o *WithHttpEndpointOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Port != nil { m["port"] = serializeValue(o.Port) }
	if o.TargetPort != nil { m["targetPort"] = serializeValue(o.TargetPort) }
	if o.Name != nil { m["name"] = serializeValue(o.Name) }
	if o.Env != nil { m["env"] = serializeValue(o.Env) }
	if o.IsProxied != nil { m["isProxied"] = serializeValue(o.IsProxied) }
	return m
}

// WithHttpsEndpointOptions carries optional parameters for WithHttpsEndpoint.
type WithHttpsEndpointOptions struct {
	Port *float64 `json:"port,omitempty"`
	TargetPort *float64 `json:"targetPort,omitempty"`
	Name *string `json:"name,omitempty"`
	Env *string `json:"env,omitempty"`
	IsProxied *bool `json:"isProxied,omitempty"`
}

func (o *WithHttpsEndpointOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Port != nil { m["port"] = serializeValue(o.Port) }
	if o.TargetPort != nil { m["targetPort"] = serializeValue(o.TargetPort) }
	if o.Name != nil { m["name"] = serializeValue(o.Name) }
	if o.Env != nil { m["env"] = serializeValue(o.Env) }
	if o.IsProxied != nil { m["isProxied"] = serializeValue(o.IsProxied) }
	return m
}

// WithUrlOptions carries optional parameters for WithUrl.
type WithUrlOptions struct {
	DisplayText *string `json:"displayText,omitempty"`
}

func (o *WithUrlOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.DisplayText != nil { m["displayText"] = serializeValue(o.DisplayText) }
	return m
}

// WaitForOptions carries optional parameters for WaitFor.
type WaitForOptions struct {
	WaitBehavior *WaitBehavior `json:"waitBehavior,omitempty"`
}

func (o *WaitForOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.WaitBehavior != nil { m["waitBehavior"] = serializeValue(o.WaitBehavior) }
	return m
}

// WaitForStartOptions carries optional parameters for WaitForStart.
type WaitForStartOptions struct {
	WaitBehavior *WaitBehavior `json:"waitBehavior,omitempty"`
}

func (o *WaitForStartOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.WaitBehavior != nil { m["waitBehavior"] = serializeValue(o.WaitBehavior) }
	return m
}

// WaitForCompletionOptions carries optional parameters for WaitForCompletion.
type WaitForCompletionOptions struct {
	ExitCode *float64 `json:"exitCode,omitempty"`
}

func (o *WaitForCompletionOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.ExitCode != nil { m["exitCode"] = serializeValue(o.ExitCode) }
	return m
}

// WithCommandOptions carries optional parameters for WithCommand.
type WithCommandOptions struct {
	CommandOptions *CommandOptions `json:"commandOptions,omitempty"`
}

func (o *WithCommandOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.CommandOptions != nil { m["commandOptions"] = serializeValue(o.CommandOptions) }
	return m
}

// WithHttpCommandOptions carries optional parameters for WithHttpCommand.
type WithHttpCommandOptions struct {
	Options *HttpCommandExportOptions `json:"options,omitempty"`
}

func (o *WithHttpCommandOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Options != nil { m["options"] = serializeValue(o.Options) }
	return m
}

// WithHttpsDeveloperCertificateOptions carries optional parameters for WithHttpsDeveloperCertificate.
type WithHttpsDeveloperCertificateOptions struct {
	Password *ParameterResource `json:"password,omitempty"`
}

func (o *WithHttpsDeveloperCertificateOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Password != nil { m["password"] = serializeValue(o.Password) }
	return m
}

// WithIconNameOptions carries optional parameters for WithIconName.
type WithIconNameOptions struct {
	IconVariant *IconVariant `json:"iconVariant,omitempty"`
}

func (o *WithIconNameOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.IconVariant != nil { m["iconVariant"] = serializeValue(o.IconVariant) }
	return m
}

// WithHttpProbeOptions carries optional parameters for WithHttpProbe.
type WithHttpProbeOptions struct {
	Path *string `json:"path,omitempty"`
	InitialDelaySeconds *float64 `json:"initialDelaySeconds,omitempty"`
	PeriodSeconds *float64 `json:"periodSeconds,omitempty"`
	TimeoutSeconds *float64 `json:"timeoutSeconds,omitempty"`
	FailureThreshold *float64 `json:"failureThreshold,omitempty"`
	SuccessThreshold *float64 `json:"successThreshold,omitempty"`
	EndpointName *string `json:"endpointName,omitempty"`
}

func (o *WithHttpProbeOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Path != nil { m["path"] = serializeValue(o.Path) }
	if o.InitialDelaySeconds != nil { m["initialDelaySeconds"] = serializeValue(o.InitialDelaySeconds) }
	if o.PeriodSeconds != nil { m["periodSeconds"] = serializeValue(o.PeriodSeconds) }
	if o.TimeoutSeconds != nil { m["timeoutSeconds"] = serializeValue(o.TimeoutSeconds) }
	if o.FailureThreshold != nil { m["failureThreshold"] = serializeValue(o.FailureThreshold) }
	if o.SuccessThreshold != nil { m["successThreshold"] = serializeValue(o.SuccessThreshold) }
	if o.EndpointName != nil { m["endpointName"] = serializeValue(o.EndpointName) }
	return m
}

// SetCancellationTokenOptions carries optional parameters for SetCancellationToken.
type SetCancellationTokenOptions struct {
	Value *CancellationToken `json:"-"`
}

func (o *SetCancellationTokenOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	return m
}

// WithPipelineStepFactoryOptions carries optional parameters for WithPipelineStepFactory.
type WithPipelineStepFactoryOptions struct {
	DependsOn []string `json:"dependsOn,omitempty"`
	RequiredBy []string `json:"requiredBy,omitempty"`
	Tags []string `json:"tags,omitempty"`
	Description *string `json:"description,omitempty"`
}

func (o *WithPipelineStepFactoryOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.DependsOn != nil { m["dependsOn"] = serializeValue(o.DependsOn) }
	if o.RequiredBy != nil { m["requiredBy"] = serializeValue(o.RequiredBy) }
	if o.Tags != nil { m["tags"] = serializeValue(o.Tags) }
	if o.Description != nil { m["description"] = serializeValue(o.Description) }
	return m
}

// WithVolumeOptions carries optional parameters for WithVolume.
type WithVolumeOptions struct {
	Name *string `json:"name,omitempty"`
	IsReadOnly *bool `json:"isReadOnly,omitempty"`
}

func (o *WithVolumeOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Name != nil { m["name"] = serializeValue(o.Name) }
	if o.IsReadOnly != nil { m["isReadOnly"] = serializeValue(o.IsReadOnly) }
	return m
}

// ArgOptions carries optional parameters for Arg.
type ArgOptions struct {
	DefaultValue *string `json:"defaultValue,omitempty"`
}

func (o *ArgOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.DefaultValue != nil { m["defaultValue"] = serializeValue(o.DefaultValue) }
	return m
}

// FromOptions carries optional parameters for From.
type FromOptions struct {
	StageName *string `json:"stageName,omitempty"`
}

func (o *FromOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.StageName != nil { m["stageName"] = serializeValue(o.StageName) }
	return m
}

// CopyOptions carries optional parameters for Copy.
type CopyOptions struct {
	Chown *string `json:"chown,omitempty"`
}

func (o *CopyOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Chown != nil { m["chown"] = serializeValue(o.Chown) }
	return m
}

// CopyFromOptions carries optional parameters for CopyFrom.
type CopyFromOptions struct {
	Chown *string `json:"chown,omitempty"`
}

func (o *CopyFromOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Chown != nil { m["chown"] = serializeValue(o.Chown) }
	return m
}

// AddContainerFilesStagesOptions carries optional parameters for AddContainerFilesStages.
type AddContainerFilesStagesOptions struct {
	Logger *Logger `json:"logger,omitempty"`
}

func (o *AddContainerFilesStagesOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Logger != nil { m["logger"] = serializeValue(o.Logger) }
	return m
}

// AddContainerFilesOptions carries optional parameters for AddContainerFiles.
type AddContainerFilesOptions struct {
	Logger *Logger `json:"logger,omitempty"`
}

func (o *AddContainerFilesOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Logger != nil { m["logger"] = serializeValue(o.Logger) }
	return m
}

// BuildOptions carries optional parameters for Build.
type BuildOptions struct {
	ResourceLogger *Logger `json:"resourceLogger,omitempty"`
	CancellationToken *CancellationToken `json:"-"`
}

func (o *BuildOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.ResourceLogger != nil { m["resourceLogger"] = serializeValue(o.ResourceLogger) }
	return m
}

// WaitForResourceStateOptions carries optional parameters for WaitForResourceState.
type WaitForResourceStateOptions struct {
	TargetState *string `json:"targetState,omitempty"`
}

func (o *WaitForResourceStateOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.TargetState != nil { m["targetState"] = serializeValue(o.TargetState) }
	return m
}

// PublishResourceUpdateOptions carries optional parameters for PublishResourceUpdate.
type PublishResourceUpdateOptions struct {
	State *string `json:"state,omitempty"`
	StateStyle *string `json:"stateStyle,omitempty"`
}

func (o *PublishResourceUpdateOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.State != nil { m["state"] = serializeValue(o.State) }
	if o.StateStyle != nil { m["stateStyle"] = serializeValue(o.StateStyle) }
	return m
}

// AddStepOptions carries optional parameters for AddStep.
type AddStepOptions struct {
	DependsOn []string `json:"dependsOn,omitempty"`
	RequiredBy []string `json:"requiredBy,omitempty"`
}

func (o *AddStepOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.DependsOn != nil { m["dependsOn"] = serializeValue(o.DependsOn) }
	if o.RequiredBy != nil { m["requiredBy"] = serializeValue(o.RequiredBy) }
	return m
}

// CreateTaskOptions carries optional parameters for CreateTask.
type CreateTaskOptions struct {
	CancellationToken *CancellationToken `json:"-"`
}

func (o *CreateTaskOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	return m
}

// CreateMarkdownTaskOptions carries optional parameters for CreateMarkdownTask.
type CreateMarkdownTaskOptions struct {
	CancellationToken *CancellationToken `json:"-"`
}

func (o *CreateMarkdownTaskOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	return m
}

// CompleteStepOptions carries optional parameters for CompleteStep.
type CompleteStepOptions struct {
	CompletionState *string `json:"completionState,omitempty"`
	CancellationToken *CancellationToken `json:"-"`
}

func (o *CompleteStepOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.CompletionState != nil { m["completionState"] = serializeValue(o.CompletionState) }
	return m
}

// CompleteStepMarkdownOptions carries optional parameters for CompleteStepMarkdown.
type CompleteStepMarkdownOptions struct {
	CompletionState *string `json:"completionState,omitempty"`
	CancellationToken *CancellationToken `json:"-"`
}

func (o *CompleteStepMarkdownOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.CompletionState != nil { m["completionState"] = serializeValue(o.CompletionState) }
	return m
}

// UpdateTaskOptions carries optional parameters for UpdateTask.
type UpdateTaskOptions struct {
	CancellationToken *CancellationToken `json:"-"`
}

func (o *UpdateTaskOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	return m
}

// UpdateTaskMarkdownOptions carries optional parameters for UpdateTaskMarkdown.
type UpdateTaskMarkdownOptions struct {
	CancellationToken *CancellationToken `json:"-"`
}

func (o *UpdateTaskMarkdownOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	return m
}

// CompleteTaskOptions carries optional parameters for CompleteTask.
type CompleteTaskOptions struct {
	CompletionMessage *string `json:"completionMessage,omitempty"`
	CompletionState *string `json:"completionState,omitempty"`
	CancellationToken *CancellationToken `json:"-"`
}

func (o *CompleteTaskOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.CompletionMessage != nil { m["completionMessage"] = serializeValue(o.CompletionMessage) }
	if o.CompletionState != nil { m["completionState"] = serializeValue(o.CompletionState) }
	return m
}

// CompleteTaskMarkdownOptions carries optional parameters for CompleteTaskMarkdown.
type CompleteTaskMarkdownOptions struct {
	CompletionState *string `json:"completionState,omitempty"`
	CancellationToken *CancellationToken `json:"-"`
}

func (o *CompleteTaskMarkdownOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.CompletionState != nil { m["completionState"] = serializeValue(o.CompletionState) }
	return m
}

// SaveStateJsonOptions carries optional parameters for SaveStateJson.
type SaveStateJsonOptions struct {
	CancellationToken *CancellationToken `json:"-"`
}

func (o *SaveStateJsonOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	return m
}

// GetValueAsyncOptions carries optional parameters for GetValueAsync.
type GetValueAsyncOptions struct {
	CancellationToken *CancellationToken `json:"-"`
}

func (o *GetValueAsyncOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	return m
}

// AppendFormattedOptions carries optional parameters for AppendFormatted.
type AppendFormattedOptions struct {
	Format *string `json:"format,omitempty"`
}

func (o *AppendFormattedOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Format != nil { m["format"] = serializeValue(o.Format) }
	return m
}

// AppendValueProviderOptions carries optional parameters for AppendValueProvider.
type AppendValueProviderOptions struct {
	Format *string `json:"format,omitempty"`
}

func (o *AppendValueProviderOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Format != nil { m["format"] = serializeValue(o.Format) }
	return m
}

// AddOptions carries optional parameters for Add.
type AddOptions struct {
	DisplayText *string `json:"displayText,omitempty"`
}

func (o *AddOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.DisplayText != nil { m["displayText"] = serializeValue(o.DisplayText) }
	return m
}

// AddForEndpointOptions carries optional parameters for AddForEndpoint.
type AddForEndpointOptions struct {
	DisplayText *string `json:"displayText,omitempty"`
}

func (o *AddForEndpointOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.DisplayText != nil { m["displayText"] = serializeValue(o.DisplayText) }
	return m
}

// AddGoAppOptions carries optional parameters for AddGoApp.
type AddGoAppOptions struct {
	BuildTags []string `json:"buildTags,omitempty"`
	LdFlags *string `json:"ldFlags,omitempty"`
	GcFlags *string `json:"gcFlags,omitempty"`
	RaceDetector *bool `json:"raceDetector,omitempty"`
}

func (o *AddGoAppOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.BuildTags != nil { m["buildTags"] = serializeValue(o.BuildTags) }
	if o.LdFlags != nil { m["ldFlags"] = serializeValue(o.LdFlags) }
	if o.GcFlags != nil { m["gcFlags"] = serializeValue(o.GcFlags) }
	if o.RaceDetector != nil { m["raceDetector"] = serializeValue(o.RaceDetector) }
	return m
}

// WithDelveServerOptions carries optional parameters for WithDelveServer.
type WithDelveServerOptions struct {
	Port *float64 `json:"port,omitempty"`
}

func (o *WithDelveServerOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Port != nil { m["port"] = serializeValue(o.Port) }
	return m
}

// ============================================================================
// Per-client handle wrapper registration
// ============================================================================

func registerWrappers(c *client) {
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpression", func(h *handle, c *client) any {
		return newHandleBackedReferenceExpression(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.AfterResourcesCreatedEvent", func(h *handle, c *client) any {
		return newAfterResourcesCreatedEventFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IAspireStore", func(h *handle, c *client) any {
		return newAspireStoreFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.BeforeResourceStartedEvent", func(h *handle, c *client) any {
		return newBeforeResourceStartedEventFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.BeforeStartEvent", func(h *handle, c *client) any {
		return newBeforeStartEventFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.CSharpAppResource", func(h *handle, c *client) any {
		return newCSharpAppResourceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.CommandLineArgsCallbackContext", func(h *handle, c *client) any {
		return newCommandLineArgsCallbackContextFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.CommandLineArgsEditor", func(h *handle, c *client) any {
		return newCommandLineArgsEditorFromHandle(h, c)
	})
	c.registerHandleWrapper("Microsoft.Extensions.Configuration.Abstractions/Microsoft.Extensions.Configuration.IConfiguration", func(h *handle, c *client) any {
		return newConfigurationFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ConnectionStringAvailableEvent", func(h *handle, c *client) any {
		return newConnectionStringAvailableEventFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerImagePushOptions", func(h *handle, c *client) any {
		return newContainerImagePushOptionsFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerImagePushOptionsCallbackContext", func(h *handle, c *client) any {
		return newContainerImagePushOptionsCallbackContextFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerImageReference", func(h *handle, c *client) any {
		return newContainerImageReferenceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerMountAnnotation", func(h *handle, c *client) any {
		return newContainerMountAnnotationFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerPortReference", func(h *handle, c *client) any {
		return newContainerPortReferenceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerRegistryResource", func(h *handle, c *client) any {
		return newContainerRegistryResourceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerResource", func(h *handle, c *client) any {
		return newContainerResourceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.DistributedApplication", func(h *handle, c *client) any {
		return newDistributedApplicationFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder", func(h *handle, c *client) any {
		return newDistributedApplicationBuilderFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.Eventing.DistributedApplicationEventSubscription", func(h *handle, c *client) any {
		return newDistributedApplicationEventSubscriptionFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.Eventing.IDistributedApplicationEventing", func(h *handle, c *client) any {
		return newDistributedApplicationEventingFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.DistributedApplicationExecutionContext", func(h *handle, c *client) any {
		return newDistributedApplicationExecutionContextFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.DistributedApplicationExecutionContextOptions", func(h *handle, c *client) any {
		return newDistributedApplicationExecutionContextOptionsFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.DistributedApplicationModel", func(h *handle, c *client) any {
		return newDistributedApplicationModelFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.IDistributedApplicationPipeline", func(h *handle, c *client) any {
		return newDistributedApplicationPipelineFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.Eventing.DistributedApplicationResourceEventSubscription", func(h *handle, c *client) any {
		return newDistributedApplicationResourceEventSubscriptionFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.Docker.DockerfileBuilder", func(h *handle, c *client) any {
		return newDockerfileBuilderFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.DockerfileBuilderCallbackContext", func(h *handle, c *client) any {
		return newDockerfileBuilderCallbackContextFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.Docker.DockerfileStage", func(h *handle, c *client) any {
		return newDockerfileStageFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.DotnetToolResource", func(h *handle, c *client) any {
		return newDotnetToolResourceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointReference", func(h *handle, c *client) any {
		return newEndpointReferenceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointReferenceExpression", func(h *handle, c *client) any {
		return newEndpointReferenceExpressionFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointUpdateContext", func(h *handle, c *client) any {
		return newEndpointUpdateContextFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EnvironmentCallbackContext", func(h *handle, c *client) any {
		return newEnvironmentCallbackContextFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.EnvironmentEditor", func(h *handle, c *client) any {
		return newEnvironmentEditorFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.Ats.EventingSubscriberRegistrationContext", func(h *handle, c *client) any {
		return newEventingSubscriberRegistrationContextFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ExecutableResource", func(h *handle, c *client) any {
		return newExecutableResourceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ExecuteCommandContext", func(h *handle, c *client) any {
		return newExecuteCommandContextFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IExecutionConfigurationBuilder", func(h *handle, c *client) any {
		return newExecutionConfigurationBuilderFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IExecutionConfigurationResult", func(h *handle, c *client) any {
		return newExecutionConfigurationResultFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ExternalServiceResource", func(h *handle, c *client) any {
		return newExternalServiceResourceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting.Go/Aspire.Hosting.Go.GoAppResource", func(h *handle, c *client) any {
		return newGoAppResourceFromHandle(h, c)
	})
	c.registerHandleWrapper("Microsoft.Extensions.Hosting.Abstractions/Microsoft.Extensions.Hosting.IHostEnvironment", func(h *handle, c *client) any {
		return newHostEnvironmentFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IComputeResource", func(h *handle, c *client) any {
		return newIComputeResourceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IContainerFilesDestinationResource", func(h *handle, c *client) any {
		return newIContainerFilesDestinationResourceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.InitializeResourceEvent", func(h *handle, c *client) any {
		return newInitializeResourceEventFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.LogFacade", func(h *handle, c *client) any {
		return newLogFacadeFromHandle(h, c)
	})
	c.registerHandleWrapper("Microsoft.Extensions.Logging.Abstractions/Microsoft.Extensions.Logging.ILogger", func(h *handle, c *client) any {
		return newLoggerFromHandle(h, c)
	})
	c.registerHandleWrapper("Microsoft.Extensions.Logging.Abstractions/Microsoft.Extensions.Logging.ILoggerFactory", func(h *handle, c *client) any {
		return newLoggerFactoryFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ParameterResource", func(h *handle, c *client) any {
		return newParameterResourceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineConfigurationContext", func(h *handle, c *client) any {
		return newPipelineConfigurationContextFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineContext", func(h *handle, c *client) any {
		return newPipelineContextFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineEditor", func(h *handle, c *client) any {
		return newPipelineEditorFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineStep", func(h *handle, c *client) any {
		return newPipelineStepFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineStepContext", func(h *handle, c *client) any {
		return newPipelineStepContextFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineStepFactoryContext", func(h *handle, c *client) any {
		return newPipelineStepFactoryContextFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineSummary", func(h *handle, c *client) any {
		return newPipelineSummaryFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ProjectResource", func(h *handle, c *client) any {
		return newProjectResourceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ProjectResourceOptions", func(h *handle, c *client) any {
		return newProjectResourceOptionsFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpressionBuilder", func(h *handle, c *client) any {
		return newReferenceExpressionBuilderFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.IReportingStep", func(h *handle, c *client) any {
		return newReportingStepFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.Pipelines.IReportingTask", func(h *handle, c *client) any {
		return newReportingTaskFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceEndpointsAllocatedEvent", func(h *handle, c *client) any {
		return newResourceEndpointsAllocatedEventFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceLoggerService", func(h *handle, c *client) any {
		return newResourceLoggerServiceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceNotificationService", func(h *handle, c *client) any {
		return newResourceNotificationServiceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceReadyEvent", func(h *handle, c *client) any {
		return newResourceReadyEventFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceStoppedEvent", func(h *handle, c *client) any {
		return newResourceStoppedEventFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceUrlsCallbackContext", func(h *handle, c *client) any {
		return newResourceUrlsCallbackContextFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceUrlsEditor", func(h *handle, c *client) any {
		return newResourceUrlsEditorFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithConnectionString", func(h *handle, c *client) any {
		return newResourceWithConnectionStringFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.IResourceWithContainerFiles", func(h *handle, c *client) any {
		return newResourceWithContainerFilesFromHandle(h, c)
	})
	c.registerHandleWrapper("System.ComponentModel/System.IServiceProvider", func(h *handle, c *client) any {
		return newServiceProviderFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.UpdateCommandStateContext", func(h *handle, c *client) any {
		return newUpdateCommandStateContextFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.IUserSecretsManager", func(h *handle, c *client) any {
		return newUserSecretsManagerFromHandle(h, c)
	})
}

// ============================================================================
// Builder construction & Build()
// ============================================================================

// Build invokes the build capability and returns the running application.
func (b *distributedApplicationBuilder) Build() (DistributedApplication, error) {
	if b.err != nil { return nil, b.err }
	result, err := b.client.invokeCapability(context.Background(), "Aspire.Hosting/build", map[string]any{
		"context": b.handle.ToJSON(),
	})
	if err != nil { return nil, err }
	app, ok := result.(DistributedApplication)
	if !ok { return nil, fmt.Errorf("aspire: build returned unexpected type %T", result) }
	return app, nil
}

// CreateBuilder establishes a connection to the AppHost and returns a new builder.
func CreateBuilder(options ...*CreateBuilderOptions) (DistributedApplicationBuilder, error) {
	socketPath := os.Getenv("REMOTE_APP_HOST_SOCKET_PATH")
	if socketPath == "" {
		return nil, fmt.Errorf("REMOTE_APP_HOST_SOCKET_PATH environment variable not set. Run this application using `aspire run`")
	}
	c := newClient(socketPath)
	if err := c.connect(context.Background(), 5*time.Second); err != nil { return nil, err }
	c.onDisconnect(func() { os.Exit(1) })
	registerWrappers(c)

	resolved := map[string]any{}
	if len(options) > 0 {
		merged := &CreateBuilderOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { resolved[k] = v }
	}
	if _, ok := resolved["Args"]; !ok { resolved["Args"] = os.Args[1:] }
	if projectDirectory, ok := resolved["ProjectDirectory"].(string); !ok || projectDirectory == "" {
		if pwd, err := os.Getwd(); err == nil { resolved["ProjectDirectory"] = pwd }
	}
	if appHostFilePath, ok := resolved["AppHostFilePath"].(string); !ok || appHostFilePath == "" {
		if appHostFilePath := os.Getenv("ASPIRE_APPHOST_FILEPATH"); appHostFilePath != "" { resolved["AppHostFilePath"] = appHostFilePath }
	}
	if dashboardApplicationName, ok := resolved["DashboardApplicationName"].(string); ok && dashboardApplicationName == "" {
		delete(resolved, "DashboardApplicationName")
	}

	result, err := c.invokeCapability(context.Background(), "Aspire.Hosting/createBuilder", map[string]any{"argsOrOptions": resolved})
	if err != nil { return nil, err }
	href, ok := result.(handleReference)
	if !ok { return nil, fmt.Errorf("aspire: createBuilder returned unexpected type %T", result) }
	return &distributedApplicationBuilder{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), c)}, nil
}

