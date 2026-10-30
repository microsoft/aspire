using Aspire.Hosting.Pipelines;
using Azure.Identity;
using Azure.Provisioning;
using Azure.Provisioning.Storage;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using Pipelines.Library;

var builder = DistributedApplication.CreateBuilder(args);

builder.Pipeline.AddAppServiceZipDeploy();

var aasEnv = builder.AddAzureAppServiceEnvironment("appservice-env");

var acaEnv = builder.AddAzureContainerAppEnvironment("aca-env")
    .ConfigureInfrastructure(infra =>
    {
        var volumeStorageAccount = infra.GetProvisionableResources().OfType<StorageAccount>().SingleOrDefault();
        if (volumeStorageAccount == null)
        {
            return;
        }
        infra.Add(new ProvisioningOutput("STORAGE_VOLUME_ACCOUNT_NAME", typeof(string))
        {
            Value = volumeStorageAccount.Name
        });
        var fileShares = infra.GetProvisionableResources().OfType<Azure.Provisioning.Storage.FileShare>().ToList();
        for (var i = 0; i < fileShares.Count; i++)
        {
            var fileShare = fileShares[i];
            infra.Add(new ProvisioningOutput($"SHARES_{i}_NAME", typeof(string))
            {
                Value = fileShare.Name
            });
        }
    });

var withBindMount = builder.AddDockerfile("with-bind-mount", ".", "./Dockerfile.bindmount")
    .WithComputeEnvironment(acaEnv)
    .WithBindMount("../data", "/data");

// This step could also be modeled as a Bicep resource with the role assignment
// for the principalId associated with the deployment.
builder.Pipeline.AddStep("assign-storage-role", async (context) =>
{
    var resourcesWithBindMounts = context.Model.Resources
        .Where(r => r.TryGetContainerMounts(out var mounts) &&
                    mounts.Any(m => m.Type == ContainerMountType.BindMount))
        .ToList();

    if (resourcesWithBindMounts.Count == 0)
    {
        return;
    }

    var storageAccountName = await acaEnv.GetOutput("storagE_VOLUME_ACCOUNT_NAME").GetValueAsync();

    if (string.IsNullOrEmpty(storageAccountName))
    {
        return;
    }

    var assignRoleTask = await context.ReportingStep
        .CreateTaskAsync($"Granting file share access to current user", context.CancellationToken)
        .ConfigureAwait(false);

    await using (assignRoleTask.ConfigureAwait(false))
    {
        try
        {
            // Get the current signed-in user's object ID
            var getUserProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "az",
                Arguments = "ad signed-in-user show --query id -o tsv",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (getUserProcess == null)
            {
                await assignRoleTask.CompleteAsync(
                    "Failed to start az CLI process",
                    CompletionState.CompletedWithWarning,
                    context.CancellationToken).ConfigureAwait(false);
                return;
            }

            var userObjectId = await getUserProcess.StandardOutput.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
            userObjectId = userObjectId.Trim();

            await getUserProcess.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);

            if (getUserProcess.ExitCode != 0)
            {
                var error = await getUserProcess.StandardError.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
                await assignRoleTask.CompleteAsync(
                    $"Failed to get signed-in user: {error}",
                    CompletionState.CompletedWithWarning,
                    context.CancellationToken).ConfigureAwait(false);
                return;
            }

            // Get the current subscription ID
            var getSubscriptionProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "az",
                Arguments = "account show --query id -o tsv",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (getSubscriptionProcess == null)
            {
                await assignRoleTask.CompleteAsync(
                    "Failed to get subscription ID",
                    CompletionState.CompletedWithWarning,
                    context.CancellationToken).ConfigureAwait(false);
                return;
            }

            var subscriptionId = await getSubscriptionProcess.StandardOutput.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
            subscriptionId = subscriptionId.Trim();

            await getSubscriptionProcess.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);

            if (getSubscriptionProcess.ExitCode != 0)
            {
                var error = await getSubscriptionProcess.StandardError.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
                await assignRoleTask.CompleteAsync(
                    $"Failed to get subscription ID: {error}",
                    CompletionState.CompletedWithWarning,
                    context.CancellationToken).ConfigureAwait(false);
                return;
            }

            // Get the resource group for the storage account
            var getResourceGroupProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "az",
                Arguments = $"storage account show --name {storageAccountName} --query resourceGroup -o tsv",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (getResourceGroupProcess == null)
            {
                await assignRoleTask.CompleteAsync(
                    "Failed to get resource group",
                    CompletionState.CompletedWithWarning,
                    context.CancellationToken).ConfigureAwait(false);
                return;
            }

            var resourceGroup = await getResourceGroupProcess.StandardOutput.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
            resourceGroup = resourceGroup.Trim();

            await getResourceGroupProcess.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);

            if (getResourceGroupProcess.ExitCode != 0)
            {
                var error = await getResourceGroupProcess.StandardError.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
                await assignRoleTask.CompleteAsync(
                    $"Failed to get resource group: {error}",
                    CompletionState.CompletedWithWarning,
                    context.CancellationToken).ConfigureAwait(false);
                return;
            }

            // Build the scope for the storage account
            var scope = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Storage/storageAccounts/{storageAccountName}";

            // Assign the Storage File Data Privileged Contributor role
            var assignRoleProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "az",
                Arguments = $"role assignment create --role \"Storage File Data Privileged Contributor\" --assignee {userObjectId} --scope {scope}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (assignRoleProcess == null)
            {
                await assignRoleTask.CompleteAsync(
                    "Failed to start az CLI process for role assignment",
                    CompletionState.CompletedWithWarning,
                    context.CancellationToken).ConfigureAwait(false);
                return;
            }

            await assignRoleProcess.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);

            if (assignRoleProcess.ExitCode != 0)
            {
                var error = await assignRoleProcess.StandardError.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
                await assignRoleTask.CompleteAsync(
                    $"Failed to assign role: {error}",
                    CompletionState.CompletedWithWarning,
                    context.CancellationToken).ConfigureAwait(false);
                return;
            }

            await assignRoleTask.CompleteAsync(
                $"Successfully assigned Storage File Data Privileged Contributor role",
                CompletionState.Completed,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await assignRoleTask.CompleteAsync(
                $"Error assigning role: {ex.Message}",
                CompletionState.CompletedWithWarning,
                context.CancellationToken).ConfigureAwait(false);
        }
    }
}, requiredBy: "upload-bind-mounts", dependsOn: WellKnownPipelineTags.ProvisionInfrastructure);

