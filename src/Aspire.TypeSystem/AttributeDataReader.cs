// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;

namespace Aspire.TypeSystem;

/// <summary>
/// Provides full-name-based discovery of ATS attributes from <see cref="CustomAttributeData"/>,
/// so that third-party authors can define their own attribute types with the same full name
/// without requiring a package reference to Aspire.Hosting.
/// </summary>
public static class AttributeDataReader
{
    private const string AspireExportAttributeFullName = HostingTypeNames.AspireExportAttribute;
    private const string AspireExportIgnoreAttributeFullName = HostingTypeNames.AspireExportIgnoreAttribute;
    private const string AspireDtoAttributeFullName = HostingTypeNames.AspireDtoAttribute;
    private const string AspireUnionAttributeFullName = HostingTypeNames.AspireUnionAttribute;

    // --- AspireExport lookup ---

    /// <summary>
    /// Gets <see cref="AspireExportData"/> from the specified <paramref name="type"/>, if present.
    /// </summary>
    public static AspireExportData? GetAspireExportData(Type type)
        => FindSingleAttribute<AspireExportData>(type.GetCustomAttributesData(), AspireExportAttributeFullName, ParseAspireExportData);

    /// <summary>
    /// Gets <see cref="AspireExportData"/> from the specified <paramref name="method"/>, if present.
    /// </summary>
    public static AspireExportData? GetAspireExportData(MethodInfo method)
        => FindSingleAttribute<AspireExportData>(method.GetCustomAttributesData(), AspireExportAttributeFullName, ParseAspireExportData);

    /// <summary>
    /// Gets <see cref="AspireExportData"/> from the specified <paramref name="property"/>, if present.
    /// </summary>
    public static AspireExportData? GetAspireExportData(PropertyInfo property)
        => FindSingleAttribute<AspireExportData>(property.GetCustomAttributesData(), AspireExportAttributeFullName, ParseAspireExportData);

    /// <summary>
    /// Gets all <see cref="AspireExportData"/> entries from the specified <paramref name="assembly"/>.
    /// </summary>
    public static IEnumerable<AspireExportData> GetAspireExportDataAll(Assembly assembly)
        => FindAllAttributes(assembly.GetCustomAttributesData(), AspireExportAttributeFullName, ParseAspireExportData);

    // --- AspireExportIgnore lookup ---

    /// <summary>
    /// Determines whether the specified <paramref name="type"/> has the AspireExportIgnore attribute.
    /// </summary>
    public static bool HasAspireExportIgnoreData(Type type)
        => HasAttribute(type.GetCustomAttributesData(), AspireExportIgnoreAttributeFullName);

    /// <summary>
    /// Determines whether the specified <paramref name="property"/> has the AspireExportIgnore attribute.
    /// </summary>
    public static bool HasAspireExportIgnoreData(PropertyInfo property)
        => HasAttribute(property.GetCustomAttributesData(), AspireExportIgnoreAttributeFullName);

    /// <summary>
    /// Determines whether the specified <paramref name="method"/> has the AspireExportIgnore attribute.
    /// </summary>
    public static bool HasAspireExportIgnoreData(MethodInfo method)
        => HasAttribute(method.GetCustomAttributesData(), AspireExportIgnoreAttributeFullName);

    // --- AspireDto lookup ---

    /// <summary>
    /// Determines whether the specified <paramref name="type"/> has the AspireDto attribute.
    /// </summary>
    public static bool HasAspireDtoData(Type type)
        => HasAttribute(type.GetCustomAttributesData(), AspireDtoAttributeFullName);

    // --- AspireUnion lookup ---

    /// <summary>
    /// Gets <see cref="AspireUnionData"/> from the specified <paramref name="parameter"/>, if present.
    /// </summary>
    public static AspireUnionData? GetAspireUnionData(ParameterInfo parameter)
        => FindSingleAttribute<AspireUnionData>(parameter.GetCustomAttributesData(), AspireUnionAttributeFullName, ParseAspireUnionData);

    /// <summary>
    /// Gets <see cref="AspireUnionData"/> from the specified <paramref name="property"/>, if present.
    /// </summary>
    public static AspireUnionData? GetAspireUnionData(PropertyInfo property)
        => FindSingleAttribute<AspireUnionData>(property.GetCustomAttributesData(), AspireUnionAttributeFullName, ParseAspireUnionData);

    // --- Generic helpers ---

    private static bool HasAttribute(IList<CustomAttributeData> attributes, string attributeFullName)
    {
        for (var i = 0; i < attributes.Count; i++)
        {
            if (IsMatch(attributes[i], attributeFullName))
            {
                return true;
            }
        }

        return false;
    }

    private static T? FindSingleAttribute<T>(IList<CustomAttributeData> attributes, string attributeFullName, Func<CustomAttributeData, T> parser) where T : class
    {
        for (var i = 0; i < attributes.Count; i++)
        {
            if (IsMatch(attributes[i], attributeFullName))
            {
                return parser(attributes[i]);
            }
        }

        return null;
    }

