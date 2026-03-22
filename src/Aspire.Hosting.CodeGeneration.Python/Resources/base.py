# base.py - Core Aspire types: base classes, reference expressions, collections
from __future__ import annotations

from enum import Enum
from typing import Any, Dict, Iterable, List

from transport import AspireClient, Handle


class ReferenceExpression:
    """Represents a reference expression passed to capabilities.
    Supports both value mode (format + valueProviders) and conditional mode (condition + whenTrue + whenFalse)."""

    def __init__(self, format_string: str, value_providers: List[Any]) -> None:
        self._format_string = format_string
        self._value_providers = value_providers
        self._condition: Any = None
        self._when_true: ReferenceExpression | None = None
        self._when_false: ReferenceExpression | None = None
        self._match_value: str | None = None
        self._is_conditional = False

    @staticmethod
    def create(format_string: str, *values: Any) -> "ReferenceExpression":
        value_providers = [_extract_reference_value(value) for value in values]
        return ReferenceExpression(format_string, value_providers)

    @staticmethod
    def create_conditional(condition: Any, match_value: str, when_true: "ReferenceExpression", when_false: "ReferenceExpression") -> "ReferenceExpression":
        """Creates a conditional reference expression from its parts."""
        expr = ReferenceExpression.__new__(ReferenceExpression)
        expr._format_string = ""
        expr._value_providers = []
        expr._condition = condition
        expr._when_true = when_true
        expr._when_false = when_false
        expr._match_value = match_value
        expr._is_conditional = True
        return expr

    def to_json(self) -> Dict[str, Any]:
        if self._is_conditional:
            return {
                "$expr": {
                    "condition": serialize_value(self._condition),
                    "whenTrue": self._when_true.to_json(),
                    "whenFalse": self._when_false.to_json(),
                    "matchValue": self._match_value,
                }
            }
        payload: Dict[str, Any] = {"format": self._format_string}
        if self._value_providers:
            payload["valueProviders"] = self._value_providers
        return {"$expr": payload}

    def __str__(self) -> str:
        if self._is_conditional:
            return "ReferenceExpression(conditional)"
        return f"ReferenceExpression({self._format_string})"


def ref_expr(format_string: str, *values: Any) -> ReferenceExpression:
    """Create a reference expression using a format string."""
    return ReferenceExpression.create(format_string, *values)


