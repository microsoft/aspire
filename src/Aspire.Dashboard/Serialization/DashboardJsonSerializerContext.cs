// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;

namespace Aspire.Dashboard.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ViewportSize))]
[JsonSerializable(typeof(BrowserInfo))]
[JsonSerializable(typeof(TerminalSizePreset[]))]
[JsonSerializable(typeof(ConsoleLogsFilters))]
[JsonSerializable(typeof(ConsoleLogs.ConsoleLogConsoleSettings))]
[JsonSerializable(typeof(TextVisualizerDialog.TextVisualizerDialogSettings))]
[JsonSerializable(typeof(TimeFormat))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class DashboardJsonSerializerContext : JsonSerializerContext;
