// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json.Nodes;

namespace NativeHosting;

internal static class NativeCliBootstrap
{
    public static JsonObject RuntimeSpec(string language)
    {
        RequireTypeScript(language);
        return new JsonObject
        {
            ["language"] = "typescript/nodejs", ["displayName"] = "Native TypeScript (Node.js)",
            ["codeGenLanguage"] = "TypeScript", ["detectionPatterns"] = new JsonArray("apphost.ts", "apphost.mts"),
            ["execute"] = new JsonObject
            {
                ["command"] = Environment.GetEnvironmentVariable("NATIVE_HOSTING_NODE") ?? "node",
                ["args"] = new JsonArray("{appHostFile}", "{args}")
            }
        };
    }

    public static JsonObject GeneratedSdk(string language)
    {
        RequireTypeScript(language);
        var files = new JsonObject();
        var assembly = typeof(NativeCliBootstrap).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("NativeSdk.", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            files[name["NativeSdk.".Length..]] = reader.ReadToEnd();
        }

        if (!files.ContainsKey("aspire.mjs"))
        {
            throw new InvalidOperationException("Publish the native server after generating and compiling its offline ATS SDK.");
        }

        return files;
    }

    public static async Task<JsonObject> GenerateCodeAsync(string language, JsonArray? assemblyNames, CancellationToken token)
    {
        RequireTypeScript(language);
        var tool = Environment.GetEnvironmentVariable("NATIVE_HOSTING_CODEGEN_TOOL")!;
        var root = Environment.GetEnvironmentVariable("NATIVE_HOSTING_CODEGEN_ROOT")
            ?? throw new InvalidOperationException("NATIVE_HOSTING_CODEGEN_ROOT must identify the experiment directory.");
        if (!Path.IsPathFullyQualified(tool) || !File.Exists(tool) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
        {
            throw new ArgumentException("Codegen tool and root must be existing absolute paths.");
        }

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("NATIVE_HOSTING_DOTNET") ?? "dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { tool, root, "--codegen-rpc" })
        {
            start.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the external ATS codegen tool.");
        try
        {
            // Drain both pipes while compiling to avoid blocking the helper on a
            // full stderr buffer. No Hosting/codegen assemblies enter this process.
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteLineAsync(new JsonObject
            {
                ["language"] = "TypeScript", ["assemblyNames"] = assemblyNames?.DeepClone()
            }.ToJsonString().AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var diagnostics = await stderr;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"External ATS codegen failed ({process.ExitCode}): {diagnostics}");
            }

            var files = JsonNode.Parse(output)?.AsObject() ?? throw new InvalidOperationException("Codegen returned no files.");
            if (files["aspire.mts"] is null || files["aspire.mjs"] is null)
            {
                throw new InvalidOperationException("Codegen did not return the TypeScript SDK.");
            }

            Console.Error.WriteLine("Native generateCode served production ATS output from an external helper.");
            return files;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    public static Process StartIntegrationHost(string socketPath)
    {
        var entry = Environment.GetEnvironmentVariable("NATIVE_HOSTING_INTEGRATION_HOST")
            ?? throw new InvalidOperationException("NATIVE_HOSTING_INTEGRATION_HOST must identify the explicit ATS integration host.");
        if (!Path.IsPathFullyQualified(entry) || !File.Exists(entry))
        {
            throw new ArgumentException("NATIVE_HOSTING_INTEGRATION_HOST must be an existing absolute path.");
        }

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("NATIVE_HOSTING_NODE") ?? "node")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(entry);
        start.Environment["REMOTE_APP_HOST_SOCKET_PATH"] = socketPath;
        start.Environment["ASPIRE_INTEGRATION_HOST_REGISTRATION_ID"] = Guid.NewGuid().ToString("N");
        return Process.Start(start) ?? throw new InvalidOperationException("Could not launch the native ATS integration host.");
    }

    private static void RequireTypeScript(string language)
    {
        if (!string.Equals(language, "TypeScript", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(language, "typescript/nodejs", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("The native exploration currently provides only its offline TypeScript SDK.");
        }
    }
}
