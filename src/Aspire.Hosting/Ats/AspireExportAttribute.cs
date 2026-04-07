// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting;

/// <summary>
/// Marks a method, type, or assembly-level type as an ATS (Aspire Type System) export.
/// </summary>
/// <remarks>
/// <para>
/// This attribute serves multiple purposes:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>Capability exports (on methods):</b> Marks a static method as an ATS capability.
/// The capability ID is automatically derived as <c>{AssemblyName}/{camelCaseMethodName}</c>.
/// For example: <c>AddRedis</c> in Aspire.Hosting.Redis becomes <c>Aspire.Hosting.Redis/addRedis</c>.
/// Specify an explicit <c>id</c> only when disambiguation is needed (e.g., multiple overloads).
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Type exports (on types):</b> Marks a type as an ATS-exported type.
/// The type ID is automatically derived as <c>{AssemblyName}/{TypeName}</c>.
/// For example: <c>RedisResource</c> in Aspire.Hosting.Redis becomes <c>Aspire.Hosting.Redis/RedisResource</c>.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Context types (on types with ExposeProperties):</b> When <see cref="ExposeProperties"/> is true,
/// the type's properties are automatically exposed as get/set capabilities for use in callbacks.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>External type exports (assembly-level):</b> For types you don't own, use assembly-level
/// with <see cref="Type"/> property to include them in the ATS type system.
/// </description>
/// </item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // Capability export on a method - capability ID is auto-derived as Aspire.Hosting.Redis/addRedis
/// [AspireExport(Description = "Adds a Redis resource")]
/// public static IResourceBuilder&lt;RedisResource&gt; AddRedis(...) { }
///
/// // Capability export with explicit ID for disambiguation (e.g., multiple overloads)
/// [AspireExport("addRedisWithPort", Description = "Adds a Redis resource with a specific port")]
/// public static IResourceBuilder&lt;RedisResource&gt; AddRedis(..., int port) { }
///
/// // Type export - type ID derived as {AssemblyName}/{TypeName}
/// [AspireExport]
/// public class RedisResource : ContainerResource { }
/// // Type ID: Aspire.Hosting.Redis/RedisResource
///
/// // Context type with properties exposed as capabilities
/// [AspireExport(ExposeProperties = true)]
/// public class EnvironmentCallbackContext
/// {
///     public Dictionary&lt;string, object&gt; EnvironmentVariables { get; }
///     // Getter: Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.environmentVariables
///     // Setter: Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.setEnvironmentVariables
/// }
///
/// // Member-level opt-in with ignore for specific members
/// [AspireExport(ExposeProperties = true)]
/// public class SomeContext
/// {
///     public string Name { get; }  // Exposed
///     [AspireExportIgnore]
///     public ILogger Logger { get; }  // Not exposed
/// }
///
/// // Assembly-level export for types you don't own
/// [assembly: AspireExport(typeof(IConfiguration))]
/// // Type ID: Microsoft.Extensions.Configuration.Abstractions/IConfiguration
/// </code>
/// </example>
[AttributeUsage(
    AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Assembly | AttributeTargets.Property,
    Inherited = false,
    AllowMultiple = true)]
