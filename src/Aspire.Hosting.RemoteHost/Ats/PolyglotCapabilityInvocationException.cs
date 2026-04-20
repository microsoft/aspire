// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.TypeSystem;

namespace Aspire.Hosting.RemoteHost.Ats;

/// <summary>
/// Exception used to convert raw hosting failures into polyglot-friendly ATS errors.
/// </summary>
internal sealed class PolyglotCapabilityInvocationException : Exception
{
    private PolyglotCapabilityInvocationException(
        string capabilityId,
        string errorCode,
        string message,
        string? parameterName = null,
        string? expected = null,
        string? actual = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        CapabilityId = capabilityId;
        ErrorCode = errorCode;
        ParameterName = parameterName;
        Expected = expected;
        Actual = actual;
    }

    /// <summary>
    /// Gets the capability ID that failed.
    /// </summary>
    public string CapabilityId { get; }

    /// <summary>
    /// Gets the ATS error code to return.
    /// </summary>
    public string ErrorCode { get; }

    /// <summary>
    /// Gets the parameter name when the failure is tied to one.
    /// </summary>
    public string? ParameterName { get; }

    /// <summary>
    /// Gets the expected type description for type mismatch failures.
    /// </summary>
    public string? Expected { get; }

    /// <summary>
    /// Gets the actual type description for type mismatch failures.
    /// </summary>
    public string? Actual { get; }

    /// <summary>
    /// Creates a type mismatch error.
    /// </summary>
    public static PolyglotCapabilityInvocationException TypeMismatch(
        string capabilityId,
        string parameterName,
        string message,
        string expected,
        string actual,
        Exception? innerException = null)
    {
        return new PolyglotCapabilityInvocationException(
            capabilityId,
            AtsErrorCodes.TypeMismatch,
            message,
            parameterName,
            expected,
            actual,
            innerException);
    }

    /// <summary>
    /// Creates an internal error with a user-facing message.
    /// </summary>
    public static PolyglotCapabilityInvocationException InternalError(
        string capabilityId,
        string message,
        Exception? innerException = null,
        string? errorCode = null)
    {
        return new PolyglotCapabilityInvocationException(
            capabilityId,
            errorCode ?? AtsErrorCodes.InternalError,
            message,
            innerException: innerException);
    }

    /// <summary>
    /// Converts this exception to a structured ATS <see cref="CapabilityException"/>.
    /// </summary>
    public CapabilityException ToCapabilityException()
    {
        var error = new AtsError
        {
            Code = ErrorCode,
            Message = Message,
            Capability = CapabilityId,
            Details = ParameterName is null && Expected is null && Actual is null
                ? null
                : new AtsErrorDetails
                {
                    Parameter = ParameterName,
                    Expected = Expected,
                    Actual = Actual
                }
        };

        return InnerException is not null
            ? new CapabilityException(error, InnerException)
            : new CapabilityException(error);
    }
}

/// <summary>
/// Formats capability failures for polyglot callers.
/// </summary>
internal static partial class PolyglotCapabilityErrorFormatter
{
    public static PolyglotCapabilityInvocationException CreateInternalError(
        string capabilityId,
        string? polyglotMethodName,
        string? clrMemberName,
        JsonObject? args,
        HandleRegistry handles,
        Exception exception,
        IReadOnlyDictionary<string, HashSet<string>>? polyglotMethodNamesByClrName = null,
        string? targetParameterName = null,
        string? errorCode = null)
    {
        var methodName = polyglotMethodName ?? AtsCapabilityScanner.DeriveMethodName(capabilityId);
        var targetContext = TryGetTargetContext(args, handles, targetParameterName);
        var scrubbedMessage = ScrubMessage(exception.Message, polyglotMethodName, clrMemberName, polyglotMethodNamesByClrName);
        var message = exception.GetType().FullName == "Aspire.Hosting.DistributedApplicationException"
            ? scrubbedMessage
            : $"Could not invoke '{methodName}'{targetContext}: {scrubbedMessage}";

        return PolyglotCapabilityInvocationException.InternalError(
            capabilityId,
            message,
            exception,
            errorCode);
    }

