// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Excludes a property, method, class, or interface from ATS export when the containing type uses
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
/// <para>
/// Apply this attribute to a class to suppress all automatic export coverage checks for the
/// type's members, or to an interface to keep an implementation contract out of generated
/// language bindings when it is intentionally not part of the ATS surface.
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
    AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Property | AttributeTargets.Method,
    Inherited = false,
    AllowMultiple = false)]
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
