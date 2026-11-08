// ============================================================================
// Enums
// ============================================================================

// DocumentedStatus describes the lifecycle state of a documented resource.
type DocumentedStatus string

const (
	// DocumentedStatusPending - The resource is pending.
	DocumentedStatusPending DocumentedStatus = "Pending"
	// DocumentedStatusRunning indicates that the resource is running.
	//
	// It accepts requests.
	DocumentedStatusRunning DocumentedStatus = "Running"
	// DocumentedStatusRetired is a resource that was retired.
	DocumentedStatusRetired DocumentedStatus = "Retired"
	DocumentedStatusUnknown DocumentedStatus = "Unknown"
)

// ============================================================================
// DTOs
// ============================================================================

// DocumentedSettings represents the settings of a documented resource.
//
// Settings are applied when the resource starts.
type DocumentedSettings struct {
	// DisplayName is the display name.
	DisplayName string `json:"displayName,omitempty"`
	// Enabled indicates whether the resource is enabled.
	Enabled bool `json:"enabled,omitempty"`
	// Port is the port of the resource.
	Port float64 `json:"port,omitempty"`
	Undocumented string `json:"undocumented,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *DocumentedSettings) ToMap() map[string]any { ... }

// ============================================================================
// Exported Values
// ============================================================================

// DocumentedDefaults groups related exported values.
var DocumentedDefaults = struct {
	// Primary is the primary settings.
	Primary *DocumentedSettings
	// Secondary is the secondary settings.
	Secondary *DocumentedSettings
}{
	Primary: &DocumentedSettings{DisplayName: "primary", Enabled: true, Port: 8080},
	Secondary: &DocumentedSettings{DisplayName: "secondary", Enabled: false, Port: 8081},
}

// ============================================================================
// Marker interfaces (from interface-metadata types)
// ============================================================================

// DocumentedMarker marks resources that carry documentation.
type DocumentedMarker interface {
	handleReference
}

// ============================================================================
// Handle wrappers
// ============================================================================

// DocumentedResource represents a documented resource.
//
// Create one with the builder.
type DocumentedResource interface {
	handleReference

	// DisplayName gets the display name.
	DisplayName() (string, error)

	// GetStatus gets the status of the resource.
	//
	// Returns: The status of the resource.
	GetStatus() (string, error)

	// IsEnabled reports whether the resource is enabled.
	IsEnabled() (bool, error)

	// Port is the port of the resource.
	Port() (float64, error)

	// Reset resets the resource to its defaults.
	//
	// Parameters:
	//   - force: Whether to discard pending changes.
	Reset(force bool) error

	// SetDisplayName sets the display name.
	SetDisplayName(value string) DocumentedResource
	Undocumented() error

	// WithDirectOptions applies the settings.
	//
	// Parameters:
	//   - options: The settings to apply.
	WithDirectOptions(options ...*DocumentedSettings) DocumentedResource

	// WithDocumentation adds documentation to the resource.
	//
	// Usage.
	//
	//	resource.WithDocumentation("docs")
	//
	// The name and settings arguments are required, so pass nil for neither. See
	// [DocumentedSettings], IResourceBuilder and WithDocumentation.
	//
	//   - name: Must be unique.
	//   - port: Defaults to 8080.
	//
	// Parameters:
	//   - name: The name of the documentation.
	//   - settings: The settings to apply, or nil to keep the defaults.
	//   - options: Optional parameters; see [WithDocumentationOptions].
	//
	// Returns: The resource builder.
	WithDocumentation(name string, settings *DocumentedSettings, options ...*WithDocumentationOptions) DocumentedResource

	// WithHeading adds a heading.
	//
	// Overview.
	//
	//   - first item
	WithHeading() DocumentedResource

	// WithLegacyDocumentation adds the legacy documentation.
	//
	// Deprecated: This API is obsolete.
	WithLegacyDocumentation() DocumentedResource

	// WithObsoleteDocumentation adds documentation using the legacy convention.
	//
	// Deprecated: Use WithDocumentation instead.
	WithObsoleteDocumentation(name string) DocumentedResource

	// WithPreviewDocumentation adds preview documentation.
	//
	// # Experimental
	//
	// Notice: This API is EXPERIMENTAL and may be changed or removed in a later
	// release.
	WithPreviewDocumentation() DocumentedResource

	// WithPreviewStatus sets the preview status.
	//
	// Parameters:
	//   - status: The status to set.
	//
	// # Experimental
	//
	// Notice: This API is EXPERIMENTAL and may be changed or removed in a later
	// release.
	//
	// Deprecated: Use WithDocumentation instead.
	WithPreviewStatus(status DocumentedStatus) DocumentedResource

	// WithSpecialText handles <special> characters & line endings. The second line
	// follows a CRLF. The third line follows a CR.
	//
	// A remark that is long enough to be wrapped across several comment lines
	// without splitting a word, and that keeps going for a while so the wrapping is
	// visible.
	WithSpecialText() DocumentedResource

	// Tags gets the tags of the resource.
	Tags() *List[string]

	// Err returns the first error recorded on this wrapper, or nil if the wrapper
	// resolved successfully and no chained operation has failed.
	Err() error
}

// documentedResource is the unexported impl of DocumentedResource.
type documentedResource struct {
	*resourceBuilderBase
	tags *List[string]
}

// newDocumentedResourceFromHandle wraps an existing handle as DocumentedResource.
func newDocumentedResourceFromHandle(h *handle, c *client) DocumentedResource { ... }

// DisplayName gets the display name.
func (s *documentedResource) DisplayName() (string, error) { ... }

// GetStatus gets the status of the resource.
func (s *documentedResource) GetStatus() (string, error) { ... }

// IsEnabled reports whether the resource is enabled.
func (s *documentedResource) IsEnabled() (bool, error) { ... }

// Port is the port of the resource.
func (s *documentedResource) Port() (float64, error) { ... }

// Reset resets the resource to its defaults.
func (s *documentedResource) Reset(force bool) error { ... }

// SetDisplayName sets the display name.
func (s *documentedResource) SetDisplayName(value string) DocumentedResource { ... }

// Tags gets the tags of the resource.
func (s *documentedResource) Tags() *List[string] { ... }

func (s *documentedResource) Undocumented() error { ... }

// WithDirectOptions applies the settings.
func (s *documentedResource) WithDirectOptions(options ...*DocumentedSettings) DocumentedResource { ... }

// WithDocumentation adds documentation to the resource.
func (s *documentedResource) WithDocumentation(name string, settings *DocumentedSettings, options ...*WithDocumentationOptions) DocumentedResource { ... }

// WithHeading adds a heading.
func (s *documentedResource) WithHeading() DocumentedResource { ... }

// WithLegacyDocumentation adds the legacy documentation.
func (s *documentedResource) WithLegacyDocumentation() DocumentedResource { ... }

// WithObsoleteDocumentation adds documentation using the legacy convention.
func (s *documentedResource) WithObsoleteDocumentation(name string) DocumentedResource { ... }

// WithPreviewDocumentation adds preview documentation.
func (s *documentedResource) WithPreviewDocumentation() DocumentedResource { ... }

// WithPreviewStatus sets the preview status.
func (s *documentedResource) WithPreviewStatus(status DocumentedStatus) DocumentedResource { ... }

// WithSpecialText handles <special> characters & line endings. The second line
// follows a CRLF. The third line follows a CR.
func (s *documentedResource) WithSpecialText() DocumentedResource { ... }

// ============================================================================
// Options structs
// ============================================================================

// WithDocumentationOptions carries optional parameters for WithDocumentation.
type WithDocumentationOptions struct {
	// Port is the port to use. Pass name to override.
	Port *float64 `json:"port,omitempty"`
	// Status is the initial status.
	Status *DocumentedStatus `json:"status,omitempty"`
	Enabled *bool `json:"enabled,omitempty"`
}

func (o *WithDocumentationOptions) ToMap() map[string]any { ... }