    public static PolyglotCapabilityInvocationException CreateTypeMismatch(
        string capabilityId,
        string? polyglotMethodName,
        JsonObject? args,
        HandleRegistry handles,
        string parameterName,
        Type expectedType,
        object actualValue,
        string? targetParameterName = null,
        Exception? innerException = null)
    {
        var methodName = polyglotMethodName ?? AtsCapabilityScanner.DeriveMethodName(capabilityId);
        var targetContext = TryGetTargetContext(args, handles, targetParameterName);
        var expectedDescription = DescribeExpectedType(expectedType);
        var actualDescription = DescribeActualValue(actualValue);
        var message = $"Could not invoke '{methodName}'{targetContext} because parameter '{parameterName}' expects {expectedDescription}, but got {actualDescription}.";

        return PolyglotCapabilityInvocationException.TypeMismatch(
            capabilityId,
            parameterName,
            message,
            expectedDescription,
            actualDescription,
            innerException);
    }

    public static object ResolveHandleArgument(
        string capabilityId,
        string? polyglotMethodName,
        JsonObject? args,
        HandleRegistry handles,
        string parameterName,
        Type expectedType,
        object handleObject,
        string? targetParameterName = null)
    {
        if (TryConvertHandle(handleObject, expectedType, out var converted))
        {
            return converted!;
        }

        throw CreateTypeMismatch(
            capabilityId,
            polyglotMethodName,
            args,
            handles,
            parameterName,
            expectedType,
            handleObject,
            targetParameterName);
    }

    private static bool TryConvertHandle(object handleObject, Type expectedType, out object? converted)
    {
        if (expectedType.IsInstanceOfType(handleObject))
        {
            converted = handleObject;
            return true;
        }

        if (CanUseOpenGenericResourceBuilder(expectedType, handleObject))
        {
            converted = handleObject;
            return true;
        }

        if (HostingTypeHelpers.IsResourceBuilderType(handleObject.GetType()) && HostingTypeHelpers.IsResourceType(expectedType))
        {
            var resource = handleObject.GetType()
                .GetProperty("Resource")?
                .GetValue(handleObject);

            if (resource is not null && expectedType.IsInstanceOfType(resource))
            {
                converted = resource;
                return true;
            }
        }

        converted = null;
        return false;
    }

    internal static bool CanUseOpenGenericResourceBuilder(Type expectedType, object handleObject)
    {
        if (!expectedType.ContainsGenericParameters ||
            !HostingTypeHelpers.IsResourceBuilderType(expectedType) ||
            !HostingTypeHelpers.IsResourceBuilderType(handleObject.GetType()))
        {
            return false;
        }

        var expectedResourceType = TryGetBuilderResourceType(expectedType);
        var actualResourceType = TryGetBuilderResourceType(handleObject);

        return expectedResourceType is not null &&
            actualResourceType is not null &&
            SatisfiesExpectedResourceType(actualResourceType, expectedResourceType);
    }

    private static string TryGetTargetContext(JsonObject? args, HandleRegistry handles, string? targetParameterName)
    {
        if (args is null)
        {
            return string.Empty;
        }

        targetParameterName ??= "context";
        if (!args.TryGetPropertyValue(targetParameterName, out var targetNode))
        {
            return string.Empty;
        }

        var resourceName = TryGetResourceName(targetNode, handles);
        return resourceName is null ? string.Empty : $" on resource '{resourceName}'";
    }

    private static string? TryGetResourceName(JsonNode? node, HandleRegistry handles)
    {
        var handleRef = HandleRef.FromJsonNode(node);
        if (handleRef is null || !handles.TryGet(handleRef.HandleId, out var handleObject, out _))
        {
            return null;
        }

        return TryGetResourceName(handleObject);
    }

