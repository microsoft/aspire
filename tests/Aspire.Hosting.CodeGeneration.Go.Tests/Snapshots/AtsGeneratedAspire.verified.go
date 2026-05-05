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

// TestConfigDto represents TestConfigDto.
type TestConfigDto struct {
	Name string `json:"Name,omitempty"`
	Port float64 `json:"Port,omitempty"`
	Enabled bool `json:"Enabled,omitempty"`
	OptionalField string `json:"OptionalField,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *TestConfigDto) ToMap() map[string]any {
	m := map[string]any{}
	m["Name"] = serializeValue(d.Name)
	m["Port"] = serializeValue(d.Port)
	m["Enabled"] = serializeValue(d.Enabled)
	m["OptionalField"] = serializeValue(d.OptionalField)
	return m
}

// TestNestedDto represents TestNestedDto.
type TestNestedDto struct {
	Id string `json:"Id,omitempty"`
	Config *TestConfigDto `json:"Config,omitempty"`
	Tags *List[string] `json:"Tags,omitempty"`
	Counts *Dict[string, float64] `json:"Counts,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *TestNestedDto) ToMap() map[string]any {
	m := map[string]any{}
	m["Id"] = serializeValue(d.Id)
	if d.Config != nil { m["Config"] = serializeValue(d.Config) }
	if d.Tags != nil { m["Tags"] = serializeValue(d.Tags) }
	if d.Counts != nil { m["Counts"] = serializeValue(d.Counts) }
	return m
}

