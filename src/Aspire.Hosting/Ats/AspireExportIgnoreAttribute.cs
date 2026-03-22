// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting;

/// <summary>
/// Excludes a property or method from ATS export when the containing type uses
/// <see cref="AspireExportAttribute.ExposeProperties"/> or <see cref="AspireExportAttribute.ExposeMethods"/>.
/// </summary>
/// <remarks>
/// <para>
/// Use this attribute on individual members to opt them out of automatic exposure
/// when the containing type uses <c>ExposeProperties = true</c> or <c>ExposeMethods = true</c>.
/// </para>
/// <para>
/// This is useful when most members should be exposed but a few contain internal
/// implementation details or types that shouldn't be part of the polyglot API.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [AspireExport(ExposeProperties = true)]
/// public class EnvironmentCallbackContext
/// {
///     // Automatically exposed as capability
///     public Dictionary&lt;string, object&gt; EnvironmentVariables { get; }
///
///     [AspireExportIgnore]  // Not exposed - internal implementation detail
///     public ILogger Logger { get; }
/// }
/// </code>
/// </example>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Method,
    Inherited = false,
    AllowMultiple = false)]
[Experimental("ASPIREATS001")]
public sealed class AspireExportIgnoreAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the reason why this member is excluded from ATS export.
    /// </summary>
    /// <remarks>
    /// Use this property to document why an API cannot be exported to polyglot app hosts,
    /// distinguishing reviewed-but-incompatible members from those not yet reviewed.
    /// </remarks>
    public string? Reason { get; set; }
}
