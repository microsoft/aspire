// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;

namespace Aspire.Tray;

internal sealed partial class TrayApplication
{
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
                    if (!RequireCurrentRow(target.Id).CanStop)
                    {
                        throw new InvalidOperationException("Stop is no longer available for the selected AppHost.");
                    }
                    var host = controller.RequireLiveInstance(target.Id);
                    if (Confirm("Stop AppHost", $"Stop this exact AppHost instance?\n\n{host.AppHostPath}\nPID: {host.AppHostPid}"))
                    {
                        // The controller revalidates the full lifetime after the modal loop.
                        controller.RequestStop(target.Id);
                    }
                    break;
                case ActionKind.Start:
                    RequireCurrentRow(target.Id);
                    if (RequireProjectFile(target.Id.AppHostPath))
                    {
                        controller.RequestStart(target.Id.AppHostPath);
                    }
                    break;
                case ActionKind.TogglePin:
                    var row = RequireCurrentRow(target.Id);
                    controller.SetPinned(row.Id.AppHostPath, !row.IsPinned);
                    break;
                case ActionKind.Explorer:
                case ActionKind.OpenIn:
                    RequireCurrentRow(target.Id);
                    if (RequireProjectFile(target.Id.AppHostPath))
                    {
                        if (smokeSeconds is not null)
                        {
                            throw new InvalidOperationException("External application launch is disabled in smoke mode.");
                        }
                        if (target.Kind == ActionKind.Explorer)
                        {
                            FolderApplication.ShowInExplorer(target.Id.AppHostPath);
                        }
                        else
                        {
                            target.Application!.Open(Path.GetDirectoryName(target.Id.AppHostPath)!);
                        }
                    }
                    break;
                case ActionKind.ClearRecent:
                    if (Confirm("Clear Recently Opened", "Clear all recently opened AppHosts?\n\nThis action is irreversible. Pinned AppHosts will be kept."))
                    {
                        controller.ClearRecent();
                    }
                    break;
                case ActionKind.Documentation:
                    OpenUrl(new Uri("https://aspire.dev"));
                    break;
                case ActionKind.About:
                    var version = typeof(TrayApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                        ?? typeof(TrayApplication).Assembly.GetName().Version?.ToString() ?? "Development build";
                    ShowMessage("About Aspire", $"Aspire\nVersion {version}\n\nAn experimental companion for Aspire.\nhttps://aspire.dev", 0);
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
            if (smokeSeconds is not null)
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
        else if (controller.State.RecentAppHosts.Any(host => TrayAppHostPath.Comparer.Equals(host.Id.AppHostPath, path)))
        {
            if (Confirm("AppHost not found", $"This AppHost no longer exists:\n\n{path}\n\nRemove it from Recently Opened?"))
            {
                controller.RemoveRecent(path);
            }
        }
        else
        {
            throw new FileNotFoundException("The selected AppHost source file no longer exists.", path);
        }
        return false;
    }

    private bool Confirm(string title, string detail)
    {
        _modalDepth++;
        try
        {
            if (smokeSeconds is not null)
            {
                var accept = ConfirmForSmoke?.Invoke(title, detail, NativeMethods.SafeConfirmation)
                    ?? throw new InvalidOperationException("Smoke confirmation handler is missing.");
                _smokeDialog = new(2, accept ? 1 : 2);
            }
            var result = NativeMethods.MessageBox(_window, detail, title, NativeMethods.SafeConfirmation);
            NativeCallException.Require(result != 0, "MessageBoxW(confirm)");
            return result == 1 && !_quitRequested;
        }
        finally
        {
            _smokeDialog = null;
            DialogReadyForSmoke = null;
            _modalDepth--;
            RequestRefresh();
        }
    }

    private void ShowMessage(string title, string detail, uint flags)
    {
        _modalDepth++;
        try
        {
            if (smokeSeconds is not null)
            {
                _smokeDialog = new(1, 1);
            }
            NativeCallException.Require(NativeMethods.MessageBox(_window, detail, title, flags) != 0, "MessageBoxW");
        }
        finally
        {
            _smokeDialog = null;
            _modalDepth--;
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
}
