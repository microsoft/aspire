// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.SelectTests;

/// <summary>
/// Lightweight, single-run breadcrumb so a crash anywhere in the selector can report WHAT it was
/// processing and HOW it got there. The selector moves through coarse <see cref="Stage"/>s (resolve
/// changed files, build the Layer 1 graph, select, write outputs) and, within a stage, marks the
/// concrete <see cref="Item"/> in hand (a changed file, a project node). On an unhandled exception the
/// CLI dumps the last-known stage + item alongside the inputs needed to re-run, instead of leaving only
/// a stack trace. Optionally echoes each step to stderr when <c>SELECTTESTS_TRACE</c> is set, giving a
/// full ordered trail for the cases where the last breadcrumb alone isn't enough.
/// </summary>
/// <remarks>
/// Per-run (not static) so concurrent runs in the same process — the test suite shares one process —
/// never clobber each other's breadcrumbs.
/// </remarks>
internal sealed class SelectionTrace
{
    // SELECTTESTS_TRACE=1 (or any non-empty value) echoes each stage/item to stderr as it happens.
    private readonly bool _verbose;

    public SelectionTrace()
    {
        _verbose = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SELECTTESTS_TRACE"));
    }

    /// <summary>The coarse stage currently executing. Set at each phase boundary in the run.</summary>
    public string Stage { get; private set; } = "(starting)";

    /// <summary>The concrete item being processed within the current stage, when one applies.</summary>
    public string? Item { get; private set; }

    /// <summary>Marks entry into a new stage and clears the per-item marker from the previous stage.</summary>
    public void EnterStage(string stage)
    {
        Stage = stage;
        Item = null;
        if (_verbose)
        {
            Console.Error.WriteLine($"[SelectTests] stage: {stage}");
        }
    }

    /// <summary>Marks the concrete item the current stage is working on (e.g. a changed file or project).</summary>
    public void Processing(string item)
    {
        Item = item;
        if (_verbose)
        {
            Console.Error.WriteLine($"[SelectTests]   processing: {item}");
        }
    }
}
