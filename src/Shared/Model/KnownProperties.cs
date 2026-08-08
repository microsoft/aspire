// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

/// <summary>
/// Models some known property names for known types of resources.
/// </summary>
/// <remarks>
/// Used as keys in the "properties" dictionary on resource snapshots and view models.
/// Should be compared using <see cref="StringComparers.ResourcePropertyName"/>.
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
        public const string WaitingFor = "resource.waitingFor";

        /// <summary>
        /// The launch configuration type identifier a resource would be launched with under a debug session,
        /// for example <c>python</c>, <c>go</c> or <c>project</c>. Taken from the resource's
        /// <c>SupportsDebuggingAnnotation</c>, so it is present for every resource that opted into debugging
        /// through <c>WithDebugSupport</c> regardless of whether a debug session is currently active; the
        /// absence of the property is the signal that the resource has no debug support at all.
        /// </summary>
        /// <remarks>
        /// IDEs use this to decide which debug adapter a resource needs without re-deriving the language from
        /// AppHost source, which is why the value is published even when the resource ends up running as a
        /// plain process because the IDE could not honor the launch configuration type.
        /// </remarks>
        public const string LaunchConfigurationType = "resource.launchConfigurationType";
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

    public static class Terminal
    {
        public const string Enabled = "terminal.enabled";
        public const string ReplicaIndex = "terminal.replicaIndex";
        public const string ReplicaCount = "terminal.replicaCount";
        public const string ConsumerUdsPath = "terminal.consumerUdsPath";
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

/// <summary>
/// Defines dashboard sort order values for known resource properties.
/// </summary>
internal static class KnownResourcePropertySortOrder
{
    public const int DisplayName = 0;
    public const int State = 1;
    public const int HealthState = 2;
    public const int StartTime = 3;
    public const int StopTime = 4;
    public const int ExitCode = 5;
    public const int ConnectionString = 6;

    // Producers use local sort orders for their own resource-specific properties. The
    // dashboard normalizes those values after the generic dashboard-owned properties.
    // This value should always be greater than the largest known resource property.
    public static int GetProducerDefinedStart() => ConnectionString + 1;
}
