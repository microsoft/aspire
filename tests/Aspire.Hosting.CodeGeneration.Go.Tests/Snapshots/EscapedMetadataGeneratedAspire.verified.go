// ============================================================================
// Enums
// ============================================================================

// EscapedStatus represents EscapedStatus.
type EscapedStatus string

const (
	// EscapedStatusReady adds Foo. func init() {} //
	EscapedStatusReady EscapedStatus = "Ready"
)

// ============================================================================
// DTOs
// ============================================================================

// EscapedSettings represents EscapedSettings.
type EscapedSettings struct {
	// Bad_Name adds Foo. func init() {} //
	Bad_Name string `json:"bad\"Name,omitempty"`
	Back_slash float64 `json:"back\\slash,omitempty"`
	Cal_lback func(con_text string) `json:"cal\"lback,omitempty"`
}

// ToMap converts the DTO to a map for JSON serialization.
func (d *EscapedSettings) ToMap() map[string]any {
	m := map[string]any{}
	m["bad\"Name"] = serializeValue(d.Bad_Name)
	m["back\\slash"] = serializeValue(d.Back_slash)
	if d.Cal_lback != nil {
		cb := d.Cal_lback
		m["cal\"lback"] = func(args ...any) any {
			cb(callbackArg[string](args, 0))
			return nil
		}
	}
	return m
}

// ============================================================================
// Exported Values
// ============================================================================

// Defaults_X groups related exported values.
var Defaults_X = struct {
	// Pri_mary adds Foo. func init() {} //
	Pri_mary string
}{
	Pri_mary: "primary",
}

// Emoji groups related exported values.
var Emoji = struct {
	Anything any
	Greeting string
	Lookup map[string]string
	Status EscapedStatus
}{
	Anything: "😀",
	Greeting: "hi 😀",
	Lookup: map[string]string{"😀": "😀"},
	Status: EscapedStatus("Ready😀"),
}

// ============================================================================
// Handle wrappers
// ============================================================================

// Child_Quote is the public interface for handle type Child_Quote.
type Child_Quote interface {
	handleReference

	// Err returns the first error recorded on this wrapper, or nil if the wrapper
	// resolved successfully and no chained operation has failed.
	Err() error
}

// child_Quote is the unexported impl of Child_Quote.
type child_Quote struct {
	*resourceBuilderBase
}

// newChild_QuoteFromHandle wraps an existing handle as Child_Quote.
func newChild_QuoteFromHandle(h *handle, c *client) Child_Quote {
	return &child_Quote{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// EscapedResource adds Foo. func init() {} //
type EscapedResource interface {
	handleReference

	// Add_Child_X adds a child.
	Add_Child_X() Child_Quote

	// AddFoo adds Foo. func init() {} //
	AddFoo() EscapedResource
	GetBackslash() (string, error)

	// GetExpression gets an expression.
	GetExpression() *ReferenceExpression
	Reset(cal_lback func(con_text string)) error

	// Allowed types for parameter un_ion: string, float64 (float64 accepts any
	// built-in Go numeric type).
	//
	// Parameters:
	//   - na_me: The name.
	//   - un_ion: The union value.
	//   - options: Optional parameters; see [With_QuoteOptions].
	With_Quote(na_me string, un_ion any, options ...*With_QuoteOptions) EscapedResource

	// Parameters:
	//   - options: Optional parameters; see [WithCancelOptions].
	WithCancel(options ...*WithCancelOptions) EscapedResource

	// WithHostileDocumentation adds Foo. func init() {} //
	//
	// Adds Foo. func init() {} //
	//
	// Parameters:
	//   - value: Adds Foo. func init() {} //
	//
	// Returns: Adds Foo. func init() {} //
	//
	// # Experimental
	//
	// Notice: This API is EXPERIMENTAL and may be changed or removed in a later
	// release.
	//
	// Deprecated: Adds Foo. func init() {} //
	WithHostileDocumentation(value string) EscapedResource

	// Tags gets the tags.
	Tags() *List[string]

	// Err returns the first error recorded on this wrapper, or nil if the wrapper
	// resolved successfully and no chained operation has failed.
	Err() error
}

// escapedResource is the unexported impl of EscapedResource.
type escapedResource struct {
	*resourceBuilderBase
	tags *List[string]
}

// newEscapedResourceFromHandle wraps an existing handle as EscapedResource.
func newEscapedResourceFromHandle(h *handle, c *client) EscapedResource {
	return &escapedResource{resourceBuilderBase: newResourceBuilderBase(h, c)}
}

// Add_Child_X adds a child.
func (s *escapedResource) Add_Child_X() Child_Quote {
	if s.err != nil { return &child_Quote{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)} }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Tests/addChild\"Quote%dId", reqArgs)
	if err != nil {
		return &child_Quote{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	href, ok := result.(handleReference)
	if !ok {
		err := fmt.Errorf("aspire: Tests/addChild\"Quote%%dId returned unexpected type %T", result)
		return &child_Quote{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}
	}
	return &child_Quote{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}
}

// AddFoo adds Foo. func init() {} //
func (s *escapedResource) AddFoo() EscapedResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Tests/addFoo", reqArgs); err != nil { s.setErr(err) }
	return s
}

func (s *escapedResource) GetBackslash() (string, error) {
	if s.err != nil { var zero string; return zero, s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Tests/getBack\\slash", reqArgs)
	if err != nil {
		var zero string
		return zero, err
	}
	return decodeAs[string](result)
}

// GetExpression gets an expression.
func (s *escapedResource) GetExpression() *ReferenceExpression {
	if s.err != nil { return nil }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Tests/getExpression\"%d", reqArgs)
	if err != nil { s.setErr(err); return nil }
	typed, ok := result.(*ReferenceExpression)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Tests/getExpression\"%%d returned unexpected type %T", result))
		return nil
	}
	return typed
}

