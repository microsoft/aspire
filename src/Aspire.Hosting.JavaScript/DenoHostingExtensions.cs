// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
        var annotation = GetOrAddDenoAnnotation(builder);
        annotation.Permissions.Add(new DenoPermission
        {
            Kind = kind,
            Deny = deny,
            Values = values ?? [],
        });
        return builder;
    }

    // ---- Blanket permission -----------------------------------------------------------------

    /// <summary>
    /// Controls the blanket <c>-A</c>/<c>--allow-all</c> grant. Deno runs deny-by-default, so Aspire grants
    /// <c>-A</c> by default to keep parity with the permissive Node/Bun runtimes. Pass <see langword="false"/> to
    /// drop to least-privilege and grant only the explicit permissions configured via the granular
    /// <c>WithDenoAllow*</c> methods.
    /// </summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoAllowAll(this IResourceBuilder<DenoAppResource> builder, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        GetOrAddDenoAnnotation(builder).AllowAll = enabled;
        return builder;
    }

    // ---- Granular permissions ---------------------------------------------------------------

    /// <summary>Grants <c>--allow-net</c>, optionally scoped to the supplied hosts (<c>--allow-net=host1,host2</c>).</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoAllowNet(this IResourceBuilder<DenoAppResource> builder, params string[] hosts)
        => AddDenoPermission(builder, DenoPermissionKind.Net, deny: false, hosts);

    /// <summary>Denies <c>--deny-net</c>, optionally scoped to the supplied hosts.</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoDenyNet(this IResourceBuilder<DenoAppResource> builder, params string[] hosts)
        => AddDenoPermission(builder, DenoPermissionKind.Net, deny: true, hosts);

    /// <summary>Grants <c>--allow-read</c>, optionally scoped to the supplied paths.</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoAllowRead(this IResourceBuilder<DenoAppResource> builder, params string[] paths)
        => AddDenoPermission(builder, DenoPermissionKind.Read, deny: false, paths);

    /// <summary>Denies <c>--deny-read</c>, optionally scoped to the supplied paths.</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoDenyRead(this IResourceBuilder<DenoAppResource> builder, params string[] paths)
        => AddDenoPermission(builder, DenoPermissionKind.Read, deny: true, paths);

    /// <summary>Grants <c>--allow-write</c>, optionally scoped to the supplied paths.</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoAllowWrite(this IResourceBuilder<DenoAppResource> builder, params string[] paths)
        => AddDenoPermission(builder, DenoPermissionKind.Write, deny: false, paths);

    /// <summary>Denies <c>--deny-write</c>, optionally scoped to the supplied paths.</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoDenyWrite(this IResourceBuilder<DenoAppResource> builder, params string[] paths)
        => AddDenoPermission(builder, DenoPermissionKind.Write, deny: true, paths);

    /// <summary>Grants <c>--allow-run</c>, optionally scoped to the supplied programs.</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoAllowRun(this IResourceBuilder<DenoAppResource> builder, params string[] programs)
        => AddDenoPermission(builder, DenoPermissionKind.Run, deny: false, programs);

    /// <summary>Denies <c>--deny-run</c>, optionally scoped to the supplied programs.</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoDenyRun(this IResourceBuilder<DenoAppResource> builder, params string[] programs)
        => AddDenoPermission(builder, DenoPermissionKind.Run, deny: true, programs);

    /// <summary>Grants <c>--allow-env</c>, optionally scoped to the supplied variable names.</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoAllowEnv(this IResourceBuilder<DenoAppResource> builder, params string[] variables)
        => AddDenoPermission(builder, DenoPermissionKind.Env, deny: false, variables);

    /// <summary>Denies <c>--deny-env</c>, optionally scoped to the supplied variable names.</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoDenyEnv(this IResourceBuilder<DenoAppResource> builder, params string[] variables)
        => AddDenoPermission(builder, DenoPermissionKind.Env, deny: true, variables);

    /// <summary>Grants <c>--allow-sys</c>, optionally scoped to the supplied APIs.</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoAllowSys(this IResourceBuilder<DenoAppResource> builder, params string[] apis)
        => AddDenoPermission(builder, DenoPermissionKind.Sys, deny: false, apis);

    /// <summary>Denies <c>--deny-sys</c>, optionally scoped to the supplied APIs.</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoDenySys(this IResourceBuilder<DenoAppResource> builder, params string[] apis)
        => AddDenoPermission(builder, DenoPermissionKind.Sys, deny: true, apis);

    /// <summary>Grants <c>--allow-ffi</c>, optionally scoped to the supplied libraries.</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoAllowFfi(this IResourceBuilder<DenoAppResource> builder, params string[] libraries)
        => AddDenoPermission(builder, DenoPermissionKind.Ffi, deny: false, libraries);

    /// <summary>Denies <c>--deny-ffi</c>, optionally scoped to the supplied libraries.</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoDenyFfi(this IResourceBuilder<DenoAppResource> builder, params string[] libraries)
        => AddDenoPermission(builder, DenoPermissionKind.Ffi, deny: true, libraries);

    // ---- Config / resolution flags ----------------------------------------------------------

    /// <summary>Sets <c>--config &lt;file&gt;</c> (path to a <c>deno.json</c>/<c>deno.jsonc</c>).</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoConfig(this IResourceBuilder<DenoAppResource> builder, string configFile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(configFile);
        GetOrAddDenoAnnotation(builder).ConfigFile = configFile;
        return builder;
    }

    /// <summary>Sets <c>--import-map &lt;file&gt;</c>.</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoImportMap(this IResourceBuilder<DenoAppResource> builder, string importMapFile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(importMapFile);
        GetOrAddDenoAnnotation(builder).ImportMap = importMapFile;
        return builder;
    }

    /// <summary>Sets <c>--lock &lt;file&gt;</c>.</summary>
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
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoNodeModulesDir(this IResourceBuilder<DenoAppResource> builder, string? mode = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var annotation = GetOrAddDenoAnnotation(builder);
        annotation.NodeModulesDirSet = true;
        annotation.NodeModulesDirMode = string.IsNullOrEmpty(mode) ? null : mode;
        return builder;
    }

    // ---- Unstable flags ---------------------------------------------------------------------

    /// <summary>
    /// Adds one or more <c>--unstable-*</c> flags. Each feature may be supplied bare (for example <c>"kv"</c>,
    /// <c>"worker-options"</c>, <c>"sloppy-imports"</c>) or fully qualified (<c>"--unstable-kv"</c>).
    /// </summary>
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

            annotation.UnstableFlags.Add(feature.StartsWith("--", StringComparison.Ordinal) ? feature : $"--unstable-{feature}");
        }

        return builder;
    }

    // ---- Watch / inspect --------------------------------------------------------------------

    /// <summary>Enables <c>--watch</c> (or <c>--watch-hmr</c> when <paramref name="hmr"/> is <see langword="true"/>).</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoWatch(this IResourceBuilder<DenoAppResource> builder, bool hmr = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var annotation = GetOrAddDenoAnnotation(builder);
        if (hmr)
        {
            annotation.WatchHmr = true;
        }
        else
        {
            annotation.Watch = true;
        }

        return builder;
    }

    /// <summary>Enables <c>--inspect</c>, optionally at <paramref name="hostPort"/> (for example <c>127.0.0.1:9229</c>).</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoInspect(this IResourceBuilder<DenoAppResource> builder, string? hostPort = null)
        => SetDenoInspect(builder, DenoInspectMode.Inspect, hostPort);

    /// <summary>Enables <c>--inspect-brk</c> (break on first statement), optionally at <paramref name="hostPort"/>.</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoInspectBrk(this IResourceBuilder<DenoAppResource> builder, string? hostPort = null)
        => SetDenoInspect(builder, DenoInspectMode.InspectBrk, hostPort);

    /// <summary>Enables <c>--inspect-wait</c> (wait for a debugger before running), optionally at <paramref name="hostPort"/>.</summary>
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
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoRun(this IResourceBuilder<DenoAppResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var annotation = GetOrAddDenoAnnotation(builder);
        annotation.Mode = DenoCommandMode.Run;
        annotation.TaskName = null;
        return builder;
    }

    /// <summary>
    /// Selects the <c>deno task &lt;taskName&gt;</c> mode, running a task defined in <c>deno.json</c> instead of a
    /// script entrypoint. Permissions are defined by the task itself and are not emitted for this mode.
    /// </summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoTask(this IResourceBuilder<DenoAppResource> builder, string taskName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(taskName);
        var annotation = GetOrAddDenoAnnotation(builder);
        annotation.Mode = DenoCommandMode.Task;
        annotation.TaskName = taskName;
        return builder;
    }

    /// <summary>Selects the <c>deno serve &lt;entrypoint&gt;</c> mode for serving an HTTP entrypoint.</summary>
    [AspireExport]
    public static IResourceBuilder<DenoAppResource> WithDenoServe(this IResourceBuilder<DenoAppResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        GetOrAddDenoAnnotation(builder).Mode = DenoCommandMode.Serve;
        return builder;
    }

    // ---- Script / raw args ------------------------------------------------------------------

    /// <summary>
    /// Appends arguments passed to the script AFTER the entrypoint. Deno forwards everything after the entrypoint
    /// to the running program.
    /// </summary>
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
    private static List<string> BuildDenoArgs(DenoCommandLineAnnotation deno, string scriptPath)
    {
        var args = new List<string>();

        switch (deno.Mode)
        {
            case DenoCommandMode.Task:
                args.Add("task");
                // Task-level permissions live in deno.json, so only resolution/unstable flags and the raw escape
                // hatch are valid here.
                AppendResolutionFlags(args, deno);
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
                args.Add("run");
                break;
        }

        AppendPermissionFlags(args, deno);
        AppendResolutionFlags(args, deno);
        AppendUnstableFlags(args, deno);
        AppendWatchFlags(args, deno);
        AppendInspectFlags(args, deno);
        args.AddRange(deno.RuntimeArgs);

        args.Add(scriptPath);
        args.AddRange(deno.ScriptArgs);
        return args;
    }

    private static void AppendPermissionFlags(List<string> args, DenoCommandLineAnnotation deno)
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

    private static void AppendResolutionFlags(List<string> args, DenoCommandLineAnnotation deno)
    {
        if (!string.IsNullOrEmpty(deno.ConfigFile))
        {
            args.Add("--config");
            args.Add(deno.ConfigFile);
        }

        if (!string.IsNullOrEmpty(deno.ImportMap))
        {
            args.Add("--import-map");
            args.Add(deno.ImportMap);
        }

        if (deno.NoLock)
        {
            args.Add("--no-lock");
        }
        else if (!string.IsNullOrEmpty(deno.Lock))
        {
            args.Add("--lock");
            args.Add(deno.Lock);
        }

        if (deno.NodeModulesDirSet)
        {
            args.Add(string.IsNullOrEmpty(deno.NodeModulesDirMode)
                ? "--node-modules-dir"
                : $"--node-modules-dir={deno.NodeModulesDirMode}");
        }
    }

    private static void AppendUnstableFlags(List<string> args, DenoCommandLineAnnotation deno)
    {
        foreach (var flag in deno.UnstableFlags)
        {
            args.Add(flag);
        }
    }

    private static void AppendWatchFlags(List<string> args, DenoCommandLineAnnotation deno)
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

    private static void AppendInspectFlags(List<string> args, DenoCommandLineAnnotation deno)
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
    /// Builds the container entrypoint array (<c>deno</c> plus args). Honors an explicit command-line annotation
    /// so the published image matches the run-mode command; falls back to <c>deno run -A &lt;script&gt;</c>.
    /// </summary>
    private static string[] BuildDenoEntrypoint(IResource resource, string command, string scriptPath)
    {
        var entrypoint = new List<string> { command };
        if (resource.TryGetLastAnnotation<DenoCommandLineAnnotation>(out var deno))
        {
            entrypoint.AddRange(BuildDenoArgs(deno, scriptPath));
        }
        else
        {
            entrypoint.Add("run");
            entrypoint.Add("-A");
            entrypoint.Add(scriptPath);
        }

        return [.. entrypoint];
    }
}
