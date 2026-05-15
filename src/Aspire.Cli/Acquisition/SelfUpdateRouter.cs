// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Decides how <c>aspire update --self</c> should behave for an
/// <see cref="InstallSource"/>. The CLI can update itself in-process only
/// for installs it owns end-to-end (script). Every other route is owned by
/// a package manager or by a separate install path and would be corrupted
/// by an in-process binary swap, so we delegate by printing an
/// installer-appropriate command.
/// </summary>
internal enum SelfUpdateAction
{
    /// <summary>
    /// Perform the existing in-process self-update flow
    /// (<c>CliDownloader</c>-driven download + binary swap).
    /// </summary>
    InProcess,

    /// <summary>
    /// Refuse to update in-process and instead print a route-appropriate
    /// command via <see cref="IUpgradeInstructionProvider"/>. Returns
    /// exit code 0 to match the existing dotnet-tool refusal contract;
    /// callers that need to detect whether an update actually happened
    /// should compare the binary version before and after the run rather
    /// than relying on the exit code.
    /// </summary>
    Delegate,
}

/// <summary>
/// Pure policy lookup that maps <see cref="InstallSource"/> to the action
/// <c>aspire update --self</c> must take.
/// </summary>
internal static class SelfUpdateRouter
{
    /// <summary>
    /// Returns the action <c>aspire update --self</c> should perform for
    /// the supplied <paramref name="source"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="InstallSource.Script"/> stays in-process — it's the route
    /// the CLI owns end-to-end. Unknown routes are refused with a hint to
    /// investigate the install or pass <c>--force</c>. The pre-PR-#16817
    /// legacy script-install case is now covered by the <c>--force</c>
    /// escape hatch.
    /// </remarks>
    public static SelfUpdateAction GetAction(InstallSource source) => source switch
    {
        InstallSource.Script => SelfUpdateAction.InProcess,
        _ => SelfUpdateAction.Delegate,
    };
}
