// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using ReconnectResources = Aspire.Dashboard.Resources.Reconnect;

namespace Aspire.Dashboard.Components.Layout;

public sealed partial class ReconnectModal : ComponentBase
{
    private const string UpgradeAspireUrl = "https://aka.ms/aspire/update-latest";

    [Inject]
    public required IStringLocalizer<ReconnectResources> Loc { get; init; }
}
