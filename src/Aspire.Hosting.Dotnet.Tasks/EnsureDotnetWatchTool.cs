// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Xml;
using Aspire.Hosting.Dotnet.Watch;
using Microsoft.Build.Framework;

namespace Aspire.Hosting.Dotnet.Tasks;

[Experimental("ASPIREWATCH001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class EnsureDotnetWatchTool : Microsoft.Build.Utilities.Task, ICancelableTask
{
    private readonly CancellationTokenSource _cancellation = new();

    [Required]
    public string AppHostDirectory { get; set; } = "";

    [Required]
    public string DotnetPath { get; set; } = "";

    [Output]
    public string ToolDllPath { get; set; } = "";

    [Output]
    public string ToolVersion { get; set; } = "";

    [Output]
    public int RuntimeMajor { get; set; }

    public override bool Execute()
    {
        try
        {
            var installation = DotnetWatchToolManager.EnsureFromEnvironmentAsync(
                AppHostDirectory,
                DotnetPath,
                message => Log.LogMessage(MessageImportance.High, message),
                _cancellation.Token).GetAwaiter().GetResult();
            ToolDllPath = installation.DllPath;
            ToolVersion = installation.Version;
            RuntimeMajor = installation.RuntimeMajor;
            return true;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            Log.LogError("Experimental .NET watch tool acquisition was canceled.");
            return false;
        }
        catch (Exception exception) when (exception is DotnetWatchToolException or IOException or UnauthorizedAccessException or JsonException or XmlException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            Log.LogError("Experimental .NET watch tool acquisition failed: {0}", exception.Message);
            return false;
        }
    }

    public void Cancel() => _cancellation.Cancel();
}
