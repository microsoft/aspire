// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001
#pragma warning disable ASPIREDOTNETPROJECT001

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure;

internal static class VirtualMachineScaleSetApplicationPackageBuilder
{
    private const long MaximumPackageSizeInBytes = 2L * 1024 * 1024 * 1024;
    private const string TargetRuntimeIdentifier = "linux-x64";
    private static readonly DateTimeOffset s_archiveTimestamp = DateTimeOffset.UnixEpoch;
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode ExecutableFileMode = DirectoryMode;
    private const UnixFileMode RegularFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.GroupRead |
        UnixFileMode.OtherRead;

    internal static async Task<VirtualMachineScaleSetApplicationPackage> BuildAsync(
        DotnetProjectResource project,
        AzureVirtualMachineScaleSetEnvironmentResource environment,
        DistributedApplicationExecutionContext executionContext,
        IValueProvider workloadIdentityClientId,
        IFileSystemService fileSystem,
        IAspireStore store,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(workloadIdentityClientId);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        using var stagingDirectory = fileSystem.TempDirectory.CreateTempSubdirectory("aspire-vmss");
        using var archiveFile = fileSystem.TempDirectory.CreateTempFile("aspire-application.tar.gz");
        var publishDirectory = Path.Combine(stagingDirectory.Path, "app");
        Directory.CreateDirectory(publishDirectory);

        var projectPath = project.Annotations.OfType<IProjectMetadata>().Single().ProjectPath;
        await PublishProjectAsync(projectPath, publishDirectory, logger, cancellationToken).ConfigureAwait(false);

        var configuration = await ResolveConfigurationAsync(
            project,
            executionContext,
            workloadIdentityClientId,
            logger,
            cancellationToken).ConfigureAwait(false);
        var entryPoint = FindEntryPoint(publishDirectory, project.Name);
        await WriteManagementFilesAsync(
            stagingDirectory.Path,
            environment.Name,
            project.Name,
            entryPoint,
            configuration.EnvironmentVariables,
            configuration.Arguments,
            cancellationToken).ConfigureAwait(false);
        await CreateArchiveAsync(stagingDirectory.Path, archiveFile.Path, entryPoint, cancellationToken).ConfigureAwait(false);
        ValidatePackageSize(new FileInfo(archiveFile.Path).Length, project.Name);

        var fingerprint = await ComputeFingerprintAsync(archiveFile.Path, cancellationToken).ConfigureAwait(false);
        using var archiveStream = File.OpenRead(archiveFile.Path);
        var storedPath = store.GetFileNameWithContent($"{environment.Name}-vm-application.tgz", archiveStream);

        return new VirtualMachineScaleSetApplicationPackage(
            storedPath,
            fingerprint.Hex,
            CreateGalleryApplicationVersion(fingerprint.Value));
    }

