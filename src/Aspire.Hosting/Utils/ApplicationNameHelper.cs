// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Hosting.Utils;

internal static partial class ApplicationNameHelper
{
    [GeneratedRegex("""^(?<name>.+?)[.-]?AppHost$""", RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    internal static partial Regex ApplicationNameRegex();
}
