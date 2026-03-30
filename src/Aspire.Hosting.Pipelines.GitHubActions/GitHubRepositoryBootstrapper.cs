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
    public static async Task<string?> BootstrapAsync(PipelineWorkflowGenerationContext context)
    {
        var logger = context.StepContext.Logger;
        var ct = context.CancellationToken;
        var interactionService = context.StepContext.Services.GetService<IInteractionService>();

        // Determine working directory from the execution context
        var cwd = Directory.GetCurrentDirectory();

        // Step 1: Check if we're already in a Git repo
        var isGitRepo = await GitHelper.IsGitRepoAsync(cwd, logger, ct).ConfigureAwait(false);

        string repoRoot;

        if (isGitRepo)
        {
            repoRoot = await GitHelper.GetRepoRootAsync(cwd, logger, ct).ConfigureAwait(false) ?? cwd;
            logger.LogInformation("Git repository detected at: {RepoRoot}", repoRoot);
        }
        else
        {
            // Offer to initialize a Git repo
            if (interactionService is not null)
            {
                var initResult = await interactionService.PromptConfirmationAsync(
                    "Initialize Git Repository",
                    "No Git repository found in the current directory. Would you like to initialize one?",
                    cancellationToken: ct).ConfigureAwait(false);

                if (initResult.Canceled || !initResult.Data)
                {
                    logger.LogInformation("Skipping Git initialization. Using current directory as root.");
                    return cwd;
                }
            }
            else
            {
                logger.LogWarning("No Git repository found and no interaction service available. Using current directory.");
                return cwd;
            }

            // Initialize Git repo
            logger.LogInformation("Initializing Git repository in {Directory}...", cwd);
            if (!await GitHelper.InitAsync(cwd, logger, ct).ConfigureAwait(false))
            {
                logger.LogError("Failed to initialize Git repository.");
                return cwd;
            }

            repoRoot = cwd;
            logger.LogInformation("Git repository initialized.");

            // Offer to create .gitignore
            var gitignorePath = Path.Combine(repoRoot, ".gitignore");
            if (!File.Exists(gitignorePath))
            {
                var createGitignore = await interactionService.PromptConfirmationAsync(
                    "Create .gitignore",
                    "Would you like to create a .gitignore file with sensible defaults for .NET/Aspire projects?",
                    cancellationToken: ct).ConfigureAwait(false);

                if (!createGitignore.Canceled && createGitignore.Data)
                {
                    await File.WriteAllTextAsync(gitignorePath, GitIgnoreTemplate.Content, ct).ConfigureAwait(false);
                    logger.LogInformation("Created .gitignore");
                    context.StepContext.Summary.Add("📄 .gitignore", gitignorePath);
                }
            }
        }

        // Step 2: Check for existing GitHub remote
        var remoteUrl = await GitHelper.GetRemoteUrlAsync(repoRoot, logger, ct: ct).ConfigureAwait(false);

        if (IsGitHubUrl(remoteUrl))
        {
            logger.LogInformation("GitHub remote already configured: {Url}", remoteUrl);
            return repoRoot;
        }

        // Step 3: Offer to create a GitHub repository
        if (interactionService is null)
        {
            logger.LogInformation("No interaction service available. Skipping GitHub repository setup.");
            return repoRoot;
        }

        var pushToGitHub = await interactionService.PromptConfirmationAsync(
            "Push to GitHub",
            remoteUrl is null
                ? "No Git remote configured. Would you like to create a GitHub repository and push?"
                : "The current remote is not a GitHub URL. Would you like to create a GitHub repository?",
            cancellationToken: ct).ConfigureAwait(false);

        if (pushToGitHub.Canceled || !pushToGitHub.Data)
        {
            return repoRoot;
        }

        // Step 4: Set up GitHub repo
        var cloneUrl = await SetupGitHubRepoAsync(repoRoot, interactionService, logger, ct).ConfigureAwait(false);
        if (cloneUrl is not null)
        {
            context.StepContext.Summary.Add("🔗 GitHub", cloneUrl);
        }

        return repoRoot;
    }

    /// <summary>
    /// After YAML generation, optionally commits and pushes all changes.
    /// </summary>
    public static async Task OfferCommitAndPushAsync(string repoRoot, PipelineStepContext stepContext)
    {
        var logger = stepContext.Logger;
        var ct = stepContext.CancellationToken;
        var interactionService = stepContext.Services.GetService<IInteractionService>();

        if (interactionService is null)
        {
            return;
        }

        // Check if there's a remote to push to
        var remoteUrl = await GitHelper.GetRemoteUrlAsync(repoRoot, logger, ct: ct).ConfigureAwait(false);
        if (remoteUrl is null)
        {
            return;
        }

        var commitResult = await interactionService.PromptConfirmationAsync(
            "Commit & Push",
            "Would you like to commit the generated workflow files and push to GitHub?",
            cancellationToken: ct).ConfigureAwait(false);

        if (commitResult.Canceled || !commitResult.Data)
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
