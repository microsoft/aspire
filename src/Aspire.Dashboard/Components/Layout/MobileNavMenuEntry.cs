// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Dashboard.Components.Deck;

namespace Aspire.Dashboard.Components.Layout;

internal record MobileNavMenuEntry(
    string Text,
    Func<Task> OnClick,
    DeckIconName? Icon = null,
    Regex? LinkMatchRegex = null);
