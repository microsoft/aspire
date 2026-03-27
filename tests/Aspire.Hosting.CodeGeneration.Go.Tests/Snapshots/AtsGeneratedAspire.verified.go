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

// IResourceWithEnvironment wraps a handle for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithEnvironment.
type IResourceWithEnvironment struct {
	HandleWrapperBase
}

// NewIResourceWithEnvironment creates a new IResourceWithEnvironment.
func NewIResourceWithEnvironment(handle *Handle, client *AspireClient) *IResourceWithEnvironment {
	return &IResourceWithEnvironment{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
	}
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

// WithDataVolume adds a data volume
func (s *TestDatabaseResource) WithDataVolume(name *string) (*TestDatabaseResource, error) {
	reqArgs := map[string]any{
		"builder": SerializeValue(s.Handle()),
	}
	if name != nil {
		reqArgs["name"] = SerializeValue(name)
	}
	result, err := s.Client().InvokeCapability("Aspire.Hosting.CodeGeneration.Go.Tests/withDataVolume", reqArgs)
	if err != nil {
		return nil, err
	}
	return result.(*TestDatabaseResource), nil
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

// ============================================================================
// Handle wrapper registrations
// ============================================================================

func init() {
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
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResource", func(h *Handle, c *AspireClient) any {
		return NewIResource(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithConnectionString", func(h *Handle, c *AspireClient) any {
		return NewIResourceWithConnectionString(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestVaultResource", func(h *Handle, c *AspireClient) any {
		return NewTestVaultResource(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting.CodeGeneration.Go.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.ITestVaultResource", func(h *Handle, c *AspireClient) any {
		return NewITestVaultResource(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder", func(h *Handle, c *AspireClient) any {
		return NewIDistributedApplicationBuilder(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithEnvironment", func(h *Handle, c *AspireClient) any {
		return NewIResourceWithEnvironment(h, c)
	})
	RegisterHandleWrapper("Aspire.Hosting/List<string>", func(h *Handle, c *AspireClient) any {
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