// TestDeeplyNestedDto represents TestDeeplyNestedDto.
type TestDeeplyNestedDto struct {
	NestedData *Dict[string, *List[*TestConfigDto]] `json:"NestedData,omitempty"`
	MetadataArray []*Dict[string, string] `json:"MetadataArray,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *TestDeeplyNestedDto) ToMap() map[string]any {
	m := map[string]any{}
	if d.NestedData != nil { m["NestedData"] = serializeValue(d.NestedData) }
	if d.MetadataArray != nil { m["MetadataArray"] = serializeValue(d.MetadataArray) }
	return m
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

// ============================================================================
// Marker interfaces (from interface-metadata types)
// ============================================================================

// Resource marks types implementing IResource.
// Methods are emitted on concrete impls; this interface is a marker for type assertions.
type Resource interface {
	handleReference
}

// ResourceWithConnectionString marks types implementing IResourceWithConnectionString.
// Methods are emitted on concrete impls; this interface is a marker for type assertions.
type ResourceWithConnectionString interface {
	handleReference
}

// TestVaultResource marks types implementing ITestVaultResource.
// Methods are emitted on concrete impls; this interface is a marker for type assertions.
type TestVaultResource interface {
	handleReference
}

// ============================================================================
// Handle wrappers
// ============================================================================

// Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource is the public interface for handle type Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource.
type Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource interface {
	handleReference
	TestWaitFor(dependency Resource) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	TestWithEnvironmentCallback(callback func(arg TestEnvironmentContext)) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithCancellableOperation(operation func(arg *CancellationToken)) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithConfig(config *TestConfigDto) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithCorrelationId(correlationId string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithCreatedAt(createdAt string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithDependency(dependency ResourceWithConnectionString) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithEndpoints(endpoints []string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithEnvironmentVariables(variables map[string]string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithMergeEndpoint(endpointName string, port float64) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithMergeEndpointScheme(endpointName string, port float64, scheme string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithMergeLabel(label string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithMergeLabelCategorized(label string, category string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithMergeLogging(logLevel string, options ...*WithMergeLoggingOptions) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithMergeLoggingPath(logLevel string, logPath string, options ...*WithMergeLoggingPathOptions) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithMergeRoute(path string, method string, handler string, priority float64) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithModifiedAt(modifiedAt string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithNestedConfig(config *TestNestedDto) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithOptionalCallback(options ...*WithOptionalCallbackOptions) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithOptionalString(options ...*WithOptionalStringOptions) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithStatus(status TestResourceStatus) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithUnionDependency(dependency any) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithValidator(validator func(arg TestResourceContext) bool) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	WithVaultDirect(option string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	Err() error
}

// aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource is the unexported impl of Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource.
type aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource struct {
	*resourceBuilderBase
}

// newAspire_Hosting_CodeGeneration_Go_TestsTestVaultResourceFromHandle wraps an existing handle as Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource.
func newAspire_Hosting_CodeGeneration_Go_TestsTestVaultResourceFromHandle(h *handle, c *client) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	return &aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// TestWaitFor waits for another resource (test version)
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) TestWaitFor(dependency Resource) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/testWaitFor", reqArgs); err != nil { s.setErr(err) }
	return s
}

// TestWithEnvironmentCallback configures environment with callback (test version)
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) TestWithEnvironmentCallback(callback func(arg TestEnvironmentContext)) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[TestEnvironmentContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/testWithEnvironmentCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCancellableOperation performs a cancellable operation
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithCancellableOperation(operation func(arg *CancellationToken)) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if operation != nil {
		cb := operation
		shim := func(args ...any) any {
			cb(callbackArg[*CancellationToken](args, 0))
			return nil
		}
		reqArgs["operation"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withCancellableOperation", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithConfig configures the resource with a DTO
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithConfig(config *TestConfigDto) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if config != nil { reqArgs["config"] = serializeValue(config) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withConfig", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCorrelationId sets the correlation ID
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithCorrelationId(correlationId string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["correlationId"] = serializeValue(correlationId)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withCorrelationId", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCreatedAt sets the created timestamp
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithCreatedAt(createdAt string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["createdAt"] = serializeValue(createdAt)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withCreatedAt", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDependency adds a dependency on another resource
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithDependency(dependency ResourceWithConnectionString) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withDependency", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEndpoints sets the endpoints
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithEndpoints(endpoints []string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if endpoints != nil { reqArgs["endpoints"] = serializeValue(endpoints) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withEndpoints", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEnvironmentVariables sets environment variables
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithEnvironmentVariables(variables map[string]string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if variables != nil { reqArgs["variables"] = serializeValue(variables) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withEnvironmentVariables", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeEndpoint configures a named endpoint
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithMergeEndpoint(endpointName string, port float64) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	reqArgs["port"] = serializeValue(port)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeEndpointScheme configures a named endpoint with scheme
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithMergeEndpointScheme(endpointName string, port float64, scheme string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	reqArgs["port"] = serializeValue(port)
	reqArgs["scheme"] = serializeValue(scheme)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpointScheme", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeLabel adds a label to the resource
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithMergeLabel(label string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["label"] = serializeValue(label)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabel", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeLabelCategorized adds a categorized label to the resource
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithMergeLabelCategorized(label string, category string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["label"] = serializeValue(label)
	reqArgs["category"] = serializeValue(category)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabelCategorized", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeLogging configures resource logging
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithMergeLogging(logLevel string, options ...*WithMergeLoggingOptions) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["logLevel"] = serializeValue(logLevel)
	if len(options) > 0 {
		merged := &WithMergeLoggingOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLogging", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeLoggingPath configures resource logging with file path
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithMergeLoggingPath(logLevel string, logPath string, options ...*WithMergeLoggingPathOptions) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["logLevel"] = serializeValue(logLevel)
	reqArgs["logPath"] = serializeValue(logPath)
	if len(options) > 0 {
		merged := &WithMergeLoggingPathOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLoggingPath", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeRoute configures a route
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithMergeRoute(path string, method string, handler string, priority float64) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["path"] = serializeValue(path)
	reqArgs["method"] = serializeValue(method)
	reqArgs["handler"] = serializeValue(handler)
	reqArgs["priority"] = serializeValue(priority)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRoute", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeRouteMiddleware configures a route with middleware
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["path"] = serializeValue(path)
	reqArgs["method"] = serializeValue(method)
	reqArgs["handler"] = serializeValue(handler)
	reqArgs["priority"] = serializeValue(priority)
	reqArgs["middleware"] = serializeValue(middleware)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRouteMiddleware", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithModifiedAt sets the modified timestamp
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithModifiedAt(modifiedAt string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["modifiedAt"] = serializeValue(modifiedAt)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withModifiedAt", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithNestedConfig configures with nested DTO
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithNestedConfig(config *TestNestedDto) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if config != nil { reqArgs["config"] = serializeValue(config) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withNestedConfig", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithOptionalCallback configures with optional callback
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithOptionalCallback(options ...*WithOptionalCallbackOptions) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithOptionalCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.Callback != nil {
			cb := merged.Callback
			shim := func(args ...any) any {
				cb(callbackArg[TestCallbackContext](args, 0))
				return nil
			}
			reqArgs["callback"] = s.client.registerCallback(shim)
		}
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithOptionalString adds an optional string parameter
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithOptionalString(options ...*WithOptionalStringOptions) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithOptionalStringOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalString", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithStatus sets the resource status
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithStatus(status TestResourceStatus) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["status"] = serializeValue(status)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withStatus", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUnionDependency adds a dependency from a string or another resource
// Allowed types for parameter dependency: string, ResourceWithConnectionString.
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithUnionDependency(dependency any) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	switch dependency.(type) {
	case string, ResourceWithConnectionString:
	default:
		err := fmt.Errorf("aspire: WithUnionDependency: parameter %q must be one of [string, ResourceWithConnectionString], got %T", "dependency", dependency)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if dependency != nil { reqArgs["dependency"] = serializeValue(dependency) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withUnionDependency", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithValidator adds validation callback
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithValidator(validator func(arg TestResourceContext) bool) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if validator != nil {
		cb := validator
		shim := func(args ...any) any {
			return cb(callbackArg[TestResourceContext](args, 0))
		}
		reqArgs["validator"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withValidator", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithVaultDirect configures vault using direct interface target
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithVaultDirect(option string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["option"] = serializeValue(option)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withVaultDirect", reqArgs); err != nil { s.setErr(err) }
	return s
}

// IDistributedApplicationBuilder is the public interface for handle type IDistributedApplicationBuilder.
type IDistributedApplicationBuilder interface {
	handleReference
	AddTestRedis(name string, options ...*AddTestRedisOptions) TestRedisResource
	AddTestVault(name string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource
	Build() (DistributedApplication, error)
	Err() error
}

// iDistributedApplicationBuilder is the unexported impl of IDistributedApplicationBuilder.
type iDistributedApplicationBuilder struct {
	*resourceBuilderBase
}

// newIDistributedApplicationBuilderFromHandle wraps an existing handle as IDistributedApplicationBuilder.
func newIDistributedApplicationBuilderFromHandle(h *handle, c *client) IDistributedApplicationBuilder {
	return &iDistributedApplicationBuilder{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// AddTestRedis adds a test Redis resource
func (s *iDistributedApplicationBuilder) AddTestRedis(name string, options ...*AddTestRedisOptions) TestRedisResource {
	if s.err != nil { return &testRedisResource{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if len(options) > 0 {
		merged := &AddTestRedisOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/addTestRedis", reqArgs)
	if err != nil {
		return &testRedisResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.CodeGeneration.Go.Tests/addTestRedis returned unexpected type %T", result)
		return &testRedisResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &testRedisResource{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// AddTestVault adds a test vault resource
func (s *iDistributedApplicationBuilder) AddTestVault(name string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return &aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/addTestVault", reqArgs)
	if err != nil {
		return &aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.CodeGeneration.Go.Tests/addTestVault returned unexpected type %T", result)
		return &aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// IResourceWithEnvironment is the public interface for handle type IResourceWithEnvironment.
type IResourceWithEnvironment interface {
	handleReference
	Err() error
}

// iResourceWithEnvironment is the unexported impl of IResourceWithEnvironment.
type iResourceWithEnvironment struct {
	*resourceBuilderBase
}

// newIResourceWithEnvironmentFromHandle wraps an existing handle as IResourceWithEnvironment.
func newIResourceWithEnvironmentFromHandle(h *handle, c *client) IResourceWithEnvironment {
	return &iResourceWithEnvironment{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// TestCallbackContext is the public interface for handle type TestCallbackContext.
type TestCallbackContext interface {
	handleReference
	CancellationToken() (*CancellationToken, error)
	Name() (string, error)
	SetCancellationToken(options ...*SetCancellationTokenOptions) TestCallbackContext
	SetName(value string) TestCallbackContext
	SetValue(value float64) TestCallbackContext
	Value() (float64, error)
	Err() error
}

// testCallbackContext is the unexported impl of TestCallbackContext.
type testCallbackContext struct {
	*resourceBuilderBase
}

// newTestCallbackContextFromHandle wraps an existing handle as TestCallbackContext.
func newTestCallbackContextFromHandle(h *handle, c *client) TestCallbackContext {
	return &testCallbackContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// CancellationToken gets the CancellationToken property
func (s *testCallbackContext) CancellationToken() (*CancellationToken, error) {
	if s.err != nil { var zero *CancellationToken; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.cancellationToken", reqArgs)
	if err != nil {
		var zero *CancellationToken
		return zero, err
	}
	return decodeAs[*CancellationToken](result)
}

// Name gets the Name property
func (s *testCallbackContext) Name() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.name", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// SetCancellationToken sets the CancellationToken property
func (s *testCallbackContext) SetCancellationToken(options ...*SetCancellationTokenOptions) TestCallbackContext {
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
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setCancellationToken", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetName sets the Name property
func (s *testCallbackContext) SetName(value string) TestCallbackContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetValue sets the Value property
func (s *testCallbackContext) SetValue(value float64) TestCallbackContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setValue", reqArgs); err != nil { s.setErr(err) }
	return s
}

// Value gets the Value property
func (s *testCallbackContext) Value() (float64, error) {
	if s.err != nil { var zero float64; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.value", reqArgs)
	if err != nil {
		var zero float64
		return zero, err
	}
	return decodeAs[float64](result)
}

// TestCollectionContext is the public interface for handle type TestCollectionContext.
type TestCollectionContext interface {
	handleReference
	Items() *List[string]
	Metadata() *Dict[string, string]
	Err() error
}

// testCollectionContext is the unexported impl of TestCollectionContext.
type testCollectionContext struct {
	*resourceBuilderBase
	items *List[string]
	metadata *Dict[string, string]
}

// newTestCollectionContextFromHandle wraps an existing handle as TestCollectionContext.
func newTestCollectionContextFromHandle(h *handle, c *client) TestCollectionContext {
	return &testCollectionContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Items gets the Items property
func (s *testCollectionContext) Items() *List[string] {
	if s.items == nil {
		s.items = newListWithGetter[string](s.handleWrapperBase, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.items")
	}
	return s.items
}

// Metadata gets the Metadata property
func (s *testCollectionContext) Metadata() *Dict[string, string] {
	if s.metadata == nil {
		s.metadata = newDictWithGetter[string, string](s.handleWrapperBase, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.metadata")
	}
	return s.metadata
}

// TestDatabaseResource is the public interface for handle type TestDatabaseResource.
type TestDatabaseResource interface {
	handleReference
	TestWaitFor(dependency Resource) TestDatabaseResource
	TestWithEnvironmentCallback(callback func(arg TestEnvironmentContext)) TestDatabaseResource
	WithCancellableOperation(operation func(arg *CancellationToken)) TestDatabaseResource
	WithConfig(config *TestConfigDto) TestDatabaseResource
	WithCorrelationId(correlationId string) TestDatabaseResource
	WithCreatedAt(createdAt string) TestDatabaseResource
	WithDataVolume(options ...*TestDatabaseResourceWithDataVolumeOptions) TestDatabaseResource
	WithDependency(dependency ResourceWithConnectionString) TestDatabaseResource
	WithEndpoints(endpoints []string) TestDatabaseResource
	WithEnvironmentVariables(variables map[string]string) TestDatabaseResource
	WithMergeEndpoint(endpointName string, port float64) TestDatabaseResource
	WithMergeEndpointScheme(endpointName string, port float64, scheme string) TestDatabaseResource
	WithMergeLabel(label string) TestDatabaseResource
	WithMergeLabelCategorized(label string, category string) TestDatabaseResource
	WithMergeLogging(logLevel string, options ...*WithMergeLoggingOptions) TestDatabaseResource
	WithMergeLoggingPath(logLevel string, logPath string, options ...*WithMergeLoggingPathOptions) TestDatabaseResource
	WithMergeRoute(path string, method string, handler string, priority float64) TestDatabaseResource
	WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) TestDatabaseResource
	WithModifiedAt(modifiedAt string) TestDatabaseResource
	WithNestedConfig(config *TestNestedDto) TestDatabaseResource
	WithOptionalCallback(options ...*WithOptionalCallbackOptions) TestDatabaseResource
	WithOptionalString(options ...*WithOptionalStringOptions) TestDatabaseResource
	WithStatus(status TestResourceStatus) TestDatabaseResource
	WithUnionDependency(dependency any) TestDatabaseResource
	WithValidator(validator func(arg TestResourceContext) bool) TestDatabaseResource
	Err() error
}

// testDatabaseResource is the unexported impl of TestDatabaseResource.
type testDatabaseResource struct {
	*resourceBuilderBase
}

// newTestDatabaseResourceFromHandle wraps an existing handle as TestDatabaseResource.
func newTestDatabaseResourceFromHandle(h *handle, c *client) TestDatabaseResource {
	return &testDatabaseResource{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// TestWaitFor waits for another resource (test version)
func (s *testDatabaseResource) TestWaitFor(dependency Resource) TestDatabaseResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/testWaitFor", reqArgs); err != nil { s.setErr(err) }
	return s
}

// TestWithEnvironmentCallback configures environment with callback (test version)
func (s *testDatabaseResource) TestWithEnvironmentCallback(callback func(arg TestEnvironmentContext)) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[TestEnvironmentContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/testWithEnvironmentCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCancellableOperation performs a cancellable operation
func (s *testDatabaseResource) WithCancellableOperation(operation func(arg *CancellationToken)) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if operation != nil {
		cb := operation
		shim := func(args ...any) any {
			cb(callbackArg[*CancellationToken](args, 0))
			return nil
		}
		reqArgs["operation"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withCancellableOperation", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithConfig configures the resource with a DTO
func (s *testDatabaseResource) WithConfig(config *TestConfigDto) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if config != nil { reqArgs["config"] = serializeValue(config) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withConfig", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCorrelationId sets the correlation ID
func (s *testDatabaseResource) WithCorrelationId(correlationId string) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["correlationId"] = serializeValue(correlationId)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withCorrelationId", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCreatedAt sets the created timestamp
func (s *testDatabaseResource) WithCreatedAt(createdAt string) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["createdAt"] = serializeValue(createdAt)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withCreatedAt", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDataVolume adds a data volume
func (s *testDatabaseResource) WithDataVolume(options ...*TestDatabaseResourceWithDataVolumeOptions) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &TestDatabaseResourceWithDataVolumeOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withDataVolume", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDependency adds a dependency on another resource
func (s *testDatabaseResource) WithDependency(dependency ResourceWithConnectionString) TestDatabaseResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withDependency", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEndpoints sets the endpoints
func (s *testDatabaseResource) WithEndpoints(endpoints []string) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if endpoints != nil { reqArgs["endpoints"] = serializeValue(endpoints) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withEndpoints", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEnvironmentVariables sets environment variables
func (s *testDatabaseResource) WithEnvironmentVariables(variables map[string]string) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if variables != nil { reqArgs["variables"] = serializeValue(variables) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withEnvironmentVariables", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeEndpoint configures a named endpoint
func (s *testDatabaseResource) WithMergeEndpoint(endpointName string, port float64) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	reqArgs["port"] = serializeValue(port)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeEndpointScheme configures a named endpoint with scheme
func (s *testDatabaseResource) WithMergeEndpointScheme(endpointName string, port float64, scheme string) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	reqArgs["port"] = serializeValue(port)
	reqArgs["scheme"] = serializeValue(scheme)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpointScheme", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeLabel adds a label to the resource
func (s *testDatabaseResource) WithMergeLabel(label string) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["label"] = serializeValue(label)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabel", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeLabelCategorized adds a categorized label to the resource
func (s *testDatabaseResource) WithMergeLabelCategorized(label string, category string) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["label"] = serializeValue(label)
	reqArgs["category"] = serializeValue(category)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabelCategorized", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeLogging configures resource logging
func (s *testDatabaseResource) WithMergeLogging(logLevel string, options ...*WithMergeLoggingOptions) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["logLevel"] = serializeValue(logLevel)
	if len(options) > 0 {
		merged := &WithMergeLoggingOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLogging", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeLoggingPath configures resource logging with file path
func (s *testDatabaseResource) WithMergeLoggingPath(logLevel string, logPath string, options ...*WithMergeLoggingPathOptions) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["logLevel"] = serializeValue(logLevel)
	reqArgs["logPath"] = serializeValue(logPath)
	if len(options) > 0 {
		merged := &WithMergeLoggingPathOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLoggingPath", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeRoute configures a route
func (s *testDatabaseResource) WithMergeRoute(path string, method string, handler string, priority float64) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["path"] = serializeValue(path)
	reqArgs["method"] = serializeValue(method)
	reqArgs["handler"] = serializeValue(handler)
	reqArgs["priority"] = serializeValue(priority)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRoute", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeRouteMiddleware configures a route with middleware
func (s *testDatabaseResource) WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["path"] = serializeValue(path)
	reqArgs["method"] = serializeValue(method)
	reqArgs["handler"] = serializeValue(handler)
	reqArgs["priority"] = serializeValue(priority)
	reqArgs["middleware"] = serializeValue(middleware)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRouteMiddleware", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithModifiedAt sets the modified timestamp
func (s *testDatabaseResource) WithModifiedAt(modifiedAt string) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["modifiedAt"] = serializeValue(modifiedAt)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withModifiedAt", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithNestedConfig configures with nested DTO
func (s *testDatabaseResource) WithNestedConfig(config *TestNestedDto) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if config != nil { reqArgs["config"] = serializeValue(config) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withNestedConfig", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithOptionalCallback configures with optional callback
func (s *testDatabaseResource) WithOptionalCallback(options ...*WithOptionalCallbackOptions) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithOptionalCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.Callback != nil {
			cb := merged.Callback
			shim := func(args ...any) any {
				cb(callbackArg[TestCallbackContext](args, 0))
				return nil
			}
			reqArgs["callback"] = s.client.registerCallback(shim)
		}
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithOptionalString adds an optional string parameter
func (s *testDatabaseResource) WithOptionalString(options ...*WithOptionalStringOptions) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithOptionalStringOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalString", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithStatus sets the resource status
func (s *testDatabaseResource) WithStatus(status TestResourceStatus) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["status"] = serializeValue(status)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withStatus", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUnionDependency adds a dependency from a string or another resource
// Allowed types for parameter dependency: string, ResourceWithConnectionString.
func (s *testDatabaseResource) WithUnionDependency(dependency any) TestDatabaseResource {
	if s.err != nil { return s }
	switch dependency.(type) {
	case string, ResourceWithConnectionString:
	default:
		err := fmt.Errorf("aspire: WithUnionDependency: parameter %q must be one of [string, ResourceWithConnectionString], got %T", "dependency", dependency)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if dependency != nil { reqArgs["dependency"] = serializeValue(dependency) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withUnionDependency", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithValidator adds validation callback
func (s *testDatabaseResource) WithValidator(validator func(arg TestResourceContext) bool) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if validator != nil {
		cb := validator
		shim := func(args ...any) any {
			return cb(callbackArg[TestResourceContext](args, 0))
		}
		reqArgs["validator"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withValidator", reqArgs); err != nil { s.setErr(err) }
	return s
}

// TestEnvironmentContext is the public interface for handle type TestEnvironmentContext.
type TestEnvironmentContext interface {
	handleReference
	Description() (string, error)
	Name() (string, error)
	Priority() (float64, error)
	SetDescription(value string) TestEnvironmentContext
	SetName(value string) TestEnvironmentContext
	SetPriority(value float64) TestEnvironmentContext
	Err() error
}

// testEnvironmentContext is the unexported impl of TestEnvironmentContext.
type testEnvironmentContext struct {
	*resourceBuilderBase
}

// newTestEnvironmentContextFromHandle wraps an existing handle as TestEnvironmentContext.
func newTestEnvironmentContextFromHandle(h *handle, c *client) TestEnvironmentContext {
	return &testEnvironmentContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Description gets the Description property
func (s *testEnvironmentContext) Description() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.description", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// Name gets the Name property
func (s *testEnvironmentContext) Name() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.name", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// Priority gets the Priority property
func (s *testEnvironmentContext) Priority() (float64, error) {
	if s.err != nil { var zero float64; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.priority", reqArgs)
	if err != nil {
		var zero float64
		return zero, err
	}
	return decodeAs[float64](result)
}

// SetDescription sets the Description property
func (s *testEnvironmentContext) SetDescription(value string) TestEnvironmentContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setDescription", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetName sets the Name property
func (s *testEnvironmentContext) SetName(value string) TestEnvironmentContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetPriority sets the Priority property
func (s *testEnvironmentContext) SetPriority(value float64) TestEnvironmentContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setPriority", reqArgs); err != nil { s.setErr(err) }
	return s
}

// TestMutableCollectionContext is the public interface for handle type TestMutableCollectionContext.
type TestMutableCollectionContext interface {
	handleReference
	SetCounts(value *Dict[string, float64]) TestMutableCollectionContext
	SetTags(value *List[string]) TestMutableCollectionContext
	Counts() *Dict[string, float64]
	Tags() *List[string]
	Err() error
}

// testMutableCollectionContext is the unexported impl of TestMutableCollectionContext.
type testMutableCollectionContext struct {
	*resourceBuilderBase
	counts *Dict[string, float64]
	tags *List[string]
}

// newTestMutableCollectionContextFromHandle wraps an existing handle as TestMutableCollectionContext.
func newTestMutableCollectionContextFromHandle(h *handle, c *client) TestMutableCollectionContext {
	return &testMutableCollectionContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Counts gets the Counts property
func (s *testMutableCollectionContext) Counts() *Dict[string, float64] {
	if s.counts == nil {
		s.counts = newDictWithGetter[string, float64](s.handleWrapperBase, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestMutableCollectionContext.counts")
	}
	return s.counts
}

// SetCounts sets the Counts property
func (s *testMutableCollectionContext) SetCounts(value *Dict[string, float64]) TestMutableCollectionContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	if value != nil { reqArgs["value"] = serializeValue(value) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestMutableCollectionContext.setCounts", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetTags sets the Tags property
func (s *testMutableCollectionContext) SetTags(value *List[string]) TestMutableCollectionContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	if value != nil { reqArgs["value"] = serializeValue(value) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestMutableCollectionContext.setTags", reqArgs); err != nil { s.setErr(err) }
	return s
}

// Tags gets the Tags property
func (s *testMutableCollectionContext) Tags() *List[string] {
	if s.tags == nil {
		s.tags = newListWithGetter[string](s.handleWrapperBase, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestMutableCollectionContext.tags")
	}
	return s.tags
}

// TestRedisResource is the public interface for handle type TestRedisResource.
type TestRedisResource interface {
	handleReference
	AddTestChildDatabase(name string, options ...*AddTestChildDatabaseOptions) TestDatabaseResource
	GetEndpoints() ([]string, error)
	GetStatusAsync(options ...*GetStatusAsyncOptions) (string, error)
	TestWaitFor(dependency Resource) TestRedisResource
	TestWithEnvironmentCallback(callback func(arg TestEnvironmentContext)) TestRedisResource
	WaitForReadyAsync(timeout float64, options ...*WaitForReadyAsyncOptions) (bool, error)
	WithCancellableOperation(operation func(arg *CancellationToken)) TestRedisResource
	WithConfig(config *TestConfigDto) TestRedisResource
	WithConnectionString(connectionString *ReferenceExpression) TestRedisResource
	WithConnectionStringDirect(connectionString string) TestRedisResource
	WithCorrelationId(correlationId string) TestRedisResource
	WithCreatedAt(createdAt string) TestRedisResource
	WithDataVolume(options ...*TestDatabaseResourceWithDataVolumeOptions) TestRedisResource
	WithDependency(dependency ResourceWithConnectionString) TestRedisResource
	WithEndpoints(endpoints []string) TestRedisResource
	WithEnvironmentVariables(variables map[string]string) TestRedisResource
	WithMergeEndpoint(endpointName string, port float64) TestRedisResource
	WithMergeEndpointScheme(endpointName string, port float64, scheme string) TestRedisResource
	WithMergeLabel(label string) TestRedisResource
	WithMergeLabelCategorized(label string, category string) TestRedisResource
	WithMergeLogging(logLevel string, options ...*WithMergeLoggingOptions) TestRedisResource
	WithMergeLoggingPath(logLevel string, logPath string, options ...*WithMergeLoggingPathOptions) TestRedisResource
	WithMergeRoute(path string, method string, handler string, priority float64) TestRedisResource
	WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) TestRedisResource
	WithModifiedAt(modifiedAt string) TestRedisResource
	WithMultiParamHandleCallback(callback func(arg1 TestCallbackContext, arg2 TestEnvironmentContext)) TestRedisResource
	WithNestedConfig(config *TestNestedDto) TestRedisResource
	WithOptionalCallback(options ...*WithOptionalCallbackOptions) TestRedisResource
	WithOptionalString(options ...*WithOptionalStringOptions) TestRedisResource
	WithPersistence(options ...*WithPersistenceOptions) TestRedisResource
	WithRedisSpecific(option string) TestRedisResource
	WithStatus(status TestResourceStatus) TestRedisResource
	WithUnionDependency(dependency any) TestRedisResource
	WithValidator(validator func(arg TestResourceContext) bool) TestRedisResource
	GetMetadata() *Dict[string, string]
	GetTags() *List[string]
	Err() error
}

// testRedisResource is the unexported impl of TestRedisResource.
type testRedisResource struct {
	*resourceBuilderBase
	getMetadata *Dict[string, string]
	getTags *List[string]
}

// newTestRedisResourceFromHandle wraps an existing handle as TestRedisResource.
func newTestRedisResourceFromHandle(h *handle, c *client) TestRedisResource {
	return &testRedisResource{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// AddTestChildDatabase adds a child database to a test Redis resource
func (s *testRedisResource) AddTestChildDatabase(name string, options ...*AddTestChildDatabaseOptions) TestDatabaseResource {
	if s.err != nil { return &testDatabaseResource{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	if len(options) > 0 {
		merged := &AddTestChildDatabaseOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/addTestChildDatabase", reqArgs)
	if err != nil {
		return &testDatabaseResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Aspire.Hosting.CodeGeneration.Go.Tests/addTestChildDatabase returned unexpected type %T", result)
		return &testDatabaseResource{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &testDatabaseResource{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// GetEndpoints gets the endpoints
func (s *testRedisResource) GetEndpoints() ([]string, error) {
	if s.err != nil { var zero []string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/getEndpoints", reqArgs)
	if err != nil {
		var zero []string
		return zero, err
	}
	return decodeAs[[]string](result)
}

// GetMetadata gets the metadata for the resource
func (s *testRedisResource) GetMetadata() *Dict[string, string] {
	if s.getMetadata == nil {
		s.getMetadata = newDictWithGetter[string, string](s.handleWrapperBase, "Aspire.Hosting.CodeGeneration.Go.Tests/getMetadata")
	}
	return s.getMetadata
}

// GetStatusAsync gets the status of the resource asynchronously
func (s *testRedisResource) GetStatusAsync(options ...*GetStatusAsyncOptions) (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &GetStatusAsyncOptions{}
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
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/getStatusAsync", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// GetTags gets the tags for the resource
func (s *testRedisResource) GetTags() *List[string] {
	if s.getTags == nil {
		s.getTags = newListWithGetter[string](s.handleWrapperBase, "Aspire.Hosting.CodeGeneration.Go.Tests/getTags")
	}
	return s.getTags
}

// TestWaitFor waits for another resource (test version)
func (s *testRedisResource) TestWaitFor(dependency Resource) TestRedisResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/testWaitFor", reqArgs); err != nil { s.setErr(err) }
	return s
}

// TestWithEnvironmentCallback configures environment with callback (test version)
func (s *testRedisResource) TestWithEnvironmentCallback(callback func(arg TestEnvironmentContext)) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[TestEnvironmentContext](args, 0))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/testWithEnvironmentCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WaitForReadyAsync waits for the resource to be ready
func (s *testRedisResource) WaitForReadyAsync(timeout float64, options ...*WaitForReadyAsyncOptions) (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["timeout"] = serializeValue(timeout)
	if len(options) > 0 {
		merged := &WaitForReadyAsyncOptions{}
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
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/waitForReadyAsync", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// WithCancellableOperation performs a cancellable operation
func (s *testRedisResource) WithCancellableOperation(operation func(arg *CancellationToken)) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if operation != nil {
		cb := operation
		shim := func(args ...any) any {
			cb(callbackArg[*CancellationToken](args, 0))
			return nil
		}
		reqArgs["operation"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withCancellableOperation", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithConfig configures the resource with a DTO
func (s *testRedisResource) WithConfig(config *TestConfigDto) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if config != nil { reqArgs["config"] = serializeValue(config) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withConfig", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithConnectionString sets the connection string using a reference expression
func (s *testRedisResource) WithConnectionString(connectionString *ReferenceExpression) TestRedisResource {
	if s.err != nil { return s }
	if connectionString != nil { if err := connectionString.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if connectionString != nil { reqArgs["connectionString"] = serializeValue(connectionString) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withConnectionString", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithConnectionStringDirect sets connection string using direct interface target
func (s *testRedisResource) WithConnectionStringDirect(connectionString string) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["connectionString"] = serializeValue(connectionString)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withConnectionStringDirect", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCorrelationId sets the correlation ID
func (s *testRedisResource) WithCorrelationId(correlationId string) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["correlationId"] = serializeValue(correlationId)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withCorrelationId", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithCreatedAt sets the created timestamp
func (s *testRedisResource) WithCreatedAt(createdAt string) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["createdAt"] = serializeValue(createdAt)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withCreatedAt", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDataVolume adds a data volume with persistence
func (s *testRedisResource) WithDataVolume(options ...*TestDatabaseResourceWithDataVolumeOptions) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &TestDatabaseResourceWithDataVolumeOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withDataVolume", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithDependency adds a dependency on another resource
func (s *testRedisResource) WithDependency(dependency ResourceWithConnectionString) TestRedisResource {
	if s.err != nil { return s }
	if dependency != nil { if err := dependency.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["dependency"] = serializeValue(dependency)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withDependency", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEndpoints sets the endpoints
func (s *testRedisResource) WithEndpoints(endpoints []string) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if endpoints != nil { reqArgs["endpoints"] = serializeValue(endpoints) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withEndpoints", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithEnvironmentVariables sets environment variables
func (s *testRedisResource) WithEnvironmentVariables(variables map[string]string) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if variables != nil { reqArgs["variables"] = serializeValue(variables) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withEnvironmentVariables", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeEndpoint configures a named endpoint
func (s *testRedisResource) WithMergeEndpoint(endpointName string, port float64) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	reqArgs["port"] = serializeValue(port)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpoint", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeEndpointScheme configures a named endpoint with scheme
func (s *testRedisResource) WithMergeEndpointScheme(endpointName string, port float64, scheme string) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["endpointName"] = serializeValue(endpointName)
	reqArgs["port"] = serializeValue(port)
	reqArgs["scheme"] = serializeValue(scheme)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeEndpointScheme", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeLabel adds a label to the resource
func (s *testRedisResource) WithMergeLabel(label string) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["label"] = serializeValue(label)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabel", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeLabelCategorized adds a categorized label to the resource
func (s *testRedisResource) WithMergeLabelCategorized(label string, category string) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["label"] = serializeValue(label)
	reqArgs["category"] = serializeValue(category)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLabelCategorized", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeLogging configures resource logging
func (s *testRedisResource) WithMergeLogging(logLevel string, options ...*WithMergeLoggingOptions) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["logLevel"] = serializeValue(logLevel)
	if len(options) > 0 {
		merged := &WithMergeLoggingOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLogging", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeLoggingPath configures resource logging with file path
func (s *testRedisResource) WithMergeLoggingPath(logLevel string, logPath string, options ...*WithMergeLoggingPathOptions) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["logLevel"] = serializeValue(logLevel)
	reqArgs["logPath"] = serializeValue(logPath)
	if len(options) > 0 {
		merged := &WithMergeLoggingPathOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeLoggingPath", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeRoute configures a route
func (s *testRedisResource) WithMergeRoute(path string, method string, handler string, priority float64) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["path"] = serializeValue(path)
	reqArgs["method"] = serializeValue(method)
	reqArgs["handler"] = serializeValue(handler)
	reqArgs["priority"] = serializeValue(priority)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRoute", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMergeRouteMiddleware configures a route with middleware
func (s *testRedisResource) WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["path"] = serializeValue(path)
	reqArgs["method"] = serializeValue(method)
	reqArgs["handler"] = serializeValue(handler)
	reqArgs["priority"] = serializeValue(priority)
	reqArgs["middleware"] = serializeValue(middleware)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMergeRouteMiddleware", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithModifiedAt sets the modified timestamp
func (s *testRedisResource) WithModifiedAt(modifiedAt string) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["modifiedAt"] = serializeValue(modifiedAt)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withModifiedAt", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMultiParamHandleCallback tests multi-param callback destructuring
func (s *testRedisResource) WithMultiParamHandleCallback(callback func(arg1 TestCallbackContext, arg2 TestEnvironmentContext)) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackArg[TestCallbackContext](args, 0), callbackArg[TestEnvironmentContext](args, 1))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMultiParamHandleCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithNestedConfig configures with nested DTO
func (s *testRedisResource) WithNestedConfig(config *TestNestedDto) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if config != nil { reqArgs["config"] = serializeValue(config) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withNestedConfig", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithOptionalCallback configures with optional callback
func (s *testRedisResource) WithOptionalCallback(options ...*WithOptionalCallbackOptions) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithOptionalCallbackOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.Callback != nil {
			cb := merged.Callback
			shim := func(args ...any) any {
				cb(callbackArg[TestCallbackContext](args, 0))
				return nil
			}
			reqArgs["callback"] = s.client.registerCallback(shim)
		}
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithOptionalString adds an optional string parameter
func (s *testRedisResource) WithOptionalString(options ...*WithOptionalStringOptions) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithOptionalStringOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withOptionalString", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithPersistence configures the Redis resource with persistence
func (s *testRedisResource) WithPersistence(options ...*WithPersistenceOptions) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithPersistenceOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withPersistence", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithRedisSpecific redis-specific configuration
func (s *testRedisResource) WithRedisSpecific(option string) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["option"] = serializeValue(option)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withRedisSpecific", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithStatus sets the resource status
func (s *testRedisResource) WithStatus(status TestResourceStatus) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["status"] = serializeValue(status)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withStatus", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithUnionDependency adds a dependency from a string or another resource
// Allowed types for parameter dependency: string, ResourceWithConnectionString.
func (s *testRedisResource) WithUnionDependency(dependency any) TestRedisResource {
	if s.err != nil { return s }
	switch dependency.(type) {
	case string, ResourceWithConnectionString:
	default:
		err := fmt.Errorf("aspire: WithUnionDependency: parameter %q must be one of [string, ResourceWithConnectionString], got %T", "dependency", dependency)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if dependency != nil { reqArgs["dependency"] = serializeValue(dependency) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withUnionDependency", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithValidator adds validation callback
func (s *testRedisResource) WithValidator(validator func(arg TestResourceContext) bool) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if validator != nil {
		cb := validator
		shim := func(args ...any) any {
			return cb(callbackArg[TestResourceContext](args, 0))
		}
		reqArgs["validator"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withValidator", reqArgs); err != nil { s.setErr(err) }
	return s
}

// TestResourceContext is the public interface for handle type TestResourceContext.
type TestResourceContext interface {
	handleReference
	GetValueAsync() (string, error)
	Name() (string, error)
	SetName(value string) TestResourceContext
	SetValue(value float64) TestResourceContext
	SetValueAsync(value string) error
	ValidateAsync() (bool, error)
	Value() (float64, error)
	Err() error
}

// testResourceContext is the unexported impl of TestResourceContext.
type testResourceContext struct {
	*resourceBuilderBase
}

// newTestResourceContextFromHandle wraps an existing handle as TestResourceContext.
func newTestResourceContextFromHandle(h *handle, c *client) TestResourceContext {
	return &testResourceContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// GetValueAsync invokes the GetValueAsync method
func (s *testResourceContext) GetValueAsync() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.getValueAsync", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// Name gets the Name property
func (s *testResourceContext) Name() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.name", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// SetName sets the Name property
func (s *testResourceContext) SetName(value string) TestResourceContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setName", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetValue sets the Value property
func (s *testResourceContext) SetValue(value float64) TestResourceContext {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setValue", reqArgs); err != nil { s.setErr(err) }
	return s
}

// SetValueAsync invokes the SetValueAsync method
func (s *testResourceContext) SetValueAsync(value string) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	_, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setValueAsync", reqArgs)
	return err
}

// ValidateAsync invokes the ValidateAsync method
func (s *testResourceContext) ValidateAsync() (bool, error) {
	if s.err != nil { var zero bool; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.validateAsync", reqArgs)
	if err != nil {
		var zero bool
		return zero, err
	}
	return decodeAs[bool](result)
}

// Value gets the Value property
func (s *testResourceContext) Value() (float64, error) {
	if s.err != nil { var zero float64; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.value", reqArgs)
	if err != nil {
		var zero float64
		return zero, err
	}
	return decodeAs[float64](result)
}

// ============================================================================
// Options structs
// ============================================================================

// AddTestRedisOptions carries optional parameters for AddTestRedis.
type AddTestRedisOptions struct {
	Port *float64 `json:"port,omitempty"`
}

func (o *AddTestRedisOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Port != nil { m["port"] = serializeValue(o.Port) }
	return m
}

// AddTestChildDatabaseOptions carries optional parameters for AddTestChildDatabase.
type AddTestChildDatabaseOptions struct {
	DatabaseName *string `json:"databaseName,omitempty"`
}

func (o *AddTestChildDatabaseOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.DatabaseName != nil { m["databaseName"] = serializeValue(o.DatabaseName) }
	return m
}

// WithPersistenceOptions carries optional parameters for WithPersistence.
type WithPersistenceOptions struct {
	Mode *TestPersistenceMode `json:"mode,omitempty"`
}

func (o *WithPersistenceOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Mode != nil { m["mode"] = serializeValue(o.Mode) }
	return m
}

// WithOptionalStringOptions carries optional parameters for WithOptionalString.
type WithOptionalStringOptions struct {
	Value *string `json:"value,omitempty"`
	Enabled *bool `json:"enabled,omitempty"`
}

func (o *WithOptionalStringOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Value != nil { m["value"] = serializeValue(o.Value) }
	if o.Enabled != nil { m["enabled"] = serializeValue(o.Enabled) }
	return m
}

// WithOptionalCallbackOptions carries optional parameters for WithOptionalCallback.
type WithOptionalCallbackOptions struct {
	Callback func(arg TestCallbackContext) `json:"-"`
}

func (o *WithOptionalCallbackOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	return m
}

// GetStatusAsyncOptions carries optional parameters for GetStatusAsync.
type GetStatusAsyncOptions struct {
	CancellationToken *CancellationToken `json:"-"`
}

func (o *GetStatusAsyncOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	return m
}

// WaitForReadyAsyncOptions carries optional parameters for WaitForReadyAsync.
type WaitForReadyAsyncOptions struct {
	CancellationToken *CancellationToken `json:"-"`
}

func (o *WaitForReadyAsyncOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	return m
}

// TestDatabaseResourceWithDataVolumeOptions carries optional parameters for WithDataVolume.
type TestDatabaseResourceWithDataVolumeOptions struct {
	Name *string `json:"name,omitempty"`
	IsReadOnly *bool `json:"isReadOnly,omitempty"`
}

func (o *TestDatabaseResourceWithDataVolumeOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Name != nil { m["name"] = serializeValue(o.Name) }
	if o.IsReadOnly != nil { m["isReadOnly"] = serializeValue(o.IsReadOnly) }
	return m
}

// WithMergeLoggingOptions carries optional parameters for WithMergeLogging.
type WithMergeLoggingOptions struct {
	EnableConsole *bool `json:"enableConsole,omitempty"`
	MaxFiles *float64 `json:"maxFiles,omitempty"`
}

func (o *WithMergeLoggingOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.EnableConsole != nil { m["enableConsole"] = serializeValue(o.EnableConsole) }
	if o.MaxFiles != nil { m["maxFiles"] = serializeValue(o.MaxFiles) }
	return m
}

// WithMergeLoggingPathOptions carries optional parameters for WithMergeLoggingPath.
type WithMergeLoggingPathOptions struct {
	EnableConsole *bool `json:"enableConsole,omitempty"`
	MaxFiles *float64 `json:"maxFiles,omitempty"`
}

func (o *WithMergeLoggingPathOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.EnableConsole != nil { m["enableConsole"] = serializeValue(o.EnableConsole) }
	if o.MaxFiles != nil { m["maxFiles"] = serializeValue(o.MaxFiles) }
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

// ============================================================================
// Per-client handle wrapper registration
// ============================================================================

func registerWrappers(c *client) {
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpression", func(h *handle, c *client) any {
		return newHandleBackedReferenceExpression(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestVaultResource", func(h *handle, c *client) any {
		return newAspire_Hosting_CodeGeneration_Go_TestsTestVaultResourceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder", func(h *handle, c *client) any {
		return newIDistributedApplicationBuilderFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithEnvironment", func(h *handle, c *client) any {
		return newIResourceWithEnvironmentFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCallbackContext", func(h *handle, c *client) any {
		return newTestCallbackContextFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCollectionContext", func(h *handle, c *client) any {
		return newTestCollectionContextFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestDatabaseResource", func(h *handle, c *client) any {
		return newTestDatabaseResourceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestEnvironmentContext", func(h *handle, c *client) any {
		return newTestEnvironmentContextFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestMutableCollectionContext", func(h *handle, c *client) any {
		return newTestMutableCollectionContextFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestRedisResource", func(h *handle, c *client) any {
		return newTestRedisResourceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestResourceContext", func(h *handle, c *client) any {
		return newTestResourceContextFromHandle(h, c)
	})
}

// ============================================================================
// Builder construction & Build()
// ============================================================================

// DistributedApplication is returned by Build(); it represents the running application.
type DistributedApplication interface { handleReference }

type distributedApplication struct { *resourceBuilderBase }

// Build invokes the build capability and returns the running application.
func (b *iDistributedApplicationBuilder) Build() (DistributedApplication, error) {
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
func CreateBuilder() (IDistributedApplicationBuilder, error) {
	socketPath := os.Getenv("REMOTE_APP_HOST_SOCKET_PATH")
	if socketPath == "" {
		return nil, fmt.Errorf("REMOTE_APP_HOST_SOCKET_PATH environment variable not set. Run this application using `aspire run`")
	}
	c := newClient(socketPath)
	if err := c.connect(context.Background(), 5*time.Second); err != nil { return nil, err }
	c.onDisconnect(func() { os.Exit(1) })
	registerWrappers(c)

	resolved := map[string]any{}
	if _, ok := resolved["Args"]; !ok { resolved["Args"] = os.Args[1:] }
	if _, ok := resolved["ProjectDirectory"]; !ok {
		if pwd, err := os.Getwd(); err == nil { resolved["ProjectDirectory"] = pwd }
	}

	result, err := c.invokeCapability(context.Background(), "Aspire.Hosting/createBuilder", map[string]any{"argsOrOptions": resolved})
	if err != nil { return nil, err }
	href, ok := result.(handleReference)
	if !ok { return nil, fmt.Errorf("aspire: createBuilder returned unexpected type %T", result) }
	return &iDistributedApplicationBuilder{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), c)}, nil
}

