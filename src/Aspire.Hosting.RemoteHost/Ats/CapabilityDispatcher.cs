// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.TypeSystem;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteHost.Ats;

/// <summary>
/// Delegate for capability implementations.
/// </summary>
/// <param name="args">The arguments as a JSON object.</param>
/// <param name="handles">The handle registry for resolving/registering handles.</param>
/// <returns>The result as JSON, or null for void operations.</returns>
internal delegate Task<JsonNode?> CapabilityHandler(
    JsonObject? args,
    HandleRegistry handles);

/// <summary>
/// Dispatches capability invocations to their implementations.
/// Scans provided assemblies for [AspireExport] attributes.
/// </summary>
internal sealed class CapabilityDispatcher
{
    private readonly ConcurrentDictionary<string, CapabilityRegistration> _capabilities = new();
    private readonly HandleRegistry _handles;
    private readonly AtsMarshaller _marshaller;
    private readonly ILogger _logger;
    private AtsContext? _atsContext;

    /// <summary>
    /// Represents a registered capability.
    /// </summary>
    private sealed class CapabilityRegistration
    {
        public required string CapabilityId { get; init; }
        public required CapabilityHandler Handler { get; init; }
        public string? Description { get; init; }
    }

    /// <summary>
    /// Creates a new CapabilityDispatcher for DI.
    /// </summary>
    /// <param name="handles">The handle registry for resolving handle references.</param>
    /// <param name="assemblyLoader">The assembly loader to get assemblies from.</param>
    /// <param name="marshaller">The marshaller for converting objects to/from JSON.</param>
    /// <param name="logger">The logger.</param>
    public CapabilityDispatcher(
        HandleRegistry handles,
        AssemblyLoader assemblyLoader,
        AtsMarshaller marshaller,
        ILogger<CapabilityDispatcher> logger)
    {
        _handles = handles;
        _marshaller = marshaller;
        _logger = logger;

        // Scan for capabilities on initialization
        ScanAssemblies(assemblyLoader.GetAssemblies());
    }

    /// <summary>
    /// Creates a new CapabilityDispatcher for testing purposes.
    /// </summary>
    /// <param name="handles">The handle registry for resolving handle references.</param>
    /// <param name="marshaller">The marshaller for converting objects to/from JSON.</param>
    /// <param name="assemblies">The assemblies to scan for capabilities.</param>
    internal CapabilityDispatcher(
        HandleRegistry handles,
        AtsMarshaller marshaller,
        IReadOnlyList<Assembly> assemblies)
    {
        _handles = handles;
        _marshaller = marshaller;
        _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<CapabilityDispatcher>.Instance;

        ScanAssemblies(assemblies);
    }

    /// <summary>
    /// Scans the provided assemblies for [AspireExport] and [AspireContextType] attributes.
    /// Uses the shared AtsCapabilityScanner for discovery.
    /// </summary>
    private void ScanAssemblies(IEnumerable<Assembly> assemblies)
    {
        var assemblyList = assemblies.ToList();

        _logger.LogDebug("Scanning {AssemblyCount} assemblies for capabilities...", assemblyList.Count);

        // Scan all assemblies at once to get combined result with AtsContext
        var result = AtsCapabilityScanner.ScanAssemblies(assemblyList);

        // Store the AtsContext for capability registration
        _atsContext = result.ToAtsContext();

        // Log diagnostics from the scanner
        foreach (var diagnostic in result.Diagnostics)
        {
            if (diagnostic.Severity == AtsDiagnosticSeverity.Error)
            {
                _logger.LogError("{Message} at {Location}", diagnostic.Message, diagnostic.Location);
            }
            else if (diagnostic.Severity == AtsDiagnosticSeverity.Info)
            {
                _logger.LogDebug("{Message} at {Location}", diagnostic.Message, diagnostic.Location);
            }
            else
            {
                _logger.LogWarning("{Message} at {Location}", diagnostic.Message, diagnostic.Location);
            }
        }

        // Register all capabilities
        foreach (var capability in result.Capabilities)
        {
            if ((capability.CapabilityKind == AtsCapabilityKind.PropertyGetter || capability.CapabilityKind == AtsCapabilityKind.PropertySetter)
                && result.Properties.TryGetValue(capability.CapabilityId, out var property))
            {
                // Context type property capability
                RegisterContextTypeProperty(capability, property);
            }
            else if (capability.CapabilityKind == AtsCapabilityKind.InstanceMethod
                && result.Methods.TryGetValue(capability.CapabilityId, out var instanceMethod))
            {
                // Context type method capability (instance method)
                RegisterContextTypeMethod(capability, instanceMethod);
            }
            else if (result.Methods.TryGetValue(capability.CapabilityId, out var method))
            {
                // Static method capability
                RegisterFromCapability(capability, method);
            }
        }

        // Log summary of all registered capabilities
        _logger.LogDebug("Registered {CapabilityCount} capabilities", _capabilities.Count);
        foreach (var capabilityId in _capabilities.Keys.OrderBy(k => k))
        {
            _logger.LogTrace("  - {CapabilityId}", capabilityId);
        }
    }

