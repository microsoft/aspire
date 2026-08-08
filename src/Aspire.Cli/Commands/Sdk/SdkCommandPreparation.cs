// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Cli.Commands.Sdk;

/// <summary>
/// The setup that <c>sdk dump</c> and <c>sdk export</c> both need before they can ask an AppHost
/// server anything: turning command-line integration arguments into references, standing up a
/// throwaway scanner AppHost, and working out which assemblies the caller actually asked about.
/// </summary>
/// <remarks>
/// Only the preparation is shared. The two commands ask different questions of the server and
/// serialize the answers differently, and deliberately keeping that apart is what stops
/// <c>sdk dump</c> from quietly becoming an alias for the canonical export.
/// </remarks>
internal static class SdkCommandPreparation
{
    /// <summary>
    /// Parses one integration argument, which is either a path to a <c>.csproj</c> or a package
    /// reference in <c>PackageName@Version</c> form (for example <c>Aspire.Hosting.Redis@13.5.0</c>).
    /// </summary>
    /// <param name="argument">The raw command-line argument.</param>
    /// <param name="requireExactVersion">
    /// When <see langword="true"/>, floating and range versions are rejected. Callers that publish
    /// artifacts keyed on the version need this; a document published under <c>13.5.*</c> would
    /// describe a different SDK after the next restore.
    /// </param>
    /// <param name="reference">The parsed reference when parsing succeeds.</param>
    /// <param name="errorExitCode">The exit code to return when parsing fails.</param>
    /// <param name="errorMessage">The user-facing failure reason when parsing fails.</param>
    /// <returns><see langword="true"/> when the argument was parsed.</returns>
    public static bool TryParseIntegrationArgument(
        string argument,
        bool requireExactVersion,
        out IntegrationReference? reference,
        out int errorExitCode,
        out string? errorMessage)
    {
        reference = null;
        errorExitCode = CliExitCodes.InvalidCommand;
        errorMessage = null;

        if (argument.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var projectFile = new FileInfo(argument);
            if (!projectFile.Exists)
            {
                errorExitCode = CliExitCodes.FailedToFindProject;
                errorMessage = $"Integration project not found: {projectFile.FullName}";
                return false;
            }

            reference = IntegrationReference.FromProject(
                IntegrationAssemblyNameResolver.Resolve(projectFile),
                projectFile.FullName);
            return true;
        }

        if (!argument.Contains('@'))
        {
            errorMessage = $"Invalid integration argument '{argument}'. Expected a .csproj path or PackageName@Version format.";
            return false;
        }

        var atIndex = argument.LastIndexOf('@');
        var packageName = argument[..atIndex];
        var packageVersion = argument[(atIndex + 1)..];

        if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(packageVersion) || packageName.Contains('@'))
        {
            errorMessage = $"Invalid package format '{argument}'. Expected PackageName@Version (e.g. Aspire.Hosting.Redis@9.2.0).";
            return false;
        }

        if (!SemVersion.TryParse(packageVersion, SemVersionStyles.Any, out _))
        {
            errorMessage = requireExactVersion
                ? $"Invalid version '{packageVersion}' in '{argument}'. Expected an exact NuGet version (e.g. 9.2.0); floating and range versions are not supported."
                : $"Invalid version '{packageVersion}' in '{argument}'. Expected a valid NuGet version (e.g. 9.2.0).";
            return false;
        }