    internal static void ValidatePackageSize(long packageSizeInBytes, string projectName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(packageSizeInBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        // Azure VM Applications accept individual packages up to 2 GiB.
        // See https://learn.microsoft.com/azure/virtual-machines/vm-applications#failure-resiliency-and-high-scale-performance.
        if (packageSizeInBytes > MaximumPackageSizeInBytes)
        {
            throw new DistributedApplicationException(
                $"The VM Application package for project '{projectName}' is {packageSizeInBytes} bytes, " +
                $"which exceeds the Azure VM Applications limit of {MaximumPackageSizeInBytes} bytes.");
        }
    }

    internal static async Task CreateArchiveAsync(
        string stagingDirectory,
        string archivePath,
        string entryPoint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);

        using var archiveStream = File.Create(archivePath);
        using var gzipStream = new GZipStream(archiveStream, CompressionLevel.SmallestSize, leaveOpen: false);
        using var tarWriter = new TarWriter(gzipStream, TarEntryFormat.Ustar, leaveOpen: false);

        var enumerationOptions = new EnumerationOptions
        {
            AttributesToSkip = 0,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false
        };

        var directories = Directory.EnumerateDirectories(stagingDirectory, "*", enumerationOptions)
            .Select(path => NormalizeArchivePath(stagingDirectory, path) + "/")
            .Order(StringComparer.Ordinal);
        foreach (var directory in directories)
        {
            var entry = new UstarTarEntry(TarEntryType.Directory, directory)
            {
                Gid = 0,
                GroupName = string.Empty,
                Mode = DirectoryMode,
                ModificationTime = s_archiveTimestamp,
                Uid = 0,
                UserName = string.Empty
            };
            await tarWriter.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        var files = Directory.EnumerateFiles(stagingDirectory, "*", enumerationOptions)
            .Select(path => (FullPath: path, ArchivePath: NormalizeArchivePath(stagingDirectory, path)))
            .OrderBy(item => item.ArchivePath, StringComparer.Ordinal);
        foreach (var file in files)
        {
            using var dataStream = File.OpenRead(file.FullPath);
            var isExecutable =
                file.ArchivePath is "install.sh" or "update.sh" or "remove.sh" ||
                string.Equals(file.ArchivePath, $"app/{entryPoint}", StringComparison.Ordinal) ||
                HasExecutableFileHeader(dataStream);
            var entry = new UstarTarEntry(TarEntryType.RegularFile, file.ArchivePath)
            {
                DataStream = dataStream,
                Gid = 0,
                GroupName = string.Empty,
                Mode = isExecutable ? ExecutableFileMode : RegularFileMode,
                ModificationTime = s_archiveTimestamp,
                Uid = 0,
                UserName = string.Empty
            };
            await tarWriter.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task WriteManagementFilesAsync(
        string stagingDirectory,
        string environmentName,
        string projectName,
        string entryPoint,
        IReadOnlyDictionary<string, string> environmentVariables,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);
        ArgumentNullException.ThrowIfNull(environmentVariables);
        ArgumentNullException.ThrowIfNull(arguments);

        var serviceName = $"aspire-{environmentName}.service";
        var environmentFileName = $"aspire-{environmentName}.env";
        var deploymentDirectory = $"/opt/aspire/{environmentName}";
        var execStart = new StringBuilder(EscapeSystemdExecArgument($"{deploymentDirectory}/app/{entryPoint}"));
        foreach (var argument in arguments)
        {
            execStart.Append(' ').Append(EscapeSystemdExecArgument(argument));
        }

        var unit = $$"""
            [Unit]
            Description=Aspire project {{projectName}}
            Wants=network-online.target
            After=network-online.target

            [Service]
            Type=simple
            User=aspire-app
            Group=aspire-app
            WorkingDirectory={{deploymentDirectory}}/app
            ExecStart={{execStart}}
            EnvironmentFile=/etc/aspire/{{environmentName}}.env
            Restart=always
            RestartSec=5
            NoNewPrivileges=true
            PrivateTmp=true
            ProtectHome=true
            ProtectSystem=full

            [Install]
            WantedBy=multi-user.target

            """;
        var install = $$"""
            #!/usr/bin/env bash
            set -euo pipefail

            source_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
            deployment_dir="{{deploymentDirectory}}"
            service_name="{{serviceName}}"
            service_user="aspire-app"

            systemctl stop "$service_name" 2>/dev/null || true
            if ! getent group "$service_user" >/dev/null; then
                groupadd --system "$service_user"
            fi
            if ! id -u "$service_user" >/dev/null 2>&1; then
                useradd --system --gid "$service_user" --home-dir "$deployment_dir" --no-create-home --shell /usr/sbin/nologin "$service_user"
            fi
            install -d -o root -g root -m 0755 "$deployment_dir"
            install -d -o root -g root -m 0755 /etc/aspire
            rm -rf "$deployment_dir/app"
            cp -a "$source_dir/app" "$deployment_dir/app"
            install -m 0755 "$source_dir/remove.sh" "$deployment_dir/remove.sh"
            install -o root -g root -m 0600 "$source_dir/{{environmentFileName}}" "/etc/aspire/{{environmentName}}.env"
            install -m 0644 "$source_dir/{{serviceName}}" "/etc/systemd/system/$service_name"
            systemctl daemon-reload
            systemctl enable "$service_name"
            systemctl restart "$service_name"

            """;
        var update = """
            #!/usr/bin/env bash
            set -euo pipefail

            source_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
            exec "$source_dir/install.sh"

            """;
        var remove = $$"""
            #!/usr/bin/env bash
            set -euo pipefail

            service_name="{{serviceName}}"
            systemctl disable --now "$service_name" 2>/dev/null || true
            rm -f "/etc/systemd/system/$service_name"
            rm -f "/etc/aspire/{{environmentName}}.env"
            systemctl daemon-reload

            """;
        var environmentFile = new StringBuilder();
        foreach (var (name, value) in environmentVariables.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            environmentFile
                .Append(name)
                .Append("=\"")
                .Append(EscapeSystemdEnvironmentValue(value))
                .AppendLine("\"");
        }

        await Task.WhenAll(
            File.WriteAllTextAsync(Path.Combine(stagingDirectory, serviceName), unit, new UTF8Encoding(false), cancellationToken),
            File.WriteAllTextAsync(Path.Combine(stagingDirectory, environmentFileName), environmentFile.ToString(), new UTF8Encoding(false), cancellationToken),
            File.WriteAllTextAsync(Path.Combine(stagingDirectory, "install.sh"), install, new UTF8Encoding(false), cancellationToken),
            File.WriteAllTextAsync(Path.Combine(stagingDirectory, "update.sh"), update, new UTF8Encoding(false), cancellationToken),
            File.WriteAllTextAsync(Path.Combine(stagingDirectory, "remove.sh"), remove, new UTF8Encoding(false), cancellationToken)).ConfigureAwait(false);
    }

    internal static async Task<VirtualMachineScaleSetWorkloadConfiguration> ResolveConfigurationAsync(
        DotnetProjectResource project,
        DistributedApplicationExecutionContext executionContext,
        IValueProvider workloadIdentityClientId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(workloadIdentityClientId);
        ArgumentNullException.ThrowIfNull(logger);

        var environmentVariables = new Dictionary<string, object>(StringComparer.Ordinal);
        if (project.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var environmentCallbacks))
        {
            var callbackContext = new EnvironmentCallbackContext(
                executionContext,
                project,
                environmentVariables,
                cancellationToken)
            {
                Logger = logger
            };
            foreach (var callback in environmentCallbacks)
            {
                await callback.Callback(callbackContext).ConfigureAwait(false);
            }
        }

        var arguments = new List<object>();
        if (project.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var argumentCallbacks))
        {
            var callbackContext = new CommandLineArgsCallbackContext(arguments, project, cancellationToken)
            {
                ExecutionContext = executionContext,
                Logger = logger
            };
            foreach (var callback in argumentCallbacks)
            {
                await callback.Callback(callbackContext).ConfigureAwait(false);
            }
        }

        RemoveDotnetRunScaffolding(arguments);

        var resolvedEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in environmentVariables)
        {
            if (!IsValidEnvironmentVariableName(name))
            {
                throw new DistributedApplicationException(
                    $"Environment variable '{name}' on project '{project.Name}' is not a valid Linux environment variable name.");
            }

            ThrowIfSecret(value, $"environment variable '{name}'", project.Name);
            if (await ResolveValueAsync(value, project, executionContext, cancellationToken).ConfigureAwait(false) is { } resolvedValue)
            {
                resolvedEnvironment[name] = resolvedValue;
            }
        }

