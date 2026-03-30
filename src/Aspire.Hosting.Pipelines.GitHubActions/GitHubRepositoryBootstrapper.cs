// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIREPIPELINES001

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Pipelines.GitHubActions;

/// <summary>
/// Orchestrates Git repository and GitHub remote bootstrapping during <c>aspire pipeline init</c>.
/// </summary>
internal static class GitHubRepositoryBootstrapper
{
    /// <summary>
    /// Bootstraps the Git repo and GitHub remote if needed, returning the repository root directory.
    /// </summary>
    /// <remarks>
    /// All user prompts use <see cref="IInteractionService.PromptInputAsync(string, string?, InteractionInput, InputsDialogInteractionOptions?, CancellationToken)"/>
    /// rather than <c>PromptConfirmationAsync</c> because the CLI-to-AppHost backchannel only
    /// proxies <c>InputsInteractionInfo</c> prompts (not <c>MessageBoxInteractionInfo</c>).
    /// </remarks>
    public static async Task<string?> BootstrapAsync(PipelineWorkflowGenerationContext context)
    {
        var logger = context.StepContext.Logger;
        var ct = context.CancellationToken;
        var interactionService = context.StepContext.Services.GetService<IInteractionService>();

        // Use the repo root already detected by the central pipeline if available.
        var repoRoot = context.RepositoryRootDirectory;

        if (repoRoot is not null)
        {
            var isGitRepo = await GitHelper.IsGitRepoAsync(repoRoot, logger, ct).ConfigureAwait(false);

            if (!isGitRepo)
            {
                // Repo root was detected (e.g. via aspire.config.json) but it's not a git repo yet.
                if (!await PromptBooleanAsync(interactionService,
                    "Initialize Git Repository",
                    "No Git repository found. Would you like to initialize one?",
                    ct).ConfigureAwait(false))
                {
                    return repoRoot;
                }

                await InitGitRepoAsync(repoRoot, interactionService, context, logger, ct).ConfigureAwait(false);
            }
        }
        else
        {
            // No repo root from central detection — try to detect ourselves.
            var cwd = Directory.GetCurrentDirectory();
            var isGitRepo = await GitHelper.IsGitRepoAsync(cwd, logger, ct).ConfigureAwait(false);

            if (isGitRepo)
            {
                repoRoot = await GitHelper.GetRepoRootAsync(cwd, logger, ct).ConfigureAwait(false) ?? cwd;
                logger.LogInformation("Git repository detected at: {RepoRoot}", repoRoot);
            }
            else
            {
                if (!await PromptBooleanAsync(interactionService,
                    "Initialize Git Repository",
                    "No Git repository found in the current directory. Would you like to initialize one?",
                    ct).ConfigureAwait(false))
                {
                    logger.LogInformation("Skipping Git initialization. Using current directory as root.");
                    return cwd;
                }

                repoRoot = cwd;
                await InitGitRepoAsync(repoRoot, interactionService, context, logger, ct).ConfigureAwait(false);
            }
        }

        // Check for existing GitHub remote
        var remoteUrl = await GitHelper.GetRemoteUrlAsync(repoRoot, logger, ct: ct).ConfigureAwait(false);

        if (IsGitHubUrl(remoteUrl))
        {
            logger.LogInformation("GitHub remote already configured: {Url}", remoteUrl);
            return repoRoot;
        }

        // Offer to create a GitHub repository
        var pushMessage = remoteUrl is null
            ? "No Git remote configured. Would you like to create a GitHub repository and push?"
            : "The current remote is not a GitHub URL. Would you like to create a GitHub repository?";

        if (!await PromptBooleanAsync(interactionService, "Push to GitHub", pushMessage, ct).ConfigureAwait(false))
        {
            return repoRoot;
        }

        // Set up GitHub repo
        var cloneUrl = await SetupGitHubRepoAsync(repoRoot, interactionService!, logger, ct).ConfigureAwait(false);
        if (cloneUrl is not null)
        {
            context.StepContext.Summary.Add("🔗 GitHub", cloneUrl);
        }

        return repoRoot;
    }

