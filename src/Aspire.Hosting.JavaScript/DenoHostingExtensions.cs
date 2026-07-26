// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
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
    private const string DenoNodeModulesDirModeNone = "none";
    private const string DenoNodeModulesDirModeAuto = "auto";
    private const string DenoNodeModulesDirModeManual = "manual";

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

        values ??= [];
        var permission = new DenoPermission
        {
            Kind = kind,
            Deny = deny,
            Values = values,
        };

        // Deno delimits permission values with commas and offers no escape syntax, so a single value containing a
        // comma silently becomes several permissions. Verified on Deno 2.9.0: `--allow-read=data,secret` intended as
        // one directory named "data,secret" instead grants `data` and `secret` separately, so the requested path is
        // denied while unrelated paths are granted. Reject it here rather than emit a command line that means
        // something other than what the caller asked for.
        foreach (var value in values)
        {
            if (value is not null && value.Contains(','))
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
    /// Controls the blanket <c>-A</c>/<c>--allow-all</c> grant. Deno runs deny-by-default, so Aspire grants
    /// <c>-A</c> by default to keep parity with the permissive Node/Bun runtimes. Pass <see langword="false"/> to
    /// drop to least-privilege and grant only the explicit permissions configured via the granular
    /// <c>WithDenoAllow*</c> methods.
    /// </summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="enabled">Whether to emit <c>-A</c>/<c>--allow-all</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoAllowAll(this IResourceBuilder<DenoAppResource> builder, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        GetOrAddDenoAnnotation(builder).AllowAll = enabled;
        return builder;
    }

    // ---- Granular permissions ---------------------------------------------------------------

    /// <summary>Grants <c>--allow-net</c>, optionally scoped to the supplied hosts.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="hosts">The host names, IP addresses, or host:port pairs to allow. When empty, all network access is allowed.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoAllowNet(this IResourceBuilder<DenoAppResource> builder, params string[] hosts)
        => AddDenoPermission(builder, DenoPermissionKind.Net, deny: false, hosts);

    /// <summary>Denies <c>--deny-net</c>, optionally scoped to the supplied hosts.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="hosts">The host names, IP addresses, or host:port pairs to deny. When empty, all network access is denied.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoDenyNet(this IResourceBuilder<DenoAppResource> builder, params string[] hosts)
        => AddDenoPermission(builder, DenoPermissionKind.Net, deny: true, hosts);

    /// <summary>Grants <c>--allow-read</c>, optionally scoped to the supplied paths.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="paths">The file system paths to allow. When empty, all read access is allowed.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoAllowRead(this IResourceBuilder<DenoAppResource> builder, params string[] paths)
        => AddDenoPermission(builder, DenoPermissionKind.Read, deny: false, paths);

    /// <summary>Denies <c>--deny-read</c>, optionally scoped to the supplied paths.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="paths">The file system paths to deny. When empty, all read access is denied.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoDenyRead(this IResourceBuilder<DenoAppResource> builder, params string[] paths)
        => AddDenoPermission(builder, DenoPermissionKind.Read, deny: true, paths);

    /// <summary>Grants <c>--allow-write</c>, optionally scoped to the supplied paths.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="paths">The file system paths to allow. When empty, all write access is allowed.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoAllowWrite(this IResourceBuilder<DenoAppResource> builder, params string[] paths)
        => AddDenoPermission(builder, DenoPermissionKind.Write, deny: false, paths);

    /// <summary>Denies <c>--deny-write</c>, optionally scoped to the supplied paths.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="paths">The file system paths to deny. When empty, all write access is denied.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoDenyWrite(this IResourceBuilder<DenoAppResource> builder, params string[] paths)
        => AddDenoPermission(builder, DenoPermissionKind.Write, deny: true, paths);

    /// <summary>Grants <c>--allow-run</c>, optionally scoped to the supplied programs.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="programs">The executable names or paths to allow. When empty, all subprocess execution is allowed.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoAllowRun(this IResourceBuilder<DenoAppResource> builder, params string[] programs)
        => AddDenoPermission(builder, DenoPermissionKind.Run, deny: false, programs);

    /// <summary>Denies <c>--deny-run</c>, optionally scoped to the supplied programs.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="programs">The executable names or paths to deny. When empty, all subprocess execution is denied.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoDenyRun(this IResourceBuilder<DenoAppResource> builder, params string[] programs)
        => AddDenoPermission(builder, DenoPermissionKind.Run, deny: true, programs);

    /// <summary>Grants <c>--allow-env</c>, optionally scoped to the supplied variable names.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="variables">The environment variable names to allow. When empty, all environment access is allowed.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoAllowEnv(this IResourceBuilder<DenoAppResource> builder, params string[] variables)
        => AddDenoPermission(builder, DenoPermissionKind.Env, deny: false, variables);

    /// <summary>Denies <c>--deny-env</c>, optionally scoped to the supplied variable names.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="variables">The environment variable names to deny. When empty, all environment access is denied.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoDenyEnv(this IResourceBuilder<DenoAppResource> builder, params string[] variables)
        => AddDenoPermission(builder, DenoPermissionKind.Env, deny: true, variables);

    /// <summary>Grants <c>--allow-import</c>, optionally scoped to the supplied import hosts.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="hosts">The import hosts to allow. When empty, all remote imports are allowed.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoAllowImport(this IResourceBuilder<DenoAppResource> builder, params string[] hosts)
        => AddDenoPermission(builder, DenoPermissionKind.Import, deny: false, hosts);

    /// <summary>Denies <c>--deny-import</c>, optionally scoped to the supplied import hosts.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="hosts">The import hosts to deny. When empty, all remote imports are denied.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoDenyImport(this IResourceBuilder<DenoAppResource> builder, params string[] hosts)
        => AddDenoPermission(builder, DenoPermissionKind.Import, deny: true, hosts);

    /// <summary>Grants <c>--allow-sys</c>, optionally scoped to the supplied APIs.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="apis">The system information APIs to allow. When empty, all system information access is allowed.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoAllowSys(this IResourceBuilder<DenoAppResource> builder, params string[] apis)
        => AddDenoPermission(builder, DenoPermissionKind.Sys, deny: false, apis);

    /// <summary>Denies <c>--deny-sys</c>, optionally scoped to the supplied APIs.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="apis">The system information APIs to deny. When empty, all system information access is denied.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoDenySys(this IResourceBuilder<DenoAppResource> builder, params string[] apis)
        => AddDenoPermission(builder, DenoPermissionKind.Sys, deny: true, apis);

    /// <summary>Grants <c>--allow-ffi</c>, optionally scoped to the supplied libraries.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="libraries">The native libraries to allow. When empty, all FFI access is allowed.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoAllowFfi(this IResourceBuilder<DenoAppResource> builder, params string[] libraries)
        => AddDenoPermission(builder, DenoPermissionKind.Ffi, deny: false, libraries);

    /// <summary>Denies <c>--deny-ffi</c>, optionally scoped to the supplied libraries.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="libraries">The native libraries to deny. When empty, all FFI access is denied.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoDenyFfi(this IResourceBuilder<DenoAppResource> builder, params string[] libraries)
        => AddDenoPermission(builder, DenoPermissionKind.Ffi, deny: true, libraries);

    // ---- Config / resolution flags ----------------------------------------------------------

    /// <summary>Sets <c>--config &lt;file&gt;</c> (path to a <c>deno.json</c>/<c>deno.jsonc</c>).</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="configFile">The Deno configuration file path.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
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
    public static IResourceBuilder<DenoAppResource> WithDenoNoLock(this IResourceBuilder<DenoAppResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var annotation = GetOrAddDenoAnnotation(builder);
        annotation.NoLock = true;
        annotation.Lock = null;
        return builder;
    }

    /// <summary>
    /// Sets <c>--node-modules-dir</c>, optionally with a mode (<c>none</c>|<c>auto</c>|<c>manual</c>) emitted as
    /// <c>--node-modules-dir=&lt;mode&gt;</c>.
    /// </summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="mode">The node_modules mode. When <see langword="null"/> or empty, emits <c>--node-modules-dir</c> without a value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="mode"/> is not <see langword="null"/>, empty, <c>none</c>, <c>auto</c>, or <c>manual</c>.</exception>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// The generated Deno Dockerfile publisher does not support <c>manual</c> mode because it excludes local
    /// <c>node_modules</c> from the build context. Use <c>auto</c> or provide a custom Dockerfile for that mode.
    /// </remarks>
    /// <ats-remarks />
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoNodeModulesDir(this IResourceBuilder<DenoAppResource> builder, string? mode = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var normalizedMode = ValidateDenoNodeModulesDirMode(mode);
        var annotation = GetOrAddDenoAnnotation(builder);
        annotation.NodeModulesDirSet = true;
        annotation.NodeModulesDirMode = normalizedMode;
        return builder;
    }

    private static string? ValidateDenoNodeModulesDirMode(string? mode)
    {
        if (string.IsNullOrEmpty(mode))
        {
            return null;
        }

        if (mode is DenoNodeModulesDirModeNone or DenoNodeModulesDirModeAuto or DenoNodeModulesDirModeManual)
        {
            return mode;
        }

        throw new ArgumentException("The node_modules mode must be 'none', 'auto', or 'manual'.", nameof(mode));
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

    /// <summary>Enables <c>--inspect</c>, optionally at <paramref name="hostPort"/> (for example <c>127.0.0.1:9229</c>).</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="hostPort">The optional inspector host:port value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoInspect(this IResourceBuilder<DenoAppResource> builder, string? hostPort = null)
        => SetDenoInspect(builder, DenoInspectMode.Inspect, hostPort);

    /// <summary>Enables <c>--inspect-brk</c> (break on first statement), optionally at <paramref name="hostPort"/>.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="hostPort">The optional inspector host:port value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoInspectBrk(this IResourceBuilder<DenoAppResource> builder, string? hostPort = null)
        => SetDenoInspect(builder, DenoInspectMode.InspectBrk, hostPort);

    /// <summary>Enables <c>--inspect-wait</c> (wait for a debugger before running), optionally at <paramref name="hostPort"/>.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <param name="hostPort">The optional inspector host:port value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoInspectWait(this IResourceBuilder<DenoAppResource> builder, string? hostPort = null)
        => SetDenoInspect(builder, DenoInspectMode.InspectWait, hostPort);

    private static IResourceBuilder<DenoAppResource> SetDenoInspect(IResourceBuilder<DenoAppResource> builder, DenoInspectMode mode, string? hostPort)
    {
        ArgumentNullException.ThrowIfNull(builder);
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
    public static IResourceBuilder<DenoAppResource> WithDenoRun(this IResourceBuilder<DenoAppResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var annotation = GetOrAddDenoAnnotation(builder);
        annotation.Mode = DenoCommandMode.Run;
        annotation.ModeSet = true;
        annotation.TaskName = null;
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
    public static IResourceBuilder<DenoAppResource> WithDenoTask(this IResourceBuilder<DenoAppResource> builder, string taskName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(taskName);
        var annotation = GetOrAddDenoAnnotation(builder);
        annotation.Mode = DenoCommandMode.Task;
        annotation.ModeSet = true;
        annotation.TaskName = taskName;
        return builder;
    }

    /// <summary>Selects the <c>deno serve &lt;entrypoint&gt;</c> mode for serving an HTTP entrypoint.</summary>
    /// <param name="builder">The Deno app resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoServe(this IResourceBuilder<DenoAppResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var annotation = GetOrAddDenoAnnotation(builder);
        annotation.Mode = DenoCommandMode.Serve;
        annotation.ModeSet = true;
        builder.WithHttpEndpoint(env: "PORT");
        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithEndpoint("http", e => e.TargetPort ??= GetNextDenoServeDefaultPort(builder), createIfNotExists: false);
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
        JavaScriptRunScriptAnnotation? runScript = null,
        JavaScriptPackageManagerAnnotation? packageManager = null)
    {
        var args = new List<object>();
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

        AppendPermissionFlags(args, deno);
        AppendResolutionFlags(args, deno);
        if (includeCachedOnly)
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

    private static void AppendPermissionFlags(List<object> args, DenoCommandLineAnnotation deno)
    {
        var hasGranularAllow = deno.Permissions.Any(p => !p.Deny);
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
            yield return string.IsNullOrEmpty(deno.NodeModulesDirMode)
                ? "--node-modules-dir"
                : $"--node-modules-dir={deno.NodeModulesDirMode}";
        }
    }

    private static void AppendUnstableFlags(List<object> args, DenoCommandLineAnnotation deno)
    {
        foreach (var flag in deno.UnstableFlags)
        {
            args.Add(flag);
        }
    }

    private static void AppendWatchFlags(List<object> args, DenoCommandLineAnnotation deno)
    {
        if (deno.WatchHmr)
        {
            args.Add("--watch-hmr");
        }

        if (deno.Watch)
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
            entrypoint.Add("-A");
            entrypoint.Add("--cached-only");
            entrypoint.Add(containerScriptPath);
        }

        NormalizeDenoContainerPathArguments(entrypoint);
        return [.. entrypoint];
    }

    private static void ThrowIfUnsupportedDenoDockerfileOptions(IResource resource)
    {
        if (resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager) &&
            !string.Equals(packageManager.ExecutableName, "deno", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Generated Deno Dockerfiles do not support alternate package manager '{packageManager.ExecutableName}'. Use WithDeno() or provide a custom Dockerfile.");
        }

        if (resource.TryGetLastAnnotation<DenoCommandLineAnnotation>(out var deno) &&
            deno.NodeModulesDirSet &&
            string.Equals(deno.NodeModulesDirMode, "manual", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("WithDenoNodeModulesDir(\"manual\") is not supported by generated Deno Dockerfiles because node_modules is excluded from the build context. Use \"auto\" or provide a custom Dockerfile.");
        }

        if (deno is not null)
        {
            // The Docker build context is the app directory, so a path that is absolute or escapes the app
            // directory is never copied into the image and would break both `deno cache` and the entrypoint.
            ThrowIfPathEscapesDenoBuildContext(deno.ConfigFile, nameof(WithDenoConfig));
            ThrowIfPathEscapesDenoBuildContext(deno.ImportMap, nameof(WithDenoImportMap));
            ThrowIfPathEscapesDenoBuildContext(deno.Lock, nameof(WithDenoLock));
        }
    }

    private static void ThrowIfPathEscapesDenoBuildContext(string? path, string methodName)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var containerPath = ToDenoContainerPath(path);
        if (Path.IsPathRooted(path) ||
            IsWindowsFullyQualifiedPath(path) ||
            containerPath == ".." ||
            containerPath.StartsWith("../", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The path '{path}' configured with {methodName} is outside the Deno application directory, so it is not part of the generated Dockerfile's build context. Move the file inside the application directory or provide a custom Dockerfile.");
        }
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
    /// Converts a host-relative path to the POSIX form used inside the generated Linux container stages.
    /// </summary>
    /// <remarks>
    /// AppHost-configured paths use the host separator, so on Windows a nested entrypoint is configured as
    /// <c>src\main.ts</c>. Emitting that verbatim into <c>deno cache</c> or <c>ENTRYPOINT</c> makes Linux treat
    /// the whole string as a single file name and the container fails to start.
    /// </remarks>
    private static string ToDenoContainerPath(string path) => path.Replace('\\', '/');

    // Deno options that Aspire emits as a separate flag/value pair where the value is a path that must be
    // rewritten to its container form.
    private static readonly string[] s_denoContainerPathFlags = ["--config", "--import-map", "--lock"];

    private static void NormalizeDenoContainerPathArguments(List<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (Array.IndexOf(s_denoContainerPathFlags, args[index]) >= 0)
            {
                args[index + 1] = ToDenoContainerPath(args[index + 1]);
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

            args.AddRange(GetResolutionFlags(deno, includeImportMap: deno.Mode != DenoCommandMode.Task));
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
        return string.Join(' ', args.Select(QuoteDockerShellArgument));
    }

    private static string QuoteDockerShellArgument(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        if (value.All(IsUnquotedDockerShellArgumentCharacter))
        {
            return value;
        }

        // Dockerfile RUN uses /bin/sh -c. Single-quote each argument and use the standard
        // POSIX shell escape sequence for embedded quotes:
        //   import map's.json -> 'import map'"'"'s.json'
        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    private static bool IsUnquotedDockerShellArgumentCharacter(char c) =>
        c is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '-'
            or '_'
            or '.'
            or '/'
            or ':'
            or '=';

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
            var runCommand = $"{executableName} {scriptCommand} {scriptName} {runScriptArguments}";
            return ["sh", "-c", $"exec {runCommand}"];
        }

        List<string> entrypoint = [executableName, scriptCommand, scriptName];
        entrypoint.AddRange(TokenizeDenoRunScriptArguments(runScriptArguments));
        return [.. entrypoint];
    }

    // Exec form performs no shell interpretation, so anything that depends on the shell - variable
    // expansion, command substitution, globbing, redirection, or operators - must keep the `sh -c` form.
    private static bool RequiresShellForDenoRunScriptArguments(string? runScriptArguments) =>
        runScriptArguments is not null && runScriptArguments.AsSpan().IndexOfAny(s_denoShellMetacharacters) >= 0;

    private static readonly SearchValues<char> s_denoShellMetacharacters =
        SearchValues.Create("$`|&;<>*?~()");

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
                index++;
                current.Append(runScriptArguments[index]);
                hasToken = true;
                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static bool ShouldUseFrozenLock(DenoCommandLineAnnotation deno, string workingDirectory)
    {
        if (deno.NoLock)
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

    private static int GetNextDenoServeDefaultPort(IResourceBuilder<DenoAppResource> builder)
    {
        var usedPorts = new HashSet<int>();
        foreach (var resource in builder.ApplicationBuilder.Resources)
        {
            if (!resource.TryGetEndpoints(out var endpoints))
            {
                continue;
            }

            foreach (var endpoint in endpoints)
            {
                if (endpoint.TargetPort is int targetPort)
                {
                    usedPorts.Add(targetPort);
                }

                if (endpoint.Port is int port)
                {
                    usedPorts.Add(port);
                }
            }
        }

        for (var port = DenoServeDefaultPort; ; port++)
        {
            if (!usedPorts.Contains(port))
            {
                return port;
            }
        }
    }

    private sealed record DenoServeEndpointArguments(string Host, object Port);
}
