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
	if d.Args != nil { m["args"] = serializeValue(d.Args) }
	if d.ProjectDirectory != nil { m["projectDirectory"] = serializeValue(d.ProjectDirectory) }
	if d.AppHostFilePath != nil { m["appHostFilePath"] = serializeValue(d.AppHostFilePath) }
	if d.ContainerRegistryOverride != nil { m["containerRegistryOverride"] = serializeValue(d.ContainerRegistryOverride) }
	m["disableDashboard"] = serializeValue(d.DisableDashboard)
	if d.DashboardApplicationName != nil { m["dashboardApplicationName"] = serializeValue(d.DashboardApplicationName) }
	m["allowUnsecuredTransport"] = serializeValue(d.AllowUnsecuredTransport)
	m["enableResourceLogging"] = serializeValue(d.EnableResourceLogging)
	if d.DefaultResourceWatchEnabled != nil { m["defaultResourceWatchEnabled"] = serializeValue(d.DefaultResourceWatchEnabled) }
	return m
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

type distributedApplicationBuilder struct { *resourceBuilderBase }

// DistributedApplication is returned by Build(); it represents the running application.
type DistributedApplication interface { handleReference }

type distributedApplication struct { *resourceBuilderBase }

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
	return &distributedApplicationBuilder{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), c)}, nil
}

