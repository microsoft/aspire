#pragma warning disable ASPIREAZURE001

using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Azure.Core;
using Azure.Identity;
using Azure.Provisioning;

namespace Pipelines.Library;

public static class DistributedApplicationPipelineExtensions
{
    public static IDistributedApplicationPipeline AddAppServiceZipDeploy(this IDistributedApplicationPipeline pipeline)
    {
        pipeline.AddStep("app-service-zip-deploy", async context =>
        {
            var appServiceEnvironments = context.Model.Resources.OfType<AzureAppServiceEnvironmentResource>();
            if (!appServiceEnvironments.Any())
            {
                return;
            }

            foreach (var appServiceEnvironment in appServiceEnvironments)
            {
                foreach (var resource in context.Model.GetComputeResources())
                {
                    var annotation = resource.GetDeploymentTargetAnnotation();
                    if (annotation != null &&
                        annotation.ComputeEnvironment == appServiceEnvironment &&
                        annotation.DeploymentTarget is AzureAppServiceWebSiteResource websiteResource)
                    {
                        if (resource is not ProjectResource projectResource)
                        {
                            continue;
                        }

                        await DeployProjectToAppServiceAsync(
                            context,
                            projectResource,
                            websiteResource,
                            appServiceEnvironment,
                            context.CancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }, dependsOn: WellKnownPipelineTags.DeployCompute);

        return pipeline;
    }

    private static async Task DeployProjectToAppServiceAsync(
        PipelineStepContext context,
        ProjectResource projectResource,
        AzureAppServiceWebSiteResource websiteResource,
        AzureAppServiceEnvironmentResource appServiceEnvironment,
        CancellationToken cancellationToken)
    {
        var projectMetadata = projectResource.GetProjectMetadata();
        var projectPath = projectMetadata.ProjectPath;

        var publishTask = await context.ReportingStep.CreateTaskAsync($"Publishing {projectResource.Name}", cancellationToken).ConfigureAwait(false);
        await using (publishTask.ConfigureAwait(false))
        {
            var publishDir = Path.Combine(Path.GetTempPath(), $"aspire-publish-{Guid.NewGuid()}");
            Directory.CreateDirectory(publishDir);

            try
            {
                var publishProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"publish \"{projectPath}\" -c Release -o \"{publishDir}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (publishProcess == null)
                {
                    await publishTask.CompleteAsync(
                        "Failed to start dotnet publish",
                        CompletionState.CompletedWithError,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                await publishProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                if (publishProcess.ExitCode != 0)
                {
                    var error = await publishProcess.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                    await publishTask.CompleteAsync(
                        $"Publish failed: {error}",
                        CompletionState.CompletedWithError,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                await publishTask.CompleteAsync(
                    "Publish completed",
                    CompletionState.Completed,
                    cancellationToken).ConfigureAwait(false);

                var zipTask = await context.ReportingStep.CreateTaskAsync($"Creating deployment package", cancellationToken).ConfigureAwait(false);
                await using (zipTask.ConfigureAwait(false))
                {
                    var zipPath = Path.Combine(Path.GetTempPath(), $"aspire-deploy-{Guid.NewGuid()}.zip");

                    ZipFile.CreateFromDirectory(publishDir, zipPath);

                    await zipTask.CompleteAsync(
                        "Deployment package created",
                        CompletionState.Completed,
                        cancellationToken).ConfigureAwait(false);

                    var uploadTask = await context.ReportingStep.CreateTaskAsync($"Uploading to {projectResource.Name}", cancellationToken).ConfigureAwait(false);
                    await using (uploadTask.ConfigureAwait(false))
                    {
                        try
                        {
                            var siteName = websiteResource.Outputs[$"{Infrastructure.NormalizeBicepIdentifier(websiteResource.Name)}_name"]?.ToString();
                            if (string.IsNullOrEmpty(siteName))
                            {
                                siteName = appServiceEnvironment.Outputs["name"]?.ToString();
                            }

                            if (string.IsNullOrEmpty(siteName))
                            {
                                await uploadTask.CompleteAsync(
                                    "Could not determine website name",
                                    CompletionState.CompletedWithError,
                                    cancellationToken).ConfigureAwait(false);
                                return;
                            }

                            var credential = new AzureCliCredential();
                            var tokenRequestContext = new TokenRequestContext(["https://management.azure.com/.default"]);
                            var accessToken = await credential.GetTokenAsync(tokenRequestContext, cancellationToken).ConfigureAwait(false);

                            var kuduUrl = $"https://{siteName}.scm.azurewebsites.net/api/zipdeploy";

                            using var httpClient = new HttpClient();
                            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
                            httpClient.Timeout = TimeSpan.FromMinutes(30);

                            await using var zipStream = File.OpenRead(zipPath);
                            using var content = new StreamContent(zipStream);
                            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

                            var response = await httpClient.PostAsync(kuduUrl, content, cancellationToken).ConfigureAwait(false);

                            if (!response.IsSuccessStatusCode)
                            {
                                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                                await uploadTask.CompleteAsync(
                                    $"Upload failed: {response.StatusCode} - {errorContent}",
                                    CompletionState.CompletedWithError,
                                    cancellationToken).ConfigureAwait(false);
                                return;
                            }

                            await uploadTask.CompleteAsync(
                                "Upload completed successfully",
                                CompletionState.Completed,
                                cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            if (File.Exists(zipPath))
                            {
                                File.Delete(zipPath);
                            }
                        }
                    }
                }
            }
            finally
            {
                if (Directory.Exists(publishDir))
                {
                    Directory.Delete(publishDir, recursive: true);
                }
            }
        }
    }
}