    /// <summary>
    /// Registers a context type property capability.
    /// </summary>
    private void RegisterContextTypeProperty(AtsCapabilityInfo capability, PropertyInfo property)
    {
        var capabilityId = capability.CapabilityId;
        var prop = property; // Capture for closure

        if (capability.CapabilityKind == AtsCapabilityKind.PropertyGetter)
        {
            // Getter capability
            CapabilityHandler getterHandler = (args, handles) =>
            {
                if (args == null || !args.TryGetPropertyValue("context", out var contextNode))
                {
                    throw CapabilityException.InvalidArgument(capabilityId, "context", "Missing required argument 'context'");
                }

                var handleRef = HandleRef.FromJsonNode(contextNode);
                if (handleRef == null)
                {
                    throw CapabilityException.InvalidArgument(capabilityId, "context", "Argument 'context' must be a handle reference");
                }

                if (!handles.TryGet(handleRef.HandleId, out var contextObj, out _))
                {
                    throw CapabilityException.HandleNotFound(handleRef.HandleId, capabilityId);
                }

                // Bridge builder -> resource: if the handle contains an IResourceBuilder<T>
                // but the property is declared on the resource type T, unwrap to the
                // correct target object. See AtsCapabilityScanner.MapToAtsTypeId.
                var target = ResolveContextTarget(contextObj!, prop.DeclaringType!);

                var value = prop.GetValue(target);
                return Task.FromResult(_marshaller.MarshalToJson(value, capability.ReturnType));
            };

            _capabilities[capabilityId] = new CapabilityRegistration
            {
                CapabilityId = capabilityId,
                Handler = getterHandler,
                Description = capability.Description ?? $"Gets the {property.Name} property"
            };
        }
        else if (capability.CapabilityKind == AtsCapabilityKind.PropertySetter)
        {
            // Setter capability - returns the context handle for fluent chaining
            CapabilityHandler setterHandler = (args, handles) =>
            {
                if (args == null || !args.TryGetPropertyValue("context", out var contextNode))
                {
                    throw CapabilityException.InvalidArgument(capabilityId, "context", "Missing required argument 'context'");
                }

                var handleRef = HandleRef.FromJsonNode(contextNode);
                if (handleRef == null)
                {
                    throw CapabilityException.InvalidArgument(capabilityId, "context", "Argument 'context' must be a handle reference");
                }

                if (!handles.TryGet(handleRef.HandleId, out var contextObj, out var typeId))
                {
                    throw CapabilityException.HandleNotFound(handleRef.HandleId, capabilityId);
                }

                if (!args.TryGetPropertyValue("value", out var valueNode))
                {
                    throw CapabilityException.InvalidArgument(capabilityId, "value", "Missing required argument 'value'");
                }

                var unmarshalContext = new AtsMarshaller.UnmarshalContext
                {
                    CapabilityId = capabilityId,
                    ParameterName = "value"
                };
                var value = _marshaller.UnmarshalFromJson(valueNode, prop.PropertyType, unmarshalContext);

                // Bridge builder -> resource for setter as well.
                var setTarget = ResolveContextTarget(contextObj!, prop.DeclaringType!);
                prop.SetValue(setTarget, value);

                // Return the context handle for fluent chaining
                return Task.FromResult<JsonNode?>(new JsonObject
                {
                    ["$handle"] = handleRef.HandleId,
                    ["$type"] = typeId
                });
            };

            _capabilities[capabilityId] = new CapabilityRegistration
            {
                CapabilityId = capabilityId,
                Handler = setterHandler,
                Description = capability.Description ?? $"Sets the {property.Name} property"
            };
        }
    }

