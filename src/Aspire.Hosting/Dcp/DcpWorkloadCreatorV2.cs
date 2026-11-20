// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIRECONTAINERSHELLEXECUTION001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREEXTENSION001

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using V2 = Aspire.Hosting.Dcp.Model.V2;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Renders resolved app-model launch configuration into plain V2 workloads.
/// </summary>
internal sealed class DcpWorkloadCreatorV2(
    DcpNameGenerator nameGenerator,
    ExecutableConfigurationResolver executableConfigurationResolver,
    DistributedApplicationExecutionContext executionContext,
    DistributedApplicationOptions applicationOptions,
    IConfiguration configuration,
    IProcessRunner processRunner)
{
    public IEnumerable<DcpWorkloadV2> Prepare(DistributedApplicationModel model, string namespaceName)
    {
        foreach (var resource in model.GetContainerResources().Cast<IResource>()
            .Concat(model.GetProjectResources()).Concat(model.GetExecutableResources())
            .Concat(model.GetContainerExecutableResources()).Distinct())
        {
            if (resource.HasAnnotationOfType<TerminalAnnotation>())
            {
                throw new DistributedApplicationException($"Resource '{resource.Name}' uses terminal execution, which is not supported by the DCP V2 pipeline. Remove WithTerminal or disable DcpPublisher:UseV2.");
            }

            resource.AddLifeCycleCommands();
            nameGenerator.EnsureDcpInstancesPopulated(resource);
            if (!resource.TryGetInstances(out var instances))
            {
                throw new DistributedApplicationException($"Resource '{resource.Name}' has no DCP instances.");
            }

            foreach (var instance in instances)
            {
                CustomResource rendered = resource switch
                {
                    IResource container when container.IsContainer() => V2.LogicalContainer.Create(instance.Name, namespaceName),
                    ContainerExecutableResource exec => V2.LogicalContainerExec.Create(
                        instance.Name, namespaceName, exec.TargetContainerResource!.GetResolvedResourceName()),
                    _ => V2.LogicalProcess.Create(instance.Name, namespaceName)
                };
                rendered.Annotate(CustomResource.ResourceNameAnnotation, resource.Name);
                rendered.Annotate(CustomResource.OtelServiceNameAnnotation, resource.Name);
                rendered.Annotate(CustomResource.OtelServiceInstanceIdAnnotation, resource.GetOtelServiceInstanceId(instance));
                rendered.Annotate(CustomResource.ResourceReplicaCount, instances.Length.ToString(CultureInfo.InvariantCulture));
                rendered.Annotate(CustomResource.ResourceReplicaIndex, instance.Index.ToString(CultureInfo.InvariantCulture));
                DcpModelUtilities.SetInitialResourceState(resource, rendered);
                yield return new(resource, rendered);
            }
        }
    }

    public async Task ConfigureAsync(DcpWorkloadV2 workload, ILogger logger, CancellationToken cancellationToken)
    {
        switch (workload.DcpResource)
        {
            case V2.LogicalProcess process:
                await ConfigureProcessAsync(workload, process, logger, cancellationToken).ConfigureAwait(false);
                break;
            case V2.LogicalContainer container:
                await ConfigureContainerAsync(workload, container, logger, cancellationToken).ConfigureAwait(false);
                break;
            case V2.LogicalContainerExec exec:
                var config = await ExecutionConfigurationBuilder.Create(workload.ModelResource)
                    .WithArgumentsConfig().WithEnvironmentVariablesConfig()
                    .BuildAsync(executionContext, logger, cancellationToken).ConfigureAwait(false);
                EnsureConfiguration(workload, config);
                var resource = (ContainerExecutableResource)workload.ModelResource;
                exec.Spec.Execution = new()
                {
                    Command = resource.Command,
                    WorkingDirectory = resource.WorkingDirectory,
                    Args = [.. resource.Args ?? [], .. config.Arguments.Select(a => a.Value)],
                    Env = config.EnvironmentVariables.Select(e => new V2.EnvVar { Name = e.Key, Value = e.Value }).ToList()
                };
                var initialArgs = resource.Args ?? [];
                exec.SetAnnotationAsObjectList(CustomResource.ResourceAppArgsAnnotation,
                    initialArgs.Select((a, index) => new AppLaunchArgumentAnnotation(a, false, index))
                        .Concat(config.Arguments.Select((a, index) => new AppLaunchArgumentAnnotation(a.Value, a.IsSensitive, initialArgs.Count + index))));
                break;
            default:
                throw new InvalidOperationException($"Unexpected V2 workload '{workload.DcpResource.Kind}'.");
        }
    }

    private async Task ConfigureProcessAsync(DcpWorkloadV2 workload, V2.LogicalProcess process, ILogger logger, CancellationToken cancellationToken)
    {
        var resolved = await executableConfigurationResolver.ResolveAsync(
            workload.ModelResource, workload.DcpResourceName, logger, EmptyConfigurationGatherer.Instance, cancellationToken).ConfigureAwait(false);
        EnsureConfiguration(workload, resolved.Configuration);
        var plan = await ExecutableLaunchPlanner.ResolveAsync(
            workload.ModelResource, resolved.Configuration, configuration, applicationOptions,
            new(ExecutableLaunchMechanism.Process, ExecutableLaunchMode.NoDebug),
            logger, cancellationToken).ConfigureAwait(false);

        process.Spec.Process = new()
        {
            ExecutablePath = plan.Command,
            WorkingDirectory = plan.WorkingDirectory,
            Args = plan.Arguments?.ToList(),
            Env = plan.EnvironmentVariables.Select(e => new V2.EnvVar { Name = e.Key, Value = e.Value }).ToList()
        };
        process.Spec.Mode = workload.ModelResource.HasPersistentLifetime() ? V2.LogicalProcessMode.Persistent : V2.LogicalProcessMode.Session;
        if (workload.ModelResource.TryGetParentProcessLifetime(out var pid, out var timestamp))
        {
            process.Spec.MonitorPid = pid;
            process.Spec.MonitorTimestamp = timestamp;
        }

        process.SetAnnotationAsObjectList(CustomResource.ResourceAppArgsAnnotation,
            plan.DisplayArguments.Select(a => new AppLaunchArgumentAnnotation(a.Value, a.IsSensitive, a.EffectiveArgumentIndex)));
        // V2 processes do not have V1's pemCertificates field. Materialize the public trust files
        // at the paths already selected by the shared configuration resolver.
        if (resolved.Certificates is { Count: > 0 } certificates &&
            resolved.Configuration.TryGetAdditionalData<CertificateTrustExecutionConfigurationData>(out _))
        {
            await WriteProcessCertificatesAsync(resolved.CertificatesRootDirectory, certificates, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ConfigureContainerAsync(DcpWorkloadV2 workload, V2.LogicalContainer container, ILogger logger, CancellationToken cancellationToken)
    {
        var resource = workload.ModelResource;
        if (!resource.TryGetContainerImageName(out var imageName))
        {
            throw new DistributedApplicationException($"Container '{resource.Name}' has no image.");
        }

        var resolved = await ContainerConfigurationResolver.ResolveAsync(resource, executionContext, logger, cancellationToken).ConfigureAwait(false);
        var config = resolved.Configuration;
        EnsureConfiguration(workload, config);
        var image = new V2.PhysicalContainerImageConfig { Image = imageName };
        if (resource.TryGetContainerImagePullPolicy(out var pullPolicy))
        {
            image.PullPolicy = pullPolicy switch
            {
                ImagePullPolicy.Default => null,
                ImagePullPolicy.Always => V2.ImagePullPolicy.Always,
                ImagePullPolicy.Missing => V2.ImagePullPolicy.Missing,
                ImagePullPolicy.Never => V2.ImagePullPolicy.Never,
                _ => throw new InvalidOperationException($"Unknown image pull policy '{pullPolicy}'.")
            };
        }
        image.Build = await BuildImageAsync(resource, logger, cancellationToken).ConfigureAwait(false);
        var arguments = config.Arguments.Select(a => a.Value).ToList();
        container.Spec.Container = new()
        {
            Image = image,
            Entrypoint = (resource as ContainerResource)?.Entrypoint,
            Command = resource is ContainerResource { ShellExecution: true } ? ["-c", string.Join(' ', arguments)] : arguments,
            Env = config.EnvironmentVariables.Select(e => new V2.EnvVar { Name = e.Key, Value = e.Value }).ToList(),
            Networks =
            [
                new()
                {
                    NetworkRef = KnownNetworkIdentifiers.DefaultAspireContainerNetwork.Value,
                    Aliases = resource.Annotations.OfType<ContainerNetworkAliasAnnotation>()
                        .Select(a => a.Network == KnownNetworkIdentifiers.DefaultAspireContainerNetwork
                            ? a.Alias
                            : throw new DistributedApplicationException("Custom container networks are not supported."))
                        .Prepend($"{resource.Name}.dev.internal").Prepend(resource.Name).ToList()
                }
            ],
            VolumeMounts = resource.Annotations.OfType<ContainerMountAnnotation>().Select(m => new V2.VolumeMount
            {
                Type = m.Type == ContainerMountType.BindMount ? V2.VolumeMountType.Bind : V2.VolumeMountType.Volume,
                Source = m.Type == ContainerMountType.BindMount ? m.Source : null,
                VolumeRef = m.Type == ContainerMountType.Volume
                    ? DcpNameGenerator.GetContainerVolumeName(GetRuntimeVolumeName(workload, m)) : null,
                Target = m.Target,
                ReadOnly = m.IsReadOnly
            }).ToList(),
            Labels = await BuildLabelsAsync(resource, logger, cancellationToken).ConfigureAwait(false),
            CreateFiles = await BuildFilesAsync(resource, resolved, cancellationToken).ConfigureAwait(false)
        };
        container.Spec.ContainerName = workload.DcpResourceName;
        container.Spec.Mode = resource.HasPersistentLifetime() ? V2.LogicalContainerMode.Persistent : V2.LogicalContainerMode.Session;
        if (resource.TryGetParentProcessLifetime(out var pid, out var timestamp))
        {
            container.Spec.MonitorPid = pid;
            container.Spec.MonitorTimestamp = timestamp;
        }
        SetDisplayArguments(container, config, resource is ContainerResource { ShellExecution: true } ? null : 0);
    }

    private async Task<V2.ContainerBuildContext?> BuildImageAsync(IResource resource, ILogger logger, CancellationToken cancellationToken)
    {
        if (resource.Annotations.OfType<DockerfileBuildAnnotation>().SingleOrDefault() is not { } build)
        {
            return null;
        }
        await DockerfileHelper.ExecuteDockerfileFactoryAsync(build, resource, executionContext.Services, cancellationToken).ConfigureAwait(false);
        var arguments = new List<V2.EnvVar>();
        foreach (var (key, value) in build.BuildArguments)
        {
            var resolved = value switch
            {
                IValueProvider provider => await provider.GetValueAsync(cancellationToken).ConfigureAwait(false),
                bool boolean => boolean ? "true" : "false",
                _ => value?.ToString()
            };
            if (resolved is not null)
            {
                arguments.Add(new() { Name = key, Value = resolved });
            }
        }
        var secrets = new List<V2.ContainerBuildSecret>();
        foreach (var (key, value) in build.BuildSecrets)
        {
            secrets.Add(value switch
            {
                FileInfo file => new() { Id = key, Type = V2.BuildSecretType.File, Source = file.FullName },
                IValueProvider provider => new()
                {
                    Id = key, Type = V2.BuildSecretType.Env,
                    Value = await provider.GetValueAsync(cancellationToken).ConfigureAwait(false)
                },
                _ => throw new DistributedApplicationException("Build secrets must be parameters or files.")
            });
        }
        var options = await resource.ProcessContainerBuildOptionsCallbackAsync(
            executionContext.Services, logger, executionContext, cancellationToken).ConfigureAwait(false);
        return new()
        {
            Context = build.ContextPath, Dockerfile = build.DockerfilePath, Stage = build.Stage,
            Args = arguments, Secrets = secrets,
            Platform = options.TargetPlatform is { } platform ? ContainerCreator.ToDcpPlatformString(platform) : null
        };
    }

    private async Task<List<V2.Label>> BuildLabelsAsync(IResource resource, ILogger logger, CancellationToken cancellationToken)
    {
        var arguments = new List<string>();
        await resource.ProcessContainerRuntimeArgValues(executionContext, (value, error) =>
        {
            if (error is not null)
            {
                throw new FailedToApplyEnvironmentException($"Failed to resolve container runtime arguments for '{resource.Name}'.", error);
            }
            if (value is string text)
            {
                arguments.Add(text);
            }
        }, logger, cancellationToken).ConfigureAwait(false);
        var labels = new List<V2.Label>();
        // Runtime arguments arrive as ["--label", "key=value"] or ["--label=key=value"].
        // V2 has typed labels but no arbitrary runtime CLI argument escape hatch.
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            string label;
            if (argument is "--label" or "-l" && index + 1 < arguments.Count)
            {
                label = arguments[++index];
            }
            else if (argument.StartsWith("--label=", StringComparison.Ordinal))
            {
                label = argument["--label=".Length..];
            }
            else
            {
                throw new DistributedApplicationException($"Container '{resource.Name}' uses runtime arguments that have no DCP V2 equivalent. Only --label is supported; disable DcpPublisher:UseV2 to use other runtime arguments.");
            }
            var separator = label.IndexOf('=');
            var key = separator < 0 ? label : label[..separator];
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new DistributedApplicationException($"Container '{resource.Name}' has an empty label key.");
            }
            labels.Add(new() { Key = key, Value = separator < 0 ? "" : label[(separator + 1)..] });
        }
        return labels;
    }

    private async Task<List<V2.CreateFileSystem>> BuildFilesAsync(
        IResource resource, ContainerConfigurationResult resolved, CancellationToken cancellationToken)
    {
        var result = new List<V2.CreateFileSystem>();
        var config = resolved.Configuration;
        config.TryGetAdditionalData<HttpsCertificateExecutionConfigurationData>(out var https);
        var privatePath = $"{resolved.CertificatesDestination}/private";
        var httpsContext = https is null ? null : new ContainerFileSystemCallbackHttpsCertificateContext
        {
            CertificatePath = ReferenceExpression.Create($"{privatePath}/{https.Certificate.Thumbprint}.crt"),
            KeyPath = https.KeyPathReference, CertificateWithKeyPath = https.CertificateWithKeyPathReference,
            PfxPath = https.PfxPathReference, Password = https.Password
        };
        foreach (var annotation in resource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>())
        {
            var items = await annotation.Callback(new()
            {
                Model = resource, Services = executionContext.Services, HttpsCertificateContext = httpsContext
            }, cancellationToken).ConfigureAwait(false);
            if (items is not null)
            {
                result.Add(new()
                {
                    Destination = annotation.DestinationPath, DefaultOwner = annotation.DefaultOwner ?? 0,
                    DefaultGroup = annotation.DefaultGroup ?? 0, Umask = (uint?)annotation.Umask,
                    Entries = items.Select(item => ConvertEntry(item.ToContainerFileSystemEntry())).ToList()
                });
            }
        }
        if (config.TryGetAdditionalData<CertificateTrustExecutionConfigurationData>(out var trust) &&
            trust.Scope != CertificateTrustScope.None && trust.Certificates.Count > 0)
        {
            var certificates = CertificateUtilities.BuildPemCertificateList(trust.Certificates);
            var bundle = string.Join('\n', certificates.Select(c => c.Contents));
            result.Add(new()
            {
                Destination = resolved.CertificatesDestination,
                Entries =
                [
                    new() { Name = "cert.pem", Contents = bundle, Type = V2.FileSystemEntryType.File },
                    new()
                    {
                        Name = "certs", Type = V2.FileSystemEntryType.Directory,
                        Entries = certificates.Select(c => new V2.FileSystemEntry
                        {
                            Name = $"{c.Thumbprint}.pem", Contents = c.Contents, Type = V2.FileSystemEntryType.OpenSSL
                        }).ToList()
                    }
                ]
            });
            if (trust.Scope != CertificateTrustScope.Append)
            {
                foreach (var path in resolved.DefaultCertificateBundlePaths)
                {
                    result.Add(new()
                    {
                        Destination = Path.GetDirectoryName(path)!.Replace('\\', '/'),
                        Entries = [new() { Name = Path.GetFileName(path), Contents = bundle, Type = V2.FileSystemEntryType.File, ContinueOnError = true }]
                    });
                }
            }
            var bundles = new List<V2.FileSystemEntry>();
            foreach (var factory in trust.CustomBundlesFactories)
            {
                bundles.Add(new()
                {
                    Name = factory.Key, Type = V2.FileSystemEntryType.File,
                    RawContents = Convert.ToBase64String(await factory.Value(trust.Certificates, cancellationToken).ConfigureAwait(false))
                });
            }
            if (bundles.Count > 0)
            {
                result.Add(new()
                {
                    Destination = $"{resolved.CertificatesDestination}/bundles", Entries = bundles
                });
            }
        }
        if (https is not null)
        {
            var thumbprint = https.Certificate.Thumbprint;
            var certificate = https.Certificate.ExportCertificatePem();
            var (key, pfx) = await DeveloperCertificateService.GetKeyMaterialAsync(
                https.Certificate, https.Password,
                https.IsKeyPathReferenced || https.IsCertificateWithKeyPathReferenced,
                https.IsPfxPathReferenced, cancellationToken).ConfigureAwait(false);
            var entries = new List<V2.FileSystemEntry>
            {
                new() { Name = $"{thumbprint}.crt", Contents = certificate, Type = V2.FileSystemEntryType.File }
            };
            if (key is not null)
            {
                entries.Add(new() { Name = $"{thumbprint}.key", Contents = new(key), Type = V2.FileSystemEntryType.File });
                if (https.IsCertificateWithKeyPathReferenced)
                {
                    entries.Add(new() { Name = $"{thumbprint}.pem", Contents = new([.. key, '\n', .. certificate]), Type = V2.FileSystemEntryType.File });
                }
                Array.Clear(key);
            }
            if (pfx is not null)
            {
                entries.Add(new() { Name = $"{thumbprint}.pfx", RawContents = Convert.ToBase64String(pfx), Type = V2.FileSystemEntryType.File });
                Array.Clear(pfx);
            }
            result.Add(new() { Destination = privatePath, Entries = entries, Umask = 0x3f });
        }
        return result;
    }

    private static V2.FileSystemEntry ConvertEntry(ContainerFileSystemEntry entry) => new()
    {
        Type = entry.Type, Name = entry.Name ?? throw new InvalidOperationException("File system entries must have names."),
        Owner = entry.Owner, Group = entry.Group,
        Mode = (uint)entry.Mode, Source = entry.Source, Contents = entry.Contents, RawContents = entry.RawContents,
        ContinueOnError = entry.ContinueOnError ?? false, Entries = entry.Entries?.Select(ConvertEntry).ToList()
    };

    private static void EnsureConfiguration(DcpWorkloadV2 workload, IExecutionConfigurationResult configuration)
    {
        if (configuration.Exception is { } error)
        {
            throw new FailedToApplyEnvironmentException($"Failed to apply configuration to '{workload.ModelResource.Name}'.", error);
        }
    }

    internal static string GetRuntimeVolumeName(DcpWorkloadV2 workload, ContainerMountAnnotation mount) =>
        string.IsNullOrEmpty(mount.Source)
            ? $"{workload.DcpResourceName}-{DcpNameGenerator.GetContainerVolumeName(mount.Target)}"
            : mount.Source;

    private static void SetDisplayArguments(CustomResource resource, IExecutionConfigurationResult configuration, int? argumentOffset)
    {
        resource.SetAnnotationAsObjectList(CustomResource.ResourceAppArgsAnnotation,
            configuration.Arguments.Select((a, index) => new AppLaunchArgumentAnnotation(a.Value, a.IsSensitive, argumentOffset + index)));
    }

    private async Task WriteProcessCertificatesAsync(
        string root, List<PemCertificate> certificates, CancellationToken cancellationToken)
    {
        // The resolver supplies absolute process trust paths, not DCP templates.
        ExecutableConfigurationResolver.CreatePrivateDirectory(root);
        var directory = Path.Join(root, "certs");
        ExecutableConfigurationResolver.CreatePrivateDirectory(directory);
        foreach (var previous in Directory.EnumerateFiles(directory))
        {
            File.Delete(previous);
        }
        await File.WriteAllTextAsync(Path.Join(root, "cert.pem"), string.Join('\n', certificates.Select(c => c.Contents)), cancellationToken).ConfigureAwait(false);
        foreach (var certificate in certificates)
        {
            var fileName = $"{certificate.Thumbprint}.pem";
            var filePath = Path.Join(directory, fileName);
            await File.WriteAllTextAsync(filePath, certificate.Contents, cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                // OpenSSL SSL_CERT_DIR requires X509_NAME_hash filenames, e.g. "9a5ba575.0".
                // The canonical-name hash is defined by OpenSSL, not a hash of the certificate DER.
                // https://docs.openssl.org/3.0/man1/openssl-rehash/
                var result = await processRunner.RunAndCaptureTextAsync(
                    "openssl", ["x509", "-hash", "-noout", "-in", filePath], directory,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var hash = result.StandardOutput.Trim();
                if (result.ExitCode != 0 || hash.Length != 8 || !hash.All(Uri.IsHexDigit))
                {
                    throw new DistributedApplicationException($"Could not prepare OpenSSL certificate directory: {result.StandardError}");
                }
                var index = 0;
                string destination;
                do
                {
                    destination = Path.Join(directory, $"{hash}.{index++}");
                }
                while (File.Exists(destination));
                File.Copy(filePath, destination);
            }
        }
    }

    private sealed class EmptyConfigurationGatherer : IExecutionConfigurationGatherer
    {
        public static EmptyConfigurationGatherer Instance { get; } = new();
        public ValueTask GatherAsync(IExecutionConfigurationGathererContext context, IResource resource, ILogger resourceLogger,
            DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
