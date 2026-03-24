# Commit Analysis and Feature Extraction

To extract features for release notes, follow this comprehensive process:

## Understanding Component Analysis Files

Each component analysis file (`analysis-output/*.md`) contains:

- **Change Summary**: File counts and statistics
- **Complete Commit List**: All commits for that component between releases
- **Top Contributors**: Who made the changes
- **Categorized Commits**: Features, bug fixes, breaking changes

## How to Analyze Commits for Features

### Step 1: Read Commit Messages for Context

```markdown
# Example from analysis file:
6b5ef9919 [release/9.4] Add ContainerBuildOptions support to ResourceContainerImageBuilder for customizing dotnet publish (#10312)
853c91898 Rename IPublishingActivityProgressReporter to IPublishingActivityReporter (#10253)
c5e604f4b Add dashboard resource to AddDockerComposeEnvironment (#9597)
4ee28c24b Only expose endpoint port in docker compose if external is set to true (#9604)
```

### Step 2: Look Up GitHub Issues and Pull Requests for Additional Context

**CRITICAL**: When commit messages include GitHub issue references (e.g., `#10587`, `(#10648)`), **always look up the issue/PR for deeper understanding**:

- **Use GitHub API tools** to fetch PR/issue details: `mcp_github_get_pull_request` or `mcp_github_get_issue`
- **Extract PR descriptions** to understand the problem being solved and implementation details
- **Review customer impact** statements and use cases from the issue
- **Identify related issues** if the commit references a backport or duplicate
- **Look for linked issues** in PR descriptions (e.g., "Fixes #10591", "Closes #10699")
- **Examine PR body for screenshots, examples, and detailed explanations**
- **Extract user pain points** from issue descriptions to understand the "why" behind changes
- **Incorporate GitHub context** into feature descriptions for better developer understanding
- **Include GitHub links** in the final documentation for traceability

#### Real Example: Deep Issue Context Extraction

**Commit:** `5e8824d70 Various aspire exec improvements (#10606)`

**Step 2a: Look up the PR:**
```markdown
# PR #10606 Description:
- Title: "aspire exec fail fast improvements"
- Fixes issues: #10591 and #10592
- Implementation: Better error handling and validation
```

**Step 2b: Look up the linked issues:**
```markdown
# Issue #10591: "aspire exec - No target resource specified error comes after searching for app host"
- User Problem: CLI wasted time searching for projects before showing required arg errors
- Pain Point: "CLI should immediately fail because of them rather than wasting the user's time"
- User Impact: Faster feedback, better developer experience

# Issue #10592: "aspire exec - Failed to parse command."
- User Problem: Confusing error message "Failed to parse command" when command not specified
- Pain Point: Unclear what was actually wrong with the command
- User Impact: Better error messages, clearer guidance
```

**Step 2c: Extract comprehensive context:**
```markdown
# Combined Understanding:
- Root Cause: aspire exec had poor validation order and unclear error messages
- User Experience: Developers frustrated by slow feedback and confusing errors
- Solution: Fail-fast validation + improved error messaging
- Business Value: Reduced developer friction, faster development iterations

# Enhanced Feature Documentation:
### ⚡ Faster aspire exec validation
The `aspire exec` command now provides immediate validation feedback, failing fast when required arguments are missing instead of spending time searching for projects first. Error messages have also been improved to be more actionable ([#10606](https://github.com/microsoft/aspire/pull/10606)).

**Before:** CLI searches for projects, then shows confusing "Failed to parse command" error
**After:** Immediate "Target resource is not specified" error with clear guidance

This improvement reduces development friction by providing faster, clearer feedback during the development workflow.
```

#### Example: Multiple Issue References in Single Commit

```markdown
# Commit message:
9ccd68154 Add links between telemetry and resources in grid values (#10648)

# After looking up GitHub PR #10648:
- Problem: Difficulty navigating between telemetry data and resources in dashboard
- Solution: Custom components for clickable resource names, span IDs, and trace IDs
- Screenshots: Shows before/after navigation experience
- Customer impact: Reduces time to correlate telemetry across distributed applications
- Implementation: Property grid value components with navigation links

# Final documentation includes GitHub reference:
### ✨ Enhanced telemetry navigation
The dashboard now provides better navigation between telemetry data and resources ([#10648](https://github.com/microsoft/aspire/pull/10648)).
```

#### GitHub Issue Pattern Recognition

**Look for these patterns in commit messages:**

- `(#12345)` - Pull request reference
- `Fixes #12345` - Closes an issue  
- `Closes #12345` - Closes an issue
- `[release/X.Y] ... (#12345)` - Backport with PR reference
- Multiple references: `(#10170) (#10313) (#10316)` - Related PRs/issues

**For each GitHub reference found:**