    /// <summary>
    /// Registers a context type method capability (instance method).
    /// </summary>
    private void RegisterContextTypeMethod(AtsCapabilityInfo capability, MethodInfo method)
    {
        var capabilityId = capability.CapabilityId;
        var parameters = method.GetParameters();

        CapabilityHandler handler = async (args, handles) =>
        {
            // First parameter is always "context" - the instance to invoke on
            if (args == null || !args.TryGetPropertyValue("context", out var contextNode))
            {
                throw CapabilityException.InvalidArgument(capabilityId, "context", "Missing required argument 'context'");
            }

            var handleRef = HandleRef.FromJsonNode(contextNode);
            if (handleRef == null)
            {
                throw CapabilityException.InvalidArgument(capabilityId, "context", "Argument 'context' must be a handle reference");
            }

            if (!handles.TryGet(handleRef.HandleId, out var contextObj, out _))
            {
                throw CapabilityException.HandleNotFound(handleRef.HandleId, capabilityId);
            }

            // Build method arguments from the remaining parameters
            var methodArgs = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                var paramName = param.Name ?? $"arg{i}";

                if (args.TryGetPropertyValue(paramName, out var argNode))
                {
                    var context = new AtsMarshaller.UnmarshalContext
                    {
                        CapabilityId = capabilityId,
                        ParameterName = paramName
                    };
                    methodArgs[i] = _marshaller.UnmarshalFromJson(argNode, param.ParameterType, context);
                }
                else if (param.HasDefaultValue)
                {
                    methodArgs[i] = param.DefaultValue;
                }
                else
                {
                    throw CapabilityException.InvalidArgument(
                        capabilityId, paramName, $"Missing required argument '{paramName}'");
                }
            }

            // Handle generic methods - resolve type parameters from actual arguments
            var methodToInvoke = method;
            if (method.ContainsGenericParameters)
            {
                methodToInvoke = GenericMethodResolver.MakeGenericMethodFromArgs(method, methodArgs);
            }

            // Bridge builder -> resource: if the handle contains an IResourceBuilder<T>
            // but the method is declared on the resource type T, unwrap to the
            // correct target object. See AtsCapabilityScanner.MapToAtsTypeId.
            var invokeTarget = ResolveContextTarget(contextObj!, methodToInvoke.DeclaringType!);

            object? result;
            try
            {
                result = await InvokeMethodAsync(methodToInvoke, invokeTarget, methodArgs, capability.RunSyncOnBackgroundThread).ConfigureAwait(false);
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                throw tie.InnerException;
            }
            catch (ArgumentException ex)
            {
                var (mismatchParam, expected, actual) = FindMismatchedParameter(methodToInvoke.GetParameters(), methodArgs, ex);
                throw CapabilityException.TypeMismatch(capabilityId, mismatchParam, expected, actual);
            }

            result = await UnwrapAsyncResultAsync(result, methodToInvoke.ReturnType).ConfigureAwait(false);

            return _marshaller.MarshalToJson(result, capability.ReturnType);
        };

