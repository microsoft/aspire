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

// TestPersistenceMode - Test persistence mode enum.
type TestPersistenceMode string

const (
	TestPersistenceModeNone TestPersistenceMode = "None"
	TestPersistenceModeVolume TestPersistenceMode = "Volume"
	TestPersistenceModeBind TestPersistenceMode = "Bind"
)

// TestResourceStatus - Test enum for type generation verification.
type TestResourceStatus string

const (
	// TestResourceStatusPending - The resource is pending.
	TestResourceStatusPending TestResourceStatus = "Pending"
	// TestResourceStatusRunning - The resource is running.
	TestResourceStatusRunning TestResourceStatus = "Running"
	// TestResourceStatusStopped - The resource is stopped.
	TestResourceStatusStopped TestResourceStatus = "Stopped"
	// TestResourceStatusFailed is the resource failed.
	TestResourceStatusFailed TestResourceStatus = "Failed"
)

// ============================================================================
// DTOs
// ============================================================================

// TestConfigDto - Test DTO to verify [AspireDto] generates TypeScript
// interfaces.
type TestConfigDto struct {
	// Name is the name of the test config.
	Name string `json:"name,omitempty"`
	// Port is the port used by the test config.
	Port float64 `json:"port,omitempty"`
	// Enabled is a value indicating whether the test config is enabled.
	Enabled bool `json:"enabled,omitempty"`
	// OptionalField is an optional test config field.
	OptionalField *string `json:"optionalField,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *TestConfigDto) ToMap() map[string]any {
	m := map[string]any{}
	m["name"] = serializeValue(d.Name)
	m["port"] = serializeValue(d.Port)
	m["enabled"] = serializeValue(d.Enabled)
	if d.OptionalField != nil { m["optionalField"] = serializeValue(d.OptionalField) }
	return m
}

// TestNestedDto - Test DTO with complex nested types.
type TestNestedDto struct {
	Id string `json:"id,omitempty"`
	Config *TestConfigDto `json:"config,omitempty"`
	Tags []string `json:"tags,omitempty"`
	Counts map[string]float64 `json:"counts,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *TestNestedDto) ToMap() map[string]any {
	m := map[string]any{}
	m["id"] = serializeValue(d.Id)
	if d.Config != nil { m["config"] = serializeValue(d.Config) }
	if d.Tags != nil { m["tags"] = serializeValue(d.Tags) }
	if d.Counts != nil { m["counts"] = serializeValue(d.Counts) }
	return m
}