builder.Pipeline.AddStep("upload-bind-mounts", async (deployingContext) =>
{
    var resourcesWithBindMounts = deployingContext.Model.Resources
        .Where(r => r.TryGetContainerMounts(out var mounts) &&
                    mounts.Any(m => m.Type == ContainerMountType.BindMount))
        .ToList();

    if (resourcesWithBindMounts.Count == 0)
    {
        return;
    }

    var totalUploads = 0;

    var storageAccountName = await acaEnv.GetOutput("storagE_VOLUME_ACCOUNT_NAME").GetValueAsync();
    var resource = withBindMount.Resource;

    if (!resource.TryGetContainerMounts(out var mounts))
    {
        return;
    }

    var bindMounts = mounts.Where(m => m.Type == ContainerMountType.BindMount).ToList();

    for (var i = 0; i < bindMounts.Count; i++)
    {
        var bindMount = bindMounts[i];
        var sourcePath = bindMount.Source;

        if (string.IsNullOrEmpty(sourcePath))
        {
            continue;
        }

        var fileShareName = await acaEnv.GetOutput($"shareS_{i}_NAME").GetValueAsync();

        var uploadTask = await deployingContext.ReportingStep
            .CreateTaskAsync($"Uploading {Path.GetFileName(sourcePath)} to {fileShareName}", deployingContext.CancellationToken)
            .ConfigureAwait(false);

        await using (uploadTask.ConfigureAwait(false))
        {
            if (!Directory.Exists(sourcePath))
            {
                await uploadTask.CompleteAsync(
                    $"Source path {sourcePath} does not exist",
                    CompletionState.CompletedWithWarning,
                    deployingContext.CancellationToken).ConfigureAwait(false);
                continue;
            }

            var files = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories);
            var fileCount = files.Length;

            var credential = new AzureCliCredential();
            var fileShareUri = new Uri($"https://{storageAccountName}.file.core.windows.net/{fileShareName}");

            var clientOptions = new ShareClientOptions
            {
                ShareTokenIntent = ShareTokenIntent.Backup
            };

            var shareClient = new ShareClient(fileShareUri, credential, clientOptions);

            foreach (var filePath in files)
            {
                var relativePath = Path.GetRelativePath(sourcePath, filePath);
                var directoryPath = Path.GetDirectoryName(relativePath) ?? string.Empty;

                var directoryClient = shareClient.GetRootDirectoryClient();

                if (!string.IsNullOrEmpty(directoryPath))
                {
                    var parts = directoryPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    foreach (var part in parts)
                    {
                        directoryClient = directoryClient.GetSubdirectoryClient(part);
                        await directoryClient.CreateIfNotExistsAsync(cancellationToken: deployingContext.CancellationToken).ConfigureAwait(false);
                    }
                }

                var fileName = Path.GetFileName(filePath);
                var fileClient = directoryClient.GetFileClient(fileName);

                using var fileStream = File.OpenRead(filePath);
                await fileClient.CreateAsync(fileStream.Length, cancellationToken: deployingContext.CancellationToken).ConfigureAwait(false);
                await fileClient.UploadAsync(fileStream, cancellationToken: deployingContext.CancellationToken).ConfigureAwait(false);
            }

            await uploadTask.CompleteAsync(
                $"Successfully uploaded {fileCount} file(s) from {sourcePath}",
                CompletionState.Completed,
                deployingContext.CancellationToken).ConfigureAwait(false);

            totalUploads += fileCount;
        }
    }
}, requiredBy: WellKnownPipelineTags.DeployCompute, dependsOn: WellKnownPipelineTags.ProvisionInfrastructure);

builder.AddProject<Projects.Publishers_ApiService>("api-service")
    .WithComputeEnvironment(aasEnv)
    .WithExternalHttpEndpoints();

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();
