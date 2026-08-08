// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Npm;

/// <summary>
/// Resolves the npm registry for a package by reading the same configuration npm itself reads.
/// </summary>
/// <remarks>
/// <para>
/// The update check exists to answer "can the command we are about to recommend actually install
/// something newer?". That command is <c>npm install -g @microsoft/aspire-cli@latest</c>, and npm
/// resolves it against the <em>user's</em> configured registry. Enterprises routinely block
/// registry.npmjs.org and pin <c>registry=</c> to an internal proxy, so a hardcoded public-npm
/// lookup would fail for exactly the users whose install would have succeeded.
/// </para>
/// <para>
/// Configuration is read directly rather than by shelling out to <c>npm config get registry</c>.
/// Spawning npm costs a process launch on the command startup path and would reintroduce a
/// Node-on-PATH requirement that the HTTP-based lookup deliberately removed.
/// </para>
/// <para>
/// Only <c>registry</c> and <c>&lt;scope&gt;:registry</c> keys are ever materialized. Credential
/// keys such as <c>//registry.example.com/:_authToken</c> are skipped while parsing, so no token
/// is read into memory, and the lookup itself stays anonymous.
/// </para>
/// See https://docs.npmjs.com/cli/using-npm/config for the precedence rules implemented here.
/// </remarks>
internal sealed class NpmRegistryResolver : INpmRegistryResolver
{
    /// <summary>
    /// npm's built-in <c>registry</c> default, used when no configuration layer supplies one.
    /// </summary>
    internal static Uri DefaultRegistryUri { get; } = new("https://registry.npmjs.org/");

    internal const string RegistryKey = "registry";
    internal const string EnvironmentVariablePrefix = "npm_config_";

    private const string ScopedRegistryKeySuffix = ":registry";
    private const string UserConfigKey = "userconfig";
    private const string NpmrcFileName = ".npmrc";

    // npm locates the project config by walking up from the working directory to the nearest
    // "local prefix". The bound stops a pathological path (or a cycle introduced by symlinks)
    // from turning a startup-path lookup into an unbounded directory walk.
    private const int MaximumLocalPrefixSearchDepth = 64;

    // A .npmrc is a small ini file; anything this large is not one, and the update check should
    // not read an arbitrarily large file off disk on the startup path.
    private const int MaximumNpmrcSizeInBytes = 1024 * 1024;

    private readonly DirectoryInfo _workingDirectory;
    private readonly DirectoryInfo _homeDirectory;
    private readonly ILogger<NpmRegistryResolver> _logger;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly Lock _configurationLock = new();

    private IReadOnlyDictionary<string, ConfigurationValue>? _configuration;

    public NpmRegistryResolver(CliExecutionContext executionContext, ILogger<NpmRegistryResolver> logger)
        : this(executionContext.WorkingDirectory, executionContext.HomeDirectory, ReadProcessEnvironment(), logger)
    {
    }

    internal NpmRegistryResolver(
        DirectoryInfo workingDirectory,
        DirectoryInfo homeDirectory,
        IReadOnlyDictionary<string, string> environment,
        ILogger<NpmRegistryResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(homeDirectory);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        _workingDirectory = workingDirectory;
        _homeDirectory = homeDirectory;
        _environment = environment;
        _logger = logger;
    }

    public NpmRegistryResolution Resolve(string packageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);

        var configuration = GetConfiguration();

        // npm consults "<scope>:registry" before the global "registry" for a scoped package, so a
        // user who routes only @microsoft through an internal proxy is honored.
        // See https://docs.npmjs.com/cli/using-npm/scope#associating-a-scope-with-a-registry.
        if (TryGetScope(packageName, out var scope) &&
            TryResolveKey(configuration, scope + ScopedRegistryKeySuffix, out var scopedResolution))
        {
            return scopedResolution;
        }

        if (TryResolveKey(configuration, RegistryKey, out var resolution))
        {
            return resolution;
        }

