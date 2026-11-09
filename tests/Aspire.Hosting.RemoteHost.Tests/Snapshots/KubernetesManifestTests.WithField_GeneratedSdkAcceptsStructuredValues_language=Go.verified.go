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
// Handle wrappers
// ============================================================================

// KubernetesManifestResource represents an arbitrary Kubernetes manifest that
// is emitted with a Kubernetes service.
type KubernetesManifestResource interface {
	handleReference

	// WithField adds or updates a manifest field using a dot-separated path.
	//
	// Values can be JSON-compatible primitives, objects, or arrays. To preserve
	// keys containing dots, such as app.kubernetes.io/component, pass an object
	// containing those keys at the parent field path. Setting a field replaces any
	// existing value at that path.
	//
	// Parameters:
	//   - path: The dot-separated manifest field path, for example spec.replicas or
	//     data.username.
	//   - value: The JSON-compatible field value.
	//
	// Returns: This manifest resource.
	WithField(path string, value any) KubernetesManifestResource

	// Err returns the first error recorded on this wrapper, or nil if the wrapper
	// resolved successfully and no chained operation has failed.
	Err() error
}

// kubernetesManifestResource is the unexported impl of KubernetesManifestResource.
type kubernetesManifestResource struct {
	*resourceBuilderBase
}

// newKubernetesManifestResourceFromHandle wraps an existing handle as KubernetesManifestResource.
func newKubernetesManifestResourceFromHandle(h *handle, c *client) KubernetesManifestResource {
	return &kubernetesManifestResource{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// WithField adds or updates a manifest field using a dot-separated path.
func (s *kubernetesManifestResource) WithField(path string, value any) KubernetesManifestResource {
	if s.Err() != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"context": s.handle.ToJSON(),
	}
	reqArgs["path"] = serializeValue(path)
	if !isNil(value) { reqArgs["value"] = serializeValue(value) }
	if _, err := s.client.invokeCapability(ctx, "Aspire.Hosting.Kubernetes/withField", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ============================================================================
// Per-client handle wrapper registration
// ============================================================================

func registerWrappers(c *client) {
	c.registerHandleWrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpression", func(h *handle, c *client) any {
		return newHandleBackedReferenceExpression(h, c)
	})
	c.registerHandleWrapper("Aspire.Hosting.Kubernetes/Aspire.Hosting.Kubernetes.KubernetesManifestResource", func(h *handle, c *client) any {
		return newKubernetesManifestResourceFromHandle(h, c)
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
	if err := b.Err(); err != nil { return nil, err }
	result, err := b.client.invokeCapability(context.Background(), "Aspire.Hosting/build", map[string]any{
		"context": b.handle.ToJSON(),
	})
	if err != nil { return nil, err }
	app, ok := result.(DistributedApplication)
	if !ok { return nil, fmt.Errorf("aspire: build returned unexpected type %T", result) }
	return app, nil
}

// CreateBuilder establishes a connection to the AppHost and returns a new builder.
func CreateBuilder() (DistributedApplicationBuilder, error) {
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
	return &distributedApplicationBuilder{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), c)}, nil
}