// Package aspire provides base types and utilities for Aspire Go SDK.
package aspire

import (
	"fmt"
)

// HandleWrapperBase is the base type for all handle wrappers.
type HandleWrapperBase struct {
	handle *Handle
	client *AspireClient
}

// NewHandleWrapperBase creates a new handle wrapper base.
func NewHandleWrapperBase(handle *Handle, client *AspireClient) HandleWrapperBase {
	return HandleWrapperBase{handle: handle, client: client}
}

// Handle returns the underlying handle.
func (h *HandleWrapperBase) Handle() *Handle {
	return h.handle
}

// Client returns the client.
func (h *HandleWrapperBase) Client() *AspireClient {
	return h.client
}

// ResourceBuilderBase extends HandleWrapperBase for resource builders.
type ResourceBuilderBase struct {
	HandleWrapperBase
}

// NewResourceBuilderBase creates a new resource builder base.
func NewResourceBuilderBase(handle *Handle, client *AspireClient) ResourceBuilderBase {
	return ResourceBuilderBase{HandleWrapperBase: NewHandleWrapperBase(handle, client)}
}

// ReferenceExpression represents a reference expression.
// Supports value mode (Format + Args) and conditional mode (Condition + WhenTrue + WhenFalse).
type ReferenceExpression struct {
	Format string
	Args   []any

	// Conditional mode fields
	Condition     any
	WhenTrue      *ReferenceExpression
	WhenFalse     *ReferenceExpression
	MatchValue    string
	isConditional bool
}

// NewReferenceExpression creates a new reference expression in value mode.
func NewReferenceExpression(format string, args ...any) *ReferenceExpression {
	return &ReferenceExpression{Format: format, Args: args}
}

// NewConditionalReferenceExpression creates a conditional reference expression from its parts.
func NewConditionalReferenceExpression(condition any, matchValue string, whenTrue *ReferenceExpression, whenFalse *ReferenceExpression) *ReferenceExpression {
	if matchValue == "" {
		matchValue = "True"
	}
	return &ReferenceExpression{
		Condition:     condition,
		WhenTrue:      whenTrue,
		WhenFalse:     whenFalse,
		MatchValue:    matchValue,
		isConditional: true,
	}
}

// RefExpr is a convenience function for creating reference expressions.
func RefExpr(format string, args ...any) *ReferenceExpression {
	return NewReferenceExpression(format, args...)
}

// ToJSON returns the reference expression as a JSON-serializable map.
func (r *ReferenceExpression) ToJSON() map[string]any {
	if r.isConditional {
		return map[string]any{
			"$expr": map[string]any{
				"condition":  SerializeValue(r.Condition),
				"whenTrue":   r.WhenTrue.ToJSON(),
				"whenFalse":  r.WhenFalse.ToJSON(),
				"matchValue": r.MatchValue,
			},
		}
	}
	return map[string]any{
		"$expr": map[string]any{
			"format": r.Format,
			"args":   r.Args,
		},
	}
}

// AspireList is a handle-backed list with lazy handle resolution.
type AspireList[T any] struct {
	HandleWrapperBase
	getterCapabilityID string
	resolvedHandle     *Handle
}

// NewAspireList creates a new AspireList.
func NewAspireList[T any](handle *Handle, client *AspireClient) *AspireList[T] {
	return &AspireList[T]{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
		resolvedHandle:    handle,
	}
}

// NewAspireListWithGetter creates a new AspireList with lazy handle resolution.
func NewAspireListWithGetter[T any](contextHandle *Handle, client *AspireClient, getterCapabilityID string) *AspireList[T] {
	return &AspireList[T]{
		HandleWrapperBase:  NewHandleWrapperBase(contextHandle, client),
		getterCapabilityID: getterCapabilityID,
	}
}

// EnsureHandle lazily resolves the list handle.
func (l *AspireList[T]) EnsureHandle() *Handle {
	if l.resolvedHandle != nil {
		return l.resolvedHandle
	}
	if l.getterCapabilityID != "" {
		result, err := l.client.InvokeCapability(l.getterCapabilityID, map[string]any{
			"context": l.handle.ToJSON(),
		})
		if err == nil {
			if handle, ok := result.(*Handle); ok {
				l.resolvedHandle = handle
			}
		}
	}
	if l.resolvedHandle == nil {
		l.resolvedHandle = l.handle
	}
	return l.resolvedHandle
}

// AspireDict is a handle-backed dictionary with lazy handle resolution.
type AspireDict[K comparable, V any] struct {
	HandleWrapperBase
	getterCapabilityID string
	resolvedHandle     *Handle
}

// NewAspireDict creates a new AspireDict.
func NewAspireDict[K comparable, V any](handle *Handle, client *AspireClient) *AspireDict[K, V] {
	return &AspireDict[K, V]{
		HandleWrapperBase: NewHandleWrapperBase(handle, client),
		resolvedHandle:    handle,
	}
}

// NewAspireDictWithGetter creates a new AspireDict with lazy handle resolution.
func NewAspireDictWithGetter[K comparable, V any](contextHandle *Handle, client *AspireClient, getterCapabilityID string) *AspireDict[K, V] {
	return &AspireDict[K, V]{
		HandleWrapperBase:  NewHandleWrapperBase(contextHandle, client),
		getterCapabilityID: getterCapabilityID,
	}
}

// EnsureHandle lazily resolves the dict handle.
func (d *AspireDict[K, V]) EnsureHandle() *Handle {
	if d.resolvedHandle != nil {
		return d.resolvedHandle
	}
	if d.getterCapabilityID != "" {
		result, err := d.client.InvokeCapability(d.getterCapabilityID, map[string]any{
			"context": d.handle.ToJSON(),
		})
		if err == nil {
			if handle, ok := result.(*Handle); ok {
				d.resolvedHandle = handle
			}
		}
	}
	if d.resolvedHandle == nil {
		d.resolvedHandle = d.handle
	}
	return d.resolvedHandle
}

// SerializeValue converts a value to its JSON representation.
func SerializeValue(value any) any {
	if value == nil {
		return nil
	}

	switch v := value.(type) {
	case *Handle:
		return v.ToJSON()
	case *ReferenceExpression:
		return v.ToJSON()
	case interface{ ToJSON() map[string]any }:
		return v.ToJSON()
	case interface{ Handle() *Handle }:
		return v.Handle().ToJSON()
	case []any:
		result := make([]any, len(v))
		for i, item := range v {
			result[i] = SerializeValue(item)
		}
		return result
	case map[string]any:
		result := make(map[string]any)
		for k, val := range v {
			result[k] = SerializeValue(val)
		}
		return result
	case fmt.Stringer:
		return v.String()
	default:
		return value
	}
}
