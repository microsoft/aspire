// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

/// <summary>
/// The content of the recordings panel.
/// </summary>
/// <param name="SelectedRunId">The run being viewed.</param>
/// <param name="SelectRunAsync">Switches to a run: the run ID of a recording, or <see langword="null"/> for the live view.</param>
public sealed record DashboardRunsDialogContent(string SelectedRunId, Func<string?, Task> SelectRunAsync);