func (s *escapedResource) Reset(cal_lback func(con_text string)) error {
	if s.err != nil { return s.err }
	ctx := context.Background()
	reqArgs := map[string]any{
		"tar\"get": s.handle.ToJSON(),
	}
	if cal_lback != nil {
		cb := cal_lback
		shim := func(args ...any) any {
			cb(callbackArg[string](args, 0))
			return nil
		}
		reqArgs["cal\"lback"] = s.client.registerCallback(shim)
	}
	_, err := s.client.invokeCapability(ctx, "Tests/void\r\nfunc injected() {}\u2028func injected() {}\u2029", reqArgs)
	return err
}

// Tags gets the tags.
func (s *escapedResource) Tags() *List[string] {
	if s.tags == nil {
		s.tags = newListWithGetter[string](s.handleWrapperBase, "Tests/tags\"List")
	}
	return s.tags
}

// Allowed types for parameter un_ion: string, float64 (float64 accepts any built-in Go numeric type).
func (s *escapedResource) With_Quote(na_me string, un_ion any, options ...*With_QuoteOptions) EscapedResource {
	if s.err != nil { return s }
	switch un_ion.(type) {
	case string, float64, int, int8, int16, int32, int64, uint, uint8, uint16, uint32, uint64, float32:
	default:
		err := fmt.Errorf("aspire: With_Quote: parameter %q must be one of [string, float64], got %T", "un\"ion", un_ion)
		s.setErr(err); return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["na\"me"] = serializeValue(na_me)
	if !isNil(un_ion) { reqArgs["un\"ion"] = serializeValue(un_ion) }
	if len(options) > 0 {
		merged := &With_QuoteOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
	}
	if _, err := s.client.invokeCapability(ctx, "Tests/with\"Quote", reqArgs); err != nil { s.setErr(err) }
	return s
}

func (s *escapedResource) WithCancel(options ...*WithCancelOptions) EscapedResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithCancelOptions{}
		for _, opt := range options {
			if opt != nil { merged = deepUpdate(merged, opt) }
		}
		for k, v := range merged.ToMap() { reqArgs[k] = v }
		if merged.Cancel_Token != nil {
			ctx = merged.Cancel_Token.Context()
			if id := s.client.registerCancellation(merged.Cancel_Token); id != "" {
				reqArgs["cancel\"Token"] = id
			}
		}
	}
	if _, err := s.client.invokeCapability(ctx, "Tests/withCancel", reqArgs); err != nil { s.setErr(err) }
	return s
}

// WithHostileDocumentation adds Foo. func init() {} //
func (s *escapedResource) WithHostileDocumentation(value string) EscapedResource {
	if s.err != nil { return s }
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["value"] = serializeValue(value)
	if _, err := s.client.invokeCapability(ctx, "Tests/withHostileDocumentation", reqArgs); err != nil { s.setErr(err) }
	return s
}

// ============================================================================
// Options structs
// ============================================================================

// With_QuoteOptions carries optional parameters for With_Quote.
type With_QuoteOptions struct {
	// Op_tional is the optional value.
	Op_tional *string `json:"op\"tional,omitempty"`
}

func (o *With_QuoteOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil { return m }
	if o.Op_tional != nil { m["op\"tional"] = serializeValue(o.Op_tional) }
	return m
}

// WithCancelOptions carries optional parameters for WithCancel.
type WithCancelOptions struct {
	Cancel_Token *CancellationToken `json:"-"`
}

func (o *WithCancelOptions) ToMap() map[string]any {
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
	c.registerHandleWrapper("Tests/Tests.Child\"Quote", func(h *handle, c *client) any {
		return newChild_QuoteFromHandle(h, c)
	})
	c.registerHandleWrapper("Tests/Tests.EscapedResource", func(h *handle, c *client) any {
		return newEscapedResourceFromHandle(h, c)
	})
}