// TestDeeplyNestedDto - Test DTO with deeply nested generic types.
type TestDeeplyNestedDto struct {
	// NestedData - Deeply nested generic: Dictionary containing List of DTOs.
	NestedData map[string][]*TestConfigDto `json:"nestedData,omitempty"`
	// MetadataArray - Array of dictionaries.
	MetadataArray []map[string]string `json:"metadataArray,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *TestDeeplyNestedDto) ToMap() map[string]any {
	m := map[string]any{}
	if d.NestedData != nil { m["nestedData"] = serializeValue(d.NestedData) }
	if d.MetadataArray != nil { m["metadataArray"] = serializeValue(d.MetadataArray) }
	return m
}

// ============================================================================
// Exported Values
// ============================================================================

// TestConfigs groups related exported values.
var TestConfigs = struct {
	// Default is the default test configuration.
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
//
// Methods are emitted on concrete impls; this interface is a marker for type
// assertions.
type Resource interface {
	handleReference
}

// ResourceWithConnectionString marks types implementing
// IResourceWithConnectionString.
//
// Methods are emitted on concrete impls; this interface is a marker for type
// assertions.
type ResourceWithConnectionString interface {
	handleReference
}

// TestMutablePromiseCollisionResourcePromise marks types implementing
// ITestMutablePromiseCollisionResourcePromise.
type TestMutablePromiseCollisionResourcePromise interface {
	handleReference
}

// TestPromiseCollisionResource marks types implementing
// ITestPromiseCollisionResource.
type TestPromiseCollisionResource interface {
	handleReference
}

// TestPromiseCollisionResourcePromise marks types implementing
// ITestPromiseCollisionResourcePromise.
type TestPromiseCollisionResourcePromise interface {
	handleReference
}

// TestVaultResource marks types implementing ITestVaultResource.
//
// Methods are emitted on concrete impls; this interface is a marker for type
// assertions.
type TestVaultResource interface {
	handleReference
}

// ============================================================================
// Handle wrappers
// ============================================================================

// Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource is the public
// interface for handle type
// Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource.
type Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource interface {
	handleReference

	// TestWaitFor waits for another resource (test version)
	TestWaitFor(dependency Resource) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// TestWithEnvironmentCallback configures environment with callback (test
	// version)
	TestWithEnvironmentCallback(callback func(arg TestEnvironmentContext)) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithCancellableOperation performs a cancellable operation
	WithCancellableOperation(operation func(arg *CancellationToken)) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithConfig configures the resource with a DTO
	WithConfig(config *TestConfigDto) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithCorrelationId sets the correlation ID
	WithCorrelationId(correlationId string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithCreatedAt sets the created timestamp
	WithCreatedAt(createdAt string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithDependency adds a dependency on another resource
	WithDependency(dependency ResourceWithConnectionString) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithEndpoints sets the endpoints
	WithEndpoints(endpoints []string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithEnvironmentVariables sets environment variables
	WithEnvironmentVariables(variables map[string]string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithMergeEndpoint configures a named endpoint
	WithMergeEndpoint(endpointName string, port float64) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithMergeEndpointScheme configures a named endpoint with scheme
	WithMergeEndpointScheme(endpointName string, port float64, scheme string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithMergeLabel adds a label to the resource
	WithMergeLabel(label string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithMergeLabelCategorized adds a categorized label to the resource
	WithMergeLabelCategorized(label string, category string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithMergeLogging configures resource logging
	//
	// Parameters:
	//   - options: Optional parameters; see [WithMergeLoggingOptions].
	WithMergeLogging(logLevel string, options ...*WithMergeLoggingOptions) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithMergeLoggingPath configures resource logging with file path
	//
	// Parameters:
	//   - options: Optional parameters; see [WithMergeLoggingPathOptions].
	WithMergeLoggingPath(logLevel string, logPath string, options ...*WithMergeLoggingPathOptions) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithMergeRoute configures a route
	WithMergeRoute(path string, method string, handler string, priority float64) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithMergeRouteMiddleware configures a route with middleware
	WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithModifiedAt sets the modified timestamp
	WithModifiedAt(modifiedAt string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithNestedConfig configures with nested DTO
	WithNestedConfig(config *TestNestedDto) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithOptionalCallback configures with optional callback
	//
	// Parameters:
	//   - options: Optional parameters; see [WithOptionalCallbackOptions].
	WithOptionalCallback(options ...*WithOptionalCallbackOptions) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithOptionalString adds an optional string parameter
	//
	// Parameters:
	//   - options: Optional parameters; see [WithOptionalStringOptions].
	WithOptionalString(options ...*WithOptionalStringOptions) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithStatus sets the resource status
	WithStatus(status TestResourceStatus) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithUnionDependency adds a dependency from a string or another resource
	//
	// Allowed types for parameter dependency: string, ResourceWithConnectionString.
	WithUnionDependency(dependency any) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithValidator adds validation callback
	WithValidator(validator func(arg TestResourceContext) bool) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// WithVaultDirect configures vault using direct interface target
	WithVaultDirect(option string) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource

	// Err returns the first error recorded on this wrapper, or nil if the wrapper
	// resolved successfully and no chained operation has failed.
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

// TestWithEnvironmentCallback configures environment with callback (test
// version)
func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) TestWithEnvironmentCallback(callback func(arg TestEnvironmentContext)) Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackHandleArg(s.client, args, 0, newTestEnvironmentContextFromHandle))
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
				cb(callbackHandleArg(s.client, args, 0, newTestCallbackContextFromHandle))
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
			return cb(callbackHandleArg(s.client, args, 0, newTestResourceContextFromHandle))
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

// IDistributedApplicationBuilder is the public interface for handle type
// IDistributedApplicationBuilder.
type IDistributedApplicationBuilder interface {
	handleReference

	// AddTestRedis adds a test Redis resource from ATS documentation.
	//
	// Parameters:
	//   - name: The ATS resource name.
	//   - options: Optional parameters; see [AddTestRedisOptions].
	//
	// Returns: The ATS test Redis resource builder.
	AddTestRedis(name string, options ...*AddTestRedisOptions) TestRedisResource

	// AddTestVault adds a test vault resource
	//
	// Parameters:
	//   - name: The resource name.
	//
	// Returns: The interface vault resource builder.
	AddTestVault(name string) TestVaultResource

	// Build invokes the build capability and returns the running application.
	Build() (DistributedApplication, error)

	// Err returns the first error recorded on this wrapper, or nil if the wrapper
	// resolved successfully and no chained operation has failed.
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

// IResourceWithEnvironment is the public interface for handle type
// IResourceWithEnvironment.
type IResourceWithEnvironment interface {
	handleReference

	// Err returns the first error recorded on this wrapper, or nil if the wrapper
	// resolved successfully and no chained operation has failed.
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

// TestCallbackContext - Test callback context for WithCustomCallback. Also used
// to verify [AspireExport(ExposeProperties = true)] scanning.
type TestCallbackContext interface {
	handleReference

	// CancellationToken is supported by ATS.
	CancellationToken() (*CancellationToken, error)

	// Name gets the Name property
	Name() (*string, error)

	// SetCancellationToken sets the CancellationToken property
	//
	// CancellationToken is supported by ATS.
	//
	// Parameters:
	//   - options: Optional parameters; see [SetCancellationTokenOptions].
	SetCancellationToken(options ...*SetCancellationTokenOptions) TestCallbackContext

	// SetName sets the Name property
	SetName(value *string) TestCallbackContext

	// SetValue sets the Value property
	SetValue(value float64) TestCallbackContext

	// Value gets the Value property
	Value() (float64, error)

	// Err returns the first error recorded on this wrapper, or nil if the wrapper
	// resolved successfully and no chained operation has failed.
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

// CancellationToken is supported by ATS.
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
//
// CancellationToken is supported by ATS.
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

// TestCollectionContext - Test context with collection properties to verify
// consistent code generation. Verifies both List and Dictionary properties
// generate proper getter patterns.
type TestCollectionContext interface {
	handleReference

	// Items - List property - should generate AspireList getter like Dictionary
	// properties.
	Items() *List[string]

	// Metadata - Dictionary property - already works with AspireDict getter.
	Metadata() *Dict[string, string]

	// Err returns the first error recorded on this wrapper, or nil if the wrapper
	// resolved successfully and no chained operation has failed.
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

// Items - List property - should generate AspireList getter like Dictionary
// properties.
func (s *testCollectionContext) Items() *List[string] {
	if s.items == nil {
		s.items = newListWithGetter[string](s.handleWrapperBase, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.items")
	}
	return s.items
}

// Metadata - Dictionary property - already works with AspireDict getter.
func (s *testCollectionContext) Metadata() *Dict[string, string] {
	if s.metadata == nil {
		s.metadata = newDictWithGetter[string, string](s.handleWrapperBase, "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.metadata")
	}
	return s.metadata
}

// TestDatabaseResource is the public interface for handle type
// TestDatabaseResource.
type TestDatabaseResource interface {
	handleReference

	// TestWaitFor waits for another resource (test version)
	TestWaitFor(dependency Resource) TestDatabaseResource

	// TestWithEnvironmentCallback configures environment with callback (test
	// version)
	TestWithEnvironmentCallback(callback func(arg TestEnvironmentContext)) TestDatabaseResource

	// WithCancellableOperation performs a cancellable operation
	WithCancellableOperation(operation func(arg *CancellationToken)) TestDatabaseResource

	// WithConfig configures the resource with a DTO
	WithConfig(config *TestConfigDto) TestDatabaseResource

	// WithCorrelationId sets the correlation ID
	WithCorrelationId(correlationId string) TestDatabaseResource

	// WithCreatedAt sets the created timestamp
	WithCreatedAt(createdAt string) TestDatabaseResource

	// WithDataVolume adds a data volume
	//
	// Parameters:
	//   - options: Optional parameters; see
	//     [TestDatabaseResourceWithDataVolumeOptions].
	WithDataVolume(options ...*TestDatabaseResourceWithDataVolumeOptions) TestDatabaseResource

	// WithDependency adds a dependency on another resource
	WithDependency(dependency ResourceWithConnectionString) TestDatabaseResource

	// WithEndpoints sets the endpoints
	WithEndpoints(endpoints []string) TestDatabaseResource

	// WithEnvironmentVariables sets environment variables
	WithEnvironmentVariables(variables map[string]string) TestDatabaseResource

	// WithMergeEndpoint configures a named endpoint
	WithMergeEndpoint(endpointName string, port float64) TestDatabaseResource

	// WithMergeEndpointScheme configures a named endpoint with scheme
	WithMergeEndpointScheme(endpointName string, port float64, scheme string) TestDatabaseResource

	// WithMergeLabel adds a label to the resource
	WithMergeLabel(label string) TestDatabaseResource

	// WithMergeLabelCategorized adds a categorized label to the resource
	WithMergeLabelCategorized(label string, category string) TestDatabaseResource

	// WithMergeLogging configures resource logging
	//
	// Parameters:
	//   - options: Optional parameters; see [WithMergeLoggingOptions].
	WithMergeLogging(logLevel string, options ...*WithMergeLoggingOptions) TestDatabaseResource

	// WithMergeLoggingPath configures resource logging with file path
	//
	// Parameters:
	//   - options: Optional parameters; see [WithMergeLoggingPathOptions].
	WithMergeLoggingPath(logLevel string, logPath string, options ...*WithMergeLoggingPathOptions) TestDatabaseResource

	// WithMergeRoute configures a route
	WithMergeRoute(path string, method string, handler string, priority float64) TestDatabaseResource

	// WithMergeRouteMiddleware configures a route with middleware
	WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) TestDatabaseResource

	// WithModifiedAt sets the modified timestamp
	WithModifiedAt(modifiedAt string) TestDatabaseResource

	// WithNestedConfig configures with nested DTO
	WithNestedConfig(config *TestNestedDto) TestDatabaseResource

	// WithOptionalCallback configures with optional callback
	//
	// Parameters:
	//   - options: Optional parameters; see [WithOptionalCallbackOptions].
	WithOptionalCallback(options ...*WithOptionalCallbackOptions) TestDatabaseResource

	// WithOptionalString adds an optional string parameter
	//
	// Parameters:
	//   - options: Optional parameters; see [WithOptionalStringOptions].
	WithOptionalString(options ...*WithOptionalStringOptions) TestDatabaseResource

	// WithStatus sets the resource status
	WithStatus(status TestResourceStatus) TestDatabaseResource

	// WithUnionDependency adds a dependency from a string or another resource
	//
	// Allowed types for parameter dependency: string, ResourceWithConnectionString.
	WithUnionDependency(dependency any) TestDatabaseResource

	// WithValidator adds validation callback
	WithValidator(validator func(arg TestResourceContext) bool) TestDatabaseResource

	// Err returns the first error recorded on this wrapper, or nil if the wrapper
	// resolved successfully and no chained operation has failed.
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

// TestWithEnvironmentCallback configures environment with callback (test
// version)
func (s *testDatabaseResource) TestWithEnvironmentCallback(callback func(arg TestEnvironmentContext)) TestDatabaseResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackHandleArg(s.client, args, 0, newTestEnvironmentContextFromHandle))
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
				cb(callbackHandleArg(s.client, args, 0, newTestCallbackContextFromHandle))
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
			return cb(callbackHandleArg(s.client, args, 0, newTestResourceContextFromHandle))
		}
		reqArgs["validator"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withValidator", reqArgs); err != nil { s.setErr(err) }
	return s
}

// TestEnvironmentContext - Test environment context used in callbacks. Verifies
// property-like object pattern (ctx.name.get(), ctx.name.set()).
type TestEnvironmentContext interface {
	handleReference

	// Description gets the Description property
	Description() (*string, error)

	// Name gets the Name property
	Name() (string, error)

	// Priority gets the Priority property
	Priority() (float64, error)

	// SetDescription sets the Description property
	SetDescription(value *string) TestEnvironmentContext

	// SetName sets the Name property
	SetName(value string) TestEnvironmentContext

	// SetPriority sets the Priority property
	SetPriority(value float64) TestEnvironmentContext

	// Err returns the first error recorded on this wrapper, or nil if the wrapper
	// resolved successfully and no chained operation has failed.
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

// TestMutableCollectionContext is the public interface for handle type
// TestMutableCollectionContext.
type TestMutableCollectionContext interface {
	handleReference

	// SetCounts sets the Counts property
	SetCounts(value *Dict[string, float64]) TestMutableCollectionContext

	// SetTags sets the Tags property
	SetTags(value *List[string]) TestMutableCollectionContext

	// Counts gets the Counts property
	Counts() *Dict[string, float64]

	// Tags gets the Tags property
	Tags() *List[string]

	// Err returns the first error recorded on this wrapper, or nil if the wrapper
	// resolved successfully and no chained operation has failed.
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

// TestMutablePromiseCollisionResource is a mutable-property-only resource used
// to verify that property setters do not require Promise wrappers.
type TestMutablePromiseCollisionResource interface {
	handleReference

	// SetValue sets the test value.
	SetValue(value string) TestMutablePromiseCollisionResource

	// Value gets the test value.
	Value() (string, error)

	// Err returns the first error recorded on this wrapper, or nil if the wrapper
	// resolved successfully and no chained operation has failed.
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

// SetValue sets the test value.
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

// Value gets the test value.
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

	// AddTestChildDatabase adds a child database to a test Redis resource
	//
	// This method tests the factory method codegen pattern where a method on
	// builder type A returns builder type B (e.g.,
	// SqlServerServerResource.AddDatabase returning SqlServerDatabaseResource).
	//
	// Parameters:
	//   - options: Optional parameters; see [AddTestChildDatabaseOptions].
	AddTestChildDatabase(name string, options ...*AddTestChildDatabaseOptions) TestDatabaseResource

	// GetEndpoints gets the endpoints
	GetEndpoints() ([]string, error)

	// GetStatusAsync gets the status of the resource asynchronously
	//
	// Parameters:
	//   - options: Optional parameters; see [GetStatusAsyncOptions].
	GetStatusAsync(options ...*GetStatusAsyncOptions) (string, error)

	// TestWaitFor waits for another resource (test version)
	TestWaitFor(dependency Resource) TestRedisResource

	// TestWithEnvironmentCallback configures environment with callback (test
	// version)
	TestWithEnvironmentCallback(callback func(arg TestEnvironmentContext)) TestRedisResource

	// WaitForReadyAsync waits for the resource to be ready
	//
	// Parameters:
	//   - options: Optional parameters; see [WaitForReadyAsyncOptions].
	WaitForReadyAsync(timeout float64, options ...*WaitForReadyAsyncOptions) (bool, error)

	// WithCancellableOperation performs a cancellable operation
	WithCancellableOperation(operation func(arg *CancellationToken)) TestRedisResource

	// WithConcreteVaultResource configures a Redis resource with the concrete vault
	// resource as a parameter.
	//
	// Parameters:
	//   - resource: The parameter-only concrete vault resource.
	//
	// Returns: The Redis resource builder.
	WithConcreteVaultResource(resource Aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) TestRedisResource

	// WithConfig configures the resource with a DTO
	WithConfig(config *TestConfigDto) TestRedisResource

	// WithConnectionString sets the connection string using a reference expression
	WithConnectionString(connectionString *ReferenceExpression) TestRedisResource

	// WithConnectionStringDirect sets connection string using direct interface
	// target
	WithConnectionStringDirect(connectionString string) TestRedisResource

	// WithCorrelationId sets the correlation ID
	WithCorrelationId(correlationId string) TestRedisResource

	// WithCreatedAt sets the created timestamp
	WithCreatedAt(createdAt string) TestRedisResource

	// WithDataVolume adds a data volume with persistence
	//
	// Parameters:
	//   - options: Optional parameters; see
	//     [TestDatabaseResourceWithDataVolumeOptions].
	WithDataVolume(options ...*TestDatabaseResourceWithDataVolumeOptions) TestRedisResource

	// WithDependency adds a dependency on another resource
	WithDependency(dependency ResourceWithConnectionString) TestRedisResource

	// WithEndpoints sets the endpoints
	WithEndpoints(endpoints []string) TestRedisResource

	// WithEnvironmentVariables sets environment variables
	WithEnvironmentVariables(variables map[string]string) TestRedisResource

	// WithMergeEndpoint configures a named endpoint
	WithMergeEndpoint(endpointName string, port float64) TestRedisResource

	// WithMergeEndpointScheme configures a named endpoint with scheme
	WithMergeEndpointScheme(endpointName string, port float64, scheme string) TestRedisResource

	// WithMergeLabel adds a label to the resource
	WithMergeLabel(label string) TestRedisResource

	// WithMergeLabelCategorized adds a categorized label to the resource
	WithMergeLabelCategorized(label string, category string) TestRedisResource

	// WithMergeLogging configures resource logging
	//
	// Parameters:
	//   - options: Optional parameters; see [WithMergeLoggingOptions].
	WithMergeLogging(logLevel string, options ...*WithMergeLoggingOptions) TestRedisResource

	// WithMergeLoggingPath configures resource logging with file path
	//
	// Parameters:
	//   - options: Optional parameters; see [WithMergeLoggingPathOptions].
	WithMergeLoggingPath(logLevel string, logPath string, options ...*WithMergeLoggingPathOptions) TestRedisResource

	// WithMergeRoute configures a route
	WithMergeRoute(path string, method string, handler string, priority float64) TestRedisResource

	// WithMergeRouteMiddleware configures a route with middleware
	WithMergeRouteMiddleware(path string, method string, handler string, priority float64, middleware string) TestRedisResource

	// WithModifiedAt sets the modified timestamp
	WithModifiedAt(modifiedAt string) TestRedisResource

	// WithMultiParamHandleCallback - Tests multi-param callback destructuring
	WithMultiParamHandleCallback(callback func(arg1 TestCallbackContext, arg2 TestEnvironmentContext)) TestRedisResource

	// WithMutablePromiseCollisionResources configures a Redis resource with
	// mutable-property and parameter-only resources whose generated names collide.
	//
	// Parameters:
	//   - resource: The mutable-property-only resource whose unused Promise wrapper
	//     would collide.
	//   - resourcePromise: The parameter-only resource whose generated name matches
	//     that Promise wrapper.
	//
	// Returns: The Redis resource builder.
	WithMutablePromiseCollisionResources(resource TestMutablePromiseCollisionResource, resourcePromise TestMutablePromiseCollisionResourcePromise) TestRedisResource

	// WithNestedConfig configures with nested DTO
	WithNestedConfig(config *TestNestedDto) TestRedisResource

	// WithOptionalCallback configures with optional callback
	//
	// Parameters:
	//   - options: Optional parameters; see [WithOptionalCallbackOptions].
	WithOptionalCallback(options ...*WithOptionalCallbackOptions) TestRedisResource

	// WithOptionalString adds an optional string parameter
	//
	// Parameters:
	//   - options: Optional parameters; see [WithOptionalStringOptions].
	WithOptionalString(options ...*WithOptionalStringOptions) TestRedisResource

	// WithPersistence configures the Redis resource with persistence
	//
	// Parameters:
	//   - options: Optional parameters; see [WithPersistenceOptions].
	WithPersistence(options ...*WithPersistenceOptions) TestRedisResource

	// WithPromiseCollisionResources configures a Redis resource with parameter-only
	// resources whose generated names collide.
	//
	// Parameters:
	//   - resource: The resource whose unused Promise wrapper would collide.
	//   - resourcePromise: The resource whose generated name matches that Promise
	//     wrapper.
	//
	// Returns: The Redis resource builder.
	WithPromiseCollisionResources(resource TestPromiseCollisionResource, resourcePromise TestPromiseCollisionResourcePromise) TestRedisResource

	// WithRedisSpecific - Redis-specific configuration
	WithRedisSpecific(option string) TestRedisResource

	// WithScalarField sets a scalar field value
	//
	// Allowed types for parameter value: string, float64, bool (float64 accepts any
	// built-in Go numeric type).
	WithScalarField(path string, value any) TestRedisResource

	// WithStatus sets the resource status
	WithStatus(status TestResourceStatus) TestRedisResource

	// WithUnionDependency adds a dependency from a string or another resource
	//
	// Allowed types for parameter dependency: string, ResourceWithConnectionString.
	WithUnionDependency(dependency any) TestRedisResource

	// WithValidator adds validation callback
	WithValidator(validator func(arg TestResourceContext) bool) TestRedisResource

	// GetMetadata gets the metadata for the resource
	GetMetadata() *Dict[string, string]

	// GetTags gets the tags for the resource
	GetTags() *List[string]

	// Err returns the first error recorded on this wrapper, or nil if the wrapper
	// resolved successfully and no chained operation has failed.
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

// TestWithEnvironmentCallback configures environment with callback (test
// version)
func (s *testRedisResource) TestWithEnvironmentCallback(callback func(arg TestEnvironmentContext)) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackHandleArg(s.client, args, 0, newTestEnvironmentContextFromHandle))
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

// WithConcreteVaultResource configures a Redis resource with the concrete vault
// resource as a parameter.
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

// WithConnectionStringDirect sets connection string using direct interface
// target
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

// WithMultiParamHandleCallback - Tests multi-param callback destructuring
func (s *testRedisResource) WithMultiParamHandleCallback(callback func(arg1 TestCallbackContext, arg2 TestEnvironmentContext)) TestRedisResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if callback != nil {
		cb := callback
		shim := func(args ...any) any {
			cb(callbackHandleArg(s.client, args, 0, newTestCallbackContextFromHandle), callbackHandleArg(s.client, args, 1, newTestEnvironmentContextFromHandle))
			return nil
		}
		reqArgs["callback"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withMultiParamHandleCallback", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithMutablePromiseCollisionResources configures a Redis resource with
// mutable-property and parameter-only resources whose generated names collide.
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
				cb(callbackHandleArg(s.client, args, 0, newTestCallbackContextFromHandle))
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

// WithPromiseCollisionResources configures a Redis resource with parameter-only
// resources whose generated names collide.
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

// WithRedisSpecific - Redis-specific configuration
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

// WithScalarField sets a scalar field value
// Allowed types for parameter value: string, float64, bool (float64 accepts any built-in Go numeric type).
func (s *testRedisResource) WithScalarField(path string, value any) TestRedisResource {
	if s.err != nil { return s }
	switch value.(type) {
	case string, float64, bool, int, int8, int16, int32, int64, uint, uint8, uint16, uint32, uint64, float32:
	default:
		err := fmt.Errorf("aspire: WithScalarField: parameter %q must be one of [string, float64, bool], got %T", "value", value)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["path"] = serializeValue(path)
	if !isNil(value) { reqArgs["value"] = serializeValue(value) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withScalarField", reqArgs); err != nil { s.setErr(err) }
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
			return cb(callbackHandleArg(s.client, args, 0, newTestResourceContextFromHandle))
		}
		reqArgs["validator"] = s.client.registerCallback(shim)
	}
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.CodeGeneration.Go.Tests/withValidator", reqArgs); err != nil { s.setErr(err) }
	return s
}

// TestResourceContext - Test context type with exposed instance methods.
// Verifies [AspireExport(ExposeMethods=true)] generates async methods.
type TestResourceContext interface {
	handleReference

	// GetValueAsync - Instance method that should be exposed as async method.
	GetValueAsync() (string, error)

	// Name gets the Name property
	Name() (string, error)

	// SetName sets the Name property
	SetName(value string) TestResourceContext

	// SetValue sets the Value property
	SetValue(value float64) TestResourceContext

	// SetValueAsync - Instance method with parameter.
	SetValueAsync(value string) error

	// ValidateAsync - Instance method with return type.
	ValidateAsync() (bool, error)

	// Value gets the Value property
	Value() (float64, error)

	// Err returns the first error recorded on this wrapper, or nil if the wrapper
	// resolved successfully and no chained operation has failed.
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

// GetValueAsync - Instance method that should be exposed as async method.
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

// SetValueAsync - Instance method with parameter.
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

// ValidateAsync - Instance method with return type.
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

// TestReturnValueContext is the public interface for handle type
// TestReturnValueContext.
type TestReturnValueContext interface {
	handleReference

	// GetInt invokes the GetInt method
	GetInt() (float64, error)

	// GetIntTaskAsync invokes the GetIntTaskAsync method
	GetIntTaskAsync() (float64, error)

	// GetIntValueTaskAsync invokes the GetIntValueTaskAsync method
	GetIntValueTaskAsync() (float64, error)

	// GetNullableInt invokes the GetNullableInt method
	GetNullableInt() (*float64, error)

	// GetNullableIntTaskAsync invokes the GetNullableIntTaskAsync method
	GetNullableIntTaskAsync() (*float64, error)

	// GetNullableIntValueTaskAsync invokes the GetNullableIntValueTaskAsync method
	GetNullableIntValueTaskAsync() (*float64, error)

	// GetNullableString invokes the GetNullableString method
	GetNullableString() (*string, error)

	// GetNullableStringTaskAsync invokes the GetNullableStringTaskAsync method
	GetNullableStringTaskAsync() (*string, error)

	// GetNullableStringValueTaskAsync invokes the GetNullableStringValueTaskAsync
	// method
	GetNullableStringValueTaskAsync() (*string, error)

	// GetString invokes the GetString method
	GetString() (string, error)

	// GetStringTaskAsync invokes the GetStringTaskAsync method
	GetStringTaskAsync() (string, error)

	// GetStringValueTaskAsync invokes the GetStringValueTaskAsync method
	GetStringValueTaskAsync() (string, error)

	// Err returns the first error recorded on this wrapper, or nil if the wrapper
	// resolved successfully and no chained operation has failed.
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

// GetNullableStringValueTaskAsync invokes the GetNullableStringValueTaskAsync
// method
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
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResource", func(h *handle, c *client) any {
		return newHandleWrapperBase(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithConnectionString", func(h *handle, c *client) any {
		return newHandleWrapperBase(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.ITestMutablePromiseCollisionResourcePromise", func(h *handle, c *client) any {
		return newHandleWrapperBase(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.ITestPromiseCollisionResource", func(h *handle, c *client) any {
		return newHandleWrapperBase(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.ITestPromiseCollisionResourcePromise", func(h *handle, c *client) any {
		return newHandleWrapperBase(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.ITestVaultResource", func(h *handle, c *client) any {
		return newHandleWrapperBase(h, c)
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
	if _, ok := resolved["args"]; !ok { resolved["args"] = os.Args[1:] }
	if projectDirectory, ok := resolved["projectDirectory"].(string); !ok || projectDirectory == "" {
		if projectDirectory := os.Getenv("ASPIRE_PROJECT_DIRECTORY"); projectDirectory != "" {
			resolved["projectDirectory"] = projectDirectory
		} else if pwd, err := os.Getwd(); err == nil {
			resolved["projectDirectory"] = pwd
		}
	}
	if appHostFilePath, ok := resolved["appHostFilePath"].(string); !ok || appHostFilePath == "" {
		if appHostFilePath := os.Getenv("ASPIRE_APPHOST_FILEPATH"); appHostFilePath != "" { resolved["appHostFilePath"] = appHostFilePath }
	}
	if dashboardApplicationName, ok := resolved["dashboardApplicationName"].(string); ok && dashboardApplicationName == "" {
		delete(resolved, "dashboardApplicationName")
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
//
// Wrappers are not synchronized: err is read and written without a lock. The
// AppHost runs each callback on its own goroutine, so a callback should use the
// wrappers it receives as arguments, not a wrapper that the goroutine running
// the builder chain (or another callback) uses at the same time.

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
// capabilities. Slices and maps are decoded element by element, see decodeInto.
func decodeAs[T any](raw any) (T, error) {
	var zero T
	if raw == nil {
		return zero, nil
	}
	if v, ok := raw.(T); ok {
		return v, nil
	}
	if kind := reflect.TypeFor[T]().Kind(); kind == reflect.Slice || kind == reflect.Map {
		var out T
		if err := decodeInto(reflect.ValueOf(&out).Elem(), raw); err != nil {
			return zero, err
		}
		return out, nil
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

// decodeInto decodes raw into dst, which must be addressable and hold its zero
// value. wrapIfHandle replaces the handle envelopes inside a []any or
// map[string]any with wrapper values, and encoding/json cannot decode those into
// an interface element type such as []Resource. So a list or a string-keyed map
// is decoded one element at a time: an element that is already assignable to the
// element type, like a handle wrapper, is stored as is, and only the others take
// a JSON round trip.
func decodeInto(dst reflect.Value, raw any) error {
	if raw == nil {
		return nil
	}
	if v := reflect.ValueOf(raw); v.Type().AssignableTo(dst.Type()) {
		dst.Set(v)
		return nil
	}
	switch src := raw.(type) {
	case []any:
		if dst.Kind() == reflect.Slice {
			dst.Set(reflect.MakeSlice(dst.Type(), len(src), len(src)))
			for i, item := range src {
				if err := decodeInto(dst.Index(i), item); err != nil {
					return fmt.Errorf("aspire: cannot decode element %d of %v: %w", i, dst.Type(), err)
				}
			}
			return nil
		}
	case map[string]any:
		if dst.Kind() == reflect.Map && dst.Type().Key().Kind() == reflect.String {
			dst.Set(reflect.MakeMapWithSize(dst.Type(), len(src)))
			for key, item := range src {
				// Map values are not addressable, so decode into a fresh value and store a copy.
				elem := reflect.New(dst.Type().Elem()).Elem()
				if err := decodeInto(elem, item); err != nil {
					return fmt.Errorf("aspire: cannot decode entry %q of %v: %w", key, dst.Type(), err)
				}
				dst.SetMapIndex(reflect.ValueOf(key).Convert(dst.Type().Key()), elem)
			}
			return nil
		}
	}
	bytes, err := json.Marshal(raw)
	if err != nil {
		return err
	}
	return json.Unmarshal(bytes, dst.Addr().Interface())
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

// callbackHandleArg fetches a positional callback argument that is a handle
// and returns it as the callback's declared wrapper type T, built with wrap
// when the transport delivered a raw *handle. The host tags each handle
// argument with the callback's declared parameter type. For a resource builder
// that is a closed generic type id (IResourceBuilder<T>) that has no registered
// wrapper, so wrapIfHandle leaves the *handle unwrapped and callbackArg alone
// would hand the user's callback a nil interface. Arguments that are already
// wrapped, nil or missing fall through to callbackArg.
func callbackHandleArg[T any](c *client, args []any, idx int, wrap func(*handle, *client) T) T {
	if idx < len(args) {
		if h, ok := args[idx].(*handle); ok {
			return wrap(h, c)
		}
	}
	return callbackArg[T](args, idx)
}

// ── deepUpdate ───────────────────────────────────────────────────────────────
//
// Used by generated code to merge variadic Options structs:
//
//	merged := &AddRedisOptions{}
//	for _, opt := range options {
//	    if opt != nil { merged = deepUpdate(merged, opt) }
//	}
//
// Neither argument is modified. Fields are merged by reflection rather than
// through a JSON round trip because Options structs hold values encoding/json
// cannot represent: callbacks (every func field fails to marshal, even a nil
// one), *CancellationToken and handle wrappers.
//
// Struct fields and map entries present on both sides are merged recursively.
// Any other value, including nil, is taken from src. A struct with unexported
// fields (such as *CancellationToken) is opaque and is taken from src whole.
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

	return mergeValues(reflect.ValueOf(dst), reflect.ValueOf(src), 0).Interface().(T)
}

func mergeValues(dst, src reflect.Value, depth int) reflect.Value {
	const depthLimit = 32
	if depth > depthLimit {
		panic("aspire: deep update recursion limit of '32' exceeded")
	}

	// Interface-typed fields (any, handles) are merged by their dynamic type.
	if dst.Kind() == reflect.Interface && src.Kind() == reflect.Interface && !dst.IsNil() && !src.IsNil() {
		dst, src = dst.Elem(), src.Elem()
	}
	if dst.Type() != src.Type() {
		return src
	}

	switch src.Kind() {
	case reflect.Pointer:
		if dst.IsNil() || src.IsNil() || !isPlainStruct(src.Type().Elem()) {
			return src
		}
		merged := reflect.New(src.Type().Elem())
		merged.Elem().Set(mergeValues(dst.Elem(), src.Elem(), depth))
		return merged

	case reflect.Struct:
		if !isPlainStruct(src.Type()) {
			return src
		}
		merged := reflect.New(src.Type()).Elem()
		for i := 0; i < merged.NumField(); i++ {
			merged.Field(i).Set(mergeValues(dst.Field(i), src.Field(i), depth+1))
		}
		return merged

	case reflect.Map:
		if dst.IsNil() {
			return src
		}
		if src.IsNil() {
			return dst
		}
		merged := reflect.MakeMapWithSize(src.Type(), dst.Len()+src.Len())
		for it := dst.MapRange(); it.Next(); {
			merged.SetMapIndex(it.Key(), it.Value())
		}
		for it := src.MapRange(); it.Next(); {
			value := it.Value()
			if existing := merged.MapIndex(it.Key()); existing.IsValid() {
				value = mergeValues(existing, value, depth+1)
			}
			merged.SetMapIndex(it.Key(), value)
		}
		return merged

	default:
		return src
	}
}

// isPlainStruct reports whether every field of t is exported, which is what
// lets reflection build a merged copy of it.
func isPlainStruct(t reflect.Type) bool {
	if t.Kind() != reflect.Struct {
		return false
	}
	for i := 0; i < t.NumField(); i++ {
		if !t.Field(i).IsExported() {
			return false
		}
	}
	return true
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
// The value comes off the wire, so a handle envelope whose $handle or $type is
// not a string is reported as an error instead of being trusted.
func wrapIfHandle(value any, c *client) (any, error) {
	if m, ok := value.(map[string]any); ok && isMarshaledHandle(m) {
		handleID, idIsString := m["$handle"].(string)
		typeID, typeIsString := m["$type"].(string)
		if !idIsString || !typeIsString {
			return nil, fmt.Errorf("aspire: malformed handle in AppHost message: $handle is %T and $type is %T, both must be strings", m["$handle"], m["$type"])
		}
		h := &handle{HandleID: handleID, TypeID: typeID}
		if c != nil {
			if factory := c.lookupWrapper(h.TypeID); factory != nil {
				return factory(h, c), nil
			}
		}
		return h, nil
	}

	if s, ok := value.([]any); ok {
		for i, v := range s {
			wrapped, err := wrapIfHandle(v, c)
			if err != nil {
				return nil, err
			}
			s[i] = wrapped
		}
		return s, nil
	}

	if m, ok := value.(map[string]any); ok {
		for k, v := range m {
			wrapped, err := wrapIfHandle(v, c)
			if err != nil {
				return nil, err
			}
			m[k] = wrapped
		}
		return m, nil
	}

	return value, nil
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

	// Callbacks run on their own goroutine, so a panic in user code would
	// otherwise terminate the whole process. Answer the AppHost with the error
	// the other runtimes send for a throwing callback (-32603, "Callback
	// execution failed: <message>"). Only the panic value is reported; the stack
	// stays inside the process.
	defer func() {
		if r := recover(); r != nil {
			c.enqueue(map[string]any{
				"jsonrpc": "2.0",
				"id":      requestID,
				"error":   map[string]any{"code": -32603, "message": fmt.Sprintf("Callback execution failed: %v", r)},
			})
		}
	}()

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
// watcher that client.registerCancellation attaches to a CancellationToken
// also notifies the AppHost, but this method does not need to wait for that
// round-trip — it returns ctx.Err() as soon as ctx is done.
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

	// cancellationsMu guards the per-client cancellation registry, which maps
	// each cancellation ID handed to the AppHost to the function that detaches
	// its watcher from the token's context.
	cancellationsMu     sync.Mutex
	cancellations       map[string]func() bool
	cancellationCounter atomic.Int64
}

// newClient constructs a client; callers must invoke connect before using it.
func newClient(socketPath string) *client {
	return &client{
		socketPath:    socketPath,
		wrappers:      make(map[string]handleWrapperFactory),
		callbacks:     make(map[string]func(...any) any),
		cancellations: make(map[string]func() bool),
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
				wrapped, err := wrapIfHandle(val, c)
				if err != nil {
					return nil, err
				}
				positionalArgs = append(positionalArgs, wrapped)
			} else {
				break
			}
		}
	} else if args != nil {
		wrapped, err := wrapIfHandle(args, c)
		if err != nil {
			return nil, err
		}
		positionalArgs = append(positionalArgs, wrapped)
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
// returns its ID. The server is notified when the token is canceled, until
// releaseCancellations detaches the watcher once the capability call returns.
//
// The ID combines a per-client counter with the clock, like callback IDs. The
// AppHost keys its cancellation registry by this ID and hands every request
// that reuses an ID the same CancellationTokenSource, so two tokens registered
// in the same clock tick must not share one.
func (c *client) registerCancellation(token *CancellationToken) string {
	if token == nil {
		return ""
	}
	id := fmt.Sprintf("ct_%d_%d", c.cancellationCounter.Add(1), time.Now().UnixMilli())
	// AfterFunc starts a goroutine only once the context is done, so a token
	// that is never canceled does not park one goroutine per registration.
	stop := context.AfterFunc(token.ctx, func() { c.cancelToken(id) })
	c.cancellationsMu.Lock()
	// Canceling a context closes its Done channel before it starts the AfterFunc
	// goroutine, so a call that returns because of this very cancel can get here
	// first. Stopping the watcher then would drop the cancelToken request, so
	// only a token that is still live is stopped.
	c.cancellations[id] = func() bool { return token.ctx.Err() == nil && stop() }
	c.cancellationsMu.Unlock()
	return id
}

// releaseCancellations detaches the watchers of the cancellation IDs carried
// in args. invokeCapability calls it when the call returns, so a long-lived
// token used for many calls does not keep one watcher per call. Generated code
// and ReferenceExpression.GetValue only ever put these IDs in top-level args.
func (c *client) releaseCancellations(args map[string]any) {
	c.cancellationsMu.Lock()
	defer c.cancellationsMu.Unlock()
	for _, v := range args {
		id, ok := v.(string)
		if !ok {
			continue
		}
		if stop, found := c.cancellations[id]; found {
			stop()
			delete(c.cancellations, id)
		}
	}
}

// connect establishes the connection to the AppHost server and starts the
// background reader and writer goroutines.
func (c *client) connect(ctx context.Context, timeout time.Duration) error {
	c.mu.Lock()
	connected := c.conn != nil
	c.mu.Unlock()
	if connected {
		return nil
	}

	// Dial without holding c.mu: the dial can take up to timeout, and
	// sendRequest, disconnect and onDisconnect all wait on the same lock.
	rawConn, err := openConnection(c.socketPath, timeout)
	if err != nil {
		return fmt.Errorf("failed to connect to AppHost: %w", err)
	}

	authToken := os.Getenv("ASPIRE_REMOTE_APPHOST_TOKEN")
	if authToken == "" {
		cErr := rawConn.Close()
		return errors.Join(errors.New("ASPIRE_REMOTE_APPHOST_TOKEN environment variable is not set"), cErr)
	}

	c.mu.Lock()
	if c.conn != nil {
		// A concurrent connect installed its connection while this one was dialing.
		c.mu.Unlock()
		_ = rawConn.Close()
		return nil
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
	defer c.releaseCancellations(args)

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
	return wrapIfHandle(result, c)
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

// rpcError is the error object of a JSON-RPC error response. Error returns the
// message alone; the code and the optional data stay reachable for callers that
// need them:
//
//	var rpcErr interface{ Code() int }
//	if errors.As(err, &rpcErr) { ... }
type rpcError struct {
	code    int
	message string
	data    any
}

func (e *rpcError) Error() string { return e.message }
func (e *rpcError) Code() int     { return e.code }
func (e *rpcError) Data() any     { return e.data }

func extractResult(response map[string]any) (any, error) {
	if errObj, hasErr := response["error"]; hasErr {
		errMap, _ := errObj.(map[string]any)
		rpcErr := &rpcError{message: getString(errMap, "message"), data: errMap["data"]}
		// A decoded response carries the code as float64; the error that
		// connection.close synthesizes for pending requests carries an int.
		switch code := errMap["code"].(type) {
		case float64:
			rpcErr.code = int(code)
		case int:
			rpcErr.code = code
		}
		if rpcErr.message == "" {
			rpcErr.message = fmt.Sprintf("AppHost returned a JSON-RPC error (code %d)", rpcErr.code)
		}
		return nil, rpcErr
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

// maxMessageSize caps the Content-Length accepted from the AppHost so a corrupt
// or hostile stream cannot make readMessage allocate an arbitrarily large body.
// It matches the cap in the Python runtime.
const maxMessageSize = 64 * 1024 * 1024

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
	if length > maxMessageSize {
		return nil, fmt.Errorf("content-length %d exceeds the maximum message size of %d bytes", length, maxMessageSize)
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