    private static IEnumerable<T> FindAllAttributes<T>(IList<CustomAttributeData> attributes, string attributeFullName, Func<CustomAttributeData, T> parser) where T : class
    {
        for (var i = 0; i < attributes.Count; i++)
        {
            if (IsMatch(attributes[i], attributeFullName))
            {
                yield return parser(attributes[i]);
            }
        }
    }

    private static bool IsMatch(CustomAttributeData data, string attributeFullName)
    {
        return string.Equals(data.AttributeType.FullName, attributeFullName, StringComparison.Ordinal);
    }

    // --- Parsers ---

    private static AspireExportData ParseAspireExportData(CustomAttributeData data)
    {
        string? id = null;
        Type? type = null;

        // Match constructor arguments by signature (arity + type) rather than parameter name,
        // so third-party attribute copies with different parameter names still work.
        // The three recognized constructor signatures are:
        //   ()           — type export
        //   (string)     — capability export (the string is the method/capability id)
        //   (Type)       — assembly-level type export
        if (data.ConstructorArguments.Count == 1)
        {
            var arg = data.ConstructorArguments[0];
            if (arg.Value is string idValue)
            {
                id = idValue;
            }
            else if (arg.Value is Type typeValue)
            {
                type = typeValue;
            }
        }

        // Read named arguments
        string? description = null;
        string? methodName = null;
        var exposeProperties = false;
        var exposeMethods = false;
        var runSyncOnBackgroundThread = false;

        for (var i = 0; i < data.NamedArguments.Count; i++)
        {
            var named = data.NamedArguments[i];
            switch (named.MemberName)
            {
                case nameof(AspireExportData.Type):
                    if (named.TypedValue.Value is Type namedType)
                    {
                        type = namedType;
                    }
                    break;
                case nameof(AspireExportData.Description):
                    description = named.TypedValue.Value as string;
                    break;
                case nameof(AspireExportData.MethodName):
                    methodName = named.TypedValue.Value as string;
                    break;
                case nameof(AspireExportData.ExposeProperties):
                    if (named.TypedValue.Value is bool ep)
                    {
                        exposeProperties = ep;
                    }
                    break;
                case nameof(AspireExportData.ExposeMethods):
                    if (named.TypedValue.Value is bool em)
                    {
                        exposeMethods = em;
                    }
                    break;
                case nameof(AspireExportData.RunSyncOnBackgroundThread):
                    if (named.TypedValue.Value is bool rs)
                    {
                        runSyncOnBackgroundThread = rs;
                    }
                    break;
            }
        }

        return new AspireExportData
        {
            Id = id,
            Type = type,
            Description = description,
            MethodName = methodName,
            ExposeProperties = exposeProperties,
            ExposeMethods = exposeMethods,
            RunSyncOnBackgroundThread = runSyncOnBackgroundThread
        };
    }

    private static AspireUnionData ParseAspireUnionData(CustomAttributeData data)
    {
        // The constructor is AspireUnionAttribute(params Type[] types).
        // CustomAttributeData represents params as either:
        //   1. A single constructor argument of type Type[] (ReadOnlyCollection<CustomAttributeTypedArgument>)
        //   2. Multiple individual constructor arguments of type Type
        var types = new List<Type>();

        if (data.ConstructorArguments.Count == 1 &&
            data.ConstructorArguments[0].Value is IReadOnlyCollection<CustomAttributeTypedArgument> elements)
        {
            // params represented as a single array argument
            foreach (var element in elements)
            {
                if (element.Value is Type t)
                {
                    types.Add(t);
                }
            }
        }
        else
        {
            // params represented as individual arguments
            for (var i = 0; i < data.ConstructorArguments.Count; i++)
            {
                if (data.ConstructorArguments[i].Value is Type t)
                {
                    types.Add(t);
                }
            }
        }

        return new AspireUnionData
        {
            Types = [.. types]
        };
    }
}

/// <summary>
/// Name-based adapter for [AspireExport] attribute data, parsed from <see cref="CustomAttributeData"/>.
/// </summary>
public sealed class AspireExportData
{
    /// <summary>
    /// Gets the method name / capability id from the constructor argument.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// Gets the CLR type for assembly-level type exports.
    /// </summary>
    public Type? Type { get; init; }

    /// <summary>
    /// Gets a description of what this export does.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the method name override for generated polyglot SDKs.
    /// </summary>
    public string? MethodName { get; init; }

    /// <summary>
    /// Gets whether to expose properties of this type as ATS capabilities.
    /// </summary>
    public bool ExposeProperties { get; init; }

    /// <summary>
    /// Gets whether to expose public instance methods of this type as ATS capabilities.
    /// </summary>
    public bool ExposeMethods { get; init; }

    /// <summary>
    /// Gets whether synchronous exported methods should be invoked on a background thread by the ATS dispatcher.
    /// </summary>
    public bool RunSyncOnBackgroundThread { get; init; }
}

/// <summary>
/// Name-based adapter for [AspireUnion] attribute data, parsed from <see cref="CustomAttributeData"/>.
/// </summary>
public sealed class AspireUnionData
{
    /// <summary>
    /// Gets the CLR types that form the union.
    /// </summary>
    public required Type[] Types { get; init; }
}