        return new NpmRegistryResolution(DefaultRegistryUri, "the npm built-in default");
    }

    private bool TryResolveKey(
        IReadOnlyDictionary<string, ConfigurationValue> configuration,
        string key,
        [NotNullWhen(true)] out NpmRegistryResolution? resolution)
    {
        resolution = null;

        if (!configuration.TryGetValue(key, out var value))
        {
            return false;
        }

        if (!TryCreateRegistryUri(value.Value, out var registryUri))
        {
            // A registry that cannot be turned into an absolute http(s) address is unusable, but it
            // is the user's configuration rather than a CLI fault. Fall through to the next layer
            // instead of failing the update check outright.
            _logger.LogDebug(
                "Ignoring unusable npm '{Key}' value from {Source}; it is not an absolute http or https address.",
                key,
                value.Source);
            return false;
        }

        resolution = new NpmRegistryResolution(registryUri, value.Source);
        _logger.LogDebug("Resolved npm '{Key}' to {Registry} from {Source}.", key, resolution.DisplayUri, value.Source);

        return true;
    }

    private IReadOnlyDictionary<string, ConfigurationValue> GetConfiguration()
    {
        // npm configuration cannot change under a running command, so the layers are read once and
        // reused. The lock keeps concurrent update checks (background prefetch racing an explicit
        // doctor run) from each doing the file I/O.
        lock (_configurationLock)
        {
            return _configuration ??= BuildConfiguration();
        }
    }

    private Dictionary<string, ConfigurationValue> BuildConfiguration()
    {
        // Highest precedence first; the first layer to supply a key wins, matching npm's
        // cli > env > project > user > global > builtin ordering. The npm CLI's own global and
        // builtin npmrc layers are not read because locating them requires npm's install prefix,
        // which is only discoverable by running npm - the process launch this lookup exists to
        // avoid. Registry pinning lives in the env or user layer in practice.
        var configuration = new Dictionary<string, ConfigurationValue>(StringComparer.Ordinal);

        MergeEnvironment(configuration);

        if (TryGetLocalPrefix(out var localPrefix))
        {
            MergeNpmrcFile(configuration, Path.Combine(localPrefix, NpmrcFileName));
        }

        MergeNpmrcFile(configuration, GetUserConfigPath(configuration));

        return configuration;
    }

    private void MergeEnvironment(Dictionary<string, ConfigurationValue> configuration)
    {
        // npm maps any "npm_config_<key>" variable onto config key "<key>", case-insensitively on
        // the prefix, so both npm_config_registry and NPM_CONFIG_REGISTRY are honored. npm also
        // injects these into the environment of scripts it runs, which makes them the right
        // highest-precedence layer when the CLI is launched through npm exec or npx.
        foreach (var (name, value) in _environment)
        {
            if (!name.StartsWith(EnvironmentVariablePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = NormalizeKey(name[EnvironmentVariablePrefix.Length..]);

            if (!IsInterestingKey(key))
            {
                continue;
            }

            AddIfAbsent(configuration, key, value, $"the {name} environment variable");
        }
    }

    private void MergeNpmrcFile(Dictionary<string, ConfigurationValue> configuration, string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return;
        }

        string[] lines;

        try
        {
            var fileInfo = new FileInfo(path);

            if (fileInfo.Length > MaximumNpmrcSizeInBytes)
            {
                _logger.LogDebug("Ignoring {Path} because it exceeds the {Limit} byte limit.", path, MaximumNpmrcSizeInBytes);
                return;
            }

            lines = File.ReadAllLines(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // An unreadable .npmrc must not fail the update check; the next layer (or the npm
            // default) still produces a usable answer.
            _logger.LogDebug(exception, "Could not read npm configuration from {Path}.", path);
            return;
        }

        foreach (var line in lines)
        {
            if (!TryParseNpmrcLine(line, out var key, out var rawValue) || !IsInterestingKey(key))
            {
                continue;
            }

            if (!TryExpandEnvironmentReferences(rawValue, out var value))
            {
                // npm fails the whole config load on an unresolved ${VAR}; skipping just this entry
                // is the softer equivalent and keeps the remaining layers usable.
                _logger.LogDebug("Ignoring npm '{Key}' in {Path} because it references an undefined environment variable.", key, path);
                continue;
            }

            AddIfAbsent(configuration, key, value, path);
        }
    }

    /// <summary>
    /// Parses a single <c>.npmrc</c> line.
    /// </summary>
    /// <remarks>
    /// <c>.npmrc</c> is ini-formatted. Representative content:
    /// <code>
    /// ; a comment
    /// # also a comment
    /// registry=https://npm.contoso.example/artifactory/api/npm/npm/
    /// @microsoft:registry = "https://npm.contoso.example/microsoft/"
    /// //npm.contoso.example/:_authToken=${NPM_TOKEN}
    /// </code>
    /// Values may be quoted and may contain '=' (a URL query), so only the first '=' separates the
    /// key from the value. Credential lines such as <c>_authToken</c> are matched by
    /// <see cref="IsInterestingKey"/> and never retained.
    /// </remarks>
    private static bool TryParseNpmrcLine(string line, out string key, [NotNullWhen(true)] out string? value)
    {
        key = string.Empty;
        value = null;

        var trimmed = line.AsSpan().Trim();

        // '[' opens an ini section header. npm's own config keys are never sectioned, so a section
        // header carries nothing this resolver needs.
        if (trimmed.IsEmpty || trimmed[0] is ';' or '#' or '[')
        {
            return false;
        }

        var separatorIndex = trimmed.IndexOf('=');

        if (separatorIndex <= 0)
        {
            return false;
        }

        var parsedKey = trimmed[..separatorIndex].Trim();

        if (parsedKey.IsEmpty)
        {
            return false;
        }

        key = NormalizeKey(parsedKey.ToString());
        value = Unquote(trimmed[(separatorIndex + 1)..].Trim());

        return true;
    }

    private static string Unquote(ReadOnlySpan<char> value)
    {
        if (value.Length >= 2 &&
            (value[0] is '"' && value[^1] is '"' || value[0] is '\'' && value[^1] is '\''))
        {
            return value[1..^1].ToString();
        }

        return value.ToString();
    }

    /// <summary>
    /// Expands the <c>${VAR}</c> references npm substitutes from the environment when it loads a
    /// <c>.npmrc</c>.
    /// </summary>
    private bool TryExpandEnvironmentReferences(string value, [NotNullWhen(true)] out string? expanded)
    {
        expanded = null;

        if (!value.Contains("${", StringComparison.Ordinal))
        {
            expanded = value;
            return true;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        var index = 0;

        while (index < value.Length)
        {
            var start = value.IndexOf("${", index, StringComparison.Ordinal);

            if (start < 0)
            {
                builder.Append(value, index, value.Length - index);
                break;
            }

            var end = value.IndexOf('}', start + 2);

            if (end < 0)
            {
                builder.Append(value, index, value.Length - index);
                break;
            }

            builder.Append(value, index, start - index);

            var variableName = value[(start + 2)..end];

            if (!_environment.TryGetValue(variableName, out var variableValue))
            {
                return false;
            }

            builder.Append(variableValue);
            index = end + 1;
        }

        expanded = builder.ToString();
        return true;
    }

    /// <summary>
    /// Finds the directory whose <c>.npmrc</c> npm would load as the project config.
    /// </summary>
    /// <remarks>
    /// npm's local prefix is the nearest ancestor of the working directory containing a
    /// <c>package.json</c> or a <c>node_modules</c> directory, falling back to the working
    /// directory itself. This layer applies even to <c>-g</c> installs, because npm loads config
    /// before it interprets the command.
    /// </remarks>
    private bool TryGetLocalPrefix([NotNullWhen(true)] out string? localPrefix)
    {
        var directory = _workingDirectory;
        var depth = 0;

        while (directory is not null && depth++ < MaximumLocalPrefixSearchDepth)
        {
            try
            {
                if (File.Exists(Path.Combine(directory.FullName, "package.json")) ||
                    Directory.Exists(Path.Combine(directory.FullName, "node_modules")))
                {
                    localPrefix = directory.FullName;
                    return true;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(exception, "Could not inspect {Directory} while locating the npm local prefix.", directory.FullName);
                break;
            }

            directory = directory.Parent;
        }

        localPrefix = _workingDirectory.FullName;
        return true;
    }

    private string? GetUserConfigPath(IReadOnlyDictionary<string, ConfigurationValue> configuration)
    {
        // npm_config_userconfig relocates the user layer, and tooling that isolates npm (CI images,
        // sandboxes) sets it. Honor it before falling back to the home directory.
        if (configuration.TryGetValue(UserConfigKey, out var userConfig) &&
            !string.IsNullOrWhiteSpace(userConfig.Value))
        {
            return userConfig.Value;
        }

        return Path.Combine(_homeDirectory.FullName, NpmrcFileName);
    }

    private static void AddIfAbsent(
        Dictionary<string, ConfigurationValue> configuration,
        string key,
        string? value,
        string source)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        // Layers are merged highest precedence first, so an existing entry always outranks this one.
        configuration.TryAdd(key, new ConfigurationValue(value.Trim(), source));
    }

    /// <summary>
    /// Limits what is retained to the registry keys and the user-config redirect, so credential
    /// entries in a <c>.npmrc</c> are never held in memory.
    /// </summary>
    private static bool IsInterestingKey(string key)
    {
        return key is RegistryKey or UserConfigKey || key.EndsWith(ScopedRegistryKeySuffix, StringComparison.Ordinal);
    }

    private static string NormalizeKey(string key)
    {
        // npm package scopes are lowercase, and npm treats config keys case-insensitively, so
        // lowercasing makes "@Microsoft:registry" and "NPM_CONFIG_REGISTRY" resolve alike.
        return key.Trim().ToLowerInvariant();
    }

    private static bool TryGetScope(string packageName, [NotNullWhen(true)] out string? scope)
    {
        scope = null;

        if (packageName.Length == 0 || packageName[0] is not '@')
        {
            return false;
        }

        var separatorIndex = packageName.IndexOf('/');

        if (separatorIndex <= 1)
        {
            return false;
        }

        scope = packageName[..separatorIndex].ToLowerInvariant();
        return true;
    }

    internal static bool TryCreateRegistryUri(string? value, [NotNullWhen(true)] out Uri? registryUri)
    {
        registryUri = null;

        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        // Registry values are habitually written without a trailing slash
        // ("https://pkgs.dev.azure.com/org/_packaging/feed/npm/registry"). Uri composition replaces
        // the last segment of a base that does not end in '/', which would silently request
        // ".../npm/<package>" and drop "registry" from the feed path, so normalize before the value
        // is ever used as a base address.
        if (!parsed.AbsolutePath.EndsWith('/'))
        {
            parsed = new UriBuilder(parsed) { Path = parsed.AbsolutePath + "/" }.Uri;
        }

        registryUri = parsed;
        return true;
    }

    private static Dictionary<string, string> ReadProcessEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    private sealed record ConfigurationValue(string Value, string Source);
}
