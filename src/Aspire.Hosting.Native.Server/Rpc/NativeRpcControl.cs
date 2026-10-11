// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Api;

namespace Aspire.Hosting.Native.Rpc;

/// <summary>Supplies CLI bootstrap and control without importing the managed AppHost runtime.</summary>
internal sealed class NativeRpcControl(NativeApplicationServer server, Action requestStop)
{
    public void RequestStop() => requestStop();

    public JsonObject ReadRuntimeState()
    {
        var observer = server.GetApplicationObserver();
        var snapshot = observer?.ReadResourceObservations();

        return new JsonObject
        {
            ["ready"] = snapshot is { Resources.Length: > 0 } && snapshot.Resources.All(resource => resource.State == "Running" && resource.Healthy),
            ["resources"] = new JsonArray(snapshot?.Resources.Select(resource => (JsonNode)new JsonObject
            {
                ["name"] = resource.Name, ["kind"] = resource.TypeId, ["state"] = resource.State,
                ["urls"] = new JsonArray(resource.Urls.Select(url => (JsonNode)JsonValue.Create(url)!).ToArray())
            }).ToArray() ?? [])
        };
    }

    public static JsonObject RuntimeSpec(string language)
    {
        RequireTypeScript(language);

        return new JsonObject
        {
            ["language"] = "typescript/nodejs", ["displayName"] = "Native TypeScript (Node.js)",
            ["codeGenLanguage"] = "TypeScript", ["detectionPatterns"] = new JsonArray("apphost.mts"),
            ["execute"] = new JsonObject
            {
                ["command"] = "npx", ["args"] = new JsonArray("--no-install", "tsx", "{appHostFile}", "{args}")
            }
        };
    }

    public static JsonObject GeneratedSdk(string language)
    {
        RequireTypeScript(language);
        var assembly = typeof(NativeRpcControl).Assembly;
        var files = new JsonObject();
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("NativeSdk.", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            files[name["NativeSdk.".Length..]] = reader.ReadToEnd();
        }
        if (!files.ContainsKey("native-client.mts") || !files.ContainsKey("aspire.mts"))
        {
            throw new InvalidOperationException("The native executable is missing its offline-generated SDK.");
        }

        return files;
    }

    private static void RequireTypeScript(string language)
    {
        if (!string.Equals(language, "TypeScript", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(language, "typescript/nodejs", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("The native bootstrap currently supports TypeScript only.");
        }
    }
}
