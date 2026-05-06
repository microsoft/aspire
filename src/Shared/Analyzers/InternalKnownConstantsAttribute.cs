// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire;

/// <summary>
/// Marks a static class as an opt-in source of named string constants for the repo-internal
/// <c>ASPIREINT001</c> analyzer (<c>Aspire.Internal.Analyzers.UseKnownConstantStringLiteralAnalyzer</c>).
/// </summary>
/// <remarks>
/// <para>
/// By default the analyzer auto-discovers any <c>internal static class</c> whose name starts
/// with <c>Known</c> and contains <c>public const string</c> members. Applying this attribute
/// is only necessary when:
/// </para>
/// <list type="bullet">
/// <item><description>The class name does not start with <c>Known</c> (use the attribute to opt the class in).</description></item>
/// <item><description>The class's constants should only apply within a specific assembly or namespace
/// (use <see cref="Assemblies"/> / <see cref="Namespaces"/> to constrain the scope).</description></item>
/// </list>
/// <para>
/// Resolution rule: when a literal site has both scoped and unscoped candidate matches, the
/// scoped candidates win (most-specific scope first). When multiple unscoped candidates remain
/// across unrelated outer Known classes, the diagnostic is suppressed as ambiguous.
/// </para>
/// <para>
/// This attribute is matched by simple name in the analyzer (no symbol-identity dependency),
/// so it can be defined per-project (linked via <c>&lt;Compile Include&gt;</c>) without forcing
/// a project reference to the analyzer assembly.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class InternalKnownConstantsAttribute : Attribute
{
    /// <summary>
    /// Restrict this class's constants to compilations whose assembly name matches one of the
    /// listed names (case-insensitive). <see langword="null"/> or empty means "any assembly".
    /// </summary>
    public string[]? Assemblies { get; set; }

    /// <summary>
    /// Restrict this class's constants to literals whose containing-type namespace starts with
    /// one of the listed namespace prefixes (a prefix matches when the consumer namespace equals
    /// the prefix or starts with the prefix followed by '.'). <see langword="null"/> or empty
    /// means "any namespace".
    /// </summary>
    public string[]? Namespaces { get; set; }
}