1. **Fetch the PR/issue details** using GitHub API tools
2. **Read the description** for implementation context
3. **Extract user-facing benefits** from problem statements
4. **Note any breaking changes** mentioned in PR descriptions
5. **Use screenshots/examples** from PRs to enhance documentation
6. **Add GitHub links** to the final What's New document

#### Real Examples from Aspire.Cli Analysis

**Pattern 1: Single PR with Multiple Linked Issues**
```markdown
# Commit: 5e8824d70 Various aspire exec improvements (#10606)
# PR #10606 description mentions: "Fixes #10591 and #10592"

# Required GitHub API calls:
- mcp_github_get_pull_request(pullNumber=10606) → Get PR context
- mcp_github_get_issue(issue_number=10591) → Get first linked issue  
- mcp_github_get_issue(issue_number=10592) → Get second linked issue

# Result: Complete understanding of user problems and solutions
```

**Pattern 2: Configuration Bug with Detailed Reproduction Steps**
```markdown
# Commit: 8f0e3850 aspire config set writes appHostPath to ~/.aspire/settings.json globally (#10700)
# PR #10700 description mentions: "Fixes #10699"

# GitHub API calls reveal:
- Issue #10699: Detailed macOS reproduction steps
- Problem: Global vs local settings file confusion
- User Impact: Incorrect path resolution breaking aspire run
- Root Cause: FindNearestSettingsFile() method walking up directory tree incorrectly
```

**Pattern 3: Copilot-Generated Enhancement**
```markdown
# Commit: b167ea5c Enhance orphan detection in Aspire AppHost (#10673)
# PR #10673: Created by GitHub Copilot coding agent

# GitHub lookup reveals:
- Problem: PID reuse vulnerability in orphan detection
- Technical Solution: Added ASPIRE_CLI_STARTED environment variable
- Implementation: Process start time verification with ±1 second tolerance
- Backwards Compatibility: Graceful fallback to PID-only logic
```

**Pattern 4: Help/UX Improvement Chain**
```markdown
# Commit: c4ead669 chore: aspire exec --help improvements (#10598)
# PR #10598 description mentions: "Fixes #10594"

# Issue #10594: "aspire exec - Unhelpful help"
- User Problem: CLI help didn't explain how to specify commands
- Specific Quote: "Nothing in the CLI help says how to specify the command"
- Solution: Added -- separator documentation and usage examples
- User Impact: Better discoverability of exec command syntax
```

### Step 3: Identify Feature Types by Commit Message Patterns

- **"Add"** commits → New features or APIs
- **"Rename"** commits → Breaking changes or API updates
- **"Improve/Enhance"** commits → Enhancements to existing features
- **"Fix"** commits → Bug fixes (usually not featured unless significant)
- **"Support for"** commits → New platform/technology integrations

### Step 4: Extract User-Facing Impact

For each significant commit, determine:

- **What capability does this enable?** (new feature)
- **What does this change for developers?** (API impact)
- **What problem does this solve?** (use case)
- **Is this breaking?** (migration needed)

## Commit-to-Feature Translation Process

### Example: Docker Compose Commits Analysis

From `Aspire.Hosting.Docker.md`:

```markdown
c5e604f4b Add dashboard resource to AddDockerComposeEnvironment (#9597)
4ee28c24b Only expose endpoint port in docker compose if external is set to true (#9604)
```

**Translation Process:**

1. **Commit c5e604f4b**: "Add dashboard resource to AddDockerComposeEnvironment"
   - **Feature**: Docker Compose with integrated Aspire Dashboard
   - **User Impact**: Developers can now add dashboard integration to Docker Compose environments
   - **API Change**: New `.WithDashboard()` method on `AddDockerComposeEnvironment`

2. **Commit 4ee28c24b**: "Only expose endpoint port in docker compose if external is set to true"
   - **Feature**: Enhanced Docker Compose security
   - **User Impact**: Better security by selective port exposure
   - **Behavior Change**: Internal endpoints use `expose`, external endpoints use `ports`

## Multi-Component Feature Identification

**Look for Related Commits Across Components:**

Example: Azure Storage API consolidation appears in multiple files:

- `Aspire.Hosting.Azure.Storage.md`: API changes
- `Aspire.Azure.Data.Tables.md`: Client registration changes
- `Aspire.Azure.Storage.Blobs.md`: Service client updates

**Synthesis Process:**

1. **Identify the pattern**: Multiple storage components have similar "rename" commits
2. **Find the theme**: API consolidation and standardization
3. **Create unified feature**: "Azure Storage API consolidation" section
4. **Show migration path**: Before/after examples for all affected components

## Commit Prioritization for Release Notes

### **High Priority Commits (Must Include):**