    private static async Task InitGitRepoAsync(
        string directory,
        IInteractionService? interactionService,
        PipelineWorkflowGenerationContext context,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogInformation("Initializing Git repository in {Directory}...", directory);

        if (!await GitHelper.InitAsync(directory, logger, ct).ConfigureAwait(false))
        {
            logger.LogError("Failed to initialize Git repository.");
            return;
        }

        logger.LogInformation("Git repository initialized.");

        // Offer to create .gitignore
        var gitignorePath = Path.Combine(directory, ".gitignore");
        if (!File.Exists(gitignorePath))
        {
            if (await PromptBooleanAsync(interactionService,
                "Create .gitignore",
                "Would you like to create a .gitignore file with sensible defaults for .NET/Aspire projects?",
                ct).ConfigureAwait(false))
            {
                await File.WriteAllTextAsync(gitignorePath, GitIgnoreTemplate.Content, ct).ConfigureAwait(false);
                logger.LogInformation("Created .gitignore");
                context.StepContext.Summary.Add("📄 .gitignore", gitignorePath);
            }
        }
    }

    /// <summary>
    /// After YAML generation, optionally commits and pushes all changes.
    /// </summary>
    public static async Task OfferCommitAndPushAsync(string repoRoot, PipelineStepContext stepContext)
    {
        var logger = stepContext.Logger;
        var ct = stepContext.CancellationToken;
        var interactionService = stepContext.Services.GetService<IInteractionService>();

        // Check if there's a remote to push to
        var remoteUrl = await GitHelper.GetRemoteUrlAsync(repoRoot, logger, ct: ct).ConfigureAwait(false);
        if (remoteUrl is null)
        {
            return;
        }

        if (!await PromptBooleanAsync(interactionService,
            "Commit & Push",
            "Would you like to commit the generated workflow files and push to GitHub?",
            ct).ConfigureAwait(false))
        {
            return;
        }

        logger.LogInformation("Committing and pushing...");

        if (!await GitHelper.AddAllAndCommitAsync(repoRoot, "Add Aspire CI/CD pipeline workflow", logger, ct).ConfigureAwait(false))
        {
            logger.LogWarning("Failed to commit changes. You may need to commit manually.");
            return;
        }

        var branch = await GitHelper.GetCurrentBranchAsync(repoRoot, logger, ct).ConfigureAwait(false) ?? "main";

        if (!await GitHelper.PushAsync(repoRoot, logger, branch: branch, ct: ct).ConfigureAwait(false))
        {
            logger.LogWarning("Failed to push. You may need to push manually with: git push -u origin {Branch}", branch);
            return;
        }

        logger.LogInformation("Pushed to {Remote} on branch {Branch}", remoteUrl, branch);
        stepContext.Summary.Add("🚀 Pushed", $"{branch} → {remoteUrl}");
    }

