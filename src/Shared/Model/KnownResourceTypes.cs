// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

// Resource type strings like "Project", "Tool", "Container" collide with XML
// element names, table column headers, and other unrelated literals. Scope to
// hosting code where these strings actually represent Aspire resource types.
[InternalKnownConstants(Namespaces = new[] { "Aspire.Hosting" })]
internal static class KnownResourceTypes
{
    public const string Executable = "Executable";
    public const string ContainerExec = "ContainerExec";
    public const string Project = "Project";
    public const string Tool = "Tool";
    public const string Container = "Container";
    public const string Parameter = "Parameter";
    public const string ConnectionString = "ConnectionString";
    public const string ExternalService = "ExternalService";
}
