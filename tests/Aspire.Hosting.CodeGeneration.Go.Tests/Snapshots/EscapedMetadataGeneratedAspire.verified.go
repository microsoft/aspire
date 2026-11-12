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
	Bad_Name   string                `json:"bad\"Name,omitempty"`
	Back_slash float64               `json:"back\\slash,omitempty"`
	Cal_lback  func(con_text string) `json:"cal\"lback,omitempty"`
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
	Lookup   map[string]string
	Status   EscapedStatus
}{
	Anything: "😀",
	Greeting: "hi 😀",
	Lookup:   map[string]string{"😀": "😀"},
	Status:   EscapedStatus("Ready😀"),
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
	// built-in Go numeric type and json.Number).
	//
	// Parameters:
	//   - na_me: The name.
	//   - un_ion: The union value.
	//   - options: Optional parameters; see [With_QuoteOption].
	With_Quote(na_me string, un_ion any, options ...With_QuoteOption) EscapedResource

	// Parameters:
	//   - options: Optional parameters; see [WithCancelOption].
	WithCancel(options ...WithCancelOption) EscapedResource

	// Parameters:
	//   - options: Optional parameters; see [WithUnsafeParameterOption].
	WithUnsafeParameter(options ...WithUnsafeParameterOption) EscapedResource

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
	if err := s.Err(); err != nil {
		return &child_Quote{resourceBuilderBase: newPropagatedErroredResourceBuilder(err, s.client)}
	}
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
	if s.Err() != nil {
		return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if _, err := s.client.invokeCapability(ctx, "Tests/addFoo", reqArgs); err != nil {
		s.setErr(err)
	}
	return s
}

func (s *escapedResource) GetBackslash() (string, error) {
	if err := s.Err(); err != nil {
		var zero string
		return zero, err
	}
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
	if s.Err() != nil {
		return nil
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	result, err := s.client.invokeCapability(ctx, "Tests/getExpression\"%d", reqArgs)
	if err != nil {
		s.setErr(err)
		return nil
	}
	typed, ok := result.(*ReferenceExpression)
	if !ok {
		s.setErr(fmt.Errorf("aspire: Tests/getExpression\"%%d returned unexpected type %T", result))
		return nil
	}
	return typed
}

func (s *escapedResource) Reset(cal_lback func(con_text string)) error {
	if err := s.Err(); err != nil {
		return err
	}
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
	s.collectionMu.Lock()
	defer s.collectionMu.Unlock()
	if s.tags == nil {
		s.tags = newListWithGetter[string](s.handleWrapperBase, "Tests/tags\"List")
	}
	return s.tags
}

// Allowed types for parameter un_ion: string, float64 (float64 accepts any built-in Go numeric type and json.Number).
func (s *escapedResource) With_Quote(na_me string, un_ion any, options ...With_QuoteOption) EscapedResource {
	if s.Err() != nil {
		return s
	}
	switch value := un_ion.(type) {
	case json.Number:
		if !isValidJSONNumber(value) {
			err := fmt.Errorf("aspire: With_Quote: parameter %q must be one of [string, float64], got invalid json.Number %q", "un\"ion", value)
			s.setErr(err)
			return s
		}
	case string, float64, int, int8, int16, int32, int64, uint, uint8, uint16, uint32, uint64, float32:
	default:
		err := fmt.Errorf("aspire: With_Quote: parameter %q must be one of [string, float64], got %T", "un\"ion", un_ion)
		s.setErr(err)
		return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	reqArgs["na\"me"] = serializeValue(na_me)
	if !isNil(un_ion) {
		reqArgs["un\"ion"] = serializeValue(un_ion)
	}
	if len(options) > 0 {
		merged := &With_QuoteOptions{}
		for _, opt := range options {
			if opt != nil {
				opt.apply(merged)
			}
		}
		for k, v := range merged.ToMap() {
			reqArgs[k] = v
		}
	}
	if _, err := s.client.invokeCapability(ctx, "Tests/with\"Quote", reqArgs); err != nil {
		s.setErr(err)
	}
	return s
}

func (s *escapedResource) WithCancel(options ...WithCancelOption) EscapedResource {
	if s.Err() != nil {
		return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithCancelOptions{}
		for _, opt := range options {
			if opt != nil {
				opt.apply(merged)
			}
		}
		for k, v := range merged.ToMap() {
			reqArgs[k] = v
		}
		if merged.Cancel_Token != nil {
			ctx = merged.Cancel_Token.Context()
			if id := s.client.registerCancellation(merged.Cancel_Token); id != "" {
				reqArgs["cancel\"Token"] = id
			}
		}
	}
	if _, err := s.client.invokeCapability(ctx, "Tests/withCancel", reqArgs); err != nil {
		s.setErr(err)
	}
	return s
}

func (s *escapedResource) WithUnsafeParameter(options ...WithUnsafeParameterOption) EscapedResource {
	if s.Err() != nil {
		return s
	}
	ctx := context.Background()
	reqArgs := map[string]any{
		"builder": s.handle.ToJSON(),
	}
	if len(options) > 0 {
		merged := &WithUnsafeParameterOptions{}
		for _, opt := range options {
			if opt != nil {
				opt.apply(merged)
			}
		}
		for k, v := range merged.ToMap() {
			reqArgs[k] = v
		}
	}
	if _, err := s.client.invokeCapability(ctx, "Tests/withUnsafeParameter", reqArgs); err != nil {
		s.setErr(err)
	}
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

// With_QuoteOption configures With_Quote.
type With_QuoteOption interface {
	apply(*With_QuoteOptions)
}

func (option *With_QuoteOptions) apply(options *With_QuoteOptions) {
	if option != nil {
		*options = *deepUpdate(options, option)
	}
}

// With_QuoteOp_tional sets Op_tional for With_Quote.
type With_QuoteOp_tional string

func (value With_QuoteOp_tional) apply(options *With_QuoteOptions) {
	options.Op_tional = (*string)(&value)
}

func (o *With_QuoteOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil {
		return m
	}
	if o.Op_tional != nil {
		m["op\"tional"] = serializeValue(o.Op_tional)
	}
	return m
}

// WithCancelOptions carries optional parameters for WithCancel.
type WithCancelOptions struct {
	Cancel_Token *CancellationToken `json:"-"`
}

// WithCancelOption configures WithCancel.
type WithCancelOption interface {
	apply(*WithCancelOptions)
}

func (option *WithCancelOptions) apply(options *WithCancelOptions) {
	if option != nil {
		*options = *deepUpdate(options, option)
	}
}

// CancelCancel_Token sets Cancel_Token for WithCancel.
type cancelCancel_Token struct{ value *CancellationToken }

func (option cancelCancel_Token) apply(options *WithCancelOptions) {
	options.Cancel_Token = option.value
}

func CancelCancel_Token(value *CancellationToken) WithCancelOption {
	return cancelCancel_Token{value: value}
}

func (o *WithCancelOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil {
		return m
	}
	return m
}

// WithUnsafeParameterOptions carries optional parameters for WithUnsafeParameter.
type WithUnsafeParameterOptions struct {
	Adds_Foo__func_init_________ *string `json:"Adds Foo.\nfunc init() {}\n// ,omitempty"`
}

// WithUnsafeParameterOption configures WithUnsafeParameter.
type WithUnsafeParameterOption interface {
	apply(*WithUnsafeParameterOptions)
}

func (option *WithUnsafeParameterOptions) apply(options *WithUnsafeParameterOptions) {
	if option != nil {
		*options = *deepUpdate(options, option)
	}
}

// UnsafeParameterAdds_Foo__func_init_________ sets Adds_Foo__func_init_________ for WithUnsafeParameter.
type UnsafeParameterAdds_Foo__func_init_________ string

func (value UnsafeParameterAdds_Foo__func_init_________) apply(options *WithUnsafeParameterOptions) {
	options.Adds_Foo__func_init_________ = (*string)(&value)
}

func (o *WithUnsafeParameterOptions) ToMap() map[string]any {
	m := map[string]any{}
	if o == nil {
		return m
	}
	if o.Adds_Foo__func_init_________ != nil {
		m["Adds Foo.\nfunc init() {}\n// "] = serializeValue(o.Adds_Foo__func_init_________)
	}
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
