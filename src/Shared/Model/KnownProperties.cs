// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

/// <summary>
/// Models some known property names for known types of resources.
/// </summary>
/// <remarks>
/// Used as keys in the "properties" dictionary on resource snapshots and view models.
/// Should be compared using <c>StringComparers.ResourcePropertyName</c>.
/// </remarks>
internal static class KnownProperties
{
    public static class Resource
    {
        public const string Uid = "resource.uid";
        public const string Name = "resource.name";
        public const string Type = "resource.type";
        public const string DisplayName = "resource.displayName";
        public const string State = "resource.state";
        public const string ExitCode = "resource.exitCode";
        public const string CreateTime = "resource.createTime";
        public const string StartTime = "resource.startTime";
        public const string StopTime = "resource.stopTime";
        public const string Source = "resource.source";
        public const string HealthState = "resource.healthState";
        public const string ConnectionString = "resource.connectionString";
        public const string ParentName = "resource.parentName";
        public const string AppArgs = "resource.appArgs";
        public const string AppArgsSensitivity = "resource.appArgsSensitivity";
        public const string ExcludeFromMcp = "resource.excludeFromMcp";
    }

    public static class Container
    {
        public const string Id = "container.id";
        public const string Image = "container.image";
        public const string Ports = "container.ports";
        public const string Command = "container.command";
        public const string Args = "container.args";
        public const string Lifetime = "container.lifetime";
    }

    public static class Executable
    {
        public const string Path = "executable.path";
        public const string Pid = "executable.pid";
        public const string WorkDir = "executable.workDir";
        public const string Args = "executable.args";
    }

    public static class Project
    {
        public const string Path = "project.path";
        public const string LaunchProfile = "project.launchProfile";
    }

    public static class Parameter
    {
        public const string Value = "Value";
    }

    public static class Tool
    {
        public const string Package = "tool.package";
        public const string Version = "tool.version";
        public const string ExecArgs = "tool.execArgs";
    }
}