- New resource types (`Add.*Resource`)
- New integration support (`Add.*support`, `Support for.*`)
- Breaking API changes (`Rename.*`, `Remove.*`, `Change.*`)
- Major CLI features (`Add.*command`, `.*exec.*`, `.*deploy.*`)
- Security improvements (`security`, `expose.*port`)

### **Medium Priority Commits (Consider Including):**

- Performance improvements (`Improve.*performance`, `Optimize.*`)
- Enhanced configuration (`Add.*configuration`, `Support.*options`)
- Better error handling (`Improve.*error`, `Add.*validation`)
- Developer experience (`Enhance.*`, `Better.*`)

### **Low Priority Commits (Usually Skip):**

- Bug fixes (`Fix.*`) unless critical or user-visible
- Internal refactoring without user impact
- Documentation updates
- Test improvements
- Code cleanup

## Writing Features from Commit Analysis

### From Commits to Documentation Sections

**Step-by-Step Process:**

1. **Identify the Commit Pattern**
   - Scan commit messages for keywords: "Add", "Support", "Improve", "Rename"
   - Look for technology names: "Azure", "Docker", "Kubernetes", "Redis"
   - Find capability words: "authentication", "configuration", "deployment"

2. **Extract User Value**
   - **What problem does this solve?** (the "why")
   - **What can developers now do?** (the capability)
   - **How do they use it?** (the API/CLI)

3. **Create Feature Section Structure**

   ```markdown
   ### ✨ Feature Name

   [Brief description of the new capability and its value]

   ```csharp
   // Code example using verified APIs from uber file
   ```

   [Explanation of the example, key benefits, and where to learn more]
   ```

### Real Examples of Commit-to-Feature Translation

**Commit:** `c5e604f4b Add dashboard resource to AddDockerComposeEnvironment (#9597)`

**Feature Section:**

```markdown
### 🖥️ Docker Compose with Aspire Dashboard integration

You can now integrate the Aspire Dashboard directly into your Docker Compose environments, providing a unified monitoring experience for both containerized and Aspire-managed resources.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("compose")
    .WithDashboard()  // ✨ New integration capability
    .WithComposeFile("docker-compose.yml");

await builder.Build().RunAsync();
```

This enhancement allows you to monitor Docker Compose services alongside your Aspire resources in a single dashboard interface.
```

**Commit:** `bdd1d34c6 Add support for containers with Dockerfile to AzureAppServiceEnvironmentResource`

**Feature Section:**

```markdown
### 🐳 Azure App Service container deployment with Dockerfile support

Azure App Service environments now support deploying containerized applications directly with <xref:Aspire.Hosting.ContainerResourceBuilderExtensions.WithDockerfile*>, making it easier to deploy custom container images to Azure.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var appService = builder.AddAzureAppService("myapp")
    .WithDockerfile("./src/MyApp");  // ✨ New Dockerfile support

await builder.Build().RunAsync();
```

This feature streamlines container deployment to Azure App Service by building and deploying directly from your Dockerfile without requiring pre-built container images.
```

### Grouping Related Commits into Unified Features

When multiple commits contribute to a single user-facing capability:

**Multiple Related Commits:**

- `853c91898 Rename IPublishingActivityProgressReporter to IPublishingActivityReporter`
- `d4eacc676 Improve the publish/deploy output with better progress reporting`
- `a1b2c3d4e Add structured logging to publish operations`

**Unified Feature Section:**

```markdown
### 🚀 Enhanced publish and deploy experience

The publish and deploy experience has been significantly improved with better progress reporting, clearer output formatting, and enhanced logging.

```bash
# Enhanced output with structured progress
aspire publish --interactive
✅ Building project...
✅ Publishing container images...
✅ Deploying to Azure...
📊 Deployment completed successfully in 2m 15s
```

Key improvements include:

- **Structured progress reporting**: Clear status updates throughout the deployment process
- **Better error messages**: More actionable error information when deployments fail
- **Enhanced logging**: Detailed logs for troubleshooting deployment issues
```

## Final Step: Verification

After this detailed analysis, continue with:

1. **Prioritize commits for inclusion:**
   - High priority: New resource types, integration support, breaking API changes, major CLI features, security improvements.
   - Medium priority: Performance, configuration, error handling, developer experience.
   - Low priority: Bug fixes (unless critical), refactoring, documentation, tests, code cleanup.

2. **Verify APIs and CLI commands:**
   - Use `analysis-output/api-changes-build-current/all-api-changes.txt` (the uber file) to confirm all API references and code samples.
   - For CLI changes, use commit analysis from `Aspire.Cli.md`.

3. **Document accurately:**
   - Never invent APIs or CLI commands; only document what is confirmed in the analysis and uber file.
   - Provide migration guidance for breaking changes.

This approach ensures a comprehensive, accurate, and traceable process for commit analysis and feature extraction.
