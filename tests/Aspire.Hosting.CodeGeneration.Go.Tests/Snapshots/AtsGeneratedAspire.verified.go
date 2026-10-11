// ===== aspire.go =====
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
    "strings"
    "time"
)

// Compile-time references to keep imports used in minimal SDKs.
var _ = context.Background
var _ = fmt.Errorf
var _ = os.Getenv
var _ = strings.EqualFold
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
	OptionalField *string `json:"OptionalField,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *TestConfigDto) ToMap() map[string]any {
	m := map[string]any{}
	m["Name"] = serializeValue(d.Name)
	m["Port"] = serializeValue(d.Port)
	m["Enabled"] = serializeValue(d.Enabled)
	if d.OptionalField != nil { m["OptionalField"] = serializeValue(d.OptionalField) }
	return m
}

// TestNestedDto represents TestNestedDto.
type TestNestedDto struct {
	Id string `json:"Id,omitempty"`
	Config *TestConfigDto `json:"Config,omitempty"`
	Tags []string `json:"Tags,omitempty"`
	Counts map[string]float64 `json:"Counts,omitempty"`
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
	NestedData map[string][]*TestConfigDto `json:"NestedData,omitempty"`
	MetadataArray []map[string]string `json:"MetadataArray,omitempty"`
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
	Default: &TestConfigDto{Name: "default", Port: 6379, Enabled: true, OptionalField: func(value string) *string { return &value }("cache")},
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

// TestMutablePromiseCollisionResourcePromise marks types implementing ITestMutablePromiseCollisionResourcePromise.
// Marker interface.
type TestMutablePromiseCollisionResourcePromise interface {
	handleReference
}

// TestPromiseCollisionResource marks types implementing ITestPromiseCollisionResource.
// Marker interface.
type TestPromiseCollisionResource interface {
	handleReference
}

// TestPromiseCollisionResourcePromise marks types implementing ITestPromiseCollisionResourcePromise.
// Marker interface.
type TestPromiseCollisionResourcePromise interface {
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
	if !isNil(dependency) { reqArgs["dependency"] = serializeValue(dependency) }
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
	AddTestVault(name string) TestVaultResource
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

// AddTestRedis adds a test Redis resource from ATS documentation.
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
func (s *iDistributedApplicationBuilder) AddTestVault(name string) TestVaultResource {
	if s.err != nil { return nil }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["name"] = serializeValue(name)
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/addTestVault", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(TestVaultResource)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Aspire.Hosting.CodeGeneration.Go.Tests/addTestVault returned unexpected type %T", result))
		return nil
	}
	return typed
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
	Name() (*string, error)
	SetCancellationToken(options ...*SetCancellationTokenOptions) TestCallbackContext
	SetName(value *string) TestCallbackContext
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

// CancellationToken cancellationToken is supported by ATS.
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
func (s *testCallbackContext) Name() (*string, error) {
	if s.err != nil { var zero *string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.name", reqArgs)
	if err != nil {
		var zero *string
		return zero, err
	}
	return decodeAs[*string](result)
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
func (s *testCallbackContext) SetName(value *string) TestCallbackContext {
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

// Items list property - should generate AspireList getter like Dictionary properties.
func (s *testCollectionContext) Items() *List[string] {
	if s.items == nil {
		s.items = newListWithGetter[string](s.handleWrapperBase, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.items")
	}
	return s.items
}

// Metadata dictionary property - already works with AspireDict getter.
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
	if !isNil(dependency) { reqArgs["dependency"] = serializeValue(dependency) }
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
	Description() (*string, error)
	Name() (string, error)
	Priority() (float64, error)
	SetDescription(value *string) TestEnvironmentContext
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
func (s *testEnvironmentContext) Description() (*string, error) {
	if s.err != nil { var zero *string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.description", reqArgs)
	if err != nil {
		var zero *string
		return zero, err
	}
	return decodeAs[*string](result)
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
func (s *testEnvironmentContext) SetDescription(value *string) TestEnvironmentContext {
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

// TestMutablePromiseCollisionResource is the public interface for handle type TestMutablePromiseCollisionResource.
type TestMutablePromiseCollisionResource interface {
	handleReference
	SetValue(value string) TestMutablePromiseCollisionResource
	Value() (string, error)
	Err() error
}

// testMutablePromiseCollisionResource is the unexported impl of TestMutablePromiseCollisionResource.
type testMutablePromiseCollisionResource struct {
	*resourceBuilderBase
}

// newTestMutablePromiseCollisionResourceFromHandle wraps an existing handle as TestMutablePromiseCollisionResource.
func newTestMutablePromiseCollisionResourceFromHandle(h *handle, c *client) TestMutablePromiseCollisionResource {
	return &testMutablePromiseCollisionResource{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// SetValue sets the Value property
func (s *testMutablePromiseCollisionResource) SetValue(value string) TestMutablePromiseCollisionResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/ITestMutablePromiseCollisionResource.setValue", reqArgs); err != nil { s.setErr(err) }
	return s
}

// Value gets or sets the test value.
func (s *testMutablePromiseCollisionResource) Value() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/ITestMutablePromiseCollisionResource.value", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
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
	WithConcreteVaultResource(resource Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) TestRedisResource
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
	WithMutablePromiseCollisionResources(resource TestMutablePromiseCollisionResource, resourcePromise TestMutablePromiseCollisionResourcePromise) TestRedisResource
	WithNestedConfig(config *TestNestedDto) TestRedisResource
	WithOptionalCallback(options ...*WithOptionalCallbackOptions) TestRedisResource
	WithOptionalString(options ...*WithOptionalStringOptions) TestRedisResource
	WithPersistence(options ...*WithPersistenceOptions) TestRedisResource
	WithPromiseCollisionResources(resource TestPromiseCollisionResource, resourcePromise TestPromiseCollisionResourcePromise) TestRedisResource
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

// WithConcreteVaultResource configures a Redis resource with the concrete vault resource as a parameter.
func (s *testRedisResource) WithConcreteVaultResource(resource Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) TestRedisResource {
	if s.err != nil { return s }
	if resource != nil { if err := resource.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["resource"] = serializeValue(resource)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withConcreteVaultResource", reqArgs); err != nil { s.setErr(err) }
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

// WithMutablePromiseCollisionResources configures a Redis resource with mutable-property and parameter-only resources whose generated names collide.
func (s *testRedisResource) WithMutablePromiseCollisionResources(resource TestMutablePromiseCollisionResource, resourcePromise TestMutablePromiseCollisionResourcePromise) TestRedisResource {
	if s.err != nil { return s }
	if resource != nil { if err := resource.Err(); err != nil { s.setErr(err); return s } }
	if resourcePromise != nil { if err := resourcePromise.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["resource"] = serializeValue(resource)
	reqArgs["resourcePromise"] = serializeValue(resourcePromise)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMutablePromiseCollisionResources", reqArgs); err != nil { s.setErr(err) }
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

// WithPromiseCollisionResources configures a Redis resource with parameter-only resources whose generated names collide.
func (s *testRedisResource) WithPromiseCollisionResources(resource TestPromiseCollisionResource, resourcePromise TestPromiseCollisionResourcePromise) TestRedisResource {
	if s.err != nil { return s }
	if resource != nil { if err := resource.Err(); err != nil { s.setErr(err); return s } }
	if resourcePromise != nil { if err := resourcePromise.Err(); err != nil { s.setErr(err); return s } }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["resource"] = serializeValue(resource)
	reqArgs["resourcePromise"] = serializeValue(resourcePromise)
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withPromiseCollisionResources", reqArgs); err != nil { s.setErr(err) }
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
	if !isNil(dependency) { reqArgs["dependency"] = serializeValue(dependency) }
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

// GetValueAsync instance method that should be exposed as async method.
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

// SetValueAsync instance method with parameter.
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

// ValidateAsync instance method with return type.
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

// TestReturnValueContext is the public interface for handle type TestReturnValueContext.
type TestReturnValueContext interface {
	handleReference
	GetInt() (float64, error)
	GetIntTaskAsync() (float64, error)
	GetIntValueTaskAsync() (float64, error)
	GetNullableInt() (*float64, error)
	GetNullableIntTaskAsync() (*float64, error)
	GetNullableIntValueTaskAsync() (*float64, error)
	GetNullableString() (*string, error)
	GetNullableStringTaskAsync() (*string, error)
	GetNullableStringValueTaskAsync() (*string, error)
	GetString() (string, error)
	GetStringTaskAsync() (string, error)
	GetStringValueTaskAsync() (string, error)
	Err() error
}

// testReturnValueContext is the unexported impl of TestReturnValueContext.
type testReturnValueContext struct {
	*resourceBuilderBase
}

// newTestReturnValueContextFromHandle wraps an existing handle as TestReturnValueContext.
func newTestReturnValueContextFromHandle(h *handle, c *client) TestReturnValueContext {
	return &testReturnValueContext{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// GetInt invokes the GetInt method
func (s *testReturnValueContext) GetInt() (float64, error) {
	if s.err != nil { var zero float64; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestReturnValueContext.getInt", reqArgs)
	if err != nil {
		var zero float64
		return zero, err
	}
	return decodeAs[float64](result)
}

// GetIntTaskAsync invokes the GetIntTaskAsync method
func (s *testReturnValueContext) GetIntTaskAsync() (float64, error) {
	if s.err != nil { var zero float64; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestReturnValueContext.getIntTaskAsync", reqArgs)
	if err != nil {
		var zero float64
		return zero, err
	}
	return decodeAs[float64](result)
}

// GetIntValueTaskAsync invokes the GetIntValueTaskAsync method
func (s *testReturnValueContext) GetIntValueTaskAsync() (float64, error) {
	if s.err != nil { var zero float64; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestReturnValueContext.getIntValueTaskAsync", reqArgs)
	if err != nil {
		var zero float64
		return zero, err
	}
	return decodeAs[float64](result)
}

// GetNullableInt invokes the GetNullableInt method
func (s *testReturnValueContext) GetNullableInt() (*float64, error) {
	if s.err != nil { var zero *float64; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestReturnValueContext.getNullableInt", reqArgs)
	if err != nil {
		var zero *float64
		return zero, err
	}
	return decodeAs[*float64](result)
}

// GetNullableIntTaskAsync invokes the GetNullableIntTaskAsync method
func (s *testReturnValueContext) GetNullableIntTaskAsync() (*float64, error) {
	if s.err != nil { var zero *float64; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestReturnValueContext.getNullableIntTaskAsync", reqArgs)
	if err != nil {
		var zero *float64
		return zero, err
	}
	return decodeAs[*float64](result)
}

// GetNullableIntValueTaskAsync invokes the GetNullableIntValueTaskAsync method
func (s *testReturnValueContext) GetNullableIntValueTaskAsync() (*float64, error) {
	if s.err != nil { var zero *float64; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestReturnValueContext.getNullableIntValueTaskAsync", reqArgs)
	if err != nil {
		var zero *float64
		return zero, err
	}
	return decodeAs[*float64](result)
}

// GetNullableString invokes the GetNullableString method
func (s *testReturnValueContext) GetNullableString() (*string, error) {
	if s.err != nil { var zero *string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestReturnValueContext.getNullableString", reqArgs)
	if err != nil {
		var zero *string
		return zero, err
	}
	return decodeAs[*string](result)
}

// GetNullableStringTaskAsync invokes the GetNullableStringTaskAsync method
func (s *testReturnValueContext) GetNullableStringTaskAsync() (*string, error) {
	if s.err != nil { var zero *string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestReturnValueContext.getNullableStringTaskAsync", reqArgs)
	if err != nil {
		var zero *string
		return zero, err
	}
	return decodeAs[*string](result)
}

// GetNullableStringValueTaskAsync invokes the GetNullableStringValueTaskAsync method
func (s *testReturnValueContext) GetNullableStringValueTaskAsync() (*string, error) {
	if s.err != nil { var zero *string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestReturnValueContext.getNullableStringValueTaskAsync", reqArgs)
	if err != nil {
		var zero *string
		return zero, err
	}
	return decodeAs[*string](result)
}

// GetString invokes the GetString method
func (s *testReturnValueContext) GetString() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestReturnValueContext.getString", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// GetStringTaskAsync invokes the GetStringTaskAsync method
func (s *testReturnValueContext) GetStringTaskAsync() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestReturnValueContext.getStringTaskAsync", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// GetStringValueTaskAsync invokes the GetStringValueTaskAsync method
func (s *testReturnValueContext) GetStringValueTaskAsync() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestReturnValueContext.getStringValueTaskAsync", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
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
	c.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.ITestMutablePromiseCollisionResource", func(h *handle, c *client) any {
		return newTestMutablePromiseCollisionResourceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestRedisResource", func(h *handle, c *client) any {
		return newTestRedisResourceFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestResourceContext", func(h *handle, c *client) any {
		return newTestResourceContextFromHandle(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestReturnValueContext", func(h *handle, c *client) any {
		return newTestReturnValueContextFromHandle(h, c)
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
	if projectDirectory, ok := resolved["ProjectDirectory"].(string); !ok || projectDirectory == "" {
		if projectDirectory := os.Getenv("ASPIRE_PROJECT_DIRECTORY"); projectDirectory != "" {
			resolved["ProjectDirectory"] = projectDirectory
		} else if pwd, err := os.Getwd(); err == nil {
			resolved["ProjectDirectory"] = pwd
		}
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
	return &iDistributedApplicationBuilder{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), c)}, nil
}


// ===== base.go =====
// Package aspire provides base types and utilities for Aspire Go SDK.
package aspire

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"reflect"
	"strings"
	"sync"
)

// ── handleWrapperBase ─────────────────────────────────────────────────────────
//
// Sequential execution model. Every capability invocation in the generated
// SDK runs synchronously. The wrapper carries:
//   - handle: the server-side handle (nil if construction failed)
//   - err:    the first error encountered along this wrapper's chain
//   - client: the client used for follow-up RPCs
//
// Fluent methods short-circuit when err is non-nil and propagate the existing
// error to subsequent calls. Add* / property getters that return a new child
// pre-populate the child's err if the parent already failed, so chains
// short-circuit cleanly without panicking.
//
// Callers retrieve the chain's error with Err() at any logical boundary
// (typically once at the end of a builder chain or before using the result of
// a value-returning method).

type handleWrapperBase struct {
	handle *handle
	err    error
	client *client
}

func newHandleWrapperBase(h *handle, c *client) *handleWrapperBase {
	return &handleWrapperBase{handle: h, client: c}
}

func newErroredHandleWrapperBase(err error, c *client) *handleWrapperBase {
	return &handleWrapperBase{err: err, client: c}
}

// getHandle implements handleReference. Returns nil if the wrapper failed
// to construct; callers should consult Err() before serializing a wrapper
// with this handle.
func (h *handleWrapperBase) getHandle() *handle { return h.handle }

// getClient returns the client used for follow-up RPCs.
func (h *handleWrapperBase) getClient() *client { return h.client }

// Err returns the first error recorded on this wrapper. Returns nil if the
// wrapper resolved successfully and no chained operation has failed.
func (h *handleWrapperBase) Err() error { return h.err }

// setErr records an error on this wrapper. The first non-nil error wins;
// later errors are dropped to keep failure causality clear in chains.
func (h *handleWrapperBase) setErr(err error) {
	if h.err == nil {
		h.err = err
	}
}

// ── resourceBuilderBase ───────────────────────────────────────────────────────

type resourceBuilderBase struct {
	*handleWrapperBase
}

func newResourceBuilderBase(h *handle, c *client) *resourceBuilderBase {
	return &resourceBuilderBase{handleWrapperBase: newHandleWrapperBase(h, c)}
}

func newErroredResourceBuilder(err error, c *client) *resourceBuilderBase {
	return &resourceBuilderBase{handleWrapperBase: newErroredHandleWrapperBase(err, c)}
}

// ── ReferenceExpression ───────────────────────────────────────────────────────

// ReferenceExpression represents a reference expression that can be passed to capabilities.
// Supports value mode (Format + ValueProviders) and conditional mode (Condition + WhenTrue + WhenFalse).
type ReferenceExpression struct {
	Format         string
	ValueProviders []any

	// Conditional mode fields
	Condition  any
	WhenTrue   *ReferenceExpression
	WhenFalse  *ReferenceExpression
	MatchValue string

	// Handle mode fields (for server-returned expressions)
	handle *handle
	client *client
}

func newHandleBackedReferenceExpression(h *handle, c *client) *ReferenceExpression {
	return &ReferenceExpression{handle: h, client: c}
}

// RefExpr is a Go-idiomatic constructor for value-mode reference expressions.
// The format string uses fmt.Sprintf-style verbs (%v, %s, %d, %f, etc.),
// one per value provider, in order. Each verb is translated to an Aspire {N}
// indexed placeholder before the expression is sent to the server.
// Use %% to emit a literal percent sign (no placeholder consumed).
//
//	Expr("Host=%v;Port=%v", host, port)
//	→ ReferenceExpression{Format: "Host={0};Port={1}", ValueProviders: [host, port]}
func RefExpr(format string, valueProviders ...any) *ReferenceExpression {
	return &ReferenceExpression{Format: fmtToAspireFormat(format), ValueProviders: valueProviders}
}

// fmtToAspireFormat converts a fmt.Sprintf-style format string to the Aspire
// {N} indexed format expected by the server.
func fmtToAspireFormat(format string) string {
	out := make([]byte, 0, len(format))
	n := 0
	i := 0
	for i < len(format) {
		if format[i] != '%' {
			out = append(out, format[i])
			i++
			continue
		}
		i++ // consume '%'
		if i >= len(format) {
			out = append(out, '%')
			break
		}
		if format[i] == '%' {
			out = append(out, '%')
			i++
			continue
		}
		// skip optional flags, width, and precision before the verb letter
		for i < len(format) && !isVerbByte(format[i]) {
			i++
		}
		if i < len(format) {
			out = append(out, fmt.Sprintf("{%d}", n)...)
			n++
			i++ // consume verb letter
		}
	}
	return string(out)
}

func isVerbByte(b byte) bool {
	return (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z')
}

// ConditionalRefExpr is a convenience constructor for conditional reference expressions.
func ConditionalRefExpr(condition any, matchValue string, whenTrue *ReferenceExpression, whenFalse *ReferenceExpression) *ReferenceExpression {
	if matchValue == "" {
		matchValue = "True"
	}
	return &ReferenceExpression{
		Condition:  condition,
		WhenTrue:   whenTrue,
		WhenFalse:  whenFalse,
		MatchValue: matchValue,
	}
}

// getHandle satisfies handleReference for handle-backed expressions.
func (r *ReferenceExpression) getHandle() *handle { return r.handle }

// Err always returns nil for in-memory ReferenceExpression instances. Provided
// to satisfy handleReference; handle-backed expressions originating from the
// server have already been validated by wrapIfHandle when the wrapper is
// returned.
func (r *ReferenceExpression) Err() error { return nil }

// ToJSON returns the reference expression as a JSON-serializable map.
func (r *ReferenceExpression) ToJSON() map[string]any {
	if r.handle != nil {
		h := r.handle.ToJSON()
		return map[string]any{"$handle": h["$handle"], "$type": h["$type"]}
	}
	if r.Condition != nil {
		return map[string]any{
			"$expr": map[string]any{
				"condition":  serializeValue(r.Condition),
				"whenTrue":   r.WhenTrue.ToJSON(),
				"whenFalse":  r.WhenFalse.ToJSON(),
				"matchValue": r.MatchValue,
			},
		}
	}
	return map[string]any{
		"$expr": map[string]any{
			"format":         r.Format,
			"valueProviders": r.ValueProviders,
		},
	}
}

// GetValue resolves the expression to its string value on the server.
// Only available on server-returned ReferenceExpression instances (handle mode).
func (r *ReferenceExpression) GetValue(token *CancellationToken) (string, error) {
	if r.handle == nil || r.client == nil {
		return "", errors.New("aspire: GetValue is only available on server-returned ReferenceExpression instances")
	}

	args := map[string]any{
		"context": r.handle.ToJSON(),
	}
	ctx := context.Background()
	if token != nil {
		ctx = token.Context()
		if id := r.client.registerCancellation(token); id != "" {
			args["cancellationToken"] = id
		}
	}

	result, err := r.client.invokeCapability(ctx, "Aspire.Hosting.ApplicationModel/getValue", args)
	if err != nil {
		return "", err
	}
	if s, ok := result.(string); ok {
		return s, nil
	}
	return "", nil
}

// ── List ──────────────────────────────────────────────────────────────────────
//
// List[T] is a handle-backed list with lazy resolution of the list handle
// itself (the getter capability is only invoked on first access). Mutating
// methods are now synchronous and return errors directly.

type List[T any] struct {
	parent             *handleWrapperBase
	getterCapabilityID string

	handleMu       sync.Mutex
	resolvedHandle *handle
}

func newList[T any](h *handle, c *client) *List[T] {
	return &List[T]{
		parent:         newHandleWrapperBase(h, c),
		resolvedHandle: h,
	}
}

func newListWithGetter[T any](parent *handleWrapperBase, getterCapabilityID string) *List[T] {
	return &List[T]{parent: parent, getterCapabilityID: getterCapabilityID}
}

func (l *List[T]) resolveListHandle(ctx context.Context) (*handle, error) {
	if l.parent.err != nil {
		return nil, l.parent.err
	}
	l.handleMu.Lock()
	cached := l.resolvedHandle
	l.handleMu.Unlock()
	if cached != nil {
		return cached, nil
	}
	if l.getterCapabilityID == "" {
		return l.parent.handle, nil
	}
	result, err := l.parent.client.invokeCapability(ctx, l.getterCapabilityID, map[string]any{
		"context": l.parent.handle.ToJSON(),
	})
	if err != nil {
		return nil, err
	}
	h, ok := result.(handleReference)
	if !ok {
		return nil, fmt.Errorf("aspire: list getter %q returned unexpected type %T", l.getterCapabilityID, result)
	}
	listHandle := h.getHandle()
	l.handleMu.Lock()
	l.resolvedHandle = listHandle
	l.handleMu.Unlock()
	return listHandle, nil
}

// ToJSON returns the list handle as a JSON-serializable map.
func (l *List[T]) ToJSON() map[string]any {
	h, err := l.resolveListHandle(context.Background())
	if err != nil || h == nil {
		return nil
	}
	out := h.ToJSON()
	return map[string]any{"$handle": out["$handle"], "$type": out["$type"]}
}

func (l *List[T]) Count() (int, error) {
	ctx := context.Background()
	h, err := l.resolveListHandle(ctx)
	if err != nil {
		return 0, err
	}
	result, err := l.parent.client.invokeCapability(ctx, "Aspire.Hosting/List.length", map[string]any{
		"list": h.ToJSON(),
	})
	if err != nil {
		return 0, err
	}
	if n, ok := result.(float64); ok {
		return int(n), nil
	}
	return 0, nil
}

func (l *List[T]) Get(index int) (T, error) {
	var zero T
	ctx := context.Background()
	h, err := l.resolveListHandle(ctx)
	if err != nil {
		return zero, err
	}
	result, err := l.parent.client.invokeCapability(ctx, "Aspire.Hosting/List.get", map[string]any{
		"list":  h.ToJSON(),
		"index": index,
	})
	if err != nil {
		return zero, err
	}
	return decodeAs[T](result)
}

func (l *List[T]) ToArray() ([]T, error) {
	ctx := context.Background()
	h, err := l.resolveListHandle(ctx)
	if err != nil {
		return nil, err
	}
	result, err := l.parent.client.invokeCapability(ctx, "Aspire.Hosting/List.toArray", map[string]any{
		"list": h.ToJSON(),
	})
	if err != nil {
		return nil, err
	}
	arr, ok := result.([]any)
	if !ok {
		return nil, nil
	}
	items := make([]T, 0, len(arr))
	for _, raw := range arr {
		v, err := decodeAs[T](raw)
		if err != nil {
			return nil, err
		}
		items = append(items, v)
	}
	return items, nil
}

func (l *List[T]) Add(item T) error {
	ctx := context.Background()
	h, err := l.resolveListHandle(ctx)
	if err != nil {
		return err
	}
	_, err = l.parent.client.invokeCapability(ctx, "Aspire.Hosting/List.add", map[string]any{
		"list": h.ToJSON(),
		"item": serializeValue(item),
	})
	return err
}

func (l *List[T]) RemoveAt(index int) error {
	ctx := context.Background()
	h, err := l.resolveListHandle(ctx)
	if err != nil {
		return err
	}
	_, err = l.parent.client.invokeCapability(ctx, "Aspire.Hosting/List.removeAt", map[string]any{
		"list":  h.ToJSON(),
		"index": index,
	})
	return err
}

func (l *List[T]) Clear() error {
	ctx := context.Background()
	h, err := l.resolveListHandle(ctx)
	if err != nil {
		return err
	}
	_, err = l.parent.client.invokeCapability(ctx, "Aspire.Hosting/List.clear", map[string]any{
		"list": h.ToJSON(),
	})
	return err
}

// ── Dict ──────────────────────────────────────────────────────────────────────

type Dict[K comparable, V any] struct {
	parent             *handleWrapperBase
	getterCapabilityID string

	handleMu       sync.Mutex
	resolvedHandle *handle
}

func newDict[K comparable, V any](h *handle, c *client) *Dict[K, V] {
	return &Dict[K, V]{
		parent:         newHandleWrapperBase(h, c),
		resolvedHandle: h,
	}
}

func newDictWithGetter[K comparable, V any](parent *handleWrapperBase, getterCapabilityID string) *Dict[K, V] {
	return &Dict[K, V]{parent: parent, getterCapabilityID: getterCapabilityID}
}

func (d *Dict[K, V]) resolveDictHandle(ctx context.Context) (*handle, error) {
	if d.parent.err != nil {
		return nil, d.parent.err
	}
	d.handleMu.Lock()
	cached := d.resolvedHandle
	d.handleMu.Unlock()
	if cached != nil {
		return cached, nil
	}
	if d.getterCapabilityID == "" {
		return d.parent.handle, nil
	}
	result, err := d.parent.client.invokeCapability(ctx, d.getterCapabilityID, map[string]any{
		"context": d.parent.handle.ToJSON(),
	})
	if err != nil {
		return nil, err
	}
	h, ok := result.(handleReference)
	if !ok {
		return nil, fmt.Errorf("aspire: dict getter %q returned unexpected type %T", d.getterCapabilityID, result)
	}
	dictHandle := h.getHandle()
	d.handleMu.Lock()
	d.resolvedHandle = dictHandle
	d.handleMu.Unlock()
	return dictHandle, nil
}

func (d *Dict[K, V]) ToJSON() map[string]any {
	h, err := d.resolveDictHandle(context.Background())
	if err != nil || h == nil {
		return nil
	}
	out := h.ToJSON()
	return map[string]any{"$handle": out["$handle"], "$type": out["$type"]}
}

func (d *Dict[K, V]) Count() (int, error) {
	ctx := context.Background()
	h, err := d.resolveDictHandle(ctx)
	if err != nil {
		return 0, err
	}
	result, err := d.parent.client.invokeCapability(ctx, "Aspire.Hosting/Dict.count", map[string]any{
		"dict": h.ToJSON(),
	})
	if err != nil {
		return 0, err
	}
	if n, ok := result.(float64); ok {
		return int(n), nil
	}
	return 0, nil
}

func (d *Dict[K, V]) Get(key K) (V, error) {
	var zero V
	ctx := context.Background()
	h, err := d.resolveDictHandle(ctx)
	if err != nil {
		return zero, err
	}
	result, err := d.parent.client.invokeCapability(ctx, "Aspire.Hosting/Dict.get", map[string]any{
		"dict": h.ToJSON(),
		"key":  serializeValue(key),
	})
	if err != nil {
		return zero, err
	}
	return decodeAs[V](result)
}

func (d *Dict[K, V]) Has(key K) (bool, error) {
	ctx := context.Background()
	h, err := d.resolveDictHandle(ctx)
	if err != nil {
		return false, err
	}
	result, err := d.parent.client.invokeCapability(ctx, "Aspire.Hosting/Dict.has", map[string]any{
		"dict": h.ToJSON(),
		"key":  serializeValue(key),
	})
	if err != nil {
		return false, err
	}
	if b, ok := result.(bool); ok {
		return b, nil
	}
	return false, nil
}

func (d *Dict[K, V]) Keys() ([]K, error) {
	ctx := context.Background()
	h, err := d.resolveDictHandle(ctx)
	if err != nil {
		return nil, err
	}
	result, err := d.parent.client.invokeCapability(ctx, "Aspire.Hosting/Dict.keys", map[string]any{
		"dict": h.ToJSON(),
	})
	if err != nil {
		return nil, err
	}
	arr, ok := result.([]any)
	if !ok {
		return nil, nil
	}
	keys := make([]K, 0, len(arr))
	for _, raw := range arr {
		k, err := decodeAs[K](raw)
		if err != nil {
			return nil, err
		}
		keys = append(keys, k)
	}
	return keys, nil
}

func (d *Dict[K, V]) Values() ([]V, error) {
	ctx := context.Background()
	h, err := d.resolveDictHandle(ctx)
	if err != nil {
		return nil, err
	}
	result, err := d.parent.client.invokeCapability(ctx, "Aspire.Hosting/Dict.values", map[string]any{
		"dict": h.ToJSON(),
	})
	if err != nil {
		return nil, err
	}
	arr, ok := result.([]any)
	if !ok {
		return nil, nil
	}
	vals := make([]V, 0, len(arr))
	for _, raw := range arr {
		v, err := decodeAs[V](raw)
		if err != nil {
			return nil, err
		}
		vals = append(vals, v)
	}
	return vals, nil
}

func (d *Dict[K, V]) ToObject() (map[string]V, error) {
	ctx := context.Background()
	h, err := d.resolveDictHandle(ctx)
	if err != nil {
		return nil, err
	}
	result, err := d.parent.client.invokeCapability(ctx, "Aspire.Hosting/Dict.toObject", map[string]any{
		"dict": h.ToJSON(),
	})
	if err != nil {
		return nil, err
	}
	m, ok := result.(map[string]any)
	if !ok {
		return nil, fmt.Errorf("aspire: dict.toObject: unexpected result type %T", result)
	}
	obj := make(map[string]V, len(m))
	for k, raw := range m {
		v, err := decodeAs[V](raw)
		if err != nil {
			return nil, err
		}
		obj[k] = v
	}
	return obj, nil
}

func (d *Dict[K, V]) Set(key K, value V) error {
	ctx := context.Background()
	h, err := d.resolveDictHandle(ctx)
	if err != nil {
		return err
	}
	_, err = d.parent.client.invokeCapability(ctx, "Aspire.Hosting/Dict.set", map[string]any{
		"dict":  h.ToJSON(),
		"key":   serializeValue(key),
		"value": serializeValue(value),
	})
	return err
}

func (d *Dict[K, V]) Remove(key K) error {
	ctx := context.Background()
	h, err := d.resolveDictHandle(ctx)
	if err != nil {
		return err
	}
	_, err = d.parent.client.invokeCapability(ctx, "Aspire.Hosting/Dict.remove", map[string]any{
		"dict": h.ToJSON(),
		"key":  serializeValue(key),
	})
	return err
}

func (d *Dict[K, V]) Clear() error {
	ctx := context.Background()
	h, err := d.resolveDictHandle(ctx)
	if err != nil {
		return err
	}
	_, err = d.parent.client.invokeCapability(ctx, "Aspire.Hosting/Dict.clear", map[string]any{
		"dict": h.ToJSON(),
	})
	return err
}

// ── Pointer helpers ───────────────────────────────────────────────────────────

func StringPtr(s string) *string    { return &s }
func IntPtr(i int) *int             { return &i }
func BoolPtr(b bool) *bool          { return &b }
func Float64Ptr(f float64) *float64 { return &f }

// ── SerializeValue ────────────────────────────────────────────────────────────

// serializeValue converts a value to its JSON-serializable representation.
//
// Dispatch priority:
//  1. *handle                                      → handle.ToJSON()
//  2. *ReferenceExpression                         → r.ToJSON()
//  3. handleReference (every wrapper impl)         → getHandle().ToJSON()
//  4. interface{ ToMap() map[string]any }          → recurse into the map
//  5. []any / map[string]any                       → recurse element-wise
//  6. any slice/array/map                          → recurse element-wise
//  7. fmt.Stringer (compatibility)                 → v.String()
//  8. default                                      → pass through
func serializeValue(value any) any {
	if isNil(value) {
		return nil
	}

	switch v := value.(type) {
	case *handle:
		return v.ToJSON()
	case *ReferenceExpression:
		return v.ToJSON()
	case handleReference:
		h := v.getHandle()
		if h == nil {
			return nil
		}
		return h.ToJSON()
	case interface{ ToMap() map[string]any }:
		return serializeValue(v.ToMap())
	case []any:
		result := make([]any, len(v))
		for i, item := range v {
			result[i] = serializeValue(item)
		}
		return result
	case map[string]any:
		result := make(map[string]any, len(v))
		for k, val := range v {
			result[k] = serializeValue(val)
		}
		return result
	default:
		if result, ok := serializeReflectedCollection(value); ok {
			return result
		}
		if stringer, ok := value.(fmt.Stringer); ok {
			return stringer.String()
		}
		return value
	}
}

// isNil recognizes typed nil values after they have been boxed into any. A direct comparison with nil
// cannot see values such as any((*ReferenceExpression)(nil)), and dispatching those through ToJSON or
// ToMap would dereference a nil receiver.
func isNil(value any) bool {
	if value == nil {
		return true
	}

	reflectedValue := reflect.ValueOf(value)
	switch reflectedValue.Kind() {
	case reflect.Chan, reflect.Func, reflect.Interface, reflect.Map, reflect.Pointer, reflect.Slice:
		return reflectedValue.IsNil()
	default:
		return false
	}
}

func serializeReflectedCollection(value any) (any, bool) {
	reflectedValue := reflect.ValueOf(value)
	switch reflectedValue.Kind() {
	case reflect.Array, reflect.Slice:
		if reflectedValue.Kind() == reflect.Slice && reflectedValue.IsNil() {
			return nil, true
		}

		result := make([]any, reflectedValue.Len())
		for i := range reflectedValue.Len() {
			result[i] = serializeValue(reflectedValue.Index(i).Interface())
		}

		return result, true
	case reflect.Map:
		if reflectedValue.IsNil() {
			return nil, true
		}

		result := make(map[string]any, reflectedValue.Len())
		iter := reflectedValue.MapRange()
		for iter.Next() {
			key := iter.Key().Interface()
			result[fmt.Sprint(serializeValue(key))] = serializeValue(iter.Value().Interface())
		}

		return result, true
	default:
		return nil, false
	}
}

// callbackArg fetches a positional argument from the slice and decodes it
// to T. Returns the zero value if the index is out of range or the value
// cannot be decoded. Used by generated callback shim wrappers that adapt
// the transport's positional `[]any` invocation back to the user's typed
// callback signature.
func callbackArg[T any](args []any, idx int) T {
	var zero T
	if idx >= len(args) {
		return zero
	}
	v, err := decodeAs[T](args[idx])
	if err != nil {
		return zero
	}
	return v
}

// decodeAs converts an arbitrary RPC result into a typed value via JSON
// round-trip. Used by typed read methods on List/Dict and by value-returning
// capabilities.
func decodeAs[T any](raw any) (T, error) {
	var zero T
	if raw == nil {
		return zero, nil
	}
	if v, ok := raw.(T); ok {
		return v, nil
	}
	bytes, err := json.Marshal(raw)
	if err != nil {
		return zero, err
	}
	var out T
	if err := json.Unmarshal(bytes, &out); err != nil {
		if decoded, ok := decodeStructFields[T](raw); ok {
			return decoded, nil
		}
		return zero, err
	}
	return out, nil
}

func decodeStructFields[T any](raw any) (T, bool) {
	var zero T
	rawMap, ok := raw.(map[string]any)
	if !ok {
		return zero, false
	}

	targetType := reflect.TypeOf((*T)(nil)).Elem()
	isPointer := targetType.Kind() == reflect.Ptr
	if isPointer {
		targetType = targetType.Elem()
	}
	if targetType.Kind() != reflect.Struct {
		return zero, false
	}

	targetValue := reflect.New(targetType)
	structValue := targetValue.Elem()
	for i := 0; i < targetType.NumField(); i++ {
		fieldInfo := targetType.Field(i)
		fieldValue := structValue.Field(i)
		if !fieldValue.CanSet() {
			continue
		}

		fieldName := fieldInfo.Name
		if tag := fieldInfo.Tag.Get("json"); tag != "" {
			name, _, _ := strings.Cut(tag, ",")
			if name == "-" {
				continue
			}
			if name != "" {
				fieldName = name
			}
		}

		rawFieldValue, ok := rawMap[fieldName]
		if !ok {
			continue
		}

		bytes, err := json.Marshal(rawFieldValue)
		if err != nil {
			continue
		}
		if err := json.Unmarshal(bytes, fieldValue.Addr().Interface()); err != nil {
			continue
		}
	}

	if isPointer {
		return targetValue.Interface().(T), true
	}

	return structValue.Interface().(T), true
}

// ── deepUpdate ───────────────────────────────────────────────────────────────
//
// Used by generated code to merge variadic Options structs:
//
//	merged := &AddRedisOptions{}
//	for _, opt := range options {
//	    if opt != nil { merged = deepUpdate(merged, opt) }
//	}
func deepUpdate[T any](dst, src T) T {
	dstType := reflect.TypeOf(dst)
	srcType := reflect.TypeOf(src)

	if dstType != srcType {
		panic(fmt.Sprintf("aspire: merge type mismatch: cannot merge %v into %v", srcType, dstType))
	}

	var nilT T
	if reflect.DeepEqual(src, nilT) {
		return dst
	}
	if reflect.DeepEqual(dst, nilT) {
		return src
	}

	v := reflect.ValueOf(dst)
	kind := v.Kind()
	if kind == reflect.Ptr {
		kind = v.Elem().Kind()
	}

	switch kind {
	case reflect.Map, reflect.Struct:
		dstMap, _ := toMap(dst)
		srcMap, _ := toMap(src)
		mergedMap := merge(dstMap, srcMap, 0)

		bytes, _ := json.Marshal(mergedMap)

		var result T
		if reflect.TypeOf(result).Kind() == reflect.Ptr {
			result = reflect.New(reflect.TypeOf(result).Elem()).Interface().(T)
		}

		if err := json.Unmarshal(bytes, &result); err != nil {
			panic(fmt.Sprintf("aspire: merge error: %v", err))
		}
		return result
	default:
		return src
	}
}

func merge(dst, src map[string]any, depth int) map[string]any {
	const depthLimit = 32
	if depth > depthLimit {
		panic("aspire: deep update recursion limit of '32' exceeded")
	}

	for key, srcVal := range src {
		if dstVal, ok := dst[key]; ok {
			srcSub, srcOk := toMap(srcVal)
			dstSub, dstOk := toMap(dstVal)

			if srcOk && dstOk {
				dst[key] = merge(dstSub, srcSub, depth+1)
				continue
			}
		}
		dst[key] = srcVal
	}
	return dst
}

func toMap(i any) (map[string]any, bool) {
	if i == nil {
		return nil, false
	}

	v := reflect.ValueOf(i)
	if v.Kind() == reflect.Ptr {
		if v.IsNil() {
			return nil, false
		}
		v = v.Elem()
	}

	switch v.Kind() {
	case reflect.Map:
		m := make(map[string]any)
		for _, k := range v.MapKeys() {
			m[fmt.Sprintf("%v", k.Interface())] = v.MapIndex(k).Interface()
		}
		return m, true

	case reflect.Struct:
		m := make(map[string]any)
		t := v.Type()
		for i := 0; i < v.NumField(); i++ {
			field := t.Field(i)
			if field.PkgPath == "" {
				m[field.Name] = v.Field(i).Interface()
			}
		}
		return m, true

	default:
		return nil, false
	}
}

// ===== go.mod =====
module apphost/modules/aspire

go 1.26
// ===== transport.go =====
// Package aspire provides the ATS transport layer for JSON-RPC, Handle, errors, callbacks
package aspire

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"reflect"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// AtsErrorCode contains standard ATS error codes.
type AtsErrorCode string

const (
	CapabilityNotFound AtsErrorCode = "CAPABILITY_NOT_FOUND"
	HandleNotFound     AtsErrorCode = "HANDLE_NOT_FOUND"
	TypeMismatch       AtsErrorCode = "TYPE_MISMATCH"
	InvalidArgument    AtsErrorCode = "INVALID_ARGUMENT"
	ArgumentOutOfRange AtsErrorCode = "ARGUMENT_OUT_OF_RANGE"
	CallbackError      AtsErrorCode = "CALLBACK_ERROR"
	InternalError      AtsErrorCode = "INTERNAL_ERROR"
)

// atsErrorDetails Error details for ATS errors.
type atsErrorDetails struct {
	Parameter *string `json:"parameter,omitempty"`
	Expected  *string `json:"expected,omitempty"`
	Actual    *string `json:"actual,omitempty"`
}

// atsError Structured error from ATS capability invocation.
type atsError struct {
	Code       string           `json:"code"`
	Message    string           `json:"message"`
	Capability string           `json:"capability,omitempty"`
	Details    *atsErrorDetails `json:"details,omitempty"`
}

func (e *atsError) Error() string {
	return e.Message
}

// tryGetAtsError safely checks if a value contains an ATS error.
func tryGetAtsError(value any) (bool, atsError) {
	m, ok := value.(map[string]any)
	if !ok {
		return false, atsError{}
	}
	errVal, hasError := m["$error"]
	if !hasError || errVal == nil {
		return false, atsError{}
	}
	switch v := errVal.(type) {
	case atsError:
		return true, v
	case map[string]any:
		var result atsError
		if data, err := json.Marshal(v); err == nil {
			if err := json.Unmarshal(data, &result); err == nil {
				return true, result
			}
		}
	}
	return false, atsError{}
}

// CapabilityError represents an error returned from a capability invocation.
type CapabilityError struct {
	err atsError
}

func (e *CapabilityError) Code() string       { return e.err.Code }
func (e *CapabilityError) Message() string    { return e.err.Message }
func (e *CapabilityError) Capability() string { return e.err.Capability }
func (e *CapabilityError) Error() string      { return e.err.Error() }

// FormatError returns a human-readable rendering of an SDK error. Capability
// errors are expanded with their structured fields (code, capability) so the
// caller can log them directly without unpacking. Useful pattern:
//
//	if err := app.Run(); err != nil {
//	    log.Fatal(aspire.FormatError(err))
//	}
//
// Non-SDK errors are returned via err.Error() unchanged.
func FormatError(err error) string {
	if err == nil {
		return ""
	}
	var capErr *CapabilityError
	if errors.As(err, &capErr) {
		var b strings.Builder
		b.WriteString("Capability Error: ")
		b.WriteString(capErr.Message())
		if code := capErr.Code(); code != "" {
			b.WriteString("\n  Code: ")
			b.WriteString(code)
		}
		if cap := capErr.Capability(); cap != "" {
			b.WriteString("\n  Capability: ")
			b.WriteString(cap)
		}
		if capErr.err.Details != nil {
			d := capErr.err.Details
			if d.Parameter != nil {
				b.WriteString("\n  Parameter: ")
				b.WriteString(*d.Parameter)
			}
			if d.Expected != nil {
				b.WriteString("\n  Expected: ")
				b.WriteString(*d.Expected)
			}
			if d.Actual != nil {
				b.WriteString("\n  Actual: ")
				b.WriteString(*d.Actual)
			}
		}
		return b.String()
	}
	return err.Error()
}

// CancellationToken wraps a context.Context to provide cooperative cancellation.
// Use NewCancellationToken to create one; call Cancel() to cancel it.
// Pass token.Context() to any standard Go library that accepts a context.
type CancellationToken struct {
	ctx    context.Context
	cancel context.CancelFunc
}

// NewCancellationToken creates a new cancellation token backed by a cancellable
// context derived from context.Background().
func NewCancellationToken() *CancellationToken {
	return newCancellationTokenFrom(context.Background())
}

// newCancellationTokenFrom creates a new cancellation token whose lifetime is
// bounded by the supplied parent context.
func newCancellationTokenFrom(parent context.Context) *CancellationToken {
	ctx, cancel := context.WithCancel(parent)
	return &CancellationToken{ctx: ctx, cancel: cancel}
}

// Cancel cancels the token and propagates cancellation to the underlying context.
func (ct *CancellationToken) Cancel() {
	if ct == nil {
		return
	}
	ct.cancel()
}

// IsCancelled returns true if the token has been canceled.
func (ct *CancellationToken) IsCancelled() bool {
	if ct == nil {
		return false
	}
	return ct.ctx.Err() != nil
}

// Context returns the underlying context.Context for use with standard Go APIs.
func (ct *CancellationToken) Context() context.Context {
	if ct == nil {
		return context.Background()
	}
	return ct.ctx
}

// handleType is the ATS type ID carried on a handle envelope.
type handleType = string

// handle represents a reference to a server-side object.
type handle struct {
	HandleID string     `json:"$handle"`
	TypeID   handleType `json:"$type"`
}

// ToJSON returns the handle as a JSON-serializable map.
func (h *handle) ToJSON() map[string]any {
	return map[string]any{
		"$handle": h.HandleID,
		"$type":   h.TypeID,
	}
}

func (h *handle) String() string {
	return fmt.Sprintf("Handle<%s>(%s)", h.TypeID, h.HandleID)
}

// isMarshaledHandle checks if a value is a marshaled handle.
func isMarshaledHandle(value any) bool {
	m, ok := value.(map[string]any)
	if !ok {
		return false
	}
	_, hasHandle := m["$handle"]
	_, hasType := m["$type"]
	return hasHandle && hasType
}

// handleReference is implemented by every handle-wrapper type. The generated
// SDK uses this interface as the universal "extract the underlying handle"
// affordance — both serializeValue and the post-RPC cast in generated bodies
// rely on it. Err() lets the generator propagate failures from a handle
// argument back into the receiver's error chain (the Err()-accumulation
// pattern that maintains fluent chaining).
type handleReference interface {
	getHandle() *handle
	Err() error
}

// handleWrapperFactory creates a wrapper for a handle.
type handleWrapperFactory func(handle *handle, client *client) any

// wrapIfHandle recursively traverses the value to wrap marshaled handles into typed DTOs.
// Lookups consult the per-client wrapper registry; ReferenceExpression and any
// generated handle types must be registered via client.registerHandleWrapper
// before the first capability invocation that could return them.
func wrapIfHandle(value any, c *client) any {
	if isMarshaledHandle(value) {
		m := value.(map[string]any)
		h := &handle{
			HandleID: m["$handle"].(string),
			TypeID:   m["$type"].(string),
		}
		if c != nil {
			if factory := c.lookupWrapper(h.TypeID); factory != nil {
				return factory(h, c)
			}
		}
		return h
	}

	if s, ok := value.([]any); ok {
		for i, v := range s {
			s[i] = wrapIfHandle(v, c)
		}
		return s
	}

	if m, ok := value.(map[string]any); ok {
		for k, v := range m {
			m[k] = wrapIfHandle(v, c)
		}
		return m
	}

	return value
}

// ── connection ────────────────────────────────────────────────────────────────
//
// connection owns all I/O for a single live socket connection. It is created by
// client.connect and torn down by connection.close. client holds a *connection
// pointer and replaces it with nil on disconnect; all other state lives here.

type connection struct {
	rawConn io.ReadWriteCloser
	reader  *bufio.Reader
	client  *client

	writeQueue chan map[string]any
	done       chan struct{}

	mu      sync.Mutex
	pending map[int64]chan map[string]any
	closed  bool

	nextID  atomic.Int64
	onClose func(error)
}

func newConnection(rawConn io.ReadWriteCloser, c *client, onClose func(error)) *connection {
	return &connection{
		rawConn:    rawConn,
		reader:     bufio.NewReader(rawConn),
		client:     c,
		writeQueue: make(chan map[string]any, 64),
		done:       make(chan struct{}),
		pending:    make(map[int64]chan map[string]any),
		onClose:    onClose,
	}
}

func (c *connection) start() {
	go c.readLoop()
	go c.writeLoop()
}

func (c *connection) close(err error) {
	c.mu.Lock()
	if c.closed {
		c.mu.Unlock()
		return
	}
	c.closed = true
	pending := c.pending
	c.pending = nil
	c.mu.Unlock()

	closeErr := c.rawConn.Close()
	close(c.done)

	if err == nil {
		err = errors.New("connection closed")
	}
	if closeErr != nil {
		err = errors.Join(err, closeErr)
	}
	errResp := map[string]any{
		"error": map[string]any{
			"code":    -32000,
			"message": err.Error(),
		},
	}
	for _, ch := range pending {
		ch <- errResp
	}

	c.onClose(err)
}

func (c *connection) writeLoop() {
	for {
		select {
		case msg := <-c.writeQueue:
			if err := writeMessage(c.rawConn, msg); err != nil {
				c.close(err)
				return
			}
		case <-c.done:
			return
		}
	}
}

func (c *connection) readLoop() {
	for {
		msg, err := readMessage(c.reader)
		if err != nil {
			c.close(err)
			return
		}
		c.dispatch(msg)
	}
}

func (c *connection) dispatch(msg map[string]any) {
	if _, hasMethod := msg["method"]; hasMethod {
		go c.handleCallbackRequest(msg)
		return
	}
	if id, ok := msg["id"]; ok {
		if reqID, ok := jsonRPCID(id); ok {
			c.mu.Lock()
			ch := c.pending[reqID]
			delete(c.pending, reqID)
			c.mu.Unlock()
			if ch != nil {
				ch <- msg
			}
		}
	}
}

func (c *connection) enqueue(msg map[string]any) {
	select {
	case c.writeQueue <- msg:
	case <-c.done:
	}
}

func (c *connection) handleCallbackRequest(message map[string]any) {
	method := getString(message, "method")
	requestID := message["id"]

	if method != "invokeCallback" {
		if requestID != nil {
			c.enqueue(map[string]any{
				"jsonrpc": "2.0",
				"id":      requestID,
				"error":   map[string]any{"code": -32601, "message": fmt.Sprintf("Unknown method: %s", method)},
			})
		}
		return
	}

	params, _ := message["params"].([]any)
	var callbackID string
	var args any
	if len(params) > 0 {
		callbackID, _ = params[0].(string)
	}
	if len(params) > 1 {
		args = params[1]
	}

	result, err := c.client.invokeCallback(callbackID, args)
	if err != nil {
		c.enqueue(map[string]any{
			"jsonrpc": "2.0",
			"id":      requestID,
			"error":   map[string]any{"code": -32000, "message": err.Error()},
		})
	} else {
		c.enqueue(map[string]any{
			"jsonrpc": "2.0",
			"id":      requestID,
			"result":  result,
		})
	}
}

// sendRequest sends a JSON-RPC request over the write queue and blocks until
// the matching response arrives or the supplied context is canceled.
//
// The ctx argument is the primary unblock mechanism for the Go caller. The
// existing per-CancellationToken server-notification goroutine spawned by
// client.registerCancellation continues to notify the AppHost, but this
// method does not need to wait for that round-trip — it returns ctx.Err()
// as soon as ctx is done.
func (c *connection) sendRequest(ctx context.Context, method string, params []any) (any, error) {
	if ctx == nil {
		ctx = context.Background()
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}

	id := c.nextID.Add(1)
	respCh := make(chan map[string]any, 1)
	msg := map[string]any{
		"jsonrpc": "2.0",
		"id":      id,
		"method":  method,
		"params":  params,
	}

	c.mu.Lock()
	if c.closed {
		c.mu.Unlock()
		return nil, errors.New("not connected to AppHost")
	}
	c.pending[id] = respCh
	c.mu.Unlock()

	select {
	case c.writeQueue <- msg:
	case <-ctx.Done():
		c.mu.Lock()
		delete(c.pending, id)
		c.mu.Unlock()
		return nil, ctx.Err()
	case <-c.done:
		c.mu.Lock()
		delete(c.pending, id)
		c.mu.Unlock()
		return nil, errors.New("not connected to AppHost")
	}

	select {
	case resp := <-respCh:
		return extractResult(resp)
	case <-ctx.Done():
		c.mu.Lock()
		delete(c.pending, id)
		c.mu.Unlock()
		return nil, ctx.Err()
	case <-c.done:
		select {
		case resp := <-respCh:
			return extractResult(resp)
		default:
			return nil, errors.New("not connected to AppHost")
		}
	}
}

// ── client ────────────────────────────────────────────────────────────────────
//
// client manages the connection lifecycle to the AppHost server. All registries
// (handle wrappers, callbacks, cancellation tokens) are scoped to the client —
// no package-level globals — so multiple clients can coexist in the same
// process without sharing state.

type client struct {
	socketPath string

	// mu guards conn and disconnectCallbacks.
	mu                  sync.Mutex
	conn                *connection
	disconnectCallbacks []func()

	// wrappersMu guards the per-client handle wrapper registry. The registry
	// is populated by registerWrappers (generated) before the first
	// invokeCapability call.
	wrappersMu sync.RWMutex
	wrappers   map[string]handleWrapperFactory

	// callbacksMu guards the per-client callback registry.
	callbacksMu     sync.RWMutex
	callbacks       map[string]func(...any) any
	callbackCounter atomic.Int64
}

// newClient constructs a client; callers must invoke connect before using it.
func newClient(socketPath string) *client {
	return &client{
		socketPath: socketPath,
		wrappers:   make(map[string]handleWrapperFactory),
		callbacks:  make(map[string]func(...any) any),
	}
}

// registerHandleWrapper registers a factory for wrapping handles of a specific
// type. Called by the generated registerWrappers function from CreateBuilder
// before any capability invocation that could return a wrapped handle.
func (c *client) registerHandleWrapper(typeID string, factory handleWrapperFactory) {
	c.wrappersMu.Lock()
	c.wrappers[typeID] = factory
	c.wrappersMu.Unlock()
}

// lookupWrapper returns the factory for a given type ID or nil.
func (c *client) lookupWrapper(typeID string) handleWrapperFactory {
	c.wrappersMu.RLock()
	defer c.wrappersMu.RUnlock()
	return c.wrappers[typeID]
}

// registerCallback registers a callback on this client and returns its ID.
func (c *client) registerCallback(callback func(...any) any) string {
	if callback == nil {
		return ""
	}
	id := fmt.Sprintf("callback_%d_%d", c.callbackCounter.Add(1), time.Now().UnixMilli())
	c.callbacksMu.Lock()
	c.callbacks[id] = callback
	c.callbacksMu.Unlock()
	return id
}

// unregisterCallback removes a callback by ID.
func (c *client) unregisterCallback(id string) bool {
	c.callbacksMu.Lock()
	defer c.callbacksMu.Unlock()
	_, exists := c.callbacks[id]
	delete(c.callbacks, id)
	return exists
}

func (c *client) invokeCallback(callbackID string, args any) (any, error) {
	if callbackID == "" {
		return nil, errors.New("callback ID missing")
	}

	c.callbacksMu.RLock()
	callback, ok := c.callbacks[callbackID]
	c.callbacksMu.RUnlock()
	if !ok {
		return nil, fmt.Errorf("callback not found: %s", callbackID)
	}

	var positionalArgs []any
	if argsMap, ok := args.(map[string]any); ok {
		for i := 0; ; i++ {
			key := fmt.Sprintf("p%d", i)
			if val, exists := argsMap[key]; exists {
				positionalArgs = append(positionalArgs, wrapIfHandle(val, c))
			} else {
				break
			}
		}
	} else if args != nil {
		positionalArgs = append(positionalArgs, wrapIfHandle(args, c))
	}

	result := callback(positionalArgs...)

	// DTO write-back protocol: if the callback result is nil, return the
	// original args object so the .NET host can detect mutations.
	if result == nil {
		return args, nil
	}

	return result, nil
}

// registerCancellation registers a cancellation token with the server and
// returns its ID. A goroutine is spawned to notify the server when the token
// is canceled.
func (c *client) registerCancellation(token *CancellationToken) string {
	if token == nil {
		return ""
	}
	id := fmt.Sprintf("ct_%d", time.Now().UnixNano())
	go func() {
		<-token.ctx.Done()
		c.cancelToken(id)
	}()
	return id
}

// connect establishes the connection to the AppHost server and starts the
// background reader and writer goroutines.
func (c *client) connect(ctx context.Context, timeout time.Duration) error {
	c.mu.Lock()
	if c.conn != nil {
		c.mu.Unlock()
		return nil
	}

	rawConn, err := openConnection(c.socketPath, timeout)
	if err != nil {
		c.mu.Unlock()
		return fmt.Errorf("failed to connect to AppHost: %w", err)
	}

	authToken := os.Getenv("ASPIRE_REMOTE_APPHOST_TOKEN")
	if authToken == "" {
		cErr := rawConn.Close()
		c.mu.Unlock()
		return errors.Join(errors.New("ASPIRE_REMOTE_APPHOST_TOKEN environment variable is not set"), cErr)
	}

	conn := newConnection(rawConn, c, c.onConnectionClose)
	c.conn = conn
	c.mu.Unlock()

	conn.start()

	if err := c.authenticate(ctx, authToken); err != nil {
		c.disconnect()
		return fmt.Errorf("failed to authenticate to AppHost: %w", err)
	}

	return nil
}

// onDisconnect registers a callback to be invoked exactly once when the
// connection is closed.
func (c *client) onDisconnect(callback func()) {
	c.mu.Lock()
	c.disconnectCallbacks = append(c.disconnectCallbacks, callback)
	c.mu.Unlock()
}

// invokeCapability invokes a capability on the server. The supplied context
// drives both server-side cancellation (if a CancellationToken is registered
// in args) and local short-circuit on cancel.
func (c *client) invokeCapability(ctx context.Context, capabilityID string, args map[string]any) (any, error) {
	if err := validateCapabilityArgs(capabilityID, args); err != nil {
		return nil, err
	}

	result, err := c.sendRequest(ctx, "invokeCapability", []any{capabilityID, c.marshalTransportValue(args)})
	if err != nil {
		return nil, err
	}
	if hasAtsErr, atsErr := tryGetAtsError(result); hasAtsErr {
		return nil, &CapabilityError{err: atsErr}
	}
	return wrapIfHandle(result, c), nil
}

func (c *client) marshalTransportValue(value any) any {
	if callback, ok := value.(func(...any) any); ok {
		return c.registerCallback(callback)
	}

	serialized := serializeValue(value)
	switch v := serialized.(type) {
	case func(...any) any:
		return c.registerCallback(v)
	case map[string]any:
		result := make(map[string]any, len(v))
		for key, nestedValue := range v {
			result[key] = c.marshalTransportValue(nestedValue)
		}
		return result
	case []any:
		result := make([]any, len(v))
		for i, item := range v {
			result[i] = c.marshalTransportValue(item)
		}
		return result
	default:
		return serialized
	}
}

func (c *client) authenticate(ctx context.Context, token string) error {
	result, err := c.sendRequest(ctx, "authenticate", []any{token})
	if err != nil {
		return err
	}
	authenticated, _ := result.(bool)
	if !authenticated {
		return errors.New("failed to authenticate to the AppHost server")
	}
	return nil
}

func (c *client) cancelToken(tokenID string) bool {
	result, err := c.sendRequest(context.Background(), "cancelToken", []any{tokenID})
	if err != nil {
		return false
	}
	b, _ := result.(bool)
	return b
}

func (c *client) ping(ctx context.Context) (string, error) {
	result, err := c.sendRequest(ctx, "ping", nil)
	if err != nil {
		return "", err
	}
	s, _ := result.(string)
	return s, nil
}

// disconnect closes the connection. Safe to call multiple times.
func (c *client) disconnect() {
	c.mu.Lock()
	conn := c.conn
	c.conn = nil
	c.mu.Unlock()
	if conn != nil {
		conn.close(nil)
	}
}

// sendRequest snapshots the active connection and delegates to it.
func (c *client) sendRequest(ctx context.Context, method string, params []any) (any, error) {
	c.mu.Lock()
	conn := c.conn
	c.mu.Unlock()
	if conn == nil {
		return nil, errors.New("not connected to AppHost")
	}
	return conn.sendRequest(ctx, method, params)
}

func (c *client) onConnectionClose(_ error) {
	c.mu.Lock()
	c.conn = nil
	callbacks := c.disconnectCallbacks
	c.disconnectCallbacks = nil
	c.mu.Unlock()
	for _, cb := range callbacks {
		cb()
	}
}

// ── Package-level I/O helpers ─────────────────────────────────────────────────

func extractResult(response map[string]any) (any, error) {
	if errObj, hasErr := response["error"]; hasErr {
		errMap, _ := errObj.(map[string]any)
		return nil, errors.New(getString(errMap, "message"))
	}
	return response["result"], nil
}

func writeMessage(w io.Writer, msg map[string]any) error {
	body, err := json.Marshal(msg)
	if err != nil {
		return err
	}
	header := fmt.Sprintf("Content-Length: %d\r\n\r\n", len(body))
	if _, err = w.Write([]byte(header)); err != nil {
		return err
	}
	_, err = w.Write(body)
	return err
}

func readMessage(reader *bufio.Reader) (map[string]any, error) {
	headers := make(map[string]string)
	for {
		line, err := reader.ReadString('\n')
		if err != nil {
			return nil, err
		}
		line = strings.TrimSpace(line)
		if line == "" {
			break
		}
		parts := strings.SplitN(line, ":", 2)
		if len(parts) == 2 {
			headers[strings.TrimSpace(strings.ToLower(parts[0]))] = strings.TrimSpace(parts[1])
		}
	}

	lengthStr := headers["content-length"]
	length, err := strconv.Atoi(lengthStr)
	if err != nil || length <= 0 {
		return nil, errors.New("invalid content-length")
	}

	body := make([]byte, length)
	_, err = io.ReadFull(reader, body)
	if err != nil {
		return nil, err
	}

	var message map[string]any
	if err := json.Unmarshal(body, &message); err != nil {
		return nil, err
	}
	return message, nil
}

func jsonRPCID(id any) (int64, bool) {
	switch v := id.(type) {
	case float64:
		return int64(v), true
	case int64:
		return v, true
	case json.Number:
		n, err := v.Int64()
		return n, err == nil
	case string:
		n, err := strconv.ParseInt(v, 10, 64)
		return n, err == nil
	default:
		return 0, false
	}
}

func getString(m map[string]any, key string) string {
	if v, ok := m[key]; ok {
		if s, ok := v.(string); ok {
			return s
		}
	}
	return ""
}

func openConnection(socketPath string, timeout time.Duration) (io.ReadWriteCloser, error) {
	if runtime.GOOS == "windows" {
		pipePath := `\\.\pipe\` + socketPath
		return openNamedPipe(pipePath)
	}
	dialer := net.Dialer{}
	if timeout > 0 {
		dialer.Timeout = timeout
	}
	return dialer.Dial("unix", socketPath)
}

func openNamedPipe(path string) (io.ReadWriteCloser, error) {
	// Go 1.26 supports Windows FILE_FLAG_* bits in os.OpenFile.
	// Overlapped I/O lets the background read and authentication write run
	// concurrently and allows Close to cancel a pending read.
	// https://go.dev/doc/go1.26#os
	const fileFlagOverlapped = 0x40000000
	f, err := os.OpenFile(path, os.O_RDWR|fileFlagOverlapped, 0)
	if err != nil {
		return nil, err
	}
	return f, nil
}

// validateCapabilityArgs checks for circular references in arguments before sending to the server.
func validateCapabilityArgs(capabilityID string, args map[string]any) error {
	if args == nil {
		return nil
	}
	ancestors := make(map[uintptr]struct{})
	return validateValue(args, "args", ancestors, capabilityID)
}

func validateValue(value any, path string, ancestors map[uintptr]struct{}, capabilityID string) error {
	if value == nil {
		return nil
	}

	val := reflect.ValueOf(value)
	switch val.Kind() {
	case reflect.Map, reflect.Slice, reflect.Ptr:
		if val.IsNil() {
			return nil
		}
		ptr := val.Pointer()
		if _, ok := ancestors[ptr]; ok {
			return fmt.Errorf("argument '%s' passed to capability '%s' contains a circular reference", path, capabilityID)
		}
		ancestors[ptr] = struct{}{}
		defer delete(ancestors, ptr)
	default:
	}

	switch v := value.(type) {
	case map[string]any:
		for key, nestedValue := range v {
			if err := validateValue(nestedValue, path+"."+key, ancestors, capabilityID); err != nil {
				return err
			}
		}
	case []any:
		for i, item := range v {
			if err := validateValue(item, fmt.Sprintf("%s[%d]", path, i), ancestors, capabilityID); err != nil {
				return err
			}
		}
	}

	return nil
}
