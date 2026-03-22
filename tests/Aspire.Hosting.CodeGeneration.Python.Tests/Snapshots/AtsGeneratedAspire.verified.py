# aspire.py - Capability-based Aspire SDK
# GENERATED CODE - DO NOT EDIT

from __future__ import annotations

import os
import sys
from dataclasses import dataclass
from enum import Enum
from typing import Any, Callable, Dict, List

from transport import AspireClient, Handle, CapabilityError, register_callback, register_handle_wrapper, register_cancellation
from base import AspireDict, AspireList, ReferenceExpression, ref_expr, HandleWrapperBase, ResourceBuilderBase, serialize_value

# ============================================================================
# Enums
# ============================================================================

class TestPersistenceMode(str, Enum):
    NONE_ = "None"
    VOLUME = "Volume"
    BIND = "Bind"

class TestResourceStatus(str, Enum):
    PENDING = "Pending"
    RUNNING = "Running"
    STOPPED = "Stopped"
    FAILED = "Failed"

# ============================================================================
# DTOs
# ============================================================================

@dataclass
class TestConfigDto:
    name: str
    port: float
    enabled: bool
    optional_field: str

    def to_dict(self) -> Dict[str, Any]:
        return {
            "Name": serialize_value(self.name),
            "Port": serialize_value(self.port),
            "Enabled": serialize_value(self.enabled),
            "OptionalField": serialize_value(self.optional_field),
        }

@dataclass
class TestNestedDto:
    id: str
    config: TestConfigDto
    tags: AspireList[str]
    counts: AspireDict[str, float]

    def to_dict(self) -> Dict[str, Any]:
        return {
            "Id": serialize_value(self.id),
            "Config": serialize_value(self.config),
            "Tags": serialize_value(self.tags),
            "Counts": serialize_value(self.counts),
        }

@dataclass
class TestDeeplyNestedDto:
    nested_data: AspireDict[str, AspireList[TestConfigDto]]
    metadata_array: list[AspireDict[str, str]]

    def to_dict(self) -> Dict[str, Any]:
        return {
            "NestedData": serialize_value(self.nested_data),
            "MetadataArray": serialize_value(self.metadata_array),
        }

# ============================================================================
# Handle Wrappers
# ============================================================================

