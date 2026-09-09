// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDENO001 // Deno APIs use the experimental Deno resource and enums internally

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;

namespace Aspire.Hosting;

/// <summary>
/// Fluent flag-surface extensions for <see cref="DenoAppResource"/>.
/// </summary>
/// <remarks>
/// These methods let a caller express the full Deno CLI flag surface (permissions, resolution flags, unstable
/// features, watch/inspect, sub-command modes, and script args) directly on <c>AddDenoApp</c>, so a Deno workload
/// no longer has to fall back to a raw <c>AddExecutable("name", "deno", ...)</c>. All methods mutate a single
/// <see cref="DenoCommandLineAnnotation"/>; flags compose regardless of call order and are emitted in valid Deno
/// CLI order: <c>deno &lt;mode&gt; [runtime-flags] &lt;entrypoint&gt; [script-args]</c>.
/// </remarks>
public static partial class JavaScriptHostingExtensions
{
    private const int DenoServeDefaultPort = 8000;

    private static DenoCommandLineAnnotation GetOrAddDenoAnnotation(IResourceBuilder<DenoAppResource> builder)
    {
        if (!builder.Resource.TryGetLastAnnotation<DenoCommandLineAnnotation>(out var annotation))
        {
            annotation = new DenoCommandLineAnnotation();
            builder.WithAnnotation(annotation);
        }

        return annotation;
    }