class HandleWrapperBase:
    """Base wrapper for ATS handle types."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def to_json(self) -> Dict[str, str]:
        return self._handle.to_json()


class ResourceBuilderBase(HandleWrapperBase):
    """Base class for resource builder wrappers."""


class AspireList(HandleWrapperBase):
    """Wrapper for mutable list handles with lazy handle resolution."""

    def __init__(
        self,
        handle_or_context: Handle,
        client: AspireClient,
        getter_capability_id: str | None = None
    ) -> None:
        super().__init__(handle_or_context, client)
        self._getter_capability_id = getter_capability_id
        self._resolved_handle: Handle | None = None if getter_capability_id else handle_or_context

    def _ensure_handle(self) -> Handle:
        """Lazily resolve the list handle by calling the getter capability."""
        if self._resolved_handle is not None:
            return self._resolved_handle
        if self._getter_capability_id:
            self._resolved_handle = self._client.invoke_capability(
                self._getter_capability_id,
                {"context": self._handle}
            )
        else:
            self._resolved_handle = self._handle
        return self._resolved_handle

    def count(self) -> int:
        handle = self._ensure_handle()
        return self._client.invoke_capability(
            "Aspire.Hosting/List.length",
            {"list": handle}
        )

    def get(self, index: int) -> Any:
        handle = self._ensure_handle()
        return self._client.invoke_capability(
            "Aspire.Hosting/List.get",
            {"list": handle, "index": index}
        )

    def add(self, item: Any) -> None:
        handle = self._ensure_handle()
        self._client.invoke_capability(
            "Aspire.Hosting/List.add",
            {"list": handle, "item": serialize_value(item)}
        )

    def remove_at(self, index: int) -> bool:
        handle = self._ensure_handle()
        return self._client.invoke_capability(
            "Aspire.Hosting/List.removeAt",
            {"list": handle, "index": index}
        )

    def clear(self) -> None:
        handle = self._ensure_handle()
        self._client.invoke_capability(
            "Aspire.Hosting/List.clear",
            {"list": handle}
        )

    def to_list(self) -> List[Any]:
        handle = self._ensure_handle()
        return self._client.invoke_capability(
            "Aspire.Hosting/List.toArray",
            {"list": handle}
        )

    def to_json(self) -> Dict[str, str]:
        if self._resolved_handle is not None:
            return self._resolved_handle.to_json()
        return self._handle.to_json()


class AspireDict(HandleWrapperBase):
    """Wrapper for mutable dictionary handles with lazy handle resolution."""

    def __init__(
        self,
        handle_or_context: Handle,
        client: AspireClient,
        getter_capability_id: str | None = None
    ) -> None:
        super().__init__(handle_or_context, client)
        self._getter_capability_id = getter_capability_id
        self._resolved_handle: Handle | None = None if getter_capability_id else handle_or_context

    def _ensure_handle(self) -> Handle:
        """Lazily resolve the dict handle by calling the getter capability."""
        if self._resolved_handle is not None:
            return self._resolved_handle
        if self._getter_capability_id:
            self._resolved_handle = self._client.invoke_capability(
                self._getter_capability_id,
                {"context": self._handle}
            )
        else:
            self._resolved_handle = self._handle
        return self._resolved_handle

    def count(self) -> int:
        handle = self._ensure_handle()
        return self._client.invoke_capability(
            "Aspire.Hosting/Dict.count",
            {"dict": handle}
        )

    def get(self, key: str) -> Any:
        handle = self._ensure_handle()
        return self._client.invoke_capability(
            "Aspire.Hosting/Dict.get",
            {"dict": handle, "key": key}
        )

    def set(self, key: str, value: Any) -> None:
        handle = self._ensure_handle()
        self._client.invoke_capability(
            "Aspire.Hosting/Dict.set",
            {"dict": handle, "key": key, "value": serialize_value(value)}
        )

    def contains_key(self, key: str) -> bool:
        handle = self._ensure_handle()
        return self._client.invoke_capability(
            "Aspire.Hosting/Dict.has",
            {"dict": handle, "key": key}
        )

    def remove(self, key: str) -> bool:
        handle = self._ensure_handle()
        return self._client.invoke_capability(
            "Aspire.Hosting/Dict.remove",
            {"dict": handle, "key": key}
        )

    def keys(self) -> List[str]:
        handle = self._ensure_handle()
        return self._client.invoke_capability(
            "Aspire.Hosting/Dict.keys",
            {"dict": handle}
        )

    def values(self) -> List[Any]:
        handle = self._ensure_handle()
        return self._client.invoke_capability(
            "Aspire.Hosting/Dict.values",
            {"dict": handle}
        )

    def to_dict(self) -> Dict[str, Any]:
        handle = self._ensure_handle()
        return self._client.invoke_capability(
            "Aspire.Hosting/Dict.toObject",
            {"dict": handle}
        )

    def to_json(self) -> Dict[str, str]:
        if self._resolved_handle is not None:
            return self._resolved_handle.to_json()
        return self._handle.to_json()


def serialize_value(value: Any) -> Any:
    if isinstance(value, ReferenceExpression):
        return value.to_json()

    if isinstance(value, Handle):
        return value.to_json()

    if hasattr(value, "to_json") and callable(value.to_json):
        return value.to_json()

    if hasattr(value, "to_dict") and callable(value.to_dict):
        return {key: serialize_value(val) for key, val in value.to_dict().items()}

    if isinstance(value, Enum):
        return value.value

    if isinstance(value, list):
        return [serialize_value(item) for item in value]

    if isinstance(value, tuple):
        return [serialize_value(item) for item in value]

    if isinstance(value, dict):
        return {key: serialize_value(val) for key, val in value.items()}

    return value


def _extract_reference_value(value: Any) -> Any:
    if value is None:
        raise ValueError("Cannot use None in reference expressions.")

    if isinstance(value, (str, int, float)):
        return value

    if isinstance(value, Handle):
        return value.to_json()

    if hasattr(value, "to_json") and callable(value.to_json):
        return value.to_json()

    raise ValueError(f"Unsupported reference expression value: {type(value).__name__}")