class IDistributedApplicationBuilder(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def add_test_redis(self, name: str, port: float | None = None) -> TestRedisResource:
        """Adds a test Redis resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        if port is not None:
            args["port"] = serialize_value(port)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/addTestRedis", args)

    def add_test_vault(self, name: str) -> TestVaultResource:
        """Adds a test vault resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/addTestVault", args)


class IResource(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class IResourceWithConnectionString(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class IResourceWithEnvironment(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class ITestVaultResource(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class ReferenceExpression(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    pass

class TestCallbackContext(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def name(self) -> str:
        """Gets the Name property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.name", args)

    def set_name(self, value: str) -> TestCallbackContext:
        """Sets the Name property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setName", args)

    def value(self) -> float:
        """Gets the Value property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.value", args)

    def set_value(self, value: float) -> TestCallbackContext:
        """Sets the Value property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setValue", args)

    def cancellation_token(self) -> CancellationToken:
        """Gets the CancellationToken property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.cancellationToken", args)

    def set_cancellation_token(self, value: CancellationToken) -> TestCallbackContext:
        """Sets the CancellationToken property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        value_id = register_cancellation(value, self._client) if value is not None else None
        if value_id is not None:
            args["value"] = value_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setCancellationToken", args)


class TestCollectionContext(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    @property
    def items(self) -> AspireList[str]:
        """Gets the Items property"""
        if not hasattr(self, '_items'):
            self._items = AspireList(
                self._handle,
                self._client,
                "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.items"
            )
        return self._items

    @property
    def metadata(self) -> AspireDict[str, str]:
        """Gets the Metadata property"""
        if not hasattr(self, '_metadata'):
            self._metadata = AspireDict(
                self._handle,
                self._client,
                "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.metadata"
            )
        return self._metadata


class TestDatabaseResource(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def with_optional_string(self, value: str | None = None, enabled: bool = True) -> IResource:
        """Adds an optional string parameter"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if value is not None:
            args["value"] = serialize_value(value)
        args["enabled"] = serialize_value(enabled)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalString", args)

    def with_config(self, config: TestConfigDto) -> IResource:
        """Configures the resource with a DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConfig", args)

    def test_with_environment_callback(self, callback: Callable[[TestEnvironmentContext], None]) -> IResourceWithEnvironment:
        """Configures environment with callback (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWithEnvironmentCallback", args)

    def with_created_at(self, created_at: str) -> IResource:
        """Sets the created timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["createdAt"] = serialize_value(created_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCreatedAt", args)

    def with_modified_at(self, modified_at: str) -> IResource:
        """Sets the modified timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["modifiedAt"] = serialize_value(modified_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withModifiedAt", args)

    def with_correlation_id(self, correlation_id: str) -> IResource:
        """Sets the correlation ID"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["correlationId"] = serialize_value(correlation_id)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCorrelationId", args)

    def with_optional_callback(self, callback: Callable[[TestCallbackContext], None] | None = None) -> IResource:
        """Configures with optional callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalCallback", args)

    def with_status(self, status: TestResourceStatus) -> IResource:
        """Sets the resource status"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["status"] = serialize_value(status)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withStatus", args)

    def with_nested_config(self, config: TestNestedDto) -> IResource:
        """Configures with nested DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withNestedConfig", args)

    def with_validator(self, validator: Callable[[TestResourceContext], bool]) -> IResource:
        """Adds validation callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        validator_id = register_callback(validator) if validator is not None else None
        if validator_id is not None:
            args["validator"] = validator_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withValidator", args)

    def test_wait_for(self, dependency: IResource) -> IResource:
        """Waits for another resource (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWaitFor", args)

    def with_dependency(self, dependency: IResourceWithConnectionString) -> IResource:
        """Adds a dependency on another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withDependency", args)

    def with_endpoints(self, endpoints: list[str]) -> IResource:
        """Sets the endpoints"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpoints"] = serialize_value(endpoints)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEndpoints", args)

    def with_environment_variables(self, variables: dict[str, str]) -> IResourceWithEnvironment:
        """Sets environment variables"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["variables"] = serialize_value(variables)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEnvironmentVariables", args)

    def with_cancellable_operation(self, operation: Callable[[CancellationToken], None]) -> IResource:
        """Performs a cancellable operation"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        operation_id = register_callback(operation) if operation is not None else None
        if operation_id is not None:
            args["operation"] = operation_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCancellableOperation", args)

    def with_data_volume(self, name: str | None = None) -> TestDatabaseResource:
        """Adds a data volume"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if name is not None:
            args["name"] = serialize_value(name)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withDataVolume", args)


class TestEnvironmentContext(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def name(self) -> str:
        """Gets the Name property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.name", args)

    def set_name(self, value: str) -> TestEnvironmentContext:
        """Sets the Name property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setName", args)

    def description(self) -> str:
        """Gets the Description property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.description", args)

    def set_description(self, value: str) -> TestEnvironmentContext:
        """Sets the Description property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setDescription", args)

    def priority(self) -> float:
        """Gets the Priority property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.priority", args)

    def set_priority(self, value: float) -> TestEnvironmentContext:
        """Sets the Priority property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setPriority", args)


class TestRedisResource(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def add_test_child_database(self, name: str, database_name: str | None = None) -> TestDatabaseResource:
        """Adds a child database to a test Redis resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["name"] = serialize_value(name)
        if database_name is not None:
            args["databaseName"] = serialize_value(database_name)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/addTestChildDatabase", args)

    def with_persistence(self, mode: TestPersistenceMode = None) -> TestRedisResource:
        """Configures the Redis resource with persistence"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["mode"] = serialize_value(mode)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withPersistence", args)

    def with_optional_string(self, value: str | None = None, enabled: bool = True) -> IResource:
        """Adds an optional string parameter"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if value is not None:
            args["value"] = serialize_value(value)
        args["enabled"] = serialize_value(enabled)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalString", args)

    def with_config(self, config: TestConfigDto) -> IResource:
        """Configures the resource with a DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConfig", args)

    @property
    def get_tags(self) -> AspireList[str]:
        """Gets the tags for the resource"""
        if not hasattr(self, '_get_tags'):
            self._get_tags = AspireList(
                self._handle,
                self._client,
                "Aspire.Hosting.CodeGeneration.Python.Tests/getTags"
            )
        return self._get_tags

    @property
    def get_metadata(self) -> AspireDict[str, str]:
        """Gets the metadata for the resource"""
        if not hasattr(self, '_get_metadata'):
            self._get_metadata = AspireDict(
                self._handle,
                self._client,
                "Aspire.Hosting.CodeGeneration.Python.Tests/getMetadata"
            )
        return self._get_metadata

    def with_connection_string(self, connection_string: ReferenceExpression) -> IResourceWithConnectionString:
        """Sets the connection string using a reference expression"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["connectionString"] = serialize_value(connection_string)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConnectionString", args)

    def test_with_environment_callback(self, callback: Callable[[TestEnvironmentContext], None]) -> IResourceWithEnvironment:
        """Configures environment with callback (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWithEnvironmentCallback", args)

    def with_created_at(self, created_at: str) -> IResource:
        """Sets the created timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["createdAt"] = serialize_value(created_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCreatedAt", args)

    def with_modified_at(self, modified_at: str) -> IResource:
        """Sets the modified timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["modifiedAt"] = serialize_value(modified_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withModifiedAt", args)

    def with_correlation_id(self, correlation_id: str) -> IResource:
        """Sets the correlation ID"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["correlationId"] = serialize_value(correlation_id)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCorrelationId", args)

    def with_optional_callback(self, callback: Callable[[TestCallbackContext], None] | None = None) -> IResource:
        """Configures with optional callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalCallback", args)

    def with_status(self, status: TestResourceStatus) -> IResource:
        """Sets the resource status"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["status"] = serialize_value(status)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withStatus", args)

    def with_nested_config(self, config: TestNestedDto) -> IResource:
        """Configures with nested DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withNestedConfig", args)

    def with_validator(self, validator: Callable[[TestResourceContext], bool]) -> IResource:
        """Adds validation callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        validator_id = register_callback(validator) if validator is not None else None
        if validator_id is not None:
            args["validator"] = validator_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withValidator", args)

    def test_wait_for(self, dependency: IResource) -> IResource:
        """Waits for another resource (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWaitFor", args)

    def get_endpoints(self) -> list[str]:
        """Gets the endpoints"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/getEndpoints", args)

    def with_connection_string_direct(self, connection_string: str) -> IResourceWithConnectionString:
        """Sets connection string using direct interface target"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["connectionString"] = serialize_value(connection_string)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConnectionStringDirect", args)

    def with_redis_specific(self, option: str) -> TestRedisResource:
        """Redis-specific configuration"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["option"] = serialize_value(option)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withRedisSpecific", args)

    def with_dependency(self, dependency: IResourceWithConnectionString) -> IResource:
        """Adds a dependency on another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withDependency", args)

    def with_endpoints(self, endpoints: list[str]) -> IResource:
        """Sets the endpoints"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpoints"] = serialize_value(endpoints)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEndpoints", args)

    def with_environment_variables(self, variables: dict[str, str]) -> IResourceWithEnvironment:
        """Sets environment variables"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["variables"] = serialize_value(variables)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEnvironmentVariables", args)

    def get_status_async(self, cancellation_token: CancellationToken | None = None) -> str:
        """Gets the status of the resource asynchronously"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        cancellation_token_id = register_cancellation(cancellation_token, self._client) if cancellation_token is not None else None
        if cancellation_token_id is not None:
            args["cancellationToken"] = cancellation_token_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/getStatusAsync", args)

    def with_cancellable_operation(self, operation: Callable[[CancellationToken], None]) -> IResource:
        """Performs a cancellable operation"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        operation_id = register_callback(operation) if operation is not None else None
        if operation_id is not None:
            args["operation"] = operation_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCancellableOperation", args)

    def wait_for_ready_async(self, timeout: float, cancellation_token: CancellationToken | None = None) -> bool:
        """Waits for the resource to be ready"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["timeout"] = serialize_value(timeout)
        cancellation_token_id = register_cancellation(cancellation_token, self._client) if cancellation_token is not None else None
        if cancellation_token_id is not None:
            args["cancellationToken"] = cancellation_token_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/waitForReadyAsync", args)

    def with_multi_param_handle_callback(self, callback: Callable[[TestCallbackContext, TestEnvironmentContext], None]) -> TestRedisResource:
        """Tests multi-param callback destructuring"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withMultiParamHandleCallback", args)

    def with_data_volume(self, name: str | None = None, is_read_only: bool = False) -> TestRedisResource:
        """Adds a data volume with persistence"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if name is not None:
            args["name"] = serialize_value(name)
        args["isReadOnly"] = serialize_value(is_read_only)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withDataVolume", args)


class TestResourceContext(HandleWrapperBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def name(self) -> str:
        """Gets the Name property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.name", args)

    def set_name(self, value: str) -> TestResourceContext:
        """Sets the Name property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setName", args)

    def value(self) -> float:
        """Gets the Value property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.value", args)

    def set_value(self, value: float) -> TestResourceContext:
        """Sets the Value property"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setValue", args)

    def get_value_async(self) -> str:
        """Invokes the GetValueAsync method"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.getValueAsync", args)

    def set_value_async(self, value: str) -> None:
        """Invokes the SetValueAsync method"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        args["value"] = serialize_value(value)
        self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setValueAsync", args)
        return None

    def validate_async(self) -> bool:
        """Invokes the ValidateAsync method"""
        args: Dict[str, Any] = { "context": serialize_value(self._handle) }
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.validateAsync", args)


class TestVaultResource(ResourceBuilderBase):
    def __init__(self, handle: Handle, client: AspireClient):
        super().__init__(handle, client)

    def with_optional_string(self, value: str | None = None, enabled: bool = True) -> IResource:
        """Adds an optional string parameter"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        if value is not None:
            args["value"] = serialize_value(value)
        args["enabled"] = serialize_value(enabled)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalString", args)

    def with_config(self, config: TestConfigDto) -> IResource:
        """Configures the resource with a DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withConfig", args)

    def test_with_environment_callback(self, callback: Callable[[TestEnvironmentContext], None]) -> IResourceWithEnvironment:
        """Configures environment with callback (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWithEnvironmentCallback", args)

    def with_created_at(self, created_at: str) -> IResource:
        """Sets the created timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["createdAt"] = serialize_value(created_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCreatedAt", args)

    def with_modified_at(self, modified_at: str) -> IResource:
        """Sets the modified timestamp"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["modifiedAt"] = serialize_value(modified_at)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withModifiedAt", args)

    def with_correlation_id(self, correlation_id: str) -> IResource:
        """Sets the correlation ID"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["correlationId"] = serialize_value(correlation_id)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCorrelationId", args)

    def with_optional_callback(self, callback: Callable[[TestCallbackContext], None] | None = None) -> IResource:
        """Configures with optional callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        callback_id = register_callback(callback) if callback is not None else None
        if callback_id is not None:
            args["callback"] = callback_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withOptionalCallback", args)

    def with_status(self, status: TestResourceStatus) -> IResource:
        """Sets the resource status"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["status"] = serialize_value(status)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withStatus", args)

    def with_nested_config(self, config: TestNestedDto) -> IResource:
        """Configures with nested DTO"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["config"] = serialize_value(config)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withNestedConfig", args)

    def with_validator(self, validator: Callable[[TestResourceContext], bool]) -> IResource:
        """Adds validation callback"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        validator_id = register_callback(validator) if validator is not None else None
        if validator_id is not None:
            args["validator"] = validator_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withValidator", args)

    def test_wait_for(self, dependency: IResource) -> IResource:
        """Waits for another resource (test version)"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/testWaitFor", args)

    def with_dependency(self, dependency: IResourceWithConnectionString) -> IResource:
        """Adds a dependency on another resource"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["dependency"] = serialize_value(dependency)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withDependency", args)

    def with_endpoints(self, endpoints: list[str]) -> IResource:
        """Sets the endpoints"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["endpoints"] = serialize_value(endpoints)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEndpoints", args)

    def with_environment_variables(self, variables: dict[str, str]) -> IResourceWithEnvironment:
        """Sets environment variables"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["variables"] = serialize_value(variables)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withEnvironmentVariables", args)

    def with_cancellable_operation(self, operation: Callable[[CancellationToken], None]) -> IResource:
        """Performs a cancellable operation"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        operation_id = register_callback(operation) if operation is not None else None
        if operation_id is not None:
            args["operation"] = operation_id
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withCancellableOperation", args)

    def with_vault_direct(self, option: str) -> ITestVaultResource:
        """Configures vault using direct interface target"""
        args: Dict[str, Any] = { "builder": serialize_value(self._handle) }
        args["option"] = serialize_value(option)
        return self._client.invoke_capability("Aspire.Hosting.CodeGeneration.Python.Tests/withVaultDirect", args)


# ============================================================================
# Handle wrapper registrations
# ============================================================================

register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCallbackContext", lambda handle, client: TestCallbackContext(handle, client))
register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestResourceContext", lambda handle, client: TestResourceContext(handle, client))
register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestEnvironmentContext", lambda handle, client: TestEnvironmentContext(handle, client))
register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCollectionContext", lambda handle, client: TestCollectionContext(handle, client))
register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestRedisResource", lambda handle, client: TestRedisResource(handle, client))
register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestDatabaseResource", lambda handle, client: TestDatabaseResource(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResource", lambda handle, client: IResource(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithConnectionString", lambda handle, client: IResourceWithConnectionString(handle, client))
register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestVaultResource", lambda handle, client: TestVaultResource(handle, client))
register_handle_wrapper("Aspire.Hosting.CodeGeneration.Python.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.ITestVaultResource", lambda handle, client: ITestVaultResource(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder", lambda handle, client: IDistributedApplicationBuilder(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpression", lambda handle, client: ReferenceExpression(handle, client))
register_handle_wrapper("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithEnvironment", lambda handle, client: IResourceWithEnvironment(handle, client))
register_handle_wrapper("Aspire.Hosting/List<string>", lambda handle, client: AspireList(handle, client))
register_handle_wrapper("Aspire.Hosting/Dict<string,string>", lambda handle, client: AspireDict(handle, client))

# ============================================================================
# Connection Helpers
# ============================================================================

def connect() -> AspireClient:
    socket_path = os.environ.get("REMOTE_APP_HOST_SOCKET_PATH")
    if not socket_path:
        raise RuntimeError("REMOTE_APP_HOST_SOCKET_PATH environment variable not set. Run this application using `aspire run`.")
    client = AspireClient(socket_path)
    client.connect()
    client.on_disconnect(lambda: sys.exit(1))
    return client

def create_builder(options: Any | None = None) -> IDistributedApplicationBuilder:
    client = connect()
    resolved_options: Dict[str, Any] = {}
    if options is not None:
        if hasattr(options, "to_dict"):
            resolved_options.update(options.to_dict())
        elif isinstance(options, dict):
            resolved_options.update(options)
    resolved_options.setdefault("Args", sys.argv[1:])
    resolved_options.setdefault("ProjectDirectory", os.environ.get("ASPIRE_PROJECT_DIRECTORY", os.getcwd()))
    result = client.invoke_capability("Aspire.Hosting/createBuilderWithOptions", {"options": resolved_options})
    return result

# Re-export commonly used types
CapabilityError = CapabilityError
Handle = Handle
ReferenceExpression = ReferenceExpression
ref_expr = ref_expr