    private static string? TryGetResourceName(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (HostingTypeHelpers.IsResourceType(value.GetType()))
        {
            return value.GetType().GetProperty("Name")?.GetValue(value)?.ToString();
        }

        if (HostingTypeHelpers.IsResourceBuilderType(value.GetType()))
        {
            var resource = value.GetType().GetProperty("Resource")?.GetValue(value);
            return TryGetResourceName(resource);
        }

        return null;
    }

    private static string DescribeExpectedType(Type type)
    {
        if (type.IsGenericParameter)
        {
            return DescribeGenericParameter(type);
        }

        if (HostingTypeHelpers.IsResourceBuilderType(type) && type.IsGenericType)
        {
            return $"a resource builder for {DescribeResourceContract(type.GetGenericArguments()[0])}";
        }

        if (HostingTypeHelpers.IsResourceType(type))
        {
            return DescribeResourceContract(type);
        }

        return DescribeType(type, article: true);
    }

    private static string DescribeActualValue(object value)
    {
        if (HostingTypeHelpers.IsResourceBuilderType(value.GetType()))
        {
            return DescribeBuilderValue(value);
        }

        if (TryGetResourceName(value) is { } resourceName)
        {
            return $"resource '{resourceName}'";
        }

        return DescribeType(value.GetType(), article: true);
    }

    private static string DescribeBuilderValue(object value)
    {
        var resourceType = TryGetBuilderResourceType(value);
        var resourceName = TryGetResourceName(value);

        if (resourceType is not null)
        {
            var resourceDescription = DescribeResourceContract(resourceType);

            return resourceName is not null
                ? $"a resource builder for {resourceDescription} named '{resourceName}'"
                : $"a resource builder for {resourceDescription}";
        }

        return resourceName is not null
            ? $"a resource builder for resource '{resourceName}'"
            : "a resource builder";
    }

    private static Type? TryGetBuilderResourceType(object value)
    {
        var valueType = value.GetType();
        if (valueType.IsGenericType && HostingTypeHelpers.IsResourceBuilderType(valueType))
        {
            return valueType.GetGenericArguments()[0];
        }

        var resource = valueType.GetProperty("Resource")?.GetValue(value);
        return resource?.GetType();
    }

    private static Type? TryGetBuilderResourceType(Type builderType)
    {
        if (builderType.IsGenericType && HostingTypeHelpers.IsResourceBuilderType(builderType))
        {
            return builderType.GetGenericArguments()[0];
        }

        var builderInterface = builderType.GetInterfaces().FirstOrDefault(static type =>
            type.IsGenericType &&
            string.Equals(type.GetGenericTypeDefinition().FullName, HostingTypeNames.ResourceBuilderInterface, StringComparison.Ordinal));

        return builderInterface?.GetGenericArguments()[0];
    }

    private static bool SatisfiesExpectedResourceType(Type actualResourceType, Type expectedResourceType)
    {
        if (expectedResourceType.IsGenericParameter)
        {
            return SatisfiesGenericParameterConstraints(actualResourceType, expectedResourceType);
        }

        return !expectedResourceType.ContainsGenericParameters && expectedResourceType.IsAssignableFrom(actualResourceType);
    }

    private static bool SatisfiesGenericParameterConstraints(Type actualResourceType, Type genericParameter)
    {
        var specialConstraints = genericParameter.GenericParameterAttributes & GenericParameterAttributes.SpecialConstraintMask;

        if (specialConstraints.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint) && actualResourceType.IsValueType)
        {
            return false;
        }

