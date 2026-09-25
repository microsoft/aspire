// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Specifies that a parameter or property accepts a union of multiple ATS types.
/// </summary>
/// <remarks>
/// <para>
/// Use on method parameters to indicate the parameter accepts multiple types:
/// </para>
/// <code>
/// [AspireExport("withEnvironment")]
/// public static IResourceBuilder&lt;T&gt; WithEnvironment&lt;T&gt;(
///     this IResourceBuilder&lt;T&gt; builder,
///     string name,
///     [AspireUnion(typeof(string), typeof(ReferenceExpression))] object value)
/// </code>
/// <para>
/// Use on properties (especially <c>Dictionary&lt;string, object&gt;</c>) to specify the value type union:
/// </para>
/// <code>
/// [AspireExport(ExposeProperties = true)]
/// public class EnvironmentCallbackContext
/// {
///     [AspireUnion(typeof(string), typeof(ReferenceExpression), typeof(EndpointReference))]
///     public Dictionary&lt;string, object&gt; EnvironmentVariables { get; }
/// }
/// </code>
/// <para>
/// <b>Important:</b> All types in the union must be valid ATS types (primitives, handles, DTOs,
/// or collections thereof). The scanner will fail with an error if any type cannot be mapped to ATS.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false)]
public sealed class AspireUnionAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance with the types that form the union.
    /// </summary>
    /// <param name="types">The CLR types that form the union. Must have at least 2 types.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="types"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="types"/> has fewer than 2 types.</exception>
    public AspireUnionAttribute(params Type[] types)
    {
        Types = types ?? throw new ArgumentNullException(nameof(types));
        if (types.Length < 2)
        {
            throw new ArgumentException("Union must have at least 2 types", nameof(types));
        }
    }

    /// <summary>
    /// Gets the CLR types that form the union.
    /// </summary>
    public Type[] Types { get; }
}
