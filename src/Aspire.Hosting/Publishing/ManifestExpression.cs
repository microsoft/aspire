// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Identifies references supported by the existing manifest expression language.
/// </summary>
internal static class ManifestExpression
{
    internal readonly record struct Token(int Start, int Length, string Text);

    public static IEnumerable<Token> GetTokens(string expression)
    {
        // Manifest strings contain references such as "https://{infra.outputs.host}".
        // Doubled braces escape literals: "{{frontend.bindings.http.tlsEnabled}}" is not a reference.
        // Match azd's scanner: https://github.com/Azure/azure-dev/blob/main/cli/azd/pkg/apphost/eval.go
        for (var i = 0; i < expression.Length; i++)
        {
            if (expression[i] is not ('{' or '}'))
            {
                continue;
            }

            if (i + 1 < expression.Length && expression[i + 1] == expression[i])
            {
                i++;
                continue;
            }

            if (expression[i] == '}')
            {
                throw new FormatException($"Unexpected '}}' in manifest expression at position {i}.");
            }

            var end = expression.IndexOf('}', i + 1);
            if (end < 0)
            {
                throw new FormatException($"Unclosed '{{' in manifest expression at position {i}.");
            }

            yield return new Token(i, end - i + 1, expression[i..(end + 1)]);
            i = end;
        }
    }

    public static bool IsSupportedReference(string expression)
    {
        // This is a vocabulary check, not a resource-existence check. Resource callbacks own the
        // referenced inputs/outputs. Keep aligned with azd's evalBindingRef, not CLR provider types:
        // https://github.com/Azure/azure-dev/blob/main/cli/azd/pkg/apphost/generate.go
        var parts = expression[1..^1].Split('.');
        if (parts.Length < 2)
        {
            return false;
        }

        if (parts[0].Length == 0)
        {
            return parts is ["", "outputs", ..] && parts.Length >= 3 && parts[2].Length > 0;
        }

        return parts[1..] switch
        {
            ["connectionString"] or ["value"] or ["containerImage"] or ["containerPort"] or ["secretOutputs"] => true,
            ["inputs", var input] => input.Length > 0,
            ["outputs" or "secretOutputs" or "secrets", ..] => parts.Length >= 3 && parts[2].Length > 0,
            ["bindings", var endpoint, "scheme" or "protocol" or "transport" or "external" or "host" or "targetPort" or "port" or "url"] => endpoint.Length > 0,
            ["bindMounts" or "volumes", var index, "storage"] => int.TryParse(index, out var value) && value >= 0,
            _ => false
        };
    }
}
