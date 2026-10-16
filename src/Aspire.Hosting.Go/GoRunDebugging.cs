// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Go;

/// <summary>
/// Retains Go run debug information without replacing configured linker flags.
/// </summary>
internal static class GoRunDebugging
{
    private const string DefaultFlags = "\"-ldflags=-s=false -w=false\"";

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
        foreach (var flag in SplitFlags(goFlags))
        {
            if (flag.StartsWith("-ldflags=", StringComparison.Ordinal) ||
                flag.StartsWith("--ldflags=", StringComparison.Ordinal))
            {
                // GOFLAGS may contain e.g. "-ldflags=-X main.version=1.2.3" or a
                // package-qualified -ldflags=all=-s. Preserve each pattern and its order.
                // Go's quoted fields have no escape syntax; use the other quote type
                // when a field already contains quotes.
                // https://go.dev/src/cmd/internal/quoted/quoted.go
                var quote = !flag.Contains('"') ? '"' : !flag.Contains('\'') ? '\'' :
                    throw new FormatException("Cannot retain Go debug information for a GOFLAGS linker flag containing both quote types. Set ldFlags explicitly with -s=false -w=false.");
                overrides.Append(' ').Append(quote).Append(flag).Append(" -s=false -w=false").Append(quote);
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
        var (resultTask, process) = runner.Run(new ProcessSpec(resource.Command)
        {
            ArgumentList = ["env", "-json", "GOFLAGS"],
            WorkingDirectory = resource.WorkingDirectory,
            EnvironmentVariables = environment,
            ResolveExecutablePath = true,
            OnOutputData = line => output.AppendLine(line)
        });

        await using (process)
        {
            await resultTask.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        }

        using var document = JsonDocument.Parse(output.ToString());

        return document.RootElement.GetProperty("GOFLAGS").GetString()
            ?? throw new InvalidOperationException("go env returned a null GOFLAGS value.");
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
