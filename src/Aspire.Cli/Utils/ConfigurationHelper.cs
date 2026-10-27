// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Hashing;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;
using Aspire.Cli.Resources;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils;

internal static class ConfigurationHelper
{
    internal const string IntegrationCacheFolderName = "integrations";
    private static readonly TimeSpan s_workspaceConfigLockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Standard options for parsing JSON that may contain non-spec features like comments and trailing commas.
    /// </summary>
    public static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    internal static void RegisterSettingsFiles(IConfigurationBuilder configuration, DirectoryInfo workingDirectory, FileInfo globalSettingsFile)
        => RegisterSettingsFiles(configuration, workingDirectory, globalSettingsFile, persistNormalization: true);

    internal static void RegisterSettingsFiles(IConfigurationBuilder configuration, DirectoryInfo workingDirectory, FileInfo globalSettingsFile, bool persistNormalization)
    {
        var currentDirectory = workingDirectory;

        // Find the nearest local settings file (prefer aspire.config.json, fall back to .aspire/settings.json)
        FileInfo? localSettingsFile = null;

        while (currentDirectory is not null)
        {
            // Check for aspire.config.json first (new format)
            var newSettingsPath = Path.Combine(currentDirectory.FullName, AspireConfigFile.FileName);
            if (File.Exists(newSettingsPath))
            {
                localSettingsFile = new FileInfo(newSettingsPath);
                break;
            }

            // TODO: Remove legacy .aspire/settings.json fallback once confident most users have migrated.
            // Tracked by https://github.com/microsoft/aspire/issues/15239
            // Fall back to .aspire/settings.json (legacy format).
            //
            // Startup is shared by every command — including read-only ones (aspire ls, ps,
            // doctor, describe, --version). Earlier versions eagerly migrated the legacy file
            // to aspire.config.json here so the workspace would move forward on the user's
            // first run of a newer CLI (https://github.com/microsoft/aspire/issues/15488),
            // but that broke the "read commands don't mutate the working tree" contract:
            // running aspire ls in a workspace that only had .aspire/settings.json was
            // silently writing aspire.config.json, polluting git status and tripping CI
            // dirty-tree checks (https://github.com/microsoft/aspire/issues/17615).
            //
            // Migration is now deferred to commands that already mutate the workspace
            // (aspire run/add/init/update/etc. via ProjectLocator.CreateSettingsFileAsync ->
            // AspireConfigFile.LoadOrCreate). Read commands continue to work against the
            // legacy file directly: AppHostPathConfigurationPolicy.TryFindAppHostPathKey
            // accepts both the legacy flat "appHostPath" key and the modern "appHost:path"
            // hierarchical key, and ProjectLocator's settings-file reader has its own legacy
            // fallback that does not write.
            var legacySettingsPath = BuildPathToSettingsJsonFile(currentDirectory.FullName);
            if (File.Exists(legacySettingsPath))
            {
                localSettingsFile = new FileInfo(legacySettingsPath);
                break;
            }

            currentDirectory = currentDirectory.Parent;
        }

        // Add global settings first (if it exists) - lower precedence
        if (File.Exists(globalSettingsFile.FullName))
        {
            AddSettingsFile(configuration, globalSettingsFile.FullName, persistNormalization);
        }

        // Then add local settings (if found) - this will override global settings
        if (localSettingsFile is not null)
        {
            AddSettingsFile(configuration, localSettingsFile.FullName, persistNormalization);
        }
    }

