//! Base types for Aspire Rust SDK.

use std::collections::HashMap;
use std::sync::Arc;

use serde::{Serialize, Deserialize};
use serde_json::{json, Value};

use crate::transport::{AspireClient, Handle};

/// Base type for all handle wrappers.
pub struct HandleWrapperBase {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HandleWrapperBase {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }
}

/// Base type for resource builders.
pub struct ResourceBuilderBase {
    base: HandleWrapperBase,
}

impl ResourceBuilderBase {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self {
            base: HandleWrapperBase::new(handle, client),
        }
    }

    pub fn handle(&self) -> &Handle {
        self.base.handle()
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        self.base.client()
    }
}

/// A reference expression for dynamic values.
/// Supports value mode (format + args) and conditional mode (condition + whenTrue + whenFalse).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ReferenceExpression {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub format: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub args: Option<Vec<Value>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    condition: Option<Value>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    when_true: Option<Box<ReferenceExpression>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    when_false: Option<Box<ReferenceExpression>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    match_value: Option<String>,
    #[serde(default)]
    is_conditional: bool,
}

impl ReferenceExpression {
    pub fn new(format: impl Into<String>, args: Vec<Value>) -> Self {
        Self {
            format: Some(format.into()),
            args: Some(args),
            condition: None,
            when_true: None,
            when_false: None,
            match_value: None,
            is_conditional: false,
        }
    }

    /// Creates a conditional reference expression from its parts.
    pub fn create_conditional(condition: Value, match_value: impl Into<String>, when_true: ReferenceExpression, when_false: ReferenceExpression) -> Self {
        Self {
            format: None,
            args: None,
            condition: Some(condition),
            when_true: Some(Box::new(when_true)),
            when_false: Some(Box::new(when_false)),
            match_value: Some(match_value.into()),
            is_conditional: true,
        }
    }

    pub fn to_json(&self) -> Value {
        if self.is_conditional {
            return json!({
                "$expr": {
                    "condition": serialize_value(self.condition.clone().unwrap()),
                    "whenTrue": self.when_true.as_ref().unwrap().to_json(),
                    "whenFalse": self.when_false.as_ref().unwrap().to_json(),
                    "matchValue": self.match_value.as_ref().unwrap()
                }
            });
        }
        json!({
            "$expr": {
                "format": self.format.as_deref().unwrap_or_default(),
                "args": self.args.as_deref().unwrap_or_default()
            }
        })
    }
}

/// Convenience function to create a reference expression.
pub fn ref_expr(format: impl Into<String>, args: Vec<Value>) -> ReferenceExpression {
    ReferenceExpression::new(format, args)
}

/// A handle-backed list with lazy handle resolution.
pub struct AspireList<T> {
    context_handle: Handle,
    client: Arc<AspireClient>,
    getter_capability_id: Option<String>,
    resolved_handle: std::cell::OnceCell<Handle>,
    _marker: std::marker::PhantomData<T>,
}

impl<T> AspireList<T> {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        let resolved = std::cell::OnceCell::new();
        let _ = resolved.set(handle.clone());
        Self {
            context_handle: handle,
            client,
            getter_capability_id: None,
            resolved_handle: resolved,
            _marker: std::marker::PhantomData,
        }
    }

    pub fn with_getter(context_handle: Handle, client: Arc<AspireClient>, getter_capability_id: impl Into<String>) -> Self {
        Self {
            context_handle,
            client,
            getter_capability_id: Some(getter_capability_id.into()),
            resolved_handle: std::cell::OnceCell::new(),
            _marker: std::marker::PhantomData,
        }
    }

    fn ensure_handle(&self) -> &Handle {
        self.resolved_handle.get_or_init(|| {
            if let Some(ref cap_id) = self.getter_capability_id {
                let mut args = HashMap::new();
                args.insert("context".to_string(), self.context_handle.to_json());
                if let Ok(result) = self.client.invoke_capability(cap_id, args) {
                    if let Ok(handle) = serde_json::from_value::<Handle>(result) {
                        return handle;
                    }
                }
            }
            self.context_handle.clone()
        })
    }

    pub fn handle(&self) -> &Handle {
        self.ensure_handle()
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }
}

/// A handle-backed dictionary with lazy handle resolution.
pub struct AspireDict<K, V> {
    context_handle: Handle,
    client: Arc<AspireClient>,
    getter_capability_id: Option<String>,
    resolved_handle: std::cell::OnceCell<Handle>,
    _key_marker: std::marker::PhantomData<K>,
    _value_marker: std::marker::PhantomData<V>,
}

impl<K, V> AspireDict<K, V> {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        let resolved = std::cell::OnceCell::new();
        let _ = resolved.set(handle.clone());
        Self {
            context_handle: handle,
            client,
            getter_capability_id: None,
            resolved_handle: resolved,
            _key_marker: std::marker::PhantomData,
            _value_marker: std::marker::PhantomData,
        }
    }

    pub fn with_getter(context_handle: Handle, client: Arc<AspireClient>, getter_capability_id: impl Into<String>) -> Self {
        Self {
            context_handle,
            client,
            getter_capability_id: Some(getter_capability_id.into()),
            resolved_handle: std::cell::OnceCell::new(),
            _key_marker: std::marker::PhantomData,
            _value_marker: std::marker::PhantomData,
        }
    }

    fn ensure_handle(&self) -> &Handle {
        self.resolved_handle.get_or_init(|| {
            if let Some(ref cap_id) = self.getter_capability_id {
                let mut args = HashMap::new();
                args.insert("context".to_string(), self.context_handle.to_json());
                if let Ok(result) = self.client.invoke_capability(cap_id, args) {
                    if let Ok(handle) = serde_json::from_value::<Handle>(result) {
                        return handle;
                    }
                }
            }
            self.context_handle.clone()
        })
    }

    pub fn handle(&self) -> &Handle {
        self.ensure_handle()
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }
}

/// Trait for types that can be serialized to JSON.
pub trait ToJson {
    fn to_json(&self) -> Value;
}

impl ToJson for Handle {
    fn to_json(&self) -> Value {
        self.to_json()
    }
}

impl ToJson for ReferenceExpression {
    fn to_json(&self) -> Value {
        self.to_json()
    }
}

/// Serialize a value to its JSON representation.
pub fn serialize_value(value: impl Into<Value>) -> Value {
    value.into()
}

/// Serialize a handle wrapper to its JSON representation.
pub fn serialize_handle(wrapper: &impl HasHandle) -> Value {
    wrapper.handle().to_json()
}

/// Trait for types that have an underlying handle.
pub trait HasHandle {
    fn handle(&self) -> &Handle;
}

impl HasHandle for HandleWrapperBase {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl HasHandle for ResourceBuilderBase {
    fn handle(&self) -> &Handle {
        self.base.handle()
    }
}
