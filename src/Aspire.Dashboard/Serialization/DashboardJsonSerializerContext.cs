// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Otlp.Http;
using Aspire.Dashboard.Utils;
using Microsoft.FluentUI.AspNetCore.Components;

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
[JsonSerializable(typeof(List<FileReferenceViewModel>))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(Orientation))]
internal sealed partial class DashboardJsonSerializerContext : JsonSerializerContext
{
	public static JsonSerializerOptions DefaultOptions { get; } = new()
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		TypeInfoResolver = Default
	};

	public static DashboardJsonSerializerContext DefaultContext { get; } = new(DefaultOptions);
}
