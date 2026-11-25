// aspire.go - Capability-based Aspire SDK
// This SDK uses the ATS (Aspire Type System) capability API.
// Capabilities are endpoints like 'Aspire.Hosting/createBuilder'.
//
// GENERATED CODE - DO NOT EDIT

package aspire

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"strings"
	"time"
)

// Compile-time references to keep imports used in minimal SDKs.
var _ = context.Background
var _ = json.Number("")
var _ = fmt.Errorf
var _ = os.Getenv
var _ = strings.EqualFold
var _ = time.Second

// ============================================================================
// DTOs
// ============================================================================

// CreateBuilderOptions - Options for creating a distributed application builder
// from polyglot apphosts.
type CreateBuilderOptions struct {
	// Args is the command line arguments.
	Args []string `json:"args,omitempty"`
	// ProjectDirectory is the directory containing the AppHost project file.
	ProjectDirectory *string `json:"projectDirectory,omitempty"`
	// AppHostFilePath is the full path to the AppHost file (e.g., apphost.ts,
	// apphost.py). Used for consistent socket path computation across CLI and
	// AppHost.
	AppHostFilePath *string `json:"appHostFilePath,omitempty"`
	// ContainerRegistryOverride - When containers are used, use this value to
	// override the container registry.
	ContainerRegistryOverride *string `json:"containerRegistryOverride,omitempty"`
	// DisableDashboard determines whether the dashboard is disabled.
	DisableDashboard bool `json:"disableDashboard,omitempty"`
	// DashboardApplicationName is the application name to display in the dashboard.
	DashboardApplicationName *string `json:"dashboardApplicationName,omitempty"`
	// AllowUnsecuredTransport allows the use of HTTP urls for the AppHost resource
	// endpoint.
	AllowUnsecuredTransport bool `json:"allowUnsecuredTransport,omitempty"`
	// EnableResourceLogging enables resource logging.
	EnableResourceLogging bool `json:"enableResourceLogging,omitempty"`
	// DefaultResourceWatchEnabled indicates whether compatible resources are
	// watched by default. An explicit value overrides AppHost configuration;
	// individual resources can override the default. AppHost edits and IDE
	// ownership are configured separately.
	DefaultResourceWatchEnabled *bool `json:"defaultResourceWatchEnabled,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *CreateBuilderOptions) ToMap() map[string]any {
	m := map[string]any{}
	if d.Args != nil {
		m["args"] = serializeValue(d.Args)
	}
	if d.ProjectDirectory != nil {
		m["projectDirectory"] = serializeValue(d.ProjectDirectory)
	}
	if d.AppHostFilePath != nil {
		m["appHostFilePath"] = serializeValue(d.AppHostFilePath)
	}
	if d.ContainerRegistryOverride != nil {
		m["containerRegistryOverride"] = serializeValue(d.ContainerRegistryOverride)
	}
	m["disableDashboard"] = serializeValue(d.DisableDashboard)
	if d.DashboardApplicationName != nil {
		m["dashboardApplicationName"] = serializeValue(d.DashboardApplicationName)
	}
	m["allowUnsecuredTransport"] = serializeValue(d.AllowUnsecuredTransport)
	m["enableResourceLogging"] = serializeValue(d.EnableResourceLogging)
	if d.DefaultResourceWatchEnabled != nil {
		m["defaultResourceWatchEnabled"] = serializeValue(d.DefaultResourceWatchEnabled)
	}
	return m
}

// CreateBuilderOption configures CreateBuilder.
type CreateBuilderOption interface {
	apply(*CreateBuilderOptions)
}

func (option *CreateBuilderOptions) apply(options *CreateBuilderOptions) {
	if option != nil {
		*options = *deepUpdate(options, option)
	}
}

// CreateBuilderArgs sets Args for CreateBuilder.
type CreateBuilderArgs []string

func (value CreateBuilderArgs) apply(options *CreateBuilderOptions) {
	options.Args = ([]string)(value)
}

// CreateBuilderProjectDirectory sets ProjectDirectory for CreateBuilder.
type CreateBuilderProjectDirectory string

func (value CreateBuilderProjectDirectory) apply(options *CreateBuilderOptions) {
	options.ProjectDirectory = (*string)(&value)
}

// CreateBuilderAppHostFilePath sets AppHostFilePath for CreateBuilder.
type CreateBuilderAppHostFilePath string

func (value CreateBuilderAppHostFilePath) apply(options *CreateBuilderOptions) {
	options.AppHostFilePath = (*string)(&value)
}

// CreateBuilderContainerRegistryOverride sets ContainerRegistryOverride for CreateBuilder.
type CreateBuilderContainerRegistryOverride string

func (value CreateBuilderContainerRegistryOverride) apply(options *CreateBuilderOptions) {
	options.ContainerRegistryOverride = (*string)(&value)
}

// CreateBuilderDisableDashboard sets DisableDashboard for CreateBuilder.
type CreateBuilderDisableDashboard bool

func (value CreateBuilderDisableDashboard) apply(options *CreateBuilderOptions) {
	options.DisableDashboard = (bool)(value)
}

// CreateBuilderDashboardApplicationName sets DashboardApplicationName for CreateBuilder.
type CreateBuilderDashboardApplicationName string

func (value CreateBuilderDashboardApplicationName) apply(options *CreateBuilderOptions) {
	options.DashboardApplicationName = (*string)(&value)
}

// CreateBuilderAllowUnsecuredTransport sets AllowUnsecuredTransport for CreateBuilder.
type CreateBuilderAllowUnsecuredTransport bool

func (value CreateBuilderAllowUnsecuredTransport) apply(options *CreateBuilderOptions) {
	options.AllowUnsecuredTransport = (bool)(value)
}

// CreateBuilderEnableResourceLogging sets EnableResourceLogging for CreateBuilder.
type CreateBuilderEnableResourceLogging bool

func (value CreateBuilderEnableResourceLogging) apply(options *CreateBuilderOptions) {
	options.EnableResourceLogging = (bool)(value)
}

// CreateBuilderDefaultResourceWatchEnabled sets DefaultResourceWatchEnabled for CreateBuilder.
type CreateBuilderDefaultResourceWatchEnabled bool

func (value CreateBuilderDefaultResourceWatchEnabled) apply(options *CreateBuilderOptions) {
	options.DefaultResourceWatchEnabled = (*bool)(&value)
}

// ============================================================================
// Per-client handle wrapper registration
// ============================================================================

func registerWrappers(c *client) {
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpression", func(h *handle, c *client) any {
		return newHandleBackedReferenceExpression(h, c)
	})
}

// ============================================================================
// Builder construction & Build()
// ============================================================================

// DistributedApplicationBuilder is the entry point to the Aspire SDK.
type DistributedApplicationBuilder interface {
	handleReference
	Build() (DistributedApplication, error)
}

type distributedApplicationBuilder struct{ *resourceBuilderBase }

// DistributedApplication is returned by Build(); it represents the running application.
type DistributedApplication interface{ handleReference }

type distributedApplication struct{ *resourceBuilderBase }

// Err returns an error combining every failure recorded by this builder or by
// any handle created from it.
func (b *distributedApplicationBuilder) Err() error {
	if err := b.client.combinedError(); err != nil {
		return err
	}
	return b.resourceBuilderBase.Err()
}

// Build invokes the build capability and returns the running application.
func (b *distributedApplicationBuilder) Build() (DistributedApplication, error) {
	if err := b.Err(); err != nil {
		return nil, err
	}
	result, err := b.client.invokeCapability(context.Background(), "Aspire.Hosting/build", map[string]any{
		"context": b.handle.ToJSON(),
	})
	if err != nil {
		return nil, err
	}
	app, ok := result.(DistributedApplication)
	if !ok {
		return nil, fmt.Errorf("aspire: build returned unexpected type %T", result)
	}
	return app, nil
}

// CreateBuilder establishes a connection to the AppHost and returns a new builder.
func CreateBuilder(options ...CreateBuilderOption) (DistributedApplicationBuilder, error) {
	socketPath := os.Getenv("REMOTE_APP_HOST_SOCKET_PATH")
	if socketPath == "" {
		return nil, fmt.Errorf("REMOTE_APP_HOST_SOCKET_PATH environment variable not set. Run this application using `aspire run`")
	}
	c := newClient(socketPath)
	if err := c.connect(context.Background(), 5*time.Second); err != nil {
		return nil, err
	}
	c.onDisconnect(func() { os.Exit(1) })
	registerWrappers(c)

	resolved := map[string]any{}
	if len(options) > 0 {
		merged := &CreateBuilderOptions{}
		for _, opt := range options {
			if opt != nil {
				opt.apply(merged)
			}
		}
		for k, v := range merged.ToMap() {
			resolved[k] = v
		}
	}
	if _, ok := resolved["args"]; !ok {
		resolved["args"] = os.Args[1:]
	}
	if projectDirectory, ok := resolved["projectDirectory"].(string); !ok || projectDirectory == "" {
		if projectDirectory := os.Getenv("ASPIRE_PROJECT_DIRECTORY"); projectDirectory != "" {
			resolved["projectDirectory"] = projectDirectory
		} else if pwd, err := os.Getwd(); err == nil {
			resolved["projectDirectory"] = pwd
		}
	}
	if appHostFilePath, ok := resolved["appHostFilePath"].(string); !ok || appHostFilePath == "" {
		if appHostFilePath := os.Getenv("ASPIRE_APPHOST_FILEPATH"); appHostFilePath != "" {
			resolved["appHostFilePath"] = appHostFilePath
		}
	}
	if dashboardApplicationName, ok := resolved["dashboardApplicationName"].(string); ok && dashboardApplicationName == "" {
		delete(resolved, "dashboardApplicationName")
	}

	result, err := c.invokeCapability(context.Background(), "Aspire.Hosting/createBuilder", map[string]any{"argsOrOptions": resolved})
	if err != nil {
		return nil, err
	}
	href, ok := result.(handleReference)
	if !ok {
		return nil, fmt.Errorf("aspire: createBuilder returned unexpected type %T", result)
	}
	return &distributedApplicationBuilder{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), c)}, nil
}
