// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Go;

/// <summary>
/// Retains Go run debug information without replacing configured linker flags.
/// </summary>
internal static class GoRunDebugging
{
    private const string DebugLinkerFlags = "-s=false -w=false";
    private const string DefaultFlags = $"\"-ldflags={DebugLinkerFlags}\"";

    public static async Task ConfigureEnvironmentAsync(EnvironmentCallbackContext context)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        object? original = context.EnvironmentVariables
            .LastOrDefault(variable => string.Equals(variable.Key, "GOFLAGS", comparison)).Value;
        if (original is IResourceBuilder<IResource> resourceBuilder)
        {
            original = resourceBuilder.Resource;
        }

        var goFlags = original is null ? null :
            (await context.Resource.ResolveValueAsync(
                context.ExecutionContext, context.Logger, original, "GOFLAGS", context.CancellationToken).ConfigureAwait(false))?.Value;
        if (string.IsNullOrEmpty(goFlags))
        {
            goFlags = await ReadGoFlagsAsync(context, goFlags).ConfigureAwait(false);
            original = null;
        }

        var overrides = new StringBuilder();
        // Validate the complete input before a fallback can leave it unchanged.
        foreach (var flag in SplitFlags(goFlags).ToArray())
        {
            if (flag.StartsWith("-ldflags=", StringComparison.Ordinal) ||
                flag.StartsWith("--ldflags=", StringComparison.Ordinal))
            {
                // GOFLAGS may contain e.g. "-ldflags=-X main.version=1.2.3" or a
                // package-qualified -ldflags=all=-s. Preserve each pattern and its order.
                // Go's quoted fields have no escape syntax. A valid unquoted field such as
                // -ldflags=-X=main.version=a'"b cannot be quoted to append linker flags.
                // https://go.dev/src/cmd/internal/quoted/quoted.go
                if (flag.Contains('"') && flag.Contains('\''))
                {
                    context.EnvironmentVariables["GOFLAGS"] = original ?? goFlags;
                    context.Logger.LogWarning("Cannot automatically retain Go debug information for a GOFLAGS linker flag containing both quote types. Preserving GOFLAGS unchanged.");
                    return;
                }

                // Per-package flags look like -ldflags=all=-X=main.version=1.2.3.
                // Insert defaults after the optional pattern but before the user's linker options,
                // so explicit -s/-w choices still win. Go identifies a pattern when the trimmed value
                // does not start with '-': https://go.dev/src/cmd/go/internal/load/flag.go
                var valueStart = flag.IndexOf('=') + 1;
                var value = flag[valueStart..].TrimStart();
                var patternEnd = value.StartsWith('-') ? -1 : value.IndexOf('=');
                var linkerFlags = value[(patternEnd + 1)..];
                // -s implies -w unless -w=false was supplied. Do not inject -w=false when the
                // effective user -s is true; other options still override their own defaults.
                // https://pkg.go.dev/cmd/link#hdr-Command_Line
                var defaults = StripsSymbols(linkerFlags) ? "-s=false" : DebugLinkerFlags;
                var quote = flag.Contains('"') ? '\'' : '"';
                overrides.Append(' ').Append(quote).Append(flag.AsSpan(0, valueStart))
                    .Append(value.AsSpan(0, patternEnd + 1))
                    .Append(defaults).Append(' ').Append(linkerFlags).Append(quote);
            }
        }

