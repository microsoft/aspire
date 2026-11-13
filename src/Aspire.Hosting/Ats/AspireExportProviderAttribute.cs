// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Marks an assembly-level opt-in attribute whose source generator provides
/// <see cref="AspireExportAttribute"/> coverage.
/// </summary>
/// <remarks>
/// Source generators can apply this attribute to their opt-in attribute type so the Aspire
/// integration analyzer recognizes assemblies whose exports are produced during compilation.
/// The opt-in attribute itself must target assemblies.
/// </remarks>
[System.Diagnostics.CodeAnalysis.Experimental("ASPIREEXPORT018", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class AspireExportProviderAttribute : Attribute;