    internal static async Task<FileLock?> TryAcquireWorkspaceConfigLockAsync(
        CliExecutionContext executionContext, FileInfo settingsFile, ILogger logger, CancellationToken cancellationToken)
    {
        // Lock files live in the cache so read-only workspaces remain usable. Canonicalizing and
        // folding the config path makes aliases on symlinked or case-insensitive volumes contend;
        // on a case-sensitive volume this can only serialize two otherwise independent writes.
        var normalizedSettingsPath = PathNormalizer.ResolveSymlinks(settingsFile.FullName)
            .Normalize(NormalizationForm.FormC)
            .ToUpperInvariant();
        var lockFileName = Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(normalizedSettingsPath))).ToLowerInvariant();
        var lockPath = Path.Combine(executionContext.CacheDirectory.FullName, "workspace-config-locks", $"{lockFileName}.lock");
        try
        {
            return await FileLock.AcquireAsync(lockPath, cancellationToken, s_workspaceConfigLockTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // Preserve the existing best-effort workspace bookkeeping when the cache
            // cannot host a lock. Reads use the same policy as workspace-default writes.
            logger.LogDebug(ex, "Proceeding without the workspace config lock at {LockPath}.", lockPath);
            return null;
        }
    }

    internal static string BuildPathToSettingsJsonFile(string workingDirectory)
    {
        return Path.Combine(workingDirectory, ".aspire", "settings.json");
    }

    internal static DirectoryInfo? GetLegacySettingsRootDirectory(FileInfo settingsFile)
    {
        if (!string.Equals(settingsFile.Name, AspireJsonConfiguration.FileName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var settingsDirectory = settingsFile.Directory;
        if (settingsDirectory is null || !string.Equals(settingsDirectory.Name, AspireJsonConfiguration.SettingsFolder, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return settingsDirectory.Parent;
    }

    internal static DirectoryInfo GetConfigRootDirectory(DirectoryInfo startDirectory)
    {
        ArgumentNullException.ThrowIfNull(startDirectory);

        var configPath = FindNearestConfigFilePath(startDirectory);
        if (configPath is null)
        {
            return startDirectory;
        }

        var configFile = new FileInfo(configPath);

        // Legacy layout: <root>/.aspire/settings.json -> <root>
        var legacyRoot = GetLegacySettingsRootDirectory(configFile);
        if (legacyRoot is not null)
        {
            return legacyRoot;
        }

        // Modern layout: <root>/aspire.config.json -> <root>
        return configFile.Directory is { Exists: true } configDirectory
            ? configDirectory
            : startDirectory;
    }

    internal static DirectoryInfo GetWorkspaceAspireDirectory(DirectoryInfo startDirectory)
    {
        var configRoot = GetConfigRootDirectory(startDirectory);
        return new DirectoryInfo(Path.Combine(configRoot.FullName, AspireJsonConfiguration.SettingsFolder));
    }

    internal static DirectoryInfo GetIntegrationCacheDirectory(DirectoryInfo startDirectory)
    {
        var workspaceAspireDirectory = GetWorkspaceAspireDirectory(startDirectory);
        return new DirectoryInfo(Path.Combine(workspaceAspireDirectory.FullName, IntegrationCacheFolderName));
    }

    /// <summary>
    /// Searches upward from <paramref name="startDirectory"/> for the nearest
    /// <c>aspire.config.json</c> or legacy <c>.aspire/settings.json</c>.
    /// </summary>
    /// <returns>The full path to the config file, or <c>null</c> if none is found.</returns>
    internal static string? FindNearestConfigFilePath(DirectoryInfo startDirectory)
    {
        var searchDir = startDirectory;
        while (searchDir is not null)
        {
            var configPath = Path.Combine(searchDir.FullName, AspireConfigFile.FileName);
            if (File.Exists(configPath))
            {
                return configPath;
            }

            var legacyPath = BuildPathToSettingsJsonFile(searchDir.FullName);
            if (File.Exists(legacyPath))
            {
                return legacyPath;
            }

            searchDir = searchDir.Parent;
        }

        return null;
    }

    internal static bool TryLoadSettingsFile(string filePath, out IConfigurationRoot configuration)
    {
        configuration = new ConfigurationRoot([]);

        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var content = File.ReadAllText(filePath);

            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            var node = JsonNode.Parse(content, documentOptions: ParseOptions);
            if (node is not JsonObject)
            {
                return false;
            }

            var cleanJson = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var bytes = System.Text.Encoding.UTF8.GetBytes(cleanJson);
            configuration = new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(bytes))
                .Build();

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns JSON that <see cref="JsonSerializer"/> produced with the final newline it does not write.
    /// </summary>
    internal static string EnsureFinalNewline(string json)
    {
        // System.Text.Json already indents with Environment.NewLine, so serialized text needs no line break
        // normalization and only lacks the trailing newline. Text that was not serialized, such as a file copied
        // from the embedded templates, goes through NormalizeJsonFileContent instead.
        //
        // An existing final line break (LF or CRLF) is kept as is, so the result never ends with a second one.
        if (json.EndsWith('\n'))
        {
            return json;
        }

        return json + Environment.NewLine;
    }

    /// <summary>
    /// Returns JSON that was not serialized by the CLI, such as a template file copied from the embedded
    /// resources, with the OS line break and exactly one trailing newline.
    /// </summary>
    internal static string NormalizeJsonFileContent(string json) => NormalizeJsonFileContent(json, Environment.NewLine);

    /// <summary>
    /// Returns JSON with <paramref name="newLine"/> as every line break and exactly one trailing
    /// <paramref name="newLine"/>.
    /// </summary>
    internal static string NormalizeJsonFileContent(string json, string newLine)
    {
        // Config files get the OS line break, so they match what an editor on that OS produces and git
        // (core.autocrlf, .gitattributes) can normalize them like any other text file. A file copied from the
        // embedded templates keeps the line breaks of the checkout that built the CLI (CRLF from a Windows
        // checkout with core.autocrlf, LF otherwise), and some templates have no trailing newline, so it is
        // rewritten to match the files the CLI serializes.
        //
        // Only CRLF and LF are rewritten, not everything string.ReplaceLineEndings treats as a line break:
        // JSON forbids raw control characters inside strings, so a raw CRLF or LF can only be whitespace
        // between tokens and replacing it cannot change a value, whereas U+0085, U+2028 and U+2029 are legal
        // raw inside a string.
        var lf = json.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";

        return newLine == "\n" ? lf : lf.Replace("\n", newLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// Serializes a JsonObject and writes it to a settings file, creating the directory if needed.
    /// </summary>
    internal static async Task WriteSettingsFileAsync(string filePath, JsonObject settings, CancellationToken cancellationToken = default)
    {
        var jsonContent = EnsureFinalNewline(JsonSerializer.Serialize(settings, JsonSourceGenerationContext.Default.JsonObject));

        EnsureDirectoryExists(filePath);
        await File.WriteAllTextAsync(filePath, jsonContent, cancellationToken);
    }

    internal static async Task WriteSettingsFileAtomicallyAsync(string filePath, JsonObject settings, CancellationToken cancellationToken)
    {
        // Resolve before choosing the sibling so replacement updates the target, not the link.
        // This does not prevent filesystem races if links change during the write;
        // concurrent link changes are not expected during normal CLI usage.
        if (!PathNormalizer.TryResolveSymlinks(filePath, out var resolvedPath))
        {
            throw new IOException($"Unable to resolve symbolic links for settings file '{filePath}'.");
        }
        filePath = resolvedPath;

        EnsureDirectoryExists(filePath);
        // A sibling is required for atomic replacement even when the workspace and
        // system temp directory are on different volumes. CreateNew rejects existing links/files.
        var temporaryPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        var created = false;
        try
        {
            var destinationExists = File.Exists(filePath);
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            UnixFileMode? destinationMode = null;
            if (!OperatingSystem.IsWindows())
            {
                // Protect the temporary contents from creation, even in a shared directory.
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                destinationMode = destinationExists ? File.GetUnixFileMode(filePath) : null;
            }
            await using (var stream = OperatingSystem.IsWindows() && destinationExists
                ? new FileInfo(temporaryPath).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None,
                    4096, FileOptions.Asynchronous, new FileInfo(filePath).GetAccessControl(AccessControlSections.Access))
                : new FileStream(temporaryPath, options))
            {
                created = true;
                // Serialize to text rather than to the stream so the final newline is added the same way as in the
                // other settings writers. Encoding.UTF8.GetBytes writes no byte order mark, like File.WriteAllText.
                var jsonContent = EnsureFinalNewline(JsonSerializer.Serialize(settings, JsonSourceGenerationContext.Default.JsonObject));
                await stream.WriteAsync(Encoding.UTF8.GetBytes(jsonContent), cancellationToken);
            }
            if (!OperatingSystem.IsWindows() && destinationMode is { } mode)
            {
                File.SetUnixFileMode(temporaryPath, mode);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows() && destinationExists)
            {
                // Replace preserves the destination DACL and other Windows file metadata.
                File.Replace(temporaryPath, filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, filePath, overwrite: true);
            }
        }
        finally
        {
            if (created)
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>
    /// Serializes a JsonObject and writes it to a settings file, creating the directory if needed.
    /// </summary>
    internal static void WriteSettingsFile(string filePath, JsonObject settings)
    {
        var jsonContent = EnsureFinalNewline(JsonSerializer.Serialize(settings, JsonSourceGenerationContext.Default.JsonObject));

        EnsureDirectoryExists(filePath);
        File.WriteAllText(filePath, jsonContent);
    }

    private static void EnsureDirectoryExists(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (directory is not null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static JsonObject? AddSettingsFile(IConfigurationBuilder configuration, string filePath, bool persistNormalization)
    {
        // Proactively normalize the settings file to prevent duplicate key errors.
        // This handles files corrupted by mixing colon and dot notation
        // (e.g., both "features:key" flat entry and "features" nested object).
        if (persistNormalization)
        {
            TryNormalizeSettingsFile(filePath);
        }

        // Pre-process the file to handle comments and trailing commas.
        // Microsoft.Extensions.Configuration.Json doesn't support JSON comments,
        // so we parse with comment support and load the clean JSON via stream.
        try
        {
            var content = File.ReadAllText(filePath);
            var node = JsonNode.Parse(content, documentOptions: ParseOptions);
            if (!persistNormalization && node is JsonObject settings)
            {
                // Completion must resolve the same flat/nested key collisions without
                // rewriting user files merely because the shell requested suggestions.
                NormalizeSettings(settings);
            }

            if (node is not null)
            {
                var cleanJson = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                var bytes = System.Text.Encoding.UTF8.GetBytes(cleanJson);
                configuration.AddJsonStream(new MemoryStream(bytes));
                return node as JsonObject;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, ErrorStrings.InvalidJsonInConfigFile, filePath, ex.Message),
                ex);
        }

        configuration.AddJsonFile(filePath, optional: true);
        return null;
    }

    /// <summary>
    /// Normalizes a settings file by converting flat colon-separated keys to nested JSON objects.
    /// </summary>
    internal static bool TryNormalizeSettingsFile(string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);

            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            var settings = JsonNode.Parse(content, documentOptions: ParseOptions)?.AsObject();

            if (settings is null || !NormalizeSettings(settings))
            {
                return false;
            }

            WriteSettingsFile(filePath, settings);

            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool NormalizeSettings(JsonObject settings)
    {
        // Only root keys use legacy flat notation. Nested keys such as
        // {"environmentVariables":{"Logging:LogLevel:Default":"Debug"}} can be literal names.
        var colonKeys = settings
            .Where(static property => property.Key.Contains(':'))
            .Select(static property => (property.Key, Value: property.Value?.DeepClone()))
            .ToArray();
        if (colonKeys.Length == 0)
        {
            return false;
        }

        // Rebuild once: removing flat keys individually shifts JsonObject's ordered storage.
        // Keep the original non-flat nodes and their order.
        var retainedProperties = settings.Where(static property => !property.Key.Contains(':')).ToArray();
        settings.Clear();
        foreach (var property in retainedProperties)
        {
            settings.Add(property.Key, property.Value);
        }

        var keyLookups = new Dictionary<JsonObject, Dictionary<string, string>>(ReferenceEqualityComparer.Instance);
        string GetKey(JsonObject obj, string key)
        {
            if (!keyLookups.TryGetValue(obj, out var keys))
            {
                keys = new Dictionary<string, string>(obj.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var property in obj)
                {
                    // Preserve the first matching spelling if case-equivalent keys already exist.
                    keys.TryAdd(property.Key, property.Key);
                }
                keyLookups.Add(obj, keys);
            }

            if (keys.TryGetValue(key, out var existing))
            {
                return existing;
            }
            keys.Add(key, key);

            return key;
        }

        // For {"DOCS":{"custom":"local"},"docs":{"api":{"sitemapUrl":"nested"}}},
        // the complete docs:api:sitemapUrl path belongs to the second section.
        var propertiesByPath = new Dictionary<string, SettingsProperty>(StringComparer.OrdinalIgnoreCase);
        void IndexProperties(JsonObject obj, string? prefix)
        {
            foreach (var property in obj)
            {
                var path = prefix is null ? property.Key : $"{prefix}:{property.Key}";
                if (!propertiesByPath.TryGetValue(path, out var existing)
                    || (existing.Parent[existing.Key] is null && property.Value is not null))
                {
                    propertiesByPath[path] = new SettingsProperty(obj, property.Key);
                }
                if (property.Value is JsonObject section)
                {
                    IndexProperties(section, path);
                }
            }
        }
        SettingsProperty? ResolveProperty(string path)
        {
            if (propertiesByPath.TryGetValue(path, out var existing))
            {
                return existing;
            }

            // Keep the first matching segment when creating a previously absent path.
            var parts = path.Split(':');
            var parent = settings;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var key = GetKey(parent, parts[i]);
                var child = parent[key];
                if (child is null)
                {
                    child = new JsonObject();
                    parent[key] = child;
                }
                if (child is not JsonObject section)
                {
                    return null;
                }
                parent = section;
            }

            return new SettingsProperty(parent, GetKey(parent, parts[^1]));
        }

        IndexProperties(settings, prefix: null);
        foreach (var (key, value) in colonKeys)
        {
            if (ResolveProperty(key) is not { } property || property.Parent[property.Key] is not null)
            {
                continue;
            }

            property.Parent[property.Key] = value;
            propertiesByPath[key] = property;
            if (value is JsonObject section)
            {
                // Later flat keys must also respect nested values introduced by this object.
                IndexProperties(section, key);
            }
        }

        return true;
    }

    private readonly record struct SettingsProperty(JsonObject Parent, string Key);
}