        // go run strips symbols and DWARF unless -s=false -w=false reaches the linker. A command-line
        // -ldflags would replace GOFLAGS linker settings rather than extend them.
        // https://pkg.go.dev/cmd/go#hdr-Compile_and_run_Go_program
        var suffix = overrides.ToString();
        context.EnvironmentVariables["GOFLAGS"] =
            original is IExpressionValue provider
                ? ReferenceExpression.Create($"{DefaultFlags} {provider}{suffix}")
                : $"{DefaultFlags} {goFlags}{suffix}";
    }

    private static async Task<string> ReadGoFlagsAsync(EnvironmentCallbackContext context, string? resourceFlags)
    {
        var environment = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (resourceFlags is not null)
        {
            environment["GOFLAGS"] = resourceFlags;
        }

        // Only Go settings and tool/config lookup inputs belong in the probe, not application dependencies.
        foreach (var (name, value) in context.EnvironmentVariables)
        {
            var variable = OperatingSystem.IsWindows() ? name.ToUpperInvariant() : name;
            if (variable is not ("GOENV" or "GOROOT" or "PATH" or
                "HOME" or "USERPROFILE" or "APPDATA" or "XDG_CONFIG_HOME"))
            {
                continue;
            }

            var resolved = await context.Resource.ResolveValueAsync(
                context.ExecutionContext, context.Logger, value, name, context.CancellationToken).ConfigureAwait(false);
            if (resolved?.Value is { } text)
            {
                environment[name] = text;
            }
        }

        // Probe the configured command because wrappers can select their own GOENV file.
        // Reading settings must not download a different Go toolchain.
        environment["GOTOOLCHAIN"] = "local";
        var output = new StringBuilder();
        var resource = (GoAppResource)context.Resource;
        var runner = context.ExecutionContext.Services.GetRequiredService<IProcessRunner>();
        try
        {
            var (resultTask, process) = runner.Run(new ProcessSpec(resource.Command)
            {
                ArgumentList = ["env", "-json", "GOFLAGS"],
                WorkingDirectory = resource.WorkingDirectory,
                EnvironmentVariables = environment,
                ResolveExecutablePath = true,
                // Disposal awaits the process result after termination. Handle exit codes here
                // so a terminated probe cannot replace caller cancellation with an exit exception.
                ThrowOnNonZeroReturnCode = false,
                OnOutputData = line => output.AppendLine(line)
            });

            await using (process)
            {
                var result = await resultTask.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException($"The Go settings probe exited with code {result.ExitCode}.");
                }
            }

            using var document = JsonDocument.Parse(output.ToString());

            return document.RootElement.GetProperty("GOFLAGS").GetString()
                ?? throw new InvalidOperationException("go env returned a null GOFLAGS value.");
        }
        catch (Exception exception) when (!context.CancellationToken.IsCancellationRequested &&
            exception is Win32Exception or IOException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            context.Logger.LogWarning(exception, "Could not read GOFLAGS using 'go env -json GOFLAGS'. Continuing with available GOFLAGS and default Go debugger flags.");
            // DCP inherits the AppHost environment, so a failed probe must not mask flags
            // already available without Go. An explicitly empty resource value still wins.
            return resourceFlags ?? Environment.GetEnvironmentVariable("GOFLAGS") ?? string.Empty;
        }
    }

    private static bool StripsSymbols(string linkerFlags)
    {
        // Go accepts -s, --s and -s=<bool>; the last value wins. Value-taking options consume
        // their next field even when it resembles a switch, e.g. -L "-s" or -buildid -w.
        // https://pkg.go.dev/flag
        var options = SplitFlags(linkerFlags).ToArray();
        var stripsSymbols = false;
        for (var index = 0; index < options.Length; index++)
        {
            var option = options[index];
            if (!option.StartsWith('-') || option is "-" or "--")
            {
                break;
            }

            var nameStart = option.StartsWith("--", StringComparison.Ordinal) ? 2 : 1;
            var separator = option.IndexOf('=', nameStart);
            var name = separator < 0 ? option[nameStart..] : option[nameStart..separator];
            if (name == "s")
            {
                stripsSymbols = separator < 0 || option[(separator + 1)..] is "1" or "t" or "T" or "TRUE" or "true" or "True";
            }
            else if (separator < 0 && name is
                "B" or "D" or "E" or "H" or "I" or "L" or "R" or "T" or "X" or
                "benchmark" or "benchmarkprofile" or "buildid" or "buildmode" or "capturehostobjs" or
                "cpuprofile" or "debugtextsize" or "debugtramp" or "extar" or "extld" or "extldflags" or
                "fipso" or "funcalign" or "importcfg" or "installsuffix" or "k" or "libgcc" or "linkmode" or
                "macos" or "macsdk" or "memprofile" or "memprofilerate" or "o" or "pluginpath" or "r" or
                "randlayout" or "strictdups" or "tmpdir")
            {
                index++;
            }
        }

        return stripsSymbols;
    }

    private static IEnumerable<string> SplitFlags(string flags)
    {
        // GOFLAGS uses Go's quoted fields, not shell escaping:
        //   -tags=netgo "-ldflags=-X main.version=1.2.3"
        // Only a quote at the start of a field opens a quoted field.
        // https://go.dev/src/cmd/internal/quoted/quoted.go
        var position = 0;
        while (position < flags.Length)
        {
            if (IsSpace(flags[position]))
            {
                position++;
                continue;
            }

            if (flags[position] is '\'' or '"')
            {
                var end = flags.IndexOf(flags[position], position + 1);
                if (end < 0)
                {
                    throw new FormatException("GOFLAGS contains an unterminated quoted flag.");
                }

                yield return flags[(position + 1)..end];
                position = end + 1;
            }
            else
            {
                var start = position;
                while (position < flags.Length && !IsSpace(flags[position]))
                {
                    position++;
                }

                yield return flags[start..position];
            }
        }
    }

    private static bool IsSpace(char value) => value is ' ' or '\t' or '\r' or '\n';
}
