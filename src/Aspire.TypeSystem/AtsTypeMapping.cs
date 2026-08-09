// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.TypeSystem;

/// <summary>
/// Static utility for deriving ATS type IDs from CLR types.
/// </summary>
/// <remarks>
/// Type IDs follow the format <c>{AssemblyName}/{FullTypeName}</c>.
/// </remarks>
public static class AtsTypeMapping
{
    /// <summary>
    /// Derives an ATS type ID from an assembly name and full type name.
    /// </summary>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="fullTypeName">The full type name including namespace.</param>
    /// <returns>The derived type ID in format {AssemblyName}/{FullTypeName}.</returns>
    public static string DeriveTypeId(string assemblyName, string fullTypeName)
    {
        return $"{assemblyName}/{fullTypeName}";
    }

    /// <summary>
    /// Derives an ATS type ID from a CLR type.
    /// </summary>
    /// <param name="type">The CLR type.</param>
    /// <returns>The derived type ID in format {AssemblyName}/{FullTypeName}.</returns>
    public static string DeriveTypeId(Type type)
    {
        var assemblyName = type.Assembly.GetName().Name ?? "Unknown";
        return DeriveTypeId(assemblyName, type.FullName ?? type.Name);
    }
}