        if (specialConstraints.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint) &&
            (!actualResourceType.IsValueType || Nullable.GetUnderlyingType(actualResourceType) is not null))
        {
            return false;
        }

        if (specialConstraints.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint) &&
            !actualResourceType.IsValueType &&
            actualResourceType.GetConstructor(Type.EmptyTypes) is null)
        {
            return false;
        }

        foreach (var constraint in genericParameter.GetGenericParameterConstraints())
        {
            if (!constraint.IsAssignableFrom(actualResourceType))
            {
                return false;
            }
        }

        return true;
    }

    private static string DescribeResourceContract(Type type)
    {
        if (type.IsGenericParameter)
        {
            return DescribeGenericParameter(type);
        }

        var typeName = type.Name;

        // String literals are used because this project does not reference Aspire.Hosting
        // and therefore cannot use typeof() for these interfaces.
        return type.FullName switch
        {
            "Aspire.Hosting.ApplicationModel.IResourceWithConnectionString" => "a resource with a connection string",
            "Aspire.Hosting.ApplicationModel.IResourceWithServiceDiscovery" => "a resource with service discovery",
            "Aspire.Hosting.ApplicationModel.IResourceWithEndpoints" => "a resource with endpoints",
            "Aspire.Hosting.ApplicationModel.IResourceWithEnvironment" => "a resource with environment variables",
            _ when typeName == "ExternalServiceResource" => "an external service resource",
            _ => DescribeType(type, article: true)
        };
    }

    private static string DescribeGenericParameter(Type type)
    {
        var constraints = type.GetGenericParameterConstraints();

        foreach (var constraint in constraints)
        {
            if (constraint.FullName == "Aspire.Hosting.ApplicationModel.IResource")
            {
                continue;
            }

            if (HostingTypeHelpers.IsResourceType(constraint))
            {
                return DescribeResourceContract(constraint);
            }
        }

        return "a compatible value";
    }

    private static string DescribeType(Type type, bool article)
    {
        var name = type.IsGenericType
            ? type.Name[..type.Name.IndexOf('`')]
            : type.Name;

        name = name switch
        {
            "String" => "string",
            "Int32" => "integer",
            "Boolean" => "boolean",
            "Uri" => "URI",
            _ => name.EndsWith("Resource", StringComparison.Ordinal) ? name[..^"Resource".Length] + " resource" : name
        };

        if (!article)
        {
            return name;
        }

        var articleValue = name.StartsWith("a ", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("an ", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : StartsWithVowelSound(name) ? "an " : "a ";

        return articleValue + name;
    }

    private static bool StartsWithVowelSound(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // Words starting with vowel letters but consonant sounds (e.g., "URI" = "yoo-are-eye")
        if (value.StartsWith("URI", StringComparison.Ordinal) ||
            value.StartsWith("URL", StringComparison.Ordinal))
        {
            return false;
        }

        return "AEIOUaeiou".Contains(value[0]);
    }

    private static string ScrubMessage(string message, string? polyglotMethodName, string? clrMemberName, IReadOnlyDictionary<string, HashSet<string>>? polyglotMethodNamesByClrName)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        if (!string.IsNullOrEmpty(polyglotMethodName) && !string.IsNullOrEmpty(clrMemberName))
        {
            message = Regex.Replace(
                message,
                $@"\b{Regex.Escape(clrMemberName)}\b",
                polyglotMethodName);
        }

        if (polyglotMethodNamesByClrName is not null)
        {
            // Replace longest CLR names first to avoid partial matches.
            foreach (var alias in polyglotMethodNamesByClrName.OrderByDescending(static pair => pair.Key.Length))
            {
                // Pick the first polyglot name from the set (typically there is only one).
                var replacement = alias.Value.First();
                message = Regex.Replace(
                    message,
                    $@"\b{Regex.Escape(alias.Key)}\b",
                    replacement);
            }
        }

        message = message.ReplaceLineEndings(" ");
        message = DoubleWhitespaceRegex().Replace(message, " ");
        message = ControlCharacterRegex().Replace(message, " ");

        return message.Trim();
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex DoubleWhitespaceRegex();

    [GeneratedRegex(@"\p{Cc}+")]
    private static partial Regex ControlCharacterRegex();
}
