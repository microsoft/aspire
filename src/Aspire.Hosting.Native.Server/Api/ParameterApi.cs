// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.Native.Api;

internal sealed partial class Composition
{
    /// <summary>Declares a configuration-backed parameter; publishing preserves an unresolved reference without reading credentials.</summary>
    [AspireExport]
    public DeclaredValue AddConfiguredParameter(string name, bool secret, bool publishing)
    {
        if (publishing)
        {
            return new(this, Model.AddUnboundParameter(name, secret));
        }
        var environmentName = "ASPIRE_PARAMETER_" + name.Replace('-', '_').ToUpperInvariant();
        var value = Environment.GetEnvironmentVariable("Parameters__" + name) ?? Environment.GetEnvironmentVariable(environmentName)
            ?? throw new InvalidOperationException($"Parameter '{name}' requires configuration through Parameters__{name} or {environmentName}.");

        return new(this, secret ? Model.AddSecretParameter(name, value) : Model.AddParameter(name, value, false));
    }

    /// <summary>Declares an explicit parameter value, exporting a default only when it is nonsecret and requested.</summary>
    [AspireExport]
    public DeclaredValue AddParameterValue(string name, string value, bool secret, bool publishValueAsDefault)
    {
        if (secret && publishValueAsDefault)
        {
            throw new ArgumentException("Secret values cannot be published as parameter defaults.");
        }

        return new(this, secret ? Model.AddSecretParameter(name, value) : Model.AddParameter(name, value, publishValueAsDefault));
    }

    /// <summary>Generates a parameter only when explicitly requested, retaining secret payloads exclusively in the native model.</summary>
    [AspireExport]
    public DeclaredValue AddGeneratedParameter(string name, string options, bool secret)
    {
        // Generation options use the polyglot input shape, for example:
        // {"minLength":24,"lower":true,"upper":true,"numeric":true,"special":false,"minUpper":2}.
        var configuration = JsonNode.Parse(options) as JsonObject
            ?? throw new ArgumentException("Parameter generation options must be an object.", nameof(options));
        string[] allowed = ["minLength", "lower", "upper", "numeric", "special", "minLower", "minUpper", "minNumeric", "minSpecial"];
        if (configuration.Any(property => !allowed.Contains(property.Key, StringComparer.Ordinal)))
        {
            throw new ArgumentException("Unsupported parameter generation option.", nameof(options));
        }
        var length = configuration["minLength"]?.GetValue<int>() ?? 22;
        string[] alphabets = ["abcdefghijklmnopqrstuvwxyz", "ABCDEFGHIJKLMNOPQRSTUVWXYZ", "0123456789", "!#$%&()*+,-./:;<=>?@[]^_{|}~"];
        string[] flags = ["lower", "upper", "numeric", "special"];
        string[] minima = ["minLower", "minUpper", "minNumeric", "minSpecial"];
        var selected = new List<string>();
        var required = new List<char>();
        for (var index = 0; index < alphabets.Length; index++)
        {
            var enabled = configuration[flags[index]]?.GetValue<bool>() ?? true;
            var minimum = configuration[minima[index]]?.GetValue<int>() ?? (enabled ? 1 : 0);
            if (minimum < 0 || minimum > 8192 || !enabled && minimum > 0)
            {
                throw new ArgumentException("Invalid generated-parameter character requirements.");
            }
            if (enabled)
            {
                selected.Add(alphabets[index]);
            }
            for (var count = 0; count < minimum; count++)
            {
                required.Add(alphabets[index][RandomNumberGenerator.GetInt32(alphabets[index].Length)]);
            }
        }
        if (length < 1 || length > 8192 || required.Count > length || selected.Count == 0)
        {
            throw new ArgumentException("Invalid generated-parameter length or character selection.");
        }
        var characters = string.Concat(selected);
        var result = new char[length];
        required.CopyTo(result);
        for (var index = required.Count; index < length; index++)
        {
            result[index] = characters[RandomNumberGenerator.GetInt32(characters.Length)];
        }
        RandomNumberGenerator.Shuffle(result.AsSpan());
        var value = new string(result);

        return new(this, secret ? Model.AddSecretParameter(name, value) : Model.AddParameter(name, value, false));
    }
}
