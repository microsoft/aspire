// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting;

internal static class AzureFunctionsHostOptions
{
    private const string EndpointConflictMessage =
        "Azure Functions SDK RunArguments define endpoint settings that do not match the Aspire endpoint. " +
        "Configure port, HTTPS, certificate, and password options in the launch profile or AppHost instead.";

    private const string UnsupportedInvocationMessage =
        "The Azure Functions SDK run command must use func start, func host start, or cmd /C func start.";

    internal static ParsedEndpointOptions Parse(IReadOnlyList<string> arguments)
    {
        var result = new ParsedEndpointOptions();

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (TryParseOptionValue(arguments, index, argument, ["--port", "-p"], out var port))
            {
                result.TokenIndexes.Add(index);
                result.PortTokenIndexes.Add(index);
                if (port.ConsumedNext)
                {
                    result.TokenIndexes.Add(++index);
                    result.PortTokenIndexes.Add(index);
                }

                if (!result.PortSpecified)
                {
                    result.PortSpecified = true;
                    if (port.Value is null ||
                        !int.TryParse(port.Value, CultureInfo.InvariantCulture, out var value) ||
                        value is <= 0 or > 65_535)
                    {
                        result.Invalid = true;
                    }
                    else
                    {
                        result.Port = value;
                    }
                }

                continue;
            }

            if (TryParseBooleanOption(arguments, index, argument, "--useHttps", out var useHttps))
            {
                result.TokenIndexes.Add(index);
                if (useHttps.ConsumedNext)
                {
                    result.TokenIndexes.Add(++index);
                }

                if (!result.UseHttpsSpecified)
                {
                    result.UseHttpsSpecified = true;
                    if (useHttps.Value is null)
                    {
                        result.Invalid = true;
                    }
                    else
                    {
                        result.UseHttps = useHttps.Value.Value;
                    }
                }

                continue;
            }

            if (TryParseOptionValue(arguments, index, argument, ["--cert"], out var cert))
            {
                result.TokenIndexes.Add(index);
                if (cert.ConsumedNext)
                {
                    result.TokenIndexes.Add(++index);
                }

                if (!result.CertSpecified)
                {
                    result.CertSpecified = true;
                    if (cert.Value is null)
                    {
                        result.Invalid = true;
                    }
                    else
                    {
                        result.Cert = cert.Value;
                    }
                }

                continue;
            }

            if (TryParseOptionValue(arguments, index, argument, ["--password"], out var password))
            {
                result.TokenIndexes.Add(index);
                if (password.ConsumedNext)
                {
                    result.TokenIndexes.Add(++index);
                }

                if (!result.PasswordSpecified)
                {
                    result.PasswordSpecified = true;
                    if (password.Value is null)
                    {
                        result.Invalid = true;
                    }
                    else
                    {
                        result.Password = password.Value;
                    }
                }
            }
        }