        reference = IntegrationReference.FromPackage(packageName, packageVersion);
        return true;
    }

    /// <summary>
    /// Finds the first assembly name that more than one integration resolves to.
    /// </summary>
    /// <param name="integrations">The parsed integration references.</param>
    /// <returns>The duplicated assembly name, or <see langword="null"/> when there is none.</returns>
    public static string? FindDuplicateAssemblyName(IReadOnlyList<IntegrationReference> integrations)
        => integrations
            .GroupBy(integration => integration.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;

    /// <summary>
    /// Gets the exporting assembly names to scope a server query to, or <see langword="null"/> when
    /// the caller asked for everything.
    /// </summary>
    /// <param name="integrations">The parsed integration references.</param>
    public static string[]? GetExportingAssemblyNames(IReadOnlyList<IntegrationReference> integrations)
        => integrations.Count > 0
            ? [.. integrations.Select(integration => integration.Name).Distinct(StringComparer.OrdinalIgnoreCase)]
            : null;

    /// <summary>
    /// Builds and starts a throwaway AppHost server that has the requested integrations restored, and
    /// returns a connected RPC client.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="PreparedSdkSession"/> owns the temporary directory and the server
    /// session; disposing it tears both down. Build failures are reported through
    /// <paramref name="interactionService"/> and surface as a null session rather than an exception,
    /// because a failed restore is a user-facing outcome and not a bug.
    /// </remarks>
    /// <param name="appHostServerProjectFactory">Creates the scanner AppHost for the temporary directory.</param>
    /// <param name="serverSessionFactory">Creates the session that runs the scanner AppHost.</param>
    /// <param name="interactionService">Reports build failures and rejections to the user.</param>
    /// <param name="logger">Receives diagnostic detail about the preparation.</param>
    /// <param name="tempDirectoryPrefix">Prefix for the throwaway project directory.</param>
    /// <param name="sdkVersion">The Aspire SDK version the scanner AppHost is restored at.</param>
    /// <param name="integrations">The integrations to restore into the scanner AppHost.</param>
    /// <param name="packageSourceOverride">A NuGet source to prefer, or <see langword="null"/> for the configured sources.</param>
    /// <param name="validateProject">
    /// A pre-flight check run against the created server project before anything is restored, or
    /// <see langword="null"/> when the caller has nothing to check. Returning a message rejects the
    /// request and reports it through <paramref name="interactionService"/>. This exists because the
    /// factory only decides between the repository and prebuilt servers once the project is created,
    /// and <c>sdk export</c> has to refuse a package the repository server would build from the
    /// current checkout instead of restoring at the requested version.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<PreparedSdkSession?> PrepareSessionAsync(
        IAppHostServerProjectFactory appHostServerProjectFactory,
        IAppHostServerSessionFactory serverSessionFactory,
        IInteractionService interactionService,
        ILogger logger,
        string tempDirectoryPrefix,
        string sdkVersion,
        IReadOnlyList<IntegrationReference> integrations,
        string? packageSourceOverride,
        Func<IAppHostServerProject, string?>? validateProject,
        CancellationToken cancellationToken)
    {
        var tempDirectory = Directory.CreateTempSubdirectory(tempDirectoryPrefix);
        var tempDir = tempDirectory.FullName;
        var disposeTempDirectory = true;

        try
        {
            var appHostServerProject = await appHostServerProjectFactory.CreateAsync(tempDir, cancellationToken);

            if (validateProject?.Invoke(appHostServerProject) is string rejection)
            {
                interactionService.DisplayError(rejection);
                return null;
            }

            logger.LogDebug("Building AppHost server with {Count} integrations", integrations.Count);

            var prepareResult = await appHostServerProject.PrepareAsync(
                sdkVersion,
                integrations,
                packageSourceOverride: packageSourceOverride,
                cancellationToken: cancellationToken);

            if (!prepareResult.Success)
            {
                interactionService.DisplayError("Failed to build capability scanner.");
                if (prepareResult.Output is not null)
                {
                    foreach (var (_, line) in prepareResult.Output.GetLines())
                    {
                        interactionService.DisplayMessage(KnownEmojis.Wrench, line);
                    }
                }
                return null;
            }

            var serverSession = serverSessionFactory.Create(appHostServerProject, environmentVariables: null, debug: false, gracefulShutdownSignaler: null, shutdownService: null, isolateConsole: false, cancellationToken);

            try
            {
                // Short-lived RPC session: StartAsync() spawns the server. We never observe the
                // exit-code task (WaitForExitAsync) because disposal flows the exit code through the
                // activity scope and the only failure mode we care about surfaces via the RPC call.
                await serverSession.StartAsync();

                var rpcClient = await serverSession.GetRpcClientAsync(cancellationToken);

                disposeTempDirectory = false;
                return new PreparedSdkSession(serverSession, rpcClient, tempDir, logger);
            }
            catch
            {
                // Ownership only transfers to PreparedSdkSession once we return one. Until then a
                // failed start leaves the scanner process alive and holding the temp directory.
                await serverSession.DisposeAsync();
                throw;
            }
        }
        finally
        {
            if (disposeTempDirectory)
            {
                DeleteTempDirectory(tempDir, logger);
            }
        }
    }

    internal static void DeleteTempDirectory(string tempDir, ILogger logger)
    {
        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to clean up temp directory {TempDir}", tempDir);
        }
    }
}

/// <summary>
/// A started AppHost scanner server and its connected RPC client. Disposing tears down the server
/// session and deletes the temporary project directory.
/// </summary>
internal sealed class PreparedSdkSession(
    IAppHostServerSession session,
    IAppHostRpcClient rpcClient,
    string tempDirectory,
    ILogger logger) : IAsyncDisposable
{
    public IAppHostRpcClient RpcClient { get; } = rpcClient;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await session.DisposeAsync();
        }
        finally
        {
            SdkCommandPreparation.DeleteTempDirectory(tempDirectory, logger);
        }
    }
}