    private static IResourceBuilder<DenoAppResource> AddDenoPermission(
        IResourceBuilder<DenoAppResource> builder,
        DenoPermissionKind kind,
        bool deny,
        string[] values)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The permission kind must be a defined DenoPermissionKind value.");
        }

        // The caller owns the params array and can keep mutating it after this call. Permissions are only read
        // when the command line is materialized (publish, or resource start), so holding the caller's array by
        // reference would let a later mutation silently rewrite the launch arguments. Snapshot it, matching the
        // copy semantics WithDenoScriptArgs and WithDenoRuntimeArgs already get from AddRange.
        string[] snapshot = values is null ? [] : [.. values];
        var permission = new DenoPermission
        {
            Kind = kind,
            Deny = deny,
            Values = snapshot,
        };

        // Deno delimits permission values with commas and offers no escape syntax, so a single value containing a
        // comma silently becomes several permissions. Verified on Deno 2.9.0: `--allow-read=data,secret` intended as
        // one directory named "data,secret" instead grants `data` and `secret` separately, so the requested path is
        // denied while unrelated paths are granted. Reject it here rather than emit a command line that means
        // something other than what the caller asked for.
        //
        // An empty params array intentionally emits an unscoped flag, but an individual null or empty value emits
        // `--allow-read=` (or the equivalent permission) and Deno 2.9 rejects it. Do not trim values: Deno accepts
        // whitespace as a permission value.
        foreach (var value in snapshot)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException("Deno permission values cannot be null or empty.", nameof(values));
            }

            if (value.Contains(','))
            {
                var flag = permission.Deny ? $"--deny-{permission.Name}" : $"--allow-{permission.Name}";
                throw new ArgumentException($"The value '{value}' cannot contain a comma. Deno separates {flag} values with commas and provides no way to escape them, so this value would be interpreted as multiple permissions. Pass each value as a separate argument.", nameof(values));
            }
        }

        var annotation = GetOrAddDenoAnnotation(builder);
        annotation.Permissions.Add(permission);
        return builder;
    }

    // ---- Blanket permission -----------------------------------------------------------------

    /// <summary>
    /// Controls the blanket <c>-A</c>/<c>--allow-all</c> grant.
    /// </summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="enabled">
    /// Whether to emit <c>-A</c>/<c>--allow-all</c>. Pass <see langword="false"/> to grant only permissions
    /// configured with <see cref="WithDenoAllow"/>.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// Without explicit permission configuration, local run mode emits <c>-A</c> for parity with Node and Bun,
    /// while generated containers default direct <c>run</c>/<c>serve</c> entrypoints to
    /// <c>--allow-net --allow-env</c>. Calling this method with <see langword="true"/> explicitly emits <c>-A</c>
    /// in both modes.
    /// </remarks>
    [AspireExport]
    [Experimental("ASPIREDENO001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<DenoAppResource> WithDenoAllowAll(this IResourceBuilder<DenoAppResource> builder, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        GetOrAddDenoAnnotation(builder).AllowAll = enabled;
        return builder;
    }

    // ---- Granular permissions ---------------------------------------------------------------

    /// <summary>Grants a Deno permission, optionally scoped to the supplied values.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="kind">The permission to grant.</param>
    /// <param name="values">Optional values that scope the permission. When empty, all access of the selected kind is allowed.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="values"/> contains a null or empty value, or a value containing a comma.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="kind"/> is not a defined <see cref="DenoPermissionKind"/> value.</exception>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREDENO001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<DenoAppResource> WithDenoAllow(this IResourceBuilder<DenoAppResource> builder, DenoPermissionKind kind, params string[] values)
        => AddDenoPermission(builder, kind, deny: false, values);

    /// <summary>Denies a Deno permission, optionally scoped to the supplied values.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="kind">The permission to deny.</param>
    /// <param name="values">Optional values that scope the permission. When empty, all access of the selected kind is denied.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="values"/> contains a null or empty value, or a value containing a comma.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="kind"/> is not a defined <see cref="DenoPermissionKind"/> value.</exception>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREDENO001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<DenoAppResource> WithDenoDeny(this IResourceBuilder<DenoAppResource> builder, DenoPermissionKind kind, params string[] values)
        => AddDenoPermission(builder, kind, deny: true, values);

    // ---- Config / resolution flags ----------------------------------------------------------

    /// <summary>Sets <c>--config &lt;file&gt;</c> (path to a <c>deno.json</c>/<c>deno.jsonc</c>).</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="configFile">The Deno configuration file path.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREDENO001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<DenoAppResource> WithDenoConfig(this IResourceBuilder<DenoAppResource> builder, string configFile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(configFile);
        GetOrAddDenoAnnotation(builder).ConfigFile = configFile;
        return builder;
    }

    /// <summary>Sets <c>--import-map &lt;file&gt;</c>.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="importMapFile">The import map file path.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREDENO001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<DenoAppResource> WithDenoImportMap(this IResourceBuilder<DenoAppResource> builder, string importMapFile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(importMapFile);
        GetOrAddDenoAnnotation(builder).ImportMap = importMapFile;
        return builder;
    }

    /// <summary>Sets <c>--lock &lt;file&gt;</c>.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="lockFile">The lockfile path.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREDENO001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<DenoAppResource> WithDenoLock(this IResourceBuilder<DenoAppResource> builder, string lockFile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(lockFile);
        var annotation = GetOrAddDenoAnnotation(builder);
        annotation.Lock = lockFile;
        annotation.NoLock = false;
        return builder;
    }

    /// <summary>Sets <c>--no-lock</c>, disabling lockfile use.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREDENO001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<DenoAppResource> WithDenoNoLock(this IResourceBuilder<DenoAppResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var annotation = GetOrAddDenoAnnotation(builder);
        annotation.NoLock = true;
        annotation.Lock = null;
        return builder;
    }

    /// <summary>
    /// Sets <c>--node-modules-dir</c>, optionally with a mode emitted as
    /// <c>--node-modules-dir=&lt;mode&gt;</c>.
    /// </summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="mode">The node_modules mode. When <see langword="null"/>, emits <c>--node-modules-dir</c> without a value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="mode"/> is not a defined <see cref="DenoNodeModulesDirMode"/> value.</exception>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// The generated Deno Dockerfile publisher does not support <c>manual</c> mode because it excludes local
    /// <c>node_modules</c> from the build context. Use <c>auto</c> or provide a custom Dockerfile for that mode.
    /// </remarks>
    [AspireExport]
    [Experimental("ASPIREDENO001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<DenoAppResource> WithDenoNodeModulesDir(this IResourceBuilder<DenoAppResource> builder, DenoNodeModulesDirMode? mode = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (mode is not null && !Enum.IsDefined(mode.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The node_modules mode must be a defined DenoNodeModulesDirMode value.");
        }

        var annotation = GetOrAddDenoAnnotation(builder);
        annotation.NodeModulesDirSet = true;
        annotation.NodeModulesDirMode = mode;
        return builder;
    }

    // ---- Unstable flags ---------------------------------------------------------------------

    /// <summary>
    /// Adds one or more <c>--unstable-*</c> flags. Each feature may be supplied bare (for example <c>"kv"</c>,
    /// <c>"worker-options"</c>, <c>"sloppy-imports"</c>) or fully qualified (<c>"--unstable-kv"</c>).
    /// </summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="features">The unstable feature names or fully-qualified <c>--unstable-*</c> flags to emit.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREDENO001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<DenoAppResource> WithDenoUnstable(this IResourceBuilder<DenoAppResource> builder, params string[] features)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var annotation = GetOrAddDenoAnnotation(builder);
        foreach (var feature in features ?? [])
        {
            if (string.IsNullOrEmpty(feature))
            {
                continue;
            }

            if (feature.StartsWith("--", StringComparison.Ordinal) &&
                !feature.StartsWith("--unstable-", StringComparison.Ordinal))
            {
                throw new ArgumentException("Qualified Deno unstable flags must start with \"--unstable-\".", nameof(features));
            }

            annotation.UnstableFlags.Add(feature.StartsWith("--unstable-", StringComparison.Ordinal) ? feature : $"--unstable-{feature}");
        }

        return builder;
    }

    // ---- Watch / inspect --------------------------------------------------------------------

    /// <summary>Enables <c>--watch</c> (or <c>--watch-hmr</c> when <paramref name="hmr"/> is <see langword="true"/>).</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="hmr">Whether to emit <c>--watch-hmr</c> instead of <c>--watch</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREDENO001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<DenoAppResource> WithDenoWatch(this IResourceBuilder<DenoAppResource> builder, bool hmr = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var annotation = GetOrAddDenoAnnotation(builder);
        if (hmr)
        {
            annotation.WatchHmr = true;
            annotation.Watch = false;
        }
        else
        {
            annotation.Watch = true;
            annotation.WatchHmr = false;
        }

        return builder;
    }

    /// <summary>Enables a Deno inspector mode, optionally at <paramref name="hostPort"/> (for example <c>127.0.0.1:9229</c>).</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="mode">The inspector mode to enable.</param>
    /// <param name="hostPort">The optional inspector host:port value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="mode"/> is not a defined <see cref="DenoInspectMode"/> value.</exception>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREDENO001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<DenoAppResource> WithDenoInspect(
        this IResourceBuilder<DenoAppResource> builder,
        DenoInspectMode mode = DenoInspectMode.Inspect,
        string? hostPort = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The inspect mode must be a defined DenoInspectMode value.");
        }

        var annotation = GetOrAddDenoAnnotation(builder);
        annotation.Inspect = mode;
        annotation.InspectHostPort = string.IsNullOrEmpty(hostPort) ? null : hostPort;
        return builder;
    }

    // ---- Modes ------------------------------------------------------------------------------

    /// <summary>Selects the <c>deno run &lt;entrypoint&gt;</c> mode (the default).</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREDENO001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<DenoAppResource> WithDenoRun(this IResourceBuilder<DenoAppResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var annotation = GetOrAddDenoAnnotation(builder);
        annotation.Mode = DenoCommandMode.Run;
        annotation.ModeSet = true;
        annotation.TaskName = null;
        RemoveDenoServeEndpoint(builder, annotation);
        return builder;
    }

    /// <summary>
    /// Selects the <c>deno task &lt;taskName&gt;</c> mode, running a task defined in <c>deno.json</c> instead of a
    /// script entrypoint. Permissions are defined by the task itself and are not emitted for this mode.
    /// </summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="taskName">The name of the task in <c>deno.json</c> to run.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREDENO001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<DenoAppResource> WithDenoTask(this IResourceBuilder<DenoAppResource> builder, string taskName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(taskName);
        var annotation = GetOrAddDenoAnnotation(builder);
        annotation.Mode = DenoCommandMode.Task;
        annotation.ModeSet = true;
        annotation.TaskName = taskName;
        RemoveDenoServeEndpoint(builder, annotation);
        return builder;
    }

    /// <summary>
    /// Restores the endpoint state changed by <see cref="WithDenoServe(IResourceBuilder{DenoAppResource})"/> when
    /// a later mode selector wins, so only <c>deno serve</c> publishes an HTTP binding or injects <c>PORT</c>.
    /// </summary>
    /// <remarks>
    /// An endpoint created by <c>WithDenoServe</c> is removed. A same-name endpoint supplied by the caller is
    /// retained and its prior environment-variable and target-port configuration is restored. The exact
    /// environment callback added by <c>WithDenoServe</c> is also removed; leaving that callback behind would wait
    /// forever for allocation of an endpoint that no longer exists.
    /// </remarks>
    private static void RemoveDenoServeEndpoint(IResourceBuilder<DenoAppResource> builder, DenoCommandLineAnnotation annotation)
    {
        if (annotation.ServeEndpoint is { } endpoint)
        {
            if (annotation.ServeEnvironmentCallback is { } environmentCallback)
            {
                builder.Resource.Annotations.Remove(environmentCallback);
            }

            if (annotation.ServeEndpointCreated)
            {
                builder.Resource.Annotations.Remove(endpoint);
            }
            else if (annotation.ServeAssignedTargetPort is { } assignedTargetPort &&
                endpoint.TargetPort == assignedTargetPort)
            {
                endpoint.TargetPort = null;
            }

            annotation.ServeEndpoint = null;
            annotation.ServeEndpointCreated = false;
            annotation.ServeEnvironmentCallback = null;
            annotation.ServeAssignedTargetPort = null;
        }
    }

    /// <summary>Selects the <c>deno serve &lt;entrypoint&gt;</c> mode for serving an HTTP entrypoint.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREDENO001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<DenoAppResource> WithDenoServe(this IResourceBuilder<DenoAppResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var annotation = GetOrAddDenoAnnotation(builder);
        annotation.Mode = DenoCommandMode.Serve;
        annotation.ModeSet = true;

        if (annotation.ServeEndpoint is not null)
        {
            return builder;
        }

        var existingEndpoint = builder.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .FirstOrDefault(e => string.Equals(e.Name, "http", StringComparison.OrdinalIgnoreCase));

        if (existingEndpoint is null)
        {
            builder.WithHttpEndpoint();
        }

        var serveEndpoint = builder.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .First(e => string.Equals(e.Name, "http", StringComparison.OrdinalIgnoreCase));
        var endpointReference = new EndpointReference(
            builder.Resource,
            serveEndpoint,
            KnownNetworkIdentifiers.LocalhostNetwork);
        var environmentCallback = new EnvironmentCallbackAnnotation(context =>
        {
            context.EnvironmentVariables["PORT"] = endpointReference.Property(EndpointProperty.TargetPort);
        });
        builder.WithAnnotation(environmentCallback);

        annotation.ServeEndpoint = serveEndpoint;
        annotation.ServeEndpointCreated = existingEndpoint is null;
        annotation.ServeEnvironmentCallback = environmentCallback;

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithEndpoint("http", e =>
            {
                if (e.TargetPort is null)
                {
                    // Target ports are private to each process/container. Reusing Deno's conventional
                    // port is deterministic and leaves host-port uniqueness to Aspire's allocator.
                    annotation.ServeAssignedTargetPort = DenoServeDefaultPort;
                    e.TargetPort = annotation.ServeAssignedTargetPort;
                }
            }, createIfNotExists: false);
        }

        return builder;
    }

    // ---- Script / raw args ------------------------------------------------------------------

    /// <summary>
    /// Appends arguments passed to the script AFTER the entrypoint. Deno forwards everything after the entrypoint
    /// to the running program.
    /// </summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="args">The script arguments to append after the entrypoint or task name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREDENO001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<DenoAppResource> WithDenoScriptArgs(this IResourceBuilder<DenoAppResource> builder, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var annotation = GetOrAddDenoAnnotation(builder);
        annotation.ScriptArgs.AddRange(args ?? []);
        return builder;
    }

    /// <summary>
    /// Appends raw runtime arguments injected verbatim BEFORE the entrypoint. This is the escape hatch that gives
    /// full parity with <c>AddExecutable("name", "deno", workdir, args...)</c> for any flag not covered by a
    /// dedicated <c>WithDeno*</c> method.
    /// </summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="args">The runtime arguments to append before the entrypoint or task name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREDENO001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<DenoAppResource> WithDenoRuntimeArgs(this IResourceBuilder<DenoAppResource> builder, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var annotation = GetOrAddDenoAnnotation(builder);
        annotation.RuntimeArgs.AddRange(args ?? []);
        return builder;
    }

    // ---- Arg builder ------------------------------------------------------------------------

    /// <summary>
    /// Builds the ordered Deno argument list (excluding the <c>deno</c> executable itself) from a command-line
    /// annotation. Runtime flags precede the entrypoint; script args follow it, matching valid Deno CLI order.
    /// </summary>
    private static List<object> BuildDenoArgs(
        DenoCommandLineAnnotation deno,
        string scriptPath,
        DenoServeEndpointArguments? serveEndpointArguments = null,
        bool includeDevelopmentFlags = true,
        bool includeCachedOnly = false,
        bool usePublishDefaultPermissions = false,
        JavaScriptRunScriptAnnotation? runScript = null,
        JavaScriptPackageManagerAnnotation? packageManager = null)
    {
        var args = new List<object>();

        // Task mode resolves flags from deno.json and never emits permissions, import map, or the
        // development-only watch/inspect flags, so the conflict surface differs from run/serve.
        var isTaskMode = deno.Mode == DenoCommandMode.Task ||
            (deno.Mode != DenoCommandMode.Serve && runScript is not null && packageManager?.ScriptCommand == "task" && !deno.ModeSet);

        ThrowIfRuntimeArgsConflictWithManagedFlags(
            deno,
            emitsServeEndpoint: deno.Mode == DenoCommandMode.Serve && serveEndpointArguments is not null,
            includeImportMap: !isTaskMode,
            includeDevelopmentFlags: includeDevelopmentFlags && !isTaskMode);

        switch (deno.Mode)
        {
            case DenoCommandMode.Task:
                args.Add("task");
                // Task-level permissions live in deno.json. Deno 2.5.6 also rejects `deno task --import-map ...`,
                // while still accepting config and dependency-management flags such as --lock and --node-modules-dir.
                AppendTaskResolutionFlags(args, deno);
                AppendUnstableFlags(args, deno);
                args.AddRange(deno.RuntimeArgs);
                args.Add(deno.TaskName ?? scriptPath);
                args.AddRange(deno.ScriptArgs);
                return args;

            case DenoCommandMode.Serve:
                args.Add("serve");
                break;

            case DenoCommandMode.Run:
            default:
                if (runScript is not null &&
                    packageManager?.ScriptCommand == "task" &&
                    !deno.ModeSet)
                {
                    args.Add("task");
                    AppendTaskResolutionFlags(args, deno);
                    AppendUnstableFlags(args, deno);
                    args.AddRange(deno.RuntimeArgs);
                    args.Add(runScript.ScriptName);
                    args.AddRange(runScript.Args);
                    args.AddRange(deno.ScriptArgs);
                    return args;
                }

                args.Add("run");
                break;
        }

        AppendPermissionFlags(args, deno, usePublishDefaultPermissions);
        AppendResolutionFlags(args, deno);
        if (includeCachedOnly && !RuntimeArgsSelectCachePolicy(deno.RuntimeArgs))
        {
            args.Add("--cached-only");
        }

        AppendUnstableFlags(args, deno);
        if (includeDevelopmentFlags)
        {
            AppendWatchFlags(args, deno);
            AppendInspectFlags(args, deno);
        }

        if (deno.Mode == DenoCommandMode.Serve && serveEndpointArguments is not null)
        {
            args.Add("--host");
            args.Add(serveEndpointArguments.Host);
            args.Add("--port");
            args.Add(serveEndpointArguments.Port);
        }

        args.AddRange(deno.RuntimeArgs);

        args.Add(scriptPath);
        args.AddRange(deno.ScriptArgs);
        return args;
    }

    private static void AppendPermissionFlags(List<object> args, DenoCommandLineAnnotation deno, bool usePublishDefaultPermissions)
    {
        var hasGranularAllow = deno.Permissions.Any(p => !p.Deny);

        // Keep local execution permissive for parity with Node/Bun, but default published images to only
        // network and environment access. Deny-only configuration narrows that publish-safe baseline; it must
        // not switch the baseline to -A and broaden access merely because a deny flag was added.
        if (usePublishDefaultPermissions && deno.AllowAll is null && !hasGranularAllow)
        {
            args.Add("--allow-net");
            args.Add("--allow-env");

            foreach (var permission in OrderPermissions(deno.Permissions).Where(p => p.Deny))
            {
                args.Add(FormatPermission(permission));
            }

            return;
        }

        // Default (AllowAll == null): grant -A only when the caller has not opted into any granular allow flag.
        var emitAllowAll = deno.AllowAll ?? !hasGranularAllow;
        if (emitAllowAll)
        {
            args.Add("-A");
            // -A subsumes granular allows; only deny flags meaningfully narrow it.
            foreach (var permission in OrderPermissions(deno.Permissions).Where(p => p.Deny))
            {
                args.Add(FormatPermission(permission));
            }

            return;
        }

        foreach (var permission in OrderPermissions(deno.Permissions))
        {
            args.Add(FormatPermission(permission));
        }
    }

    // Deterministic, valid-CLI ordering independent of fluent call order: by permission category, allow before deny.
    private static IEnumerable<DenoPermission> OrderPermissions(IEnumerable<DenoPermission> permissions)
        => permissions.OrderBy(p => (int)p.Kind).ThenBy(p => p.Deny ? 1 : 0);

    private static string FormatPermission(DenoPermission permission)
    {
        var prefix = permission.Deny ? "--deny-" : "--allow-";
        return permission.Values.Count == 0
            ? $"{prefix}{permission.Name}"
            : $"{prefix}{permission.Name}={string.Join(",", permission.Values)}";
    }

    private static void AppendResolutionFlags(List<object> args, DenoCommandLineAnnotation deno)
    {
        args.AddRange(GetResolutionFlags(deno));
    }

    private static void AppendTaskResolutionFlags(List<object> args, DenoCommandLineAnnotation deno)
    {
        args.AddRange(GetResolutionFlags(deno, includeImportMap: false));
    }

    private static IEnumerable<string> GetResolutionFlags(DenoCommandLineAnnotation deno)
        => GetResolutionFlags(deno, includeImportMap: true);

    private static IEnumerable<string> GetResolutionFlags(DenoCommandLineAnnotation deno, bool includeImportMap)
    {
        if (!string.IsNullOrEmpty(deno.ConfigFile))
        {
            yield return "--config";
            yield return deno.ConfigFile;
        }

        if (includeImportMap && !string.IsNullOrEmpty(deno.ImportMap))
        {
            yield return "--import-map";
            yield return deno.ImportMap;
        }

        if (deno.NoLock)
        {
            yield return "--no-lock";
        }
        else if (!string.IsNullOrEmpty(deno.Lock))
        {
            yield return "--lock";
            yield return deno.Lock;
        }

        if (deno.NodeModulesDirSet)
        {
            yield return deno.NodeModulesDirMode is not { } mode
                ? "--node-modules-dir"
                : $"--node-modules-dir={GetDenoNodeModulesDirModeValue(mode)}";
        }
    }

    private static string GetDenoNodeModulesDirModeValue(DenoNodeModulesDirMode mode) => mode switch
    {
        DenoNodeModulesDirMode.None => "none",
        DenoNodeModulesDirMode.Auto => "auto",
        DenoNodeModulesDirMode.Manual => "manual",
        _ => throw new InvalidOperationException($"Unsupported Deno node_modules mode '{mode}'."),
    };

    private static void AppendUnstableFlags(List<object> args, DenoCommandLineAnnotation deno)
    {
        foreach (var flag in deno.UnstableFlags)
        {
            args.Add(flag);
        }
    }

    /// <summary>
    /// Rejects <see cref="WithDenoRuntimeArgs(IResourceBuilder{DenoAppResource}, string[])"/> entries that
    /// collide with a flag Aspire already emits for this resource.
    /// </summary>
    /// <remarks>
    /// Verified against Deno 2.9.0: single-occurrence options fail with
    /// <c>error: the argument '--config &lt;FILE&gt;' cannot be used multiple times</c>, and mutually exclusive
    /// pairs (<c>--config</c> with <c>--no-config</c>, <c>--no-lock</c> with <c>--lock</c>,
    /// <c>--watch</c> with <c>--watch-hmr</c>) fail with <c>cannot be used with</c>. Both are clap errors that
    /// never mention Aspire, so the resource simply fails to start with nothing pointing at the knob that caused it.
    /// <para>
    /// Repeatable options are deliberately absent from this check. Deno merges <c>--allow-read=/tmp</c> with
    /// <c>--allow-read=/var</c> and accepts <c>-A</c> alongside <c>--allow-all</c>, so layering extra grants
    /// over the managed ones is legitimate and must keep working.
    /// </para>
    /// </remarks>
    private static void ThrowIfRuntimeArgsConflictWithManagedFlags(
        DenoCommandLineAnnotation deno,
        bool emitsServeEndpoint,
        bool includeImportMap,
        bool includeDevelopmentFlags)
    {
        foreach (var arg in deno.RuntimeArgs)
        {
            // Both spellings reach Deno's parser: "--port 3000" (separate value) and "--port=3000".
            var name = arg.AsSpan();
            var separator = name.IndexOf('=');
            if (separator >= 0)
            {
                name = name[..separator];
            }

            if (GetManagedDenoFlagConflict(name, deno, emitsServeEndpoint, includeImportMap, includeDevelopmentFlags) is not { } conflict)
            {
                continue;
            }

            throw new InvalidOperationException(
                $"The argument '{arg}' cannot be configured with {nameof(WithDenoRuntimeArgs)} because {conflict.Source} already emits {conflict.ManagedFlag}, and Deno rejects those arguments when they are combined. {conflict.Remedy}");
        }
    }

    private static (string ManagedFlag, string Source, string Remedy)? GetManagedDenoFlagConflict(
        ReadOnlySpan<char> name,
        DenoCommandLineAnnotation deno,
        bool emitsServeEndpoint,
        bool includeImportMap,
        bool includeDevelopmentFlags)
    {
        if (emitsServeEndpoint && (name.Equals("--host", StringComparison.Ordinal) || name.Equals("--port", StringComparison.Ordinal)))
        {
            return ("--host and --port from the resource's endpoint", nameof(WithDenoServe), "Configure the endpoint instead, for example WithHttpEndpoint(port: 5005).");
        }

        // -c is an alias for --config, while --no-config is mutually exclusive with it.
        if (!string.IsNullOrEmpty(deno.ConfigFile) &&
            (name.Equals("--config", StringComparison.Ordinal) ||
             name.Equals("-c", StringComparison.Ordinal) ||
             name.Equals("--no-config", StringComparison.Ordinal)))
        {
            return ("--config", nameof(WithDenoConfig), $"Pass the configuration file to {nameof(WithDenoConfig)} instead.");
        }

        if (includeImportMap && !string.IsNullOrEmpty(deno.ImportMap) && name.Equals("--import-map", StringComparison.Ordinal))
        {
            return ("--import-map", nameof(WithDenoImportMap), $"Pass the import map to {nameof(WithDenoImportMap)} instead.");
        }

        // --no-lock and --lock are mutually exclusive, so either managed spelling conflicts with either raw one.
        if ((deno.NoLock || !string.IsNullOrEmpty(deno.Lock)) &&
            (name.Equals("--lock", StringComparison.Ordinal) || name.Equals("--no-lock", StringComparison.Ordinal)))
        {
            var managedFlag = deno.NoLock ? "--no-lock" : "--lock";
            var source = deno.NoLock ? nameof(WithDenoNoLock) : nameof(WithDenoLock);
            return (managedFlag, source, $"Configure locking with {source} instead.");
        }

        if (deno.NodeModulesDirSet && name.Equals("--node-modules-dir", StringComparison.Ordinal))
        {
            return ("--node-modules-dir", nameof(WithDenoNodeModulesDir), $"Pass the mode to {nameof(WithDenoNodeModulesDir)} instead.");
        }

        if (!includeDevelopmentFlags)
        {
            return null;
        }

        if ((deno.Watch || deno.WatchHmr) &&
            (name.Equals("--watch", StringComparison.Ordinal) || name.Equals("--watch-hmr", StringComparison.Ordinal)))
        {
            var managedFlag = deno.WatchHmr ? "--watch-hmr" : "--watch";
            return (managedFlag, nameof(WithDenoWatch), $"Select the watch mode with {nameof(WithDenoWatch)} instead.");
        }

        if (deno.Inspect is { } inspectMode && name.StartsWith("--inspect", StringComparison.Ordinal))
        {
            var managedFlag = inspectMode switch
            {
                DenoInspectMode.InspectBrk => "--inspect-brk",
                DenoInspectMode.InspectWait => "--inspect-wait",
                _ => "--inspect",
            };

            return (managedFlag, nameof(WithDenoInspect), $"Configure the inspector with {nameof(WithDenoInspect)} instead.");
        }

        return null;
    }

    /// <summary>
    /// Reports whether the caller already selected a module cache policy through
    /// <see cref="WithDenoRuntimeArgs(IResourceBuilder{DenoAppResource}, string[])"/>.
    /// </summary>
    /// <remarks>
    /// <c>--cached-only</c> is an Aspire default (published images pre-populate <c>DENO_DIR</c>, so a cold
    /// network fetch at startup indicates a broken image) rather than a hard requirement. Deno accepts
    /// <c>--cached-only --reload</c> without error but <c>--cached-only</c> silently wins, verified on 2.9.0
    /// against a real <c>jsr:</c> import: a cold cache fails identically with and without <c>--reload</c>.
    /// Emitting both would therefore turn an explicit caller instruction into a no-op, so drop the default
    /// instead of overriding the caller.
    /// </remarks>
    private static bool RuntimeArgsSelectCachePolicy(IEnumerable<string> runtimeArgs)
    {
        foreach (var arg in runtimeArgs)
        {
            var name = arg.AsSpan();
            var separator = name.IndexOf('=');
            if (separator >= 0)
            {
                name = name[..separator];
            }

            if (name.Equals("--reload", StringComparison.Ordinal) ||
                name.Equals("-r", StringComparison.Ordinal) ||
                name.Equals("--cached-only", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void AppendWatchFlags(List<object> args, DenoCommandLineAnnotation deno)
    {
        // Deno rejects "--watch-hmr" combined with "--watch", so these must stay mutually exclusive.
        // WithDenoWatch already clears the other flag; the else-if keeps that invariant local to the emitter.
        if (deno.WatchHmr)
        {
            args.Add("--watch-hmr");
        }
        else if (deno.Watch)
        {
            args.Add("--watch");
        }
    }

    private static void AppendInspectFlags(List<object> args, DenoCommandLineAnnotation deno)
    {
        if (deno.Inspect is not { } mode)
        {
            return;
        }

        var flag = mode switch
        {
            DenoInspectMode.InspectBrk => "--inspect-brk",
            DenoInspectMode.InspectWait => "--inspect-wait",
            _ => "--inspect",
        };

        args.Add(string.IsNullOrEmpty(deno.InspectHostPort) ? flag : $"{flag}={deno.InspectHostPort}");
    }

    /// <summary>
    /// Builds the container entrypoint array (<c>deno</c> plus args). Honors publish-safe command-line flags from
    /// the explicit Deno annotation, excluding development-only watch and inspector flags.
    /// </summary>
    private static string[] BuildDenoEntrypoint(IResource resource, string command, string scriptPath)
    {
        if (resource.TryGetLastAnnotation<JavaScriptPublishModeAnnotation>(out var publishMode) &&
            publishMode.Mode == JavaScriptPublishMode.PackageScript)
        {
            var packageScriptManager = resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageScriptManagerAnnotation)
                ? packageScriptManagerAnnotation
                : throw new InvalidOperationException("PublishAsPackageScript requires a Deno package manager. Add a deno.json file or call WithDeno().");

            return BuildDenoPackageScriptEntrypoint(
                packageScriptManager.ExecutableName,
                packageScriptManager.ScriptCommand ?? "task",
                publishMode.ScriptName!,
                publishMode.RunScriptArguments);
        }

        var entrypoint = new List<string> { command };
        var deno = resource.TryGetLastAnnotation<DenoCommandLineAnnotation>(out var denoAnnotation) ? denoAnnotation : null;
        var runScript = resource.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out var runScriptAnnotation) ? runScriptAnnotation : null;
        var packageManager = resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManagerAnnotation) ? packageManagerAnnotation : null;
        var containerScriptPath = ToDenoContainerPath(scriptPath);

        if (deno is not null)
        {
            var serveEndpointArguments = deno.Mode == DenoCommandMode.Serve
                ? GetDenoServeEndpointArguments(resource, isPublishMode: true, useLiteralTargetPort: true)
                : null;
            entrypoint.AddRange(BuildDenoArgs(
                deno,
                containerScriptPath,
                serveEndpointArguments,
                includeDevelopmentFlags: false,
                includeCachedOnly: deno.Mode != DenoCommandMode.Task,
                usePublishDefaultPermissions: true,
                runScript: runScript,
                packageManager: packageManager).Cast<string>());
        }
        else if (runScript is not null && packageManager?.ScriptCommand == "task")
        {
            entrypoint.Add("task");
            entrypoint.Add(runScript.ScriptName);
            entrypoint.AddRange(runScript.Args);
        }
        else
        {
            entrypoint.Add("run");
            entrypoint.Add("--allow-net");
            entrypoint.Add("--allow-env");
            entrypoint.Add("--cached-only");
            entrypoint.Add(containerScriptPath);
        }

        NormalizeDenoContainerPathArguments(entrypoint);
        return [.. entrypoint];
    }

    private static void ThrowIfUnsupportedDenoDockerfileOptions(IResource resource)
    {
        if (resource.TryGetLastAnnotation<JavaScriptPublishModeAnnotation>(out var publishMode) &&
            publishMode.Mode is JavaScriptPublishMode.StaticWebsite or JavaScriptPublishMode.NodeServer)
        {
            var publishMethod = publishMode.Mode == JavaScriptPublishMode.StaticWebsite
                ? nameof(PublishAsStaticWebsite)
                : nameof(PublishAsNodeServer);
            throw new InvalidOperationException($"Generated Deno Dockerfiles do not support {publishMethod}. Use AddJavaScriptApp(...).WithDeno() or provide a custom Dockerfile.");
        }

        if (resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager) &&
            !string.Equals(packageManager.ExecutableName, "deno", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Generated Deno Dockerfiles do not support alternate package manager '{packageManager.ExecutableName}'. Use WithDeno() or provide a custom Dockerfile.");
        }

        if (resource.TryGetLastAnnotation<DenoCommandLineAnnotation>(out var deno) &&
            deno.NodeModulesDirSet &&
            deno.NodeModulesDirMode == DenoNodeModulesDirMode.Manual)
        {
            throw new InvalidOperationException("The 'manual' node_modules mode is not supported by generated Deno Dockerfiles because node_modules is excluded from the build context. Use the 'auto' mode or provide a custom Dockerfile.");
        }

        if (deno is not null)
        {
            if (deno.RuntimeArgs.Any(argument =>
                argument == "--env-file" ||
                argument.StartsWith("--env-file=", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "Generated Deno Dockerfiles do not support '--env-file' because dotenv files can contain secrets that would be copied into the container image. Use Aspire environment variables or secret parameters, or provide a custom Dockerfile that handles the file securely.");
            }

            // The Docker build context is the app directory, so a path that is absolute or escapes the app
            // directory is never copied into the image and would break both `deno cache` and the entrypoint.
            ThrowIfPathEscapesDenoBuildContext(deno.ConfigFile, nameof(WithDenoConfig));
            ThrowIfPathEscapesDenoBuildContext(deno.ImportMap, nameof(WithDenoImportMap));
            ThrowIfPathEscapesDenoBuildContext(deno.Lock, nameof(WithDenoLock));
        }
    }

    /// <summary>
    /// Rejects a configured path that would resolve outside the generated Dockerfile's build context.
    /// </summary>
    /// <remarks>
    /// Validation uses the same platform-independent normalizer as the generated Dockerfile. Both <c>/</c> and
    /// <c>\</c> are treated as separators so Windows rooted and UNC paths cannot become absolute only after they
    /// are emitted into the Linux container. Traversal is resolved by depth: <c>config/../deno.json</c> stays
    /// inside the context and normalizes to <c>deno.json</c>, while <c>config/../../outside.json</c> escapes it.
    /// </remarks>
    private static void ThrowIfPathEscapesDenoBuildContext(string? path, string methodName)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        if (!TryNormalizeDenoContainerRelativePath(path, out _))
        {
            throw new InvalidOperationException($"The path '{path}' configured with {methodName} is outside the Deno application directory, so it is not part of the generated Dockerfile's build context. Move the file inside the application directory or provide a custom Dockerfile.");
        }
    }

    private static bool TryNormalizeDenoContainerRelativePath(string path, out string normalizedPath)
    {
        var containerPath = path.Replace('\\', '/');
        if (containerPath.StartsWith('/') || IsWindowsDriveQualifiedPath(containerPath))
        {
            normalizedPath = string.Empty;
            return false;
        }

        // Deno accepts remote import maps. They are not build-context paths and must retain the URI's double slash.
        if (Uri.TryCreate(containerPath, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            normalizedPath = containerPath;
            return true;
        }

        var normalizedSegments = new List<string>();
        foreach (var segment in containerPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (normalizedSegments.Count == 0)
                {
                    normalizedPath = string.Empty;
                    return false;
                }

                normalizedSegments.RemoveAt(normalizedSegments.Count - 1);
                continue;
            }

            normalizedSegments.Add(segment);
        }

        normalizedPath = string.Join('/', normalizedSegments);
        return true;
    }

    /// <summary>
    /// Rejects Deno-specific command-line options when a non-Deno package manager is the effective launcher.
    /// The <c>WithDeno*</c> flags produce a Deno argument vector (for example <c>run -A --watch main.ts</c>),
    /// which is meaningless once the command is switched to another package manager such as <c>npm</c>.
    /// </summary>
    private static void ThrowIfDenoOptionsConflictWithPackageManager(IResource resource)
    {
        if (resource.TryGetLastAnnotation<DenoCommandLineAnnotation>(out _) &&
            resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager) &&
            !string.Equals(packageManager.ExecutableName, "deno", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Deno command-line options configured with the WithDeno* methods cannot be combined with package manager '{packageManager.ExecutableName}' on resource '{resource.Name}'. Remove the WithDeno* options or use WithDeno().");
        }
    }

    /// <summary>
    /// Converts a host-relative path to normalized POSIX form for the generated Linux container stages.
    /// </summary>
    /// <remarks>
    /// AppHost-configured paths use the host separator, so on Windows a nested entrypoint is configured as
    /// <c>src\main.ts</c>. Emitting that verbatim into <c>deno cache</c> or <c>ENTRYPOINT</c> makes Linux treat
    /// the whole string as a single file name and the container fails to start.
    /// </remarks>
    private static string ToDenoContainerPath(string path)
        => TryNormalizeDenoContainerRelativePath(path, out var normalizedPath)
            ? normalizedPath
            : path.Replace('\\', '/');

    // Deno options that Aspire emits as a separate flag/value pair where the value is a path that must be
    // rewritten to its container form.
    private static readonly string[] s_denoContainerPathFlags = ["--cert", "--config", "-c", "--import-map", "--lock"];
    private static readonly string[] s_denoContainerPathListFlags =
        ["--allow-read", "--deny-read", "--allow-write", "--deny-write", "--allow-ffi", "--deny-ffi"];

    private static void NormalizeDenoContainerPathArguments(List<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            var separator = argument.IndexOf('=');
            var flag = separator >= 0 ? argument[..separator] : argument;
            if (separator >= 0 && Array.IndexOf(s_denoContainerPathListFlags, flag) >= 0)
            {
                var normalizedValues = argument[(separator + 1)..]
                    .Split(',')
                    .Select(ToDenoContainerPath);
                args[index] = $"{flag}={string.Join(',', normalizedValues)}";
                continue;
            }

            if (Array.IndexOf(s_denoContainerPathFlags, flag) < 0)
            {
                continue;
            }

            if (separator >= 0)
            {
                args[index] = $"{flag}={ToDenoContainerPath(argument[(separator + 1)..])}";
            }
            else if (index + 1 < args.Count)
            {
                args[index + 1] = ToDenoContainerPath(args[index + 1]);
                index++;
            }
        }
    }

    // Raw runtime flags that "deno cache" accepts AND that govern module resolution, so omitting them from the
    // build-time cache step changes what gets downloaded (or whether the download can happen at all).
    //
    // Verified against Deno 2.9.0 by running "deno cache <flag> m.ts": permission, inspector, watch, and
    // --cached-only flags are rejected outright ("error: unexpected argument"), so the forwarding set has to be an
    // allowlist. Forwarding deno.RuntimeArgs wholesale would break "docker build" for the very common "-A".
    //
    // Two behaviors that shape the split below, both verified on 2.9.0:
    //  * --frozen fails with "the argument '--frozen[=<BOOLEAN>]' cannot be used multiple times", so flags that
    //    BuildDenoCacheCommand already emits are never forwarded. Raw duplicates of the managed resolution flags
    //    (--config/--import-map/--lock/--no-lock/--node-modules-dir) are unreachable here because
    //    ThrowIfRuntimeArgsConflictWithManagedFlags rejects them earlier whenever the managed setter was used.
    //  * Despite "--lock [<FILE>]" being documented as an optional value, clap consumes the following token:
    //    "deno cache --lock m.ts" fails with "the following required arguments were not provided: <file>...".
    //    A bare trailing --lock would therefore swallow the entrypoint, so it is only forwarded with a value.
    private static readonly string[] s_denoCacheValueFlags =
        ["--cert", "--conditions", "--config", "-c", "--import-map", "--lock", "--minimum-dependency-age"];

    private static readonly string[] s_denoCacheStandaloneFlags =
        [
            "--no-remote", "--no-npm", "--no-config", "--no-lock", "--vendor", "--allow-import", "-I",
            "--deny-import", "--allow-scripts", "--node-modules-dir", "--node-modules-linker", "--env-file"
        ];

    private static IEnumerable<string> GetCacheCompatibleRuntimeArgs(List<string> runtimeArgs)
    {
        for (var index = 0; index < runtimeArgs.Count; index++)
        {
            var arg = runtimeArgs[index];
            var name = arg.AsSpan();
            var separator = name.IndexOf('=');
            var hasInlineValue = separator >= 0;
            if (hasInlineValue)
            {
                name = name[..separator];
            }

            var nameText = name.ToString();
            if (Array.IndexOf(s_denoCacheStandaloneFlags, nameText) >= 0)
            {
                yield return arg;
                continue;
            }

            if (Array.IndexOf(s_denoCacheValueFlags, nameText) < 0)
            {
                // Anything else is a run-time concern (permissions, inspector, watch) that "deno cache" rejects,
                // or a bare value belonging to such a flag. Either way it must not reach the cache command.
                continue;
            }

            if (hasInlineValue)
            {
                yield return arg;
                continue;
            }

            // Only forward the space-separated spelling when the value is actually present, so a trailing flag
            // cannot consume the entrypoint that BuildDenoCacheCommand appends after these arguments.
            if (index + 1 < runtimeArgs.Count && !runtimeArgs[index + 1].StartsWith('-'))
            {
                yield return arg;
                yield return runtimeArgs[index + 1];
                index++;
            }
        }
    }

    private static string BuildDenoCacheCommand(IResource resource, string scriptPath, string workingDirectory)
    {
        var args = new List<string> { "deno", "cache" };
        var hasRunScript = resource.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out _) &&
            resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager) &&
            packageManager.ScriptCommand == "task";

        if (resource.TryGetLastAnnotation<DenoCommandLineAnnotation>(out var deno))
        {
            var isTaskMode = deno.Mode == DenoCommandMode.Task || (hasRunScript && deno.Mode == DenoCommandMode.Run && !deno.ModeSet);
            if (isTaskMode)
            {
                return "mkdir -p /deno-dir";
            }

            args.AddRange(GetResolutionFlags(deno));
            args.AddRange(GetCacheCompatibleRuntimeArgs(deno.RuntimeArgs));
            args.AddRange(deno.UnstableFlags);
            if (ShouldUseFrozenLock(deno, workingDirectory))
            {
                args.Add("--frozen");
            }
        }
        else if (hasRunScript)
        {
            return "mkdir -p /deno-dir";
        }
        else if (File.Exists(Path.Combine(workingDirectory, "deno.lock")))
        {
            args.Add("--frozen");
        }

        args.Add(ToDenoContainerPath(scriptPath));
        NormalizeDenoContainerPathArguments(args);
        return JoinDockerShellCommand(args);
    }

    /// <summary>
    /// Builds the ENTRYPOINT for a Deno package-script container.
    /// </summary>
    /// <remarks>
    /// Exec form is preferred because Deno runtime images can be shell-less (for example
    /// <c>denoland/deno:2.1-distroless</c>), where a <c>["sh", "-c", ...]</c> entrypoint fails to start.
    /// Arguments that rely on the shell (for example <c>"-- --port $PORT"</c>) cannot be expressed in exec
    /// form, so those keep the shell entrypoint and therefore require a shell-capable runtime image.
    /// </remarks>
    internal static string[] BuildDenoPackageScriptEntrypoint(string executableName, string scriptCommand, string scriptName, string? runScriptArguments)
    {
        if (RequiresShellForDenoRunScriptArguments(runScriptArguments))
        {
            // Only runScriptArguments is meant to be shell-evaluated. The command itself is fixed data
            // (a task name can legitimately contain spaces, e.g. "build prod"), so quote those parts or
            // the shell would word-split them into a different command.
            var runCommand = $"{QuoteDockerShellArgument(executableName)} {QuoteDockerShellArgument(scriptCommand)} {QuoteDockerShellArgument(scriptName)} {runScriptArguments}";
            return ["sh", "-c", $"exec {runCommand}"];
        }

        List<string> entrypoint = [executableName, scriptCommand, scriptName];
        entrypoint.AddRange(TokenizeDenoRunScriptArguments(runScriptArguments));
        return [.. entrypoint];
    }

    // Exec form performs no shell interpretation, so anything that depends on the shell - variable
    // expansion, command substitution, globbing, redirection, or operators - must keep the `sh -c` form.
    //
    // This is deliberately an allowlist of characters the tokenizer reproduces faithfully rather than a
    // denylist of shell metacharacters. A denylist fails open: any character nobody thought to enumerate
    // is silently assumed inert and gets baked into exec form with different semantics. Real cases that a
    // metacharacter denylist missed here: `[ab].ts` (bracket expression), `#1` (comment - the rest of the
    // line is discarded by the shell), `{a,b}.ts` (brace expansion on bash/ash though not dash), and an
    // embedded newline (a command separator, not whitespace).
    private static bool RequiresShellForDenoRunScriptArguments(string? runScriptArguments) =>
        runScriptArguments is not null && !runScriptArguments.All(IsShellInertRunScriptArgumentCharacter);

    // Characters whose meaning to `sh` is identical to their meaning to TokenizeDenoRunScriptArguments.
    // Quoting characters are inert because the tokenizer implements the same POSIX quoting rules the shell
    // does. '!' is inert because history expansion is interactive-only and never applies under `sh -c`.
    private static bool IsShellInertRunScriptArgumentCharacter(char c) =>
        c is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or ' '
            or '\t'
            or '\''
            or '"'
            or '\\'
            or '-'
            or '_'
            or '.'
            or '/'
            or ':'
            or '='
            or '+'
            or ','
            or '@'
            or '%'
            or '^'
            or '!';

    /// <summary>
    /// Splits a free-form run-script argument string into individual argv entries.
    /// </summary>
    /// <remarks>
    /// <c>PublishAsPackageScript(runScriptArguments: ...)</c> takes a single string because it mirrors what a
    /// developer would type in a shell. An exec-form ENTRYPOINT needs a real argument vector, so the string is
    /// tokenized here using POSIX-shell word-splitting rules:
    /// <code>
    /// --port 8080          -> ["--port", "8080"]
    /// --name 'my app'      -> ["--name", "my app"]
    /// --path "/a b"        -> ["--path", "/a b"]
    /// --msg "say \"hi\""   -> ["--msg", "say \"hi\""]
    /// </code>
    /// Inputs that need actual shell behavior never reach this method; see
    /// <see cref="RequiresShellForDenoRunScriptArguments"/>.
    /// </remarks>
    private static List<string> TokenizeDenoRunScriptArguments(string? runScriptArguments)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(runScriptArguments))
        {
            return tokens;
        }

        var current = new StringBuilder();
        var hasToken = false;
        var quote = '\0';

        for (var index = 0; index < runScriptArguments.Length; index++)
        {
            var c = runScriptArguments[index];

            if (quote == '\0' && char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            if (quote == '\0' && c is '\'' or '"')
            {
                quote = c;
                // An empty quoted argument ("" or '') is still an argument.
                hasToken = true;
                continue;
            }

            if (quote != '\0' && c == quote)
            {
                quote = '\0';
                continue;
            }

            // Backslash escapes only apply inside double quotes and outside quotes, matching POSIX shells.
            // Inside single quotes every character is literal.
            if (c == '\\' && quote != '\'' && index + 1 < runScriptArguments.Length)
            {
                var next = runScriptArguments[index + 1];

                // POSIX rules differ by context. Unquoted, a backslash escapes the character that follows it.
                // Inside double quotes it is only an escape before $ ` " \ and <newline>; before anything else
                // the backslash is retained literally, so `--pattern "\d+"` must stay `--pattern \d+` rather
                // than collapsing to `d+`.
                // https://pubs.opengroup.org/onlinepubs/9699919799/utilities/V3_chap02.html#tag_18_02_03
                if (quote == '\0' || next is '$' or '`' or '"' or '\\' or '\n')
                {
                    index++;

                    // A backslash immediately before a newline is a line continuation in both contexts:
                    // both characters are removed rather than producing a literal newline.
                    if (next != '\n')
                    {
                        current.Append(next);
                        hasToken = true;
                    }

                    continue;
                }
            }

            current.Append(c);
            hasToken = true;
        }

        if (quote != '\0')
        {
            // A shell rejects this outright:
            //   $ sh -c "printf '%s' --name 'unterminated"
            //   sh: unexpected EOF while looking for matching `''
            // Silently closing the quote here would publish an exec-form command that differs from what the
            // caller wrote, so fail at build time rather than shipping a container that runs something else.
            var quoteKind = quote == '\'' ? "single" : "double";
            throw new InvalidOperationException(
                $"The Deno run script arguments '{runScriptArguments}' contain an unterminated {quoteKind} quote. Close the quote so the arguments can be parsed the way a shell would parse them.");
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static bool ShouldUseFrozenLock(DenoCommandLineAnnotation deno, string workingDirectory)
    {
        if (deno.NoLock || deno.RuntimeArgs.Contains("--no-lock", StringComparer.Ordinal))
        {
            return false;
        }

        var lockFile = string.IsNullOrEmpty(deno.Lock) ? "deno.lock" : deno.Lock;
        return File.Exists(Path.Combine(workingDirectory, lockFile));
    }

    private static DenoServeEndpointArguments? GetDenoServeEndpointArguments(IResource resource, bool isPublishMode, bool useLiteralTargetPort = false)
    {
        if (resource is not IResourceWithEndpoints endpointsResource)
        {
            return null;
        }

        var endpoint = endpointsResource.GetEndpoint("http");
        if (!endpoint.Exists)
        {
            return null;
        }

        var host = isPublishMode ? "0.0.0.0" : endpoint.EndpointAnnotation.TargetHost;
        object port = useLiteralTargetPort
            ? (endpoint.EndpointAnnotation.TargetPort ?? DenoServeDefaultPort).ToString(CultureInfo.InvariantCulture)
            : endpoint.Property(EndpointProperty.TargetPort);

        return new(host, port);
    }

    private sealed record DenoServeEndpointArguments(string Host, object Port);
}