        return result;
    }

    internal static void ConfigureReplicas(IResourceBuilder<AzureFunctionsResource> builder)
    {
        var resource = builder.Resource;
        var endpoint = resource.Annotations.OfType<EndpointAnnotation>().Single();
        builder.ApplicationBuilder.OnBeforeStart((context, _) =>
        {
            if (!context.Model.Resources.Contains(resource) || resource.GetReplicaCount() <= 1)
            {
                return Task.CompletedTask;
            }

            var arguments = CommandLineArgsParser.Parse(
                resource.GetEffectiveLaunchProfile()?.LaunchProfile.CommandLineArgs ?? string.Empty);
            var options = Parse(arguments);
            if (options.Port is not int port)
            {
                return Task.CompletedTask;
            }

            if (!resource.Annotations.Contains(endpoint))
            {
                throw new DistributedApplicationException(
                    $"Azure Functions resource '{resource.Name}' cannot replicate a fixed-port launch profile after its Functions endpoint has been removed.");
            }
            if (!endpoint.IsProxied)
            {
                // Only replace the fixed listener inferred from the profile, not a
                // different target port explicitly configured by the application.
                if (endpoint.Port != port || endpoint.TargetPort != port)
                {
                    return Task.CompletedTask;
                }

                endpoint.IsProxied = true;
                endpoint.TargetPort = null;
            }

            if (endpoint.TargetPort is not null)
            {
                return Task.CompletedTask;
            }

            // A profile such as "--port 7071 --useHttps" now describes the public
            // proxy port. Supply "--port <replica target> --useHttps" ourselves and
            // suppress the SDK/IDE's second application of the original profile.
            // Base .NET defaults have already captured its environment variables.
            builder.WithAnnotation(new ReplicatedLaunchProfileAnnotation(arguments, endpoint.Name, options), ResourceAnnotationMutationBehavior.Replace);
            builder.WithAnnotation(new ExcludeLaunchProfileAnnotation(), ResourceAnnotationMutationBehavior.Replace);
            return Task.CompletedTask;
        });

        builder.WithArgs(context =>
        {
            if (!resource.TryGetLastAnnotation<ReplicatedLaunchProfileAnnotation>(out var profile))
            {
                return;
            }

            context.Args.Add("--port");
            context.Args.Add(resource.GetEndpoint(profile.EndpointName).Property(EndpointProperty.TargetPort));
            for (var index = 0; index < profile.Arguments.Count; index++)
            {
                if (!profile.Options.PortTokenIndexes.Contains(index))
                {
                    context.Args.Add(profile.Arguments[index]);
                }
            }
        });
    }

    internal static int? GetReplicatedProfilePort(AzureFunctionsResource resource) =>
        resource.TryGetLastAnnotation<ReplicatedLaunchProfileAnnotation>(out var profile) ? profile.Options.Port : null;

    internal static IReadOnlyList<string> NormalizeSdkRunArguments(
        AzureFunctionsResource resource,
        string runCommand,
        IReadOnlyList<string> runArguments)
    {
        var optionsStart = GetHostOptionsStart(runCommand, runArguments);
        var sdkHostArguments = runArguments.Skip(optionsStart).ToArray();
        var sdkOptions = Parse(sdkHostArguments);
        if (sdkOptions.Invalid)
        {
            throw new DistributedApplicationException(EndpointConflictMessage);
        }

        if (sdkOptions.TokenIndexes.Count == 0)
        {
            return runArguments;
        }

        var launchProfileArguments = resource.TryGetLastAnnotation<ReplicatedLaunchProfileAnnotation>(out var profile)
            ? profile.Arguments
            : CommandLineArgsParser.Parse(resource.GetEffectiveLaunchProfile()?.LaunchProfile.CommandLineArgs ?? string.Empty);
        var launchProfileOptions = Parse(launchProfileArguments);
        var endpointName = profile?.EndpointName ?? (launchProfileOptions.UseHttps ? "https" : "http");
        var endpoint = resource.Annotations
            .OfType<EndpointAnnotation>()
            .LastOrDefault(annotation => annotation.Name == endpointName);
        var endpointPort = GetReplicatedProfilePort(resource) ?? endpoint?.TargetPort ?? endpoint?.Port;
        var useHttps = string.Equals(endpoint?.UriScheme, "https", StringComparison.Ordinal);

        var conflicts =
            (sdkOptions.PortSpecified && (endpointPort is null || sdkOptions.Port != endpointPort)) ||
            (sdkOptions.UseHttpsSpecified && sdkOptions.UseHttps != useHttps) ||
            (sdkOptions.CertSpecified && (!launchProfileOptions.CertSpecified || sdkOptions.Cert != launchProfileOptions.Cert)) ||
            (sdkOptions.PasswordSpecified && (!launchProfileOptions.PasswordSpecified || sdkOptions.Password != launchProfileOptions.Password));
        if (conflicts)
        {
            throw new DistributedApplicationException(EndpointConflictMessage);
        }

        return runArguments
            .Where((_, index) => index < optionsStart || !sdkOptions.TokenIndexes.Contains(index - optionsStart))
            .ToArray();
    }

    private static int GetHostOptionsStart(string runCommand, IReadOnlyList<string> arguments)
    {
        var commandName = GetCommandName(runCommand);
        var index = 0;
        if (commandName is "cmd" or "cmd.exe")
        {
            if (arguments.Count < 2 ||
                !string.Equals(arguments[0], "/c", StringComparison.OrdinalIgnoreCase) ||
                !IsFuncCommand(arguments[1]))
            {
                throw new DistributedApplicationException(UnsupportedInvocationMessage);
            }

            index = 2;
        }
        else if (!IsFuncCommand(runCommand))
        {
            throw new DistributedApplicationException(UnsupportedInvocationMessage);
        }

        if (index < arguments.Count && string.Equals(arguments[index], "host", StringComparison.OrdinalIgnoreCase))
        {
            index++;
        }
        if (index >= arguments.Count || !string.Equals(arguments[index], "start", StringComparison.OrdinalIgnoreCase))
        {
            throw new DistributedApplicationException(UnsupportedInvocationMessage);
        }

        return index + 1;
    }

    private static bool IsFuncCommand(string command) =>
        GetCommandName(command) is "func" or "func.exe" or "func.cmd" or "func.bat" or "func.ps1";

    private static string GetCommandName(string command)
    {
        var normalized = command.Replace('\\', '/');
        return normalized[(normalized.LastIndexOf('/') + 1)..].ToLowerInvariant();
    }

    private static bool TryParseOptionValue(
        IReadOnlyList<string> arguments,
        int index,
        string argument,
        string[] names,
        out ParsedValue value)
    {
        foreach (var name in names)
        {
            if (argument == name)
            {
                var next = index + 1 < arguments.Count ? arguments[index + 1] : null;
                var consumedNext = next is not null && !IsCommandLineOption(next);
                value = new(consumedNext ? next : null, consumedNext);
                return true;
            }

            if (argument.StartsWith($"{name}=", StringComparison.Ordinal) ||
                argument.StartsWith($"{name}:", StringComparison.Ordinal))
            {
                value = new(argument[(name.Length + 1)..], ConsumedNext: false);
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryParseBooleanOption(
        IReadOnlyList<string> arguments,
        int index,
        string argument,
        string name,
        out ParsedBoolean value)
    {
        if (argument == $"{name}+")
        {
            value = new(true, ConsumedNext: false);
            return true;
        }
        if (argument == $"{name}-")
        {
            value = new(false, ConsumedNext: false);
            return true;
        }
        if (!TryParseOptionValue(arguments, index, argument, [name], out var parsed))
        {
            value = default;
            return false;
        }

        if (parsed.Value is null)
        {
            value = new(true, ConsumedNext: false);
            return true;
        }

        if (TryParseBooleanValue(parsed.Value, out var boolean))
        {
            value = new(boolean, parsed.ConsumedNext);
            return true;
        }

        value = new(null, parsed.ConsumedNext);
        return true;
    }

    private static bool TryParseBooleanValue(string value, out bool result)
    {
        switch (value.ToLowerInvariant())
        {
            case "true":
            case "on":
            case "1":
                result = true;
                return true;
            case "false":
            case "off":
            case "0":
                result = false;
                return true;
            default:
                result = default;
                return false;
        }
    }

    private static bool IsCommandLineOption(string value) => value.StartsWith('-');

    private readonly record struct ParsedValue(string? Value, bool ConsumedNext);

    private readonly record struct ParsedBoolean(bool? Value, bool ConsumedNext);

    internal sealed class ParsedEndpointOptions
    {
        public int? Port { get; set; }

        public bool PortSpecified { get; set; }

        public bool UseHttps { get; set; }

        public bool UseHttpsSpecified { get; set; }

        public string? Cert { get; set; }

        public bool CertSpecified { get; set; }

        public string? Password { get; set; }

        public bool PasswordSpecified { get; set; }

        public bool Invalid { get; set; }

        public HashSet<int> TokenIndexes { get; } = [];

        public HashSet<int> PortTokenIndexes { get; } = [];
    }

    private sealed class ReplicatedLaunchProfileAnnotation(
        IReadOnlyList<string> arguments,
        string endpointName,
        ParsedEndpointOptions options) : IResourceAnnotation
    {
        public IReadOnlyList<string> Arguments { get; } = arguments;

        public string EndpointName { get; } = endpointName;

        public ParsedEndpointOptions Options { get; } = options;
    }
}