[Experimental("ASPIREATS001")]
public sealed class AspireExportAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance for a capability export (on methods) with an explicit capability ID.
    /// </summary>
    /// <param name="id">
    /// The capability name for this method. The full capability ID is computed
    /// as <c>{AssemblyName}/{id}</c>.
    /// Use this overload only when disambiguation is needed (e.g., multiple overloads of the same method).
    /// When not specified, the capability ID is automatically derived from the method name using camelCase.
    /// </param>
    public AspireExportAttribute(string id)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
    }

    /// <summary>
    /// Initializes a new instance for a type or method export.
    /// </summary>
    /// <remarks>
    /// For type exports, the type ID is automatically derived as <c>{AssemblyName}/{TypeName}</c>.
    /// Set <see cref="ExposeProperties"/> to true for context types whose properties
    /// should be exposed as get/set capabilities.
    /// For method exports, the capability ID is automatically derived from the method name using camelCase.
    /// </remarks>
    public AspireExportAttribute()
    {
    }

    /// <summary>
    /// Initializes a new instance for an assembly-level type export.
    /// </summary>
    /// <param name="type">The CLR type to export to ATS.</param>
    /// <remarks>
    /// Use this constructor at assembly level for types you don't own.
    /// The type ID is derived as <c>{type.Assembly.Name}/{type.Name}</c>.
    /// </remarks>
    /// <example>
    /// <code>
    /// [assembly: AspireExport(typeof(IConfiguration))]
    /// // Type ID: Microsoft.Extensions.Configuration.Abstractions/IConfiguration
    /// </code>
    /// </example>
    public AspireExportAttribute(Type type)
    {
        Type = type ?? throw new ArgumentNullException(nameof(type));
    }

    /// <summary>
    /// Gets the method name for capability exports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The full capability ID is computed as <c>{AssemblyName}/{Id}</c>.
    /// </para>
    /// <para>
    /// This is null for type exports.
    /// </para>
    /// </remarks>
    public string? Id { get; }

    /// <summary>
    /// Gets or sets the CLR type for assembly-level type exports.
    /// </summary>
    /// <remarks>
    /// Use this at assembly level to export types you don't own to ATS.
    /// </remarks>
    public Type? Type { get; set; }

    /// <summary>
    /// Gets or sets a description of what this export does.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the method name to use in generated polyglot SDKs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When not specified, the method name from <see cref="Id"/> is used directly.
    /// </para>
    /// <para>
    /// Use this property to override the generated name when disambiguation is needed
    /// (e.g., to avoid collisions with another integration's method of the same name).
    /// Each language generator will apply its own formatting convention
    /// (camelCase for TypeScript, snake_case for Python, etc.).
    /// </para>
    /// </remarks>
    public string? MethodName { get; set; }

    /// <summary>
    /// Gets or sets whether to expose properties of this type as ATS capabilities.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When true, the type's public instance properties that return ATS-compatible types
    /// are automatically exposed as get/set capabilities (unless marked with <see cref="AspireExportIgnoreAttribute"/>).
    /// </para>
    /// <para>
    /// Use this for context types passed to callbacks (like <c>EnvironmentCallbackContext</c>)
    /// that provide access to runtime state.
    /// </para>
    /// <para>
    /// Property capabilities are named as:
    /// <list type="bullet">
    /// <item><description><c>{Package}/{TypeName}.{propertyName}</c> for getters (camelCase property name)</description></item>
    /// <item><description><c>{Package}/{TypeName}.set{PropertyName}</c> for setters (writable properties only)</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public bool ExposeProperties { get; set; }

    /// <summary>
    /// Gets or sets whether to expose public instance methods of this type as ATS capabilities.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When true, the type's public instance methods are automatically exposed as capabilities
    /// (unless marked with <see cref="AspireExportIgnoreAttribute"/>).
    /// </para>
    /// <para>
    /// Method capabilities are named as <c>{Package}/{TypeName}.{methodName}</c> (camelCase method name).
    /// The first parameter of the capability will be a handle to the instance.
    /// </para>
    /// </remarks>
    public bool ExposeMethods { get; set; }

    /// <summary>
    /// Gets or sets whether synchronous exported methods should be invoked on a background thread by the ATS dispatcher.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set this to <see langword="true"/> for synchronous exports that may invoke synchronous callback delegates which in turn
    /// re-enter the remote host through ATS. Running the export on a background thread allows the JSON-RPC request loop to
    /// continue processing nested callback and capability invocations while the synchronous method waits for the callback to
    /// complete.
    /// </para>
    /// <para>
    /// This setting only affects synchronous exported methods. Async exports that already return <see cref="Task"/> or
    /// <see cref="Task{TResult}"/> are awaited normally and do not use this option.
    /// </para>
    /// <para>
    /// When applied to a type, this setting also applies to exported members discovered from that type unless an individual
    /// member overrides it.
    /// </para>
    /// </remarks>
    public bool RunSyncOnBackgroundThread { get; set; }
}
