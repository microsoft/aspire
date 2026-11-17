// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.Utils;

internal static class HelpOutputVerifyExtensions
{
    /// <summary>
    /// Normalizes the root command name in System.CommandLine help output to the shipped <c>aspire</c> name.
    /// </summary>
    /// <remarks>
    /// System.CommandLine derives the root command name from <see cref="Environment.GetCommandLineArgs"/>[0].
    /// Starting with .NET 11 (https://github.com/dotnet/runtime/pull/131671) that is the apphost invocation name
    /// instead of the managed <c>Aspire.Cli.Tests.dll</c>, so the
    /// name is "Aspire.Cli.Tests" on Windows (from Aspire.Cli.Tests.exe) but "Aspire.Cli" on Linux/macOS, because
    /// <see cref="Path.GetFileNameWithoutExtension(string)"/> treats ".Tests" in the extension-less apphost name as
    /// an extension. The usage line looks like:
    ///   Usage:
    ///     Aspire.Cli.Tests resource &lt;resource&gt; &lt;command&gt; [options]
    /// See https://github.com/dotnet/command-line-api/issues/2850.
    /// </remarks>
    public static SettingsTask ScrubRootCommandName(this SettingsTask settings)
    {
        return settings.AddScrubber(builder => builder.Replace($"  {System.CommandLine.RootCommand.ExecutableName} ", "  aspire "));
    }
}