        _capabilities[capabilityId] = new CapabilityRegistration
        {
            CapabilityId = capabilityId,
            Handler = handler,
            Description = capability.Description ?? $"Invokes the {method.Name} method"
        };
    }

    /// <summary>
    /// Registers a capability from its info and method.
    /// Uses metadata from the shared scanner, creates runtime handler for invocation.
    /// </summary>
    private void RegisterFromCapability(AtsCapabilityInfo capability, MethodInfo method)
    {
        var capabilityId = capability.CapabilityId;
        var parameters = method.GetParameters();

        // Create a handler that invokes the method via reflection
        CapabilityHandler handler = async (args, handles) =>
        {
            var methodArgs = new object?[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                var paramName = param.Name ?? $"arg{i}";

                if (args != null && args.TryGetPropertyValue(paramName, out var argNode))
                {
                    var context = new AtsMarshaller.UnmarshalContext
                    {
                        CapabilityId = capabilityId,
                        ParameterName = paramName
                    };
                    methodArgs[i] = _marshaller.UnmarshalFromJson(argNode, param.ParameterType, context);
                }
                else if (param.HasDefaultValue)
                {
                    methodArgs[i] = param.DefaultValue;
                }
                else
                {
                    throw CapabilityException.InvalidArgument(
                        capabilityId, paramName, $"Missing required argument '{paramName}'");
                }
            }

            // Handle generic methods - resolve type parameters from actual arguments
            var methodToInvoke = method;
            if (method.ContainsGenericParameters)
            {
                methodToInvoke = GenericMethodResolver.MakeGenericMethodFromArgs(method, methodArgs);
            }

            object? result;
            try
            {
                result = await InvokeMethodAsync(methodToInvoke, target: null, methodArgs, capability.RunSyncOnBackgroundThread).ConfigureAwait(false);
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                // Unwrap the TargetInvocationException to get the actual exception
                throw tie.InnerException;
            }
            catch (ArgumentException ex)
            {
                var (mismatchParam, expected, actual) = FindMismatchedParameter(methodToInvoke.GetParameters(), methodArgs, ex);
                throw CapabilityException.TypeMismatch(capabilityId, mismatchParam, expected, actual);
            }

            result = await UnwrapAsyncResultAsync(result, methodToInvoke.ReturnType).ConfigureAwait(false);

            return _marshaller.MarshalToJson(result, capability.ReturnType);
        };

        _capabilities[capabilityId] = new CapabilityRegistration
        {
            CapabilityId = capabilityId,
            Handler = handler,
            Description = capability.Description
        };
    }

    /// <summary>
    /// Registers a capability with its handler.
    /// </summary>
    /// <param name="capabilityId">The capability ID (e.g., "Aspire.Hosting.Redis/addRedis").</param>
    /// <param name="handler">The handler that implements the capability.</param>
    /// <param name="description">Optional description of the capability.</param>
    public void Register(
        string capabilityId,
        CapabilityHandler handler,
        string? description = null)
    {
        _capabilities[capabilityId] = new CapabilityRegistration
        {
            CapabilityId = capabilityId,
            Handler = handler,
            Description = description
        };
    }

    /// <summary>
    /// Invokes a capability by ID with the given arguments.
    /// Type validation is performed by the CLR at runtime.
    /// </summary>
    /// <param name="capabilityId">The capability ID.</param>
    /// <param name="args">The arguments as a JSON object.</param>
    /// <returns>The result as JSON, or null for void methods.</returns>
    public async Task<JsonNode?> InvokeAsync(string capabilityId, JsonObject? args)
    {
        // Look up the capability
        if (!_capabilities.TryGetValue(capabilityId, out var registration))
        {
            throw CapabilityException.CapabilityNotFound(capabilityId);
        }

        args ??= new JsonObject();

        try
        {
            return await registration.Handler(args, _handles).ConfigureAwait(false);
        }
        catch (CapabilityException)
        {
            throw;
        }
        catch (InvalidCastException ex)
        {
            // Convert CLR cast failures to ATS error
            throw CapabilityException.TypeMismatch(capabilityId, "unknown", "unknown", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capability {CapabilityId} failed with {ExceptionType}: {Message}", capabilityId, ex.GetType().Name, ex.Message);
            throw CapabilityException.InternalError(capabilityId, ex.Message, ex);
        }
    }

    /// <summary>
    /// Invokes a capability by ID with the given arguments synchronously.
    /// This is a convenience method that blocks until the async operation completes.
    /// For production use, prefer InvokeAsync.
    /// </summary>
    /// <param name="capabilityId">The capability ID.</param>
    /// <param name="args">The arguments as a JSON object.</param>
    /// <returns>The result as JSON, or null for void methods.</returns>
    public JsonNode? Invoke(string capabilityId, JsonObject? args)
    {
        return InvokeAsync(capabilityId, args).GetAwaiter().GetResult();
    }

    private static async Task<object?> InvokeMethodAsync(MethodInfo method, object? target, object?[] methodArgs, bool runSyncOnBackgroundThread)
    {
        if (runSyncOnBackgroundThread && !IsAsyncReturnType(method.ReturnType))
        {
            return await Task.Run(() => InvokeMethodCore(method, target, methodArgs)).ConfigureAwait(false);
        }

        return InvokeMethodCore(method, target, methodArgs);
    }

    private static bool IsAsyncReturnType(Type returnType)
    {
        if (typeof(Task).IsAssignableFrom(returnType) || returnType == typeof(ValueTask))
        {
            return true;
        }

        return returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>);
    }

    private static async Task<object?> UnwrapAsyncResultAsync(object? result, Type returnType)
    {
        try
        {
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                return GetAsyncResultValue(task);
            }

            if (returnType == typeof(ValueTask) && result is ValueTask valueTask)
            {
                await valueTask.ConfigureAwait(false);
                return null;
            }

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                var asTask = returnType.GetMethod(nameof(ValueTask<int>.AsTask), BindingFlags.Instance | BindingFlags.Public)
                    ?? throw new InvalidOperationException($"Unable to await ValueTask result for return type '{returnType}'.");
                var boxedTask = asTask.Invoke(result, null) as Task
                    ?? throw new InvalidOperationException($"Unable to convert ValueTask result for return type '{returnType}' to Task.");

                await boxedTask.ConfigureAwait(false);
                return GetAsyncResultValue(boxedTask);
            }

            return result;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    private static object? GetAsyncResultValue(Task task)
    {
        var taskType = task.GetType();
        if (!taskType.IsGenericType)
        {
            return null;
        }

        var resultProperty = taskType.GetProperty("Result");
        return resultProperty?.GetValue(task);
    }

    private static object? InvokeMethodCore(MethodInfo method, object? target, object?[] methodArgs)
    {
        try
        {
            return method.Invoke(target, methodArgs);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }

    /// <summary>
    /// Finds the mismatched parameter by comparing actual argument types against expected parameter types.
    /// Falls back to parsing the exception message if no mismatch is found by inspection.
    /// </summary>
    private static (string ParameterName, string ExpectedType, string ActualType) FindMismatchedParameter(
        ParameterInfo[] parameters, object?[] args, ArgumentException ex)
    {
        // Inspect actual args vs parameter types to find the mismatch
        for (int i = 0; i < parameters.Length && i < args.Length; i++)
        {
            var param = parameters[i];
            var arg = args[i];
            var paramType = param.ParameterType;

            if (arg is null)
            {
                if (paramType.IsValueType && Nullable.GetUnderlyingType(paramType) is null)
                {
                    return (param.Name ?? $"arg{i}", paramType.ToString(), "null");
                }
            }
            else if (!paramType.IsAssignableFrom(arg.GetType()))
            {
                return (param.Name ?? $"arg{i}", paramType.ToString(), arg.GetType().ToString());
            }
        }

        // Fallback: parse the exception message for type names
        var message = ex.Message;
        var match = Regex.Match(message, @"type '([^']+)'.*(?:to|into) type '([^']+)'");
        if (match.Success)
        {
            return (ex.ParamName ?? "unknown", match.Groups[2].Value, match.Groups[1].Value);
        }

        return (ex.ParamName ?? "unknown", "unknown", "unknown");
    }

    /// <summary>
    /// Resolves the correct target object for a member invocation.
    /// Handles the case where the handle contains an <c>IResourceBuilder&lt;T&gt;</c> but the
    /// member is declared on the resource type <c>T</c>, since
    /// <c>AtsCapabilityScanner.MapToAtsTypeId</c> maps both to the same type ID.
    /// </summary>
    private static object ResolveContextTarget(object contextObj, Type declaringType)
    {
        if (declaringType.IsInstanceOfType(contextObj))
        {
            return contextObj;
        }

        // Handle is a builder, but the member is on the resource type - extract .Resource
        if (HostingTypeHelpers.IsResourceBuilderType(contextObj.GetType()))
        {
            var resource = contextObj.GetType()
                .GetProperty("Resource", BindingFlags.Instance | BindingFlags.Public)?
                .GetValue(contextObj);

            if (resource is not null && declaringType.IsInstanceOfType(resource))
            {
                return resource;
            }
        }

        // No bridge matched — return as-is; the CLR will throw TargetException if the
        // object truly doesn't match the declaring type.
        return contextObj;
    }

    /// <summary>
    /// Gets all registered capability IDs.
    /// </summary>
    public IEnumerable<string> GetCapabilityIds() => _capabilities.Keys;

    /// <summary>
    /// Checks if a capability is registered.
    /// </summary>
    public bool HasCapability(string capabilityId) => _capabilities.ContainsKey(capabilityId);
}

