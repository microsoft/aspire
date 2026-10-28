// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

internal sealed partial class TrayApplication
{
    private FolderApplication[] _installedApplications = [];

    private void Dispatch(ActionTarget target)
    {
        if (_quitRequested)
        {
            return;
        }
        _dispatchDepth++;
        try
        {
            switch (target.Kind)
            {
                case ActionKind.Dashboard:
                    OpenUrl(controller.GetDashboardUri(target.Id));
                    break;
                case ActionKind.Stop:
                    // Stopping isn't confirmed, as in the dashboard. The controller revalidates the
                    // exact process lifetime, so a stale action never stops a replacement.
                    if (!RequireCurrentRow(target.Id).CanStop)
                    {
                        throw new InvalidOperationException("Stop is no longer available for the selected AppHost.");
                    }
                    controller.RequestStop(controller.RequireLiveInstance(target.Id).Id);
                    break;
                case ActionKind.Start:
                    RequireCurrentRow(target.Id);
                    if (RequireProjectFile(target.Id.AppHostPath))
                    {
                        controller.RequestStart(target.Id.AppHostPath);
                    }
                    break;
                case ActionKind.ClearRecent:
                    // The panel has already confirmed it inline.
                    controller.ClearRecent();
                    break;
                case ActionKind.RemoveRecent:
                    controller.RemoveRecent(target.Id.AppHostPath);
                    break;
                case ActionKind.TogglePin:
                    var row = RequireCurrentRow(target.Id);
                    controller.SetPinned(row.Id.AppHostPath, !row.IsPinned);
                    break;
                case ActionKind.CopyPath:
                    RequireCurrentRow(target.Id);
                    CopyPath(Path.GetDirectoryName(target.Id.AppHostPath)!);
                    break;
                case ActionKind.Explorer:
                case ActionKind.OpenIn:
                    RequireCurrentRow(target.Id);
                    var application = target.Kind == ActionKind.OpenIn
                        ? target.Application ?? throw new InvalidOperationException("The selected application is no longer available.")
                        : null;
                    if (RequireProjectFile(target.Id.AppHostPath))
                    {
                        if (smokeSeconds is not null && !_interactiveSmoke)
                        {
                            throw new InvalidOperationException("External application launch is disabled in smoke mode.");
                        }
                        HidePanel();
                        if (application is null)
                        {
                            FolderApplication.ShowInExplorer(target.Id.AppHostPath);
                        }
                        else
                        {
                            application.Open(Path.GetDirectoryName(target.Id.AppHostPath)!);
                        }
                    }
                    break;
                case ActionKind.Documentation:
                    OpenUrl(new Uri("https://aspire.dev"));
                    break;
                case ActionKind.Quit:
                    RequestQuit();
                    break;
            }
        }
        catch (Exception ex)
        {
            // This boundary covers native commands and their nested modal loops.
            controller.ReportActionError(ex.Message);
            Program.Log($"Tray action failed: {ex.Message}");
            if (smokeSeconds is not null && !_interactiveSmoke)
            {
                if (ErrorForSmoke is null)
                {
                    throw;
                }
                ErrorForSmoke(ex);
            }
            else
            {
                ShowMessage("Aspire", ex.Message, NativeMethods.MbIconError);
            }
        }
        finally
        {
            _dispatchDepth--;
            RequestRefresh();
        }
    }

    private AppHostMenuItem RequireCurrentRow(AppHostId id)
    {
        var state = controller.State;
        return state.AppHosts.Concat(state.RecentAppHosts).SingleOrDefault(host => host.Id == id)
            ?? throw new InvalidOperationException("The selected AppHost is no longer available.");
    }

    /// <summary>
    /// Checks that an AppHost's source file still exists before acting on it. A missing pinned
    /// AppHost is pruned. A missing recent one is offered for removal inline in the panel.
    /// </summary>
    private bool RequireProjectFile(string path)
    {
        if (!TrayAppHostPath.IsMissing(path))
        {
            TrayAppHostPath.RequireExistingFile(path);
            return true;
        }
        if (controller.State.AppHosts.Any(host => host.IsPinned && !host.IsRunning && TrayAppHostPath.Comparer.Equals(host.Id.AppHostPath, path)))
        {
            controller.PruneMissingPinnedAppHosts();
        }
        else if (controller.State.RecentAppHosts.FirstOrDefault(host => TrayAppHostPath.Comparer.Equals(host.Id.AppHostPath, path)) is { } recent
            && _panel is not null)
        {
            ApplyPanelResponse(_panel.ConfirmRemoveMissing(recent));
        }
        else
        {
            throw new FileNotFoundException("The selected AppHost source file no longer exists.", path);
        }
        return false;
    }

    private void ShowMessage(string title, string detail, uint flags)
    {
        var previousOwner = _modalOwner;
        _modalOwner = _window;
        _modalDepth++;
        try
        {
            if (smokeSeconds is not null && !_interactiveSmoke)
            {
                // MB_OK reports IDOK as its default, but its sole button uses IDCANCEL
                // so Escape can dismiss it. Sending IDOK leaves the native dialog open.
                _smokeDialog = new(1, 2);
            }
            NativeCallException.Require(NativeMethods.MessageBox(_modalOwner, detail, title, flags) != 0, "MessageBoxW");
        }
        finally
        {
            _smokeDialog = null;
            _modalDepth--;
            _modalOwner = previousOwner;
            RequestRefresh();
        }
    }

    private void OpenUrl(Uri uri)
    {
        if (smokeSeconds is not null)
        {
            (OpenUrlForSmoke ?? throw new InvalidOperationException("Smoke URL handler is missing."))(uri);
            return;
        }
        // ShellExecute receives only a controller-validated http(s) URL, never a command string.
        var result = NativeMethods.ShellExecute(_window, "open", uri.AbsoluteUri, null, null, 1);
        if (result <= 32)
        {
            throw new NativeCallException("ShellExecuteW(open URL)", (int)result);
        }
    }

    private enum ActionKind
    {
        Dashboard, Stop, Start, TogglePin, Explorer, CopyPath, OpenIn, ClearRecent, RemoveRecent, Documentation, Quit
    }

    private sealed record ActionTarget(ActionKind Kind, AppHostId Id = default, FolderApplication? Application = null);
}
