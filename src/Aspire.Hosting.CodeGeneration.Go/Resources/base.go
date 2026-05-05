// Package aspire provides base types and utilities for Aspire Go SDK.
package aspire

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"reflect"
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
//  2. handleReference (every wrapper impl)         → getHandle().ToJSON()
//  3. *ReferenceExpression                         → r.ToJSON()
//  4. interface{ ToMap() map[string]any }          → recurse into the map
//  5. []any / map[string]any                       → recurse element-wise
//  6. fmt.Stringer (compatibility)                 → v.String()
//  7. default                                      → pass through
func serializeValue(value any) any {
	if value == nil {
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
	case fmt.Stringer:
		return v.String()
	default:
		return value
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
		return zero, err
	}
	return out, nil
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
