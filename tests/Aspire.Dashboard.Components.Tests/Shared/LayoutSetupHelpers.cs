// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Bunit;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal static class LayoutSetupHelpers
{
    public static void SetupMobileNavMenuKeyboardNavigation(TestContext context)
    {
        context.JSInterop.SetupModule(invocation => invocation.Identifier == "initializeMobileNavMenuKeyboardNavigation");
        context.JSInterop.SetupVoid("disposeMobileNavMenuKeyboardNavigation", _ => true);
    }
}