/// <summary>
/// Extension methods for working with JSON in capability handlers.
/// </summary>
internal static class CapabilityJsonExtensions
{

    /// <summary>
    /// Gets a required string argument.
    /// </summary>
    public static string GetRequiredString(this JsonObject args, string name, string capabilityId)
    {
        if (!args.TryGetPropertyValue(name, out var node) || node is not JsonValue value)
        {
            throw CapabilityException.InvalidArgument(capabilityId, name, $"Missing required argument '{name}'");
        }

        return value.GetValue<string>() ??
            throw CapabilityException.InvalidArgument(capabilityId, name, $"Argument '{name}' cannot be null");
    }

    /// <summary>
    /// Gets an optional string argument.
    /// </summary>
    public static string? GetOptionalString(this JsonObject args, string name)
    {
        if (args.TryGetPropertyValue(name, out var node) && node is JsonValue value)
        {
            return value.GetValue<string>();
        }
        return null;
    }

    /// <summary>
    /// Gets an optional int argument.
    /// </summary>
    public static int? GetOptionalInt(this JsonObject args, string name)
    {
        if (args.TryGetPropertyValue(name, out var node) && node is JsonValue value)
        {
            return value.GetValue<int>();
        }
        return null;
    }

    /// <summary>
    /// Gets a required handle reference.
    /// </summary>
    public static T GetRequiredHandle<T>(
        this JsonObject args,
        string name,
        string capabilityId,
        HandleRegistry handles) where T : class
    {
        if (!args.TryGetPropertyValue(name, out var node))
        {
            throw CapabilityException.InvalidArgument(capabilityId, name, $"Missing required argument '{name}'");
        }

        var handleRef = HandleRef.FromJsonNode(node) ??
            throw CapabilityException.InvalidArgument(capabilityId, name, $"Argument '{name}' must be a handle reference");

        if (!handles.TryGet(handleRef.HandleId, out var obj, out _))
        {
            throw CapabilityException.HandleNotFound(handleRef.HandleId, capabilityId);
        }

        if (obj is not T typed)
        {
            throw CapabilityException.TypeMismatch(
                capabilityId, name, typeof(T).Name, obj?.GetType().Name ?? "null");
        }

        return typed;
    }

    /// <summary>
    /// Deserializes a DTO from a JSON argument.
    /// </summary>
    public static T? GetDto<T>(this JsonObject args, string name) where T : class
    {
        if (args.TryGetPropertyValue(name, out var node) && node is JsonObject obj)
        {
            return JsonSerializer.Deserialize<T>(obj.ToJsonString(), AtsMarshaller.JsonOptions);
        }
        return null;
    }

    /// <summary>
    /// Creates a handle result for returning from a capability.
    /// </summary>
    public static JsonObject CreateHandleResult(this HandleRegistry handles, object obj, string typeId)
    {
        return handles.Marshal(obj, typeId);
    }
}
