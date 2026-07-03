// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// The Deno sub-command a <see cref="DenoAppResource"/> is launched with.
/// </summary>
internal enum DenoCommandMode
{
    /// <summary>Execute a script entrypoint with <c>deno run</c>.</summary>
    Run,

    /// <summary>Execute a named task from <c>deno.json</c> with <c>deno task</c>.</summary>
    Task,

    /// <summary>Serve an HTTP entrypoint with <c>deno serve</c>.</summary>
    Serve,
}

/// <summary>
/// The Deno inspector flavor.
/// </summary>
internal enum DenoInspectMode
{
    /// <summary><c>--inspect</c> — attach a debugger; execution starts immediately.</summary>
    Inspect,

    /// <summary><c>--inspect-brk</c> — attach a debugger and break on the first statement.</summary>
    InspectBrk,

    /// <summary><c>--inspect-wait</c> — wait for a debugger to attach before running any code.</summary>
    InspectWait,
}

/// <summary>
/// A granular Deno permission grant/denial (for example <c>--allow-net</c> or <c>--deny-read</c>).
/// </summary>
internal enum DenoPermissionKind
{
    Net,
    Read,
    Write,
    Run,
    Env,
    Sys,
    Ffi,
}

/// <summary>
/// A single granular Deno permission flag with an optional comma-separated value list.
/// </summary>
internal sealed class DenoPermission
{
    public required DenoPermissionKind Kind { get; init; }

    public required bool Deny { get; init; }

    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>
    /// The flag name segment used to build <c>--allow-{name}</c>/<c>--deny-{name}</c>.
    /// </summary>
    public string Name => Kind switch
    {
        DenoPermissionKind.Net => "net",
        DenoPermissionKind.Read => "read",
        DenoPermissionKind.Write => "write",
        DenoPermissionKind.Run => "run",
        DenoPermissionKind.Env => "env",
        DenoPermissionKind.Sys => "sys",
        DenoPermissionKind.Ffi => "ffi",
        _ => Kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>
/// Captures the full Deno command-line surface for a <see cref="DenoAppResource"/>.
/// </summary>
/// <remarks>
/// When present on a Deno resource this annotation fully controls the emitted argument list, replacing the
/// default <c>deno run -A &lt;script&gt;</c> form. The fluent <c>WithDeno*</c> extension methods mutate a single
/// instance of this annotation so flags compose predictably regardless of call order, and the resulting args are
/// emitted in valid Deno CLI order: <c>deno &lt;mode&gt; [runtime-flags] &lt;entrypoint&gt; [script-args]</c>.
/// </remarks>
internal sealed class DenoCommandLineAnnotation : IResourceAnnotation
{
    /// <summary>The sub-command mode (<c>run</c>, <c>task</c>, or <c>serve</c>). Defaults to <c>run</c>.</summary>
    public DenoCommandMode Mode { get; set; } = DenoCommandMode.Run;

    /// <summary>The task name to invoke when <see cref="Mode"/> is <see cref="DenoCommandMode.Task"/>.</summary>
    public string? TaskName { get; set; }

    /// <summary>
    /// Tri-state <c>-A</c>/<c>--allow-all</c> control. <see langword="null"/> means "default": emit <c>-A</c>
    /// only when no granular allow permission has been configured (preserving backward-compatible behavior).
    /// </summary>
    public bool? AllowAll { get; set; }

    /// <summary>Granular permission grants/denials.</summary>
    public List<DenoPermission> Permissions { get; } = [];

    /// <summary><c>--config &lt;file&gt;</c>.</summary>
    public string? ConfigFile { get; set; }

    /// <summary><c>--import-map &lt;file&gt;</c>.</summary>
    public string? ImportMap { get; set; }

    /// <summary><c>--lock &lt;file&gt;</c>.</summary>
    public string? Lock { get; set; }

    /// <summary><c>--no-lock</c>.</summary>
    public bool NoLock { get; set; }

    /// <summary>Whether <c>--node-modules-dir</c> was requested.</summary>
    public bool NodeModulesDirSet { get; set; }

    /// <summary>Optional mode for <c>--node-modules-dir=&lt;mode&gt;</c> (<c>none</c>|<c>auto</c>|<c>manual</c>).</summary>
    public string? NodeModulesDirMode { get; set; }

    /// <summary>Fully-formed <c>--unstable-*</c> flags.</summary>
    public List<string> UnstableFlags { get; } = [];

    /// <summary><c>--watch</c>.</summary>
    public bool Watch { get; set; }

    /// <summary><c>--watch-hmr</c>.</summary>
    public bool WatchHmr { get; set; }

    /// <summary>The inspector flavor, if any.</summary>
    public DenoInspectMode? Inspect { get; set; }

    /// <summary>Optional <c>host:port</c> for the inspector flag.</summary>
    public string? InspectHostPort { get; set; }

    /// <summary>Raw runtime args injected verbatim BEFORE the entrypoint (escape hatch / AddExecutable parity).</summary>
    public List<string> RuntimeArgs { get; } = [];

    /// <summary>Args passed to the script AFTER the entrypoint.</summary>
    public List<string> ScriptArgs { get; } = [];
}