    /// <summary>
    /// Prompts a yes/no question via <see cref="IInteractionService.PromptInputAsync(string, string?, InteractionInput, InputsDialogInteractionOptions?, CancellationToken)"/>
    /// using <see cref="InputType.Boolean"/>, which is supported by the CLI backchannel.
    /// Returns <c>false</c> if the interaction service is unavailable or the user declines/cancels.
    /// </summary>
    private static async Task<bool> PromptBooleanAsync(
        IInteractionService? interactionService,
        string title,
        string message,
        CancellationToken ct)
    {
        if (interactionService is null)
        {
            return false;
        }

        var result = await interactionService.PromptInputAsync(
            title,
            message,
            new InteractionInput
            {
                Name = "confirm",
                Label = title,
                InputType = InputType.Boolean,
                Value = "true",
                Required = true
            },
            cancellationToken: ct).ConfigureAwait(false);

        if (result.Canceled || result.Data?.Value is null)
        {
            return false;
        }

        return string.Equals(result.Data.Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> SetupGitHubRepoAsync(
        string repoRoot,
        IInteractionService interactionService,
        ILogger logger,
        CancellationToken ct)
    {
        using var github = new GitHubApiClient(logger);

        // Check if gh CLI is installed
        if (!await GitHubApiClient.IsGhInstalledAsync(ct).ConfigureAwait(false))
        {
            logger.LogWarning("The GitHub CLI (gh) is not installed. Install it from https://cli.github.com/ to create repositories.");
            return null;
        }

        // Get auth token
        var token = await github.GetAuthTokenAsync(ct).ConfigureAwait(false);
        if (token is null)
        {
            logger.LogWarning("Not authenticated with GitHub CLI. Run 'gh auth login' first.");
            return null;
        }

        // Fetch user and orgs in parallel
        var userTask = github.GetAuthenticatedUserAsync(token, ct);
        var orgsTask = github.GetUserOrgsAsync(token, ct);

        await Task.WhenAll(userTask, orgsTask).ConfigureAwait(false);

        var username = await userTask.ConfigureAwait(false);
        var orgs = await orgsTask.ConfigureAwait(false);

        if (username is null)
        {
            logger.LogWarning("Could not determine authenticated GitHub user.");
            return null;
        }

        // Build owner choices: personal account + orgs
        var ownerChoices = new List<KeyValuePair<string, string>>
        {
            new(username, $"{username} (personal account)")
        };

        foreach (var org in orgs)
        {
            ownerChoices.Add(new(org, org));
        }

        // Prompt for owner
        var ownerResult = await interactionService.PromptInputAsync(
            "GitHub Repository Owner",
            "Select the owner for the new repository.",
            new InteractionInput
            {
                Name = "owner",
                Label = "Owner",
                InputType = InputType.Choice,
                Options = ownerChoices,
                Value = username,
                Required = true
            },
            cancellationToken: ct).ConfigureAwait(false);

        if (ownerResult.Canceled || ownerResult.Data?.Value is null)
        {
            return null;
        }

        var selectedOwner = ownerResult.Data.Value;
        var isOrg = selectedOwner != username;

        // Prompt for repo name (default: directory name)
        var defaultRepoName = new DirectoryInfo(repoRoot).Name;

        var nameResult = await interactionService.PromptInputAsync(
            "Repository Name",
            null,
            new InteractionInput
            {
                Name = "repoName",
                Label = "Repository name",
                InputType = InputType.Text,
                Value = defaultRepoName,
                Required = true
            },
            cancellationToken: ct).ConfigureAwait(false);

        if (nameResult.Canceled || string.IsNullOrWhiteSpace(nameResult.Data?.Value))
        {
            return null;
        }

        var repoName = nameResult.Data.Value;

        // Prompt for visibility
        var visibilityResult = await interactionService.PromptInputAsync(
            "Repository Visibility",
            null,
            new InteractionInput
            {
                Name = "visibility",
                Label = "Visibility",
                InputType = InputType.Choice,
                Options =
                [
                    new("private", "Private"),
                    new("public", "Public")
                ],
                Value = "private",
                Required = true
            },
            cancellationToken: ct).ConfigureAwait(false);

        if (visibilityResult.Canceled)
        {
            return null;
        }

        var isPrivate = visibilityResult.Data?.Value != "public";

        // Create the repo
        logger.LogInformation("Creating GitHub repository {Owner}/{Repo}...", selectedOwner, repoName);

        var cloneUrl = await github.CreateRepoAsync(
            token,
            repoName,
            isOrg ? selectedOwner : null,
            isPrivate,
            ct).ConfigureAwait(false);

        if (cloneUrl is null)
        {
            logger.LogError("Failed to create GitHub repository.");
            return null;
        }

        logger.LogInformation("Created repository: {Url}", cloneUrl);

        // Add remote
        if (!await GitHelper.AddRemoteAsync(repoRoot, cloneUrl, logger, ct: ct).ConfigureAwait(false))
        {
            logger.LogWarning("Failed to add remote. You can add it manually: git remote add origin {Url}", cloneUrl);
        }
        else
        {
            logger.LogInformation("Added remote 'origin' → {Url}", cloneUrl);
        }

        return cloneUrl;
    }

    private static bool IsGitHubUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        return url.Contains("github.com", StringComparison.OrdinalIgnoreCase);
    }
}