        var identityClientId = await workloadIdentityClientId.GetValueAsync(
            new ValueProviderContext
            {
                Caller = project,
                ExecutionContext = executionContext
            },
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(identityClientId))
        {
            throw new DistributedApplicationException(
                $"The workload identity client ID for project '{project.Name}' could not be resolved.");
        }

        resolvedEnvironment.TryAdd("DOTNET_ENVIRONMENT", "Production");
        resolvedEnvironment["AZURE_CLIENT_ID"] = identityClientId;
        resolvedEnvironment["AZURE_TOKEN_CREDENTIALS"] = "ManagedIdentityCredential";

        var resolvedArguments = new List<string>(arguments.Count);
        foreach (var argument in arguments)
        {
            ThrowIfSecret(argument, "command-line argument", project.Name);
            if (await ResolveValueAsync(argument, project, executionContext, cancellationToken).ConfigureAwait(false) is { } resolvedArgument)
            {
                resolvedArguments.Add(resolvedArgument);
            }
        }

        return new VirtualMachineScaleSetWorkloadConfiguration(resolvedEnvironment, resolvedArguments);
    }

    private static void RemoveDotnetRunScaffolding(List<object> arguments)
    {
        // AddDotnetProject emits launcher arguments in this shape:
        //   run --project <path> [--configuration <configuration>] --no-launch-profile <application arguments>
        // The VM Application starts the published executable directly, so only arguments after the
        // dotnet-run prefix belong on the generated systemd ExecStart command.
        if (arguments.Count < 4 ||
            arguments[0] is not string { } verb ||
            !string.Equals(verb, "run", StringComparison.Ordinal) ||
            arguments[1] is not string projectOption ||
            projectOption is not ("--project" or "--file"))
        {
            return;
        }

        var launchProfileOptionIndex = arguments.FindIndex(
            argument => argument is string value && string.Equals(value, "--no-launch-profile", StringComparison.Ordinal));
        if (launchProfileOptionIndex >= 0)
        {
            arguments.RemoveRange(0, launchProfileOptionIndex + 1);
        }
    }

    private static async Task PublishProjectAsync(
        string projectPath,
        string outputDirectory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(projectPath),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("publish");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--runtime");
        startInfo.ArgumentList.Add(TargetRuntimeIdentifier);
        startInfo.ArgumentList.Add("--self-contained");
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("--nologo");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start 'dotnet publish' for project '{projectPath}'.");

        // Read both streams concurrently so a full redirected pipe cannot block the publish process.
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);
        logger.LogDebug("dotnet publish output for {ProjectPath}:{NewLine}{StandardOutput}", projectPath, Environment.NewLine, standardOutput);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'dotnet publish' failed for project '{projectPath}' with exit code {process.ExitCode}:{Environment.NewLine}{LimitOutput(standardError)}");
        }
    }

    private static string FindEntryPoint(string publishDirectory, string projectName)
    {
        var runtimeConfigFiles = Directory.GetFiles(
            publishDirectory,
            "*.runtimeconfig.json",
            SearchOption.TopDirectoryOnly);
        if (runtimeConfigFiles.Length != 1)
        {
            throw new InvalidOperationException(
                $"Published project '{projectName}' produced {runtimeConfigFiles.Length} runtime configuration files; exactly one is required.");
        }

        const string runtimeConfigSuffix = ".runtimeconfig.json";
        var runtimeConfigName = Path.GetFileName(runtimeConfigFiles[0]);
        var entryPoint = runtimeConfigName[..^runtimeConfigSuffix.Length];
        if (!File.Exists(Path.Combine(publishDirectory, entryPoint)))
        {
            throw new InvalidOperationException(
                $"The self-contained Linux executable '{entryPoint}' was not found in the publish output for project '{projectName}'.");
        }

        return entryPoint;
    }

    private static async Task<(string Hex, ulong Value)> ComputeFingerprintAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var hash = new XxHash3();
        using var stream = File.OpenRead(filePath);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.Append(buffer.AsSpan(0, bytesRead));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var bytes = hash.GetCurrentHash();
        return (Convert.ToHexString(bytes).ToLowerInvariant(), BinaryPrimitives.ReadUInt64BigEndian(bytes));
    }

    private static string CreateGalleryApplicationVersion(ulong fingerprint)
    {
        var minor = (fingerprint >> 31) & int.MaxValue;
        var patch = fingerprint & int.MaxValue;
        return $"1.{minor}.{patch}";
    }

    private static string NormalizeArchivePath(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool HasExecutableFileHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[4];
        var bytesRead = stream.Read(header);
        stream.Position = 0;

        return bytesRead >= 2 && header[0] == (byte)'#' && header[1] == (byte)'!' ||
            bytesRead == header.Length &&
            header.SequenceEqual((ReadOnlySpan<byte>)[0x7f, (byte)'E', (byte)'L', (byte)'F']);
    }

    private static async ValueTask<string?> ResolveValueAsync(
        object? value,
        IResource caller,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        var valueProvider = value switch
        {
            IValueProvider provider => provider,
            IResourceBuilder<IResource> builder when builder.Resource is IValueProvider provider => provider,
            _ => null
        };
        if (valueProvider is not null)
        {
            return await valueProvider.GetValueAsync(
                new ValueProviderContext
                {
                    Caller = caller,
                    ExecutionContext = executionContext
                },
                cancellationToken).ConfigureAwait(false);
        }

        return value switch
        {
            string text => text,
            null => null,
            _ => value.ToString()
        };
    }

    private static void ThrowIfSecret(object? value, string configurationDescription, string projectName)
    {
        if (ContainsSecret(value, []))
        {
            throw new DistributedApplicationException(
                $"The {configurationDescription} on project '{projectName}' contains a secret. " +
                "Azure Virtual Machine Scale Set compute environments do not support materializing secrets into VM Application packages.");
        }
    }

    private static bool ContainsSecret(object? value, HashSet<object> visited)
    {
        if (value is null || !visited.Add(value))
        {
            return false;
        }

        if (value is ParameterResource { Secret: true } or IAzureKeyVaultSecretReference)
        {
            return true;
        }

#pragma warning disable CS0618
        if (value is BicepSecretOutputReference)
        {
            return true;
        }
#pragma warning restore CS0618

        if (value is IResourceBuilder<IResource> builder)
        {
            return ContainsSecret(builder.Resource, visited);
        }

        return value is IValueWithReferences valueWithReferences &&
            valueWithReferences.References.Any(reference => ContainsSecret(reference, visited));
    }

    private static bool IsValidEnvironmentVariableName(string name)
    {
        if (name.Length == 0 || name[0] is not ('_' or >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
        {
            return false;
        }

        foreach (var character in name.AsSpan(1))
        {
            if (character is not ('_' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'))
            {
                return false;
            }
        }

        return true;
    }

    private static string EscapeSystemdExecArgument(string value)
        => $"\"{value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal)}\"";

    private static string EscapeSystemdEnvironmentValue(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

    private static string LimitOutput(string output)
    {
        const int maximumLength = 4_000;
        var trimmed = output.Trim();
        return trimmed.Length <= maximumLength ? trimmed : trimmed[^maximumLength..];
    }
}

internal sealed record VirtualMachineScaleSetApplicationPackage(
    string Path,
    string Fingerprint,
    string Version);

internal sealed record VirtualMachineScaleSetWorkloadConfiguration(
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    IReadOnlyList<string> Arguments);
