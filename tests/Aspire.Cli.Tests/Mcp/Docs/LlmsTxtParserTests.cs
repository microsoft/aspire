// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Aspire.Cli.Mcp.Docs;

namespace Aspire.Cli.Tests.Mcp.Docs;

public class LlmsTxtParserTests
{
    [Fact]
    public async Task ParseAsync_WithEmptyString_ReturnsEmptyList()
    {
        var result = await LlmsTxtParser.ParseAsync("").DefaultTimeout();

        Assert.Empty(result);
    }

    [Fact]
    public async Task ParseAsync_WithWhitespaceOnly_ReturnsEmptyList()
    {
        var result = await LlmsTxtParser.ParseAsync("   \n\t\n   ").DefaultTimeout();

        Assert.Empty(result);
    }

    [Fact]
    public async Task ParseAsync_WithNoH1Headers_ReturnsEmptyList()
    {
        var content = """
            This is just regular text.
            No headers here.
            ## This is H2 but no H1
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Empty(result);
    }

    [Fact]
    public async Task ParseAsync_WithSingleDocument_ParsesCorrectly()
    {
        var content = """
            # My Document Title
            > This is the summary.

            Some body content here.
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Single(result);
        var doc = result[0];
        Assert.Equal("My Document Title", doc.Title);
        Assert.Equal("my-document-title", doc.Slug);
        Assert.Equal("This is the summary.", doc.Summary);
        Assert.Contains("Some body content here.", doc.Content);
    }

    [Fact]
    public async Task ParseAsync_WithMultipleDocuments_ParsesAll()
    {
        var content = """
            # First Document
            > First summary.

            First content.

            # Second Document
            > Second summary.

            Second content.

            # Third Document
            Third content without summary.
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Equal(3, result.Count);
        Assert.Equal("First Document", result[0].Title);
        Assert.Equal("Second Document", result[1].Title);
        Assert.Equal("Third Document", result[2].Title);
    }

    [Fact]
    public async Task ParseAsync_WithSections_ParsesSectionsCorrectly()
    {
        var content = """
            # Main Document
            > Document summary.

            ## Section One
            Section one content.

            ## Section Two
            Section two content.

            ### Subsection
            Subsection content.
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Single(result);
        var doc = result[0];
        Assert.Equal(3, doc.Sections.Count);

        Assert.Equal("Section One", doc.Sections[0].Heading);
        Assert.Equal(2, doc.Sections[0].Level);

        Assert.Equal("Section Two", doc.Sections[1].Heading);
        Assert.Equal(2, doc.Sections[1].Level);

        Assert.Equal("Subsection", doc.Sections[2].Heading);
        Assert.Equal(3, doc.Sections[2].Level);
    }

    [Fact]
    public async Task ParseAsync_SectionContent_IncludesNestedHeadings()
    {
        var content = """
            # Document

            ## Parent Section
            Parent content.

            ### Child Section
            Child content.

            ## Another Section
            Another content.
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        var doc = result[0];
        var parentSection = doc.Sections.First(s => s.Heading == "Parent Section");

        // Parent section should include content up to the next H2
        Assert.Contains("Parent content.", parentSection.Content);
        Assert.Contains("### Child Section", parentSection.Content);
        Assert.Contains("Child content.", parentSection.Content);
        Assert.DoesNotContain("Another content.", parentSection.Content);
    }

    [Fact]
    public async Task ParseAsync_WithNoSummary_SummaryIsNull()
    {
        var content = """
            # Document Without Summary

            Just regular content, no blockquote.
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Single(result);
        Assert.Null(result[0].Summary);
    }

    [Fact]
    public async Task ParseAsync_SlugGeneration_HandlesSpecialCharacters()
    {
        var content = """
            # Hello, World! How's It Going?
            Content.
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Single(result);
        Assert.Equal("hello-world-hows-it-going", result[0].Slug);
    }

    [Fact]
    public async Task ParseAsync_SlugGeneration_HandlesMultipleSpaces()
    {
        var content = """
            # Title   With   Multiple   Spaces
            Content.
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Single(result);
        // Multiple spaces should become single hyphens
        Assert.Equal("title-with-multiple-spaces", result[0].Slug);
    }

    [Fact]
    public async Task ParseAsync_SlugGeneration_TrimsHyphens()
    {
        var content = """
            # - Title With Leading Dash -
            Content.
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Single(result);
        // Leading and trailing hyphens should be trimmed
        Assert.DoesNotMatch("^-", result[0].Slug);
        Assert.DoesNotMatch("-$", result[0].Slug);
    }

    [Fact]
    public async Task ParseAsync_WithCodeBlocks_PreservesContent()
    {
        var content = """
            # Code Example

            ## Usage

            ```csharp
            var builder = DistributedApplication.CreateBuilder(args);
            var redis = builder.AddRedis("cache");
            ```

            More text.
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Single(result);
        Assert.Contains("```csharp", result[0].Content);
        Assert.Contains("var redis = builder.AddRedis(\"cache\");", result[0].Content);
    }

    [Fact]
    public async Task ParseAsync_H1WithoutSpace_NotRecognizedAsDocument()
    {
        // "#NoSpace" should not be recognized as H1
        var content = """
            #NoSpaceAfterHash
            Content.
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Empty(result);
    }

    [Fact]
    public async Task ParseAsync_H2AtStart_NotRecognizedAsDocument()
    {
        var content = """
            ## This is H2
            Content.
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Empty(result);
    }

    [Fact]
    public async Task ParseAsync_WithLeadingWhitespace_StillParsesH1()
    {
        var content = """
               # Document With Leading Spaces
            Content.
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Single(result);
        Assert.Equal("Document With Leading Spaces", result[0].Title);
    }

    [Fact]
    public async Task ParseAsync_PreservesNewlinesInContent()
    {
        var content = """
            # Document

            Line 1.

            Line 2.

            Line 3.
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Single(result);
        Assert.Contains("\n", result[0].Content);
    }

    [Fact]
    public async Task ParseAsync_DocumentBoundariesAreCorrect()
    {
        var content = """
            # First
            First content with ## inside text.

            # Second
            Second content.
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Equal(2, result.Count);
        Assert.Contains("## inside text", result[0].Content);
        Assert.DoesNotContain("Second content", result[0].Content);
        Assert.DoesNotContain("First content", result[1].Content);
    }

    [Fact]
    public async Task ParseAsync_RealWorldExample_ParsesCorrectly()
    {
        var content = """
            # Aspire overview
            > Aspire is an opinionated stack for building observable, production-ready, distributed apps.

            Aspire is delivered through a collection of NuGet packages that handle specific cloud-native concerns.

            ## Why use Aspire?

            Aspire provides:
            - **Orchestration**: Built-in tooling for running and connecting multi-project applications
            - **Integrations**: NuGet packages for popular services like Redis, PostgreSQL, and Azure
            - **Tooling**: Project templates and CLI tools

            ### Getting started

            To get started with Aspire, install the workload:

            ```bash
            dotnet workload install aspire
            ```

            # Service defaults
            > Configure common defaults for ASP.NET Core apps.

            Service defaults provide a consistent configuration for ASP.NET Core applications.

            ## Configuration

            Add service defaults to your project:

            ```csharp
            builder.AddServiceDefaults();
            ```
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Equal(2, result.Count);

        // First document
        var overview = result[0];
        Assert.Equal("Aspire overview", overview.Title);
        Assert.Equal("aspire-overview", overview.Slug);
        Assert.Equal("Aspire is an opinionated stack for building observable, production-ready, distributed apps.", overview.Summary);
        Assert.Equal(2, overview.Sections.Count);
        Assert.Equal("Why use Aspire?", overview.Sections[0].Heading);
        Assert.Equal("Getting started", overview.Sections[1].Heading);

        // Second document
        var serviceDefaults = result[1];
        Assert.Equal("Service defaults", serviceDefaults.Title);
        Assert.Equal("service-defaults", serviceDefaults.Slug);
        Assert.Equal("Configure common defaults for ASP.NET Core apps.", serviceDefaults.Summary);
        Assert.Single(serviceDefaults.Sections);
        Assert.Equal("Configuration", serviceDefaults.Sections[0].Heading);
    }

    [Fact]
    public async Task ParseAsync_WithCancellation_ThrowsOperationCanceledException()
    {
        var content = """
            # Document 1
            Content 1.

            # Document 2
            Content 2.
            """;

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // TaskCanceledException derives from OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await LlmsTxtParser.ParseAsync(content, cts.Token));
    }

    [Fact]
    public async Task ParseAsync_EmptyTitle_SkipsDocument()
    {
        var content = """
            #
            Content without title.

            # Valid Title
            Valid content.
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Single(result);
        Assert.Equal("Valid Title", result[0].Title);
    }

    [Fact]
    public async Task ParseAsync_MultipleSummaryBlockquotes_UsesFirst()
    {
        var content = """
            # Document
            > First summary.
            > Second line that should be ignored.

            Content.
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Single(result);
        Assert.Equal("First summary.", result[0].Summary);
    }

    [Fact]
    public async Task ParseAsync_BlockquoteAfterSection_NotUsedAsSummary()
    {
        var content = """
            # Document

            ## Section
            > This blockquote is in a section, not a summary.
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Single(result);
        Assert.Null(result[0].Summary);
    }

    [Theory]
    [InlineData("Simple", "simple")]
    [InlineData("Two Words", "two-words")]
    [InlineData("UPPERCASE", "uppercase")]
    [InlineData("MixedCase", "mixedcase")]
    [InlineData("with-hyphens", "with-hyphens")]
    [InlineData("123 Numbers", "123-numbers")]
    [InlineData("Special!@#$%Characters", "specialcharacters")]
    public async Task ParseAsync_SlugGeneration_VariousCases(string title, string expectedSlug)
    {
        var content = $"# {title}\nContent.";

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Single(result);
        Assert.Equal(expectedSlug, result[0].Slug);
    }

    [Fact]
    public async Task ParseAsync_InlineSections_ParsesCorrectly()
    {
        // Minified content with inline sections using [Section titled...] markers (like aspire.dev format)
        var content = "# Document Title\n> Summary text. ## First Section [Section titled \"First Section\"] Content for first section. ## Second Section [Section titled \"Second Section\"] Content for second section. ### Subsection [Section titled \"Subsection\"] Subsection content.";

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Single(result);
        var doc = result[0];
        Assert.Equal("Document Title", doc.Title);
        Assert.StartsWith("Summary text.", doc.Summary);

        // Should find all sections with proper heading extraction
        var h2Sections = doc.Sections.Where(s => s.Level == 2).Select(s => s.Heading).ToList();
        Assert.Contains("First Section", h2Sections);
        Assert.Contains("Second Section", h2Sections);

        var h3Sections = doc.Sections.Where(s => s.Level == 3).Select(s => s.Heading).ToList();
        Assert.Contains("Subsection", h3Sections);
    }

    [Fact]
    public async Task ParseAsync_CodeBlocksExcluded_DoesNotParseHashesInCode()
    {
        var content = """
            # Document
            > Summary.

            Some content.

            ```csharp
            // ## This is a comment, not a heading
            var x = "## Also not a heading";
            ```

            ## Real Section
            Real section content.
            """;

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Single(result);
        var doc = result[0];

        // Should only find the real section, not the code block content
        Assert.Single(doc.Sections);
        Assert.Equal("Real Section", doc.Sections[0].Heading);
        Assert.Equal(2, doc.Sections[0].Level);
    }

    [Fact]
    public async Task ParseAsync_SectionTitledMarker_StrippedFromHeading()
    {
        // Content with [Section titled...] markers like aspire.dev uses
        var content = "# Main Doc\n> Summary. ## Getting Started [Section titled \"Getting Started\"] This section explains...";

        var result = await LlmsTxtParser.ParseAsync(content).DefaultTimeout();

        Assert.Single(result);
        var doc = result[0];
        Assert.Single(doc.Sections);
        Assert.Equal("Getting Started", doc.Sections[0].Heading);
    }

    [Fact]
    public async Task ParseAsync_AspireDotDevContent_ParsesFourDocuments()
    {
        var result = await LlmsTxtParser.ParseAsync(AspireDotDevFourArticleExample).DefaultTimeout();

        Assert.Equal(4, result.Count);
    }

    [Fact]
    public async Task ParseAsync_AspireDotDevContent_ParsesDocumentTitlesCorrectly()
    {
        var result = await LlmsTxtParser.ParseAsync(AspireDotDevFourArticleExample).DefaultTimeout();

        // Note: First article starts after a blank line following the <SYSTEM> tag
        Assert.Equal("Certificate configuration", result[0].Title);
        Assert.Equal("AppHost configuration", result[1].Title);
        Assert.Equal("Docker Compose to Aspire AppHost", result[2].Title);
        Assert.Equal("AppHost eventing APIs", result[3].Title);
    }

    [Fact]
    public async Task ParseAsync_AspireDotDevContent_GeneratesCorrectSlugs()
    {
        var result = await LlmsTxtParser.ParseAsync(AspireDotDevFourArticleExample).DefaultTimeout();

        Assert.Equal("certificate-configuration", result[0].Slug);
        Assert.Equal("apphost-configuration", result[1].Slug);
        Assert.Equal("docker-compose-to-aspire-apphost", result[2].Slug);
        Assert.Equal("apphost-eventing-apis", result[3].Slug);
    }

    [Fact]
    public async Task ParseAsync_AspireDotDevContent_ParsesSummariesCorrectly()
    {
        var result = await LlmsTxtParser.ParseAsync(AspireDotDevFourArticleExample).DefaultTimeout();

        Assert.Equal("Learn how to configure HTTPS endpoints and certificate trust for resources in Aspire to enable secure communication.", result[0].Summary);
        Assert.Equal("Learn about the Aspire AppHost configuration options.", result[1].Summary);
        Assert.Equal("Quick reference for converting Docker Compose YAML syntax to Aspire C# API calls.", result[2].Summary);
        Assert.Equal("Learn how to use the Aspire AppHost eventing features.", result[3].Summary);
    }

    [Fact]
    public async Task ParseAsync_AspireDotDevContent_ParsesSectionsForCertificatesDoc()
    {
        var result = await LlmsTxtParser.ParseAsync(AspireDotDevFourArticleExample).DefaultTimeout();

        var certificatesDoc = result[0];
        Assert.True(certificatesDoc.Sections.Count > 0, "Certificate doc should have sections");

        // Check for expected H2 sections
        var h2Sections = certificatesDoc.Sections.Where(s => s.Level == 2).Select(s => s.Heading).ToList();
        Assert.Contains("HTTPS endpoint configuration", h2Sections);
        Assert.Contains("Certificate trust configuration", h2Sections);
        Assert.Contains("Common scenarios", h2Sections);
        Assert.Contains("Limitations", h2Sections);

        // Also verify H3 sections exist (hierarchical)
        var h3Sections = certificatesDoc.Sections.Where(s => s.Level == 3).ToList();
        Assert.True(h3Sections.Count > 0, "Should have H3 subsections");
    }

    [Fact]
    public async Task ParseAsync_AspireDotDevContent_ParsesSectionsForAppHostConfigDoc()
    {
        var result = await LlmsTxtParser.ParseAsync(AspireDotDevFourArticleExample).DefaultTimeout();

        var appHostConfigDoc = result[1];
        Assert.True(appHostConfigDoc.Sections.Count > 0, "AppHost config doc should have sections");

        var h2Sections = appHostConfigDoc.Sections.Where(s => s.Level == 2).Select(s => s.Heading).ToList();
        Assert.Contains("Common configuration", h2Sections);
        Assert.Contains("Version update notifications", h2Sections);
        Assert.Contains("Resource service", h2Sections);
        Assert.Contains("Dashboard", h2Sections);
        Assert.Contains("Internal", h2Sections);
    }

    [Fact]
    public async Task ParseAsync_AspireDotDevContent_ParsesSectionsForDockerComposeDoc()
    {
        var result = await LlmsTxtParser.ParseAsync(AspireDotDevFourArticleExample).DefaultTimeout();

        var dockerComposeDoc = result[2];
        Assert.True(dockerComposeDoc.Sections.Count > 0, "Docker Compose doc should have sections");

        var h2Sections = dockerComposeDoc.Sections.Where(s => s.Level == 2).Select(s => s.Heading).ToList();
        Assert.Contains("Service definitions", h2Sections);
        Assert.Contains("Images and builds", h2Sections);
        Assert.Contains("Port mappings", h2Sections);
        Assert.Contains("Environment variables", h2Sections);
        Assert.Contains("Volumes and storage", h2Sections);
    }

    [Fact]
    public async Task ParseAsync_AspireDotDevContent_ParsesSectionsForEventingDoc()
    {
        var result = await LlmsTxtParser.ParseAsync(AspireDotDevFourArticleExample).DefaultTimeout();

        var eventingDoc = result[3];
        Assert.True(eventingDoc.Sections.Count > 0, "Eventing doc should have sections");

        var h2Sections = eventingDoc.Sections.Where(s => s.Level == 2).Select(s => s.Heading).ToList();
        Assert.Contains("AppHost eventing", h2Sections);
        Assert.Contains("Resource eventing", h2Sections);
        Assert.Contains("Publish events", h2Sections);
        Assert.Contains("Eventing subscribers", h2Sections);

        // Verify H3 sections exist for detailed subsections
        var h3Sections = eventingDoc.Sections.Where(s => s.Level == 3).Select(s => s.Heading).ToList();
        Assert.Contains("Subscribe to AppHost events", h3Sections);
        Assert.Contains("Subscribe to resource events", h3Sections);
    }

    [Fact]
    public async Task ParseAsync_AspireDotDevContent_ContentContainsCodeBlocks()
    {
        var result = await LlmsTxtParser.ParseAsync(AspireDotDevFourArticleExample).DefaultTimeout();

        // HTTPS certificates doc should have C# code examples
        Assert.Contains("```csharp", result[0].Content);
        Assert.Contains("WithHttpsDeveloperCertificate", result[0].Content);

        // AppHost config doc should have JSON example
        Assert.Contains("```json", result[1].Content);
        Assert.Contains("launchSettings.json", result[1].Content);

        // Docker Compose doc should have examples
        Assert.Contains("builder.AddContainer", result[2].Content);

        // Eventing doc should have C# examples
        Assert.Contains("```csharp", result[3].Content);
        Assert.Contains("Subscribe<BeforeStartEvent>", result[3].Content);
    }

    [Fact]
    public async Task ParseAsync_AspireDotDevContent_DocumentBoundariesAreCorrect()
    {
        var result = await LlmsTxtParser.ParseAsync(AspireDotDevFourArticleExample).DefaultTimeout();

        // Each document's content should not contain other documents' titles
        Assert.DoesNotContain("# AppHost configuration", result[0].Content);
        Assert.DoesNotContain("# Docker Compose to Aspire AppHost", result[0].Content);

        Assert.DoesNotContain("# HTTPS certificates", result[1].Content);
        Assert.DoesNotContain("# Docker Compose to Aspire AppHost", result[1].Content);

        Assert.DoesNotContain("# HTTPS certificates", result[2].Content);
        Assert.DoesNotContain("# AppHost eventing APIs", result[2].Content);

        Assert.DoesNotContain("# Docker Compose to Aspire AppHost", result[3].Content);
        Assert.DoesNotContain("# AppHost configuration", result[3].Content);
    }

    #region Copied example from llms-small.txt from aspire.dev (four articles)

    private const string AspireDotDevFourArticleExample = """
    <SYSTEM>This is the abridged developer documentation for Aspire</SYSTEM>

    # Certificate configuration

    > Learn how to configure HTTPS endpoints and certificate trust for resources in Aspire to enable secure communication.

    Aspire provides two complementary sets of certificate APIs: 1. **HTTPS endpoint APIs**: Configure the certificates that resources use for their own HTTPS endpoints (server authentication) 2. **Certificate trust APIs**: Configure which certificates resources trust when making outbound HTTPS connections (client authentication) Both sets of APIs work together to enable secure HTTPS communication during local development. For example, a Vite frontend might use `WithHttpsDeveloperCertificate` to serve HTTPS traffic, while also using `WithDeveloperCertificateTrust` to trust the dashboardâ€™s OTLP endpoint certificate. Caution Certificate customization only applies at run time. Custom certificates arenâ€™t included in publish or deployment artifacts. ### Why HTTPS matters [Section titled â€œWhy HTTPS mattersâ€](#why-https-matters) HTTPS is essential for protecting the security and privacy of data transmitted between services. It encrypts traffic to prevent eavesdropping, tampering, and man-in-the-middle attacks. For production environments, HTTPS is a fundamental security requirement. However, enabling HTTPS during local development to match the production configuration presents unique challenges. Development environments typically use self-signed certificates that browsers and applications donâ€™t trust by default. Managing these certificates across multiple services, containers, and different language runtimes can be complex and time-consuming, often creating friction in the development workflow. Aspire simplifies HTTPS configuration for local development by providing APIs to: * Configure HTTPS endpoints with appropriate certificates for server authentication * Manage certificate trust so resources can communicate with services using self-signed certificates * Automatically handle the .NET provided ASP.NET Core development certificate (a per-user self-signed certificate valid only for local domains) across different resource types ## HTTPS endpoint configuration [Section titled â€œHTTPS endpoint configurationâ€](#https-endpoint-configuration) HTTPS endpoint configuration determines which certificate a resource presents when serving HTTPS traffic. This is server-side certificate configuration for resources that host HTTPS/TLS endpoints. ### Default behavior [Section titled â€œDefault behaviorâ€](#default-behavior) For resources that have a certificate configuration defined with `WithHttpsCertificateConfiguration`, Aspire attempts to configure it to use the ASP.NET Core development certificate if available. This automatic configuration works for many common resource types including YARP, Redis, and Keycloak containers; Vite based JavaScript apps; and Python apps using Uvicorn. You can control this behavior using the HTTPS endpoint APIs described below. ### Use the development certificate [Section titled â€œUse the development certificateâ€](#use-the-development-certificate) To explicitly configure a resource to use the ASP.NET Core development certificate for its HTTPS endpoints: AppHost.cs ```csharp var builder = DistributedApplication.CreateBuilder(args); // Explicitly use the developer certificate var nodeApp = builder.AddViteApp("frontend", "../frontend") .WithHttpsDeveloperCertificate(); // Use developer certificate with an encrypted private key var certPassword = builder.AddParameter("cert-password", secret: true); var pythonApp = builder.AddUvicornApp("api", "../api", "app:main") .WithHttpsDeveloperCertificate(certPassword); builder.Build().Run(); ``` The `WithHttpsDeveloperCertificate` method: * Configures the resource to use the ASP.NET Core development certificate * Only applies in run mode (local development) * Optionally accepts a password parameter for encrypted certificate private keys * Works with containers, Node.js, Python, and other resource types ### Use a custom certificate [Section titled â€œUse a custom certificateâ€](#use-a-custom-certificate) To configure a resource to use a specific X.509 certificate for HTTPS endpoints: AppHost.cs ```csharp using System.Security.Cryptography.X509Certificates; var builder = DistributedApplication.CreateBuilder(args); // Load your certificate var certificate = new X509Certificate2("path/to/certificate.pfx", "password"); // Use the certificate for HTTPS endpoints builder.AddContainer("api", "my-api:latest") .WithHttpsCertificate(certificate); // Use certificate with a password parameter var certPassword = builder.AddParameter("cert-password", secret: true); builder.AddNpmApp("frontend", "../frontend") .WithHttpsCertificate(certificate, certPassword); builder.Build().Run(); ``` The certificate must: * Include a private key * Be a valid X.509 certificate * Be appropriate for server authentication ### Disable HTTPS certificate configuration [Section titled â€œDisable HTTPS certificate configurationâ€](#disable-https-certificate-configuration) To prevent Aspire from configuring any HTTPS certificate for a resource: AppHost.cs ```csharp var builder = DistributedApplication.CreateBuilder(args); // Disable automatic HTTPS certificate configuration var redis = builder.AddRedis("cache") .WithoutHttpsCertificate(); builder.Build().Run(); ``` Use `WithoutHttpsCertificate` when: * The resource doesnâ€™t support HTTPS * You want to manually configure certificates * The resource has its own certificate management ### Customize certificate configuration [Section titled â€œCustomize certificate configurationâ€](#customize-certificate-configuration) For resources that need custom certificate configuration logic, use `WithHttpsCertificateConfiguration` to specify how certificate files should be passed to the resource: AppHost.cs ```csharp var builder = DistributedApplication.CreateBuilder(args); builder.AddContainer("api", "my-api:latest") .WithHttpsCertificateConfiguration(ctx => { // Pass certificate paths as command line arguments ctx.Arguments.Add("--tls-cert"); ctx.Arguments.Add(ctx.CertificatePath); ctx.Arguments.Add("--tls-key"); ctx.Arguments.Add(ctx.KeyPath); // Or set environment variables ctx.EnvironmentVariables["TLS_CERT_FILE"] = ctx.CertificatePath; ctx.EnvironmentVariables["TLS_KEY_FILE"] = ctx.KeyPath; // Use PFX format if the resource requires it ctx.EnvironmentVariables["TLS_PFX_FILE"] = ctx.PfxPath; // Include password if needed if (ctx.Password is not null) { ctx.EnvironmentVariables["TLS_KEY_PASSWORD"] = ctx.Password; } return Task.CompletedTask; }); builder.Build().Run(); ``` The callback receives an `HttpsCertificateConfigurationCallbackAnnotationContext` that provides: * `CertificatePath`: Path to the certificate file in PEM format * `KeyPath`: Path to the private key file in PEM format * `PfxPath`: Path to the certificate in PFX/PKCS#12 format * `Password`: The password for the private key, if configured * `Arguments`: Command line arguments list to modify * `EnvironmentVariables`: Environment variables dictionary to modify * `ExecutionContext`: The current execution context * `Resource`: The resource being configured ## Certificate trust configuration [Section titled â€œCertificate trust configurationâ€](#certificate-trust-configuration) Certificate trust configuration determines which certificates a resource trusts when making outbound HTTPS connections. This is client-side certificate configuration. ### When to use certificate trust [Section titled â€œWhen to use certificate trustâ€](#when-to-use-certificate-trust) Certificate trust customization is valuable when: * Resources need to trust the ASP.NET Core development certificate for local HTTPS communication * Containerized services must communicate with the dashboard over HTTPS * Python or Node.js applications need to trust custom certificate authorities * Youâ€™re working with services that have specific certificate trust requirements * Resources need to establish secure telemetry connections to the Aspire dashboard ### Development certificate trust [Section titled â€œDevelopment certificate trustâ€](#development-certificate-trust) By default, Aspire attempts to add trust for the ASP.NET Core development certificate to resources that wouldnâ€™t otherwise trust it. This enables resources to communicate with the dashboard OTLP collector endpoint over HTTPS and any other HTTPS endpoints secured by the development certificate. You can control this behavior per resource using the `WithDeveloperCertificateTrust` API or through AppHost configuration settings. #### Configure development certificate trust per resource [Section titled â€œConfigure development certificate trust per resourceâ€](#configure-development-certificate-trust-per-resource) To explicitly enable or disable development certificate trust for a specific resource: AppHost.cs ```csharp var builder = DistributedApplication.CreateBuilder(args); // Explicitly enable development certificate trust var nodeApp = builder.AddNpmApp("frontend", "../frontend") .WithDeveloperCertificateTrust(trust: true); // Disable development certificate trust var pythonApp = builder.AddPythonApp("api", "../api", "main.py") .WithDeveloperCertificateTrust(trust: false); builder.Build().Run(); ``` ### Certificate authority collections [Section titled â€œCertificate authority collectionsâ€](#certificate-authority-collections) Certificate authority collections allow you to bundle custom certificates and make them available to resources. You create a collection using the `AddCertificateAuthorityCollection` method and then reference it from resources that need to trust those certificates. #### Create and use a certificate authority collection [Section titled â€œCreate and use a certificate authority collectionâ€](#create-and-use-a-certificate-authority-collection) AppHost.cs ```csharp using System.Security.Cryptography.X509Certificates; var builder = DistributedApplication.CreateBuilder(args); // Load your custom certificates var certificates = new X509Certificate2Collection(); certificates.ImportFromPemFile("path/to/certificate.pem"); // Create a certificate authority collection var certBundle = builder.AddCertificateAuthorityCollection("my-bundle") .WithCertificates(certificates); // Apply the certificate bundle to resources builder.AddNpmApp("my-project", "../myapp") .WithCertificateAuthorityCollection(certBundle); builder.Build().Run(); ``` In the preceding example, the certificate bundle is created with custom certificates and then applied to a Node.js application, enabling it to trust those certificates. ### Certificate trust scopes [Section titled â€œCertificate trust scopesâ€](#certificate-trust-scopes) Certificate trust scopes control how custom certificates interact with a resourceâ€™s default trusted certificates. Different scopes provide flexibility in managing certificate trust based on your applicationâ€™s requirements. The `WithCertificateTrustScope` API accepts a `CertificateTrustScope` value to specify the trust behavior. #### Available trust scopes [Section titled â€œAvailable trust scopesâ€](#available-trust-scopes) Aspire supports the following certificate trust scopes: * **Append**: Appends custom certificates to the default trusted certificates * **Override**: Replaces the default trusted certificates with only the configured certificates * **System**: Combines custom certificates with system root certificates and uses them to override the defaults * **None**: Disables all custom certificate trust configuration #### Append mode [Section titled â€œAppend modeâ€](#append-mode) Attempts to append the configured certificates to the default trusted certificates for a given resource. This mode is useful when you want to add trust for additional certificates while maintaining trust for the systemâ€™s default certificates. This is the default scope for most resources. For Python resources, only OTEL trust configuration will be applied in this mode. AppHost.cs ```csharp var builder = DistributedApplication.CreateBuilder(args); builder.AddNodeApp("api", "../api") .WithCertificateTrustScope(CertificateTrustScope.Append); builder.Build().Run(); ``` #### Override mode [Section titled â€œOverride modeâ€](#override-mode) Attempts to override a resource to only trust the configured certificates, replacing the default trusted certificates entirely. This mode is useful when you need strict control over which certificates are trusted. AppHost.cs ```csharp var builder = DistributedApplication.CreateBuilder(args); var certBundle = builder.AddCertificateAuthorityCollection("custom-certs") .WithCertificates(myCertificates); builder.AddPythonModule("api", "./api", "uvicorn") .WithCertificateAuthorityCollection(certBundle) .WithCertificateTrustScope(CertificateTrustScope.Override); builder.Build().Run(); ``` #### System mode [Section titled â€œSystem modeâ€](#system-mode) Attempts to combine the configured certificates with the default system root certificates and use them to override the default trusted certificates for a resource. This mode is intended to support Python or other languages that donâ€™t work well with Append mode. This is the default scope for Python projects because Python only has mechanisms to fully override certificate trust. AppHost.cs ```csharp var builder = DistributedApplication.CreateBuilder(args); builder.AddPythonApp("worker", "../worker", "main.py") .WithCertificateTrustScope(CertificateTrustScope.System); builder.Build().Run(); ``` #### None mode [Section titled â€œNone modeâ€](#none-mode) Disables all custom certificate trust for the resource, causing it to rely solely on its default certificate trust behavior. This is the default scope for .NET projects on Windows, as thereâ€™s no way to automatically change the default system store source. AppHost.cs ```csharp var builder = DistributedApplication.CreateBuilder(args); builder.AddContainer("service", "myimage") .WithCertificateTrustScope(CertificateTrustScope.None); builder.Build().Run(); ``` ### Custom certificate trust configuration [Section titled â€œCustom certificate trust configurationâ€](#custom-certificate-trust-configuration) For advanced scenarios, you can specify custom certificate trust behavior using a callback API. This callback allows you to customize the command line arguments and environment variables required to configure certificate trust for different resource types. #### Configure certificate trust with a callback [Section titled â€œConfigure certificate trust with a callbackâ€](#configure-certificate-trust-with-a-callback) Use `WithCertificateTrustConfiguration` to customize how certificate trust is configured for a resource: AppHost.cs ```csharp var builder = DistributedApplication.CreateBuilder(args); builder.AddContainer("api", "myimage") .WithCertificateTrustConfiguration(ctx => { // Add a command line argument ctx.Arguments.Add("--use-system-ca"); // Set environment variables with certificate paths // CertificateBundlePath resolves to the path of the custom certificate bundle file ctx.EnvironmentVariables["MY_CUSTOM_CERT_VAR"] = ctx.CertificateBundlePath; // CertificateDirectoriesPath resolves to paths containing individual certificates ctx.EnvironmentVariables["CERTS_DIR"] = ctx.CertificateDirectoriesPath; return Task.CompletedTask; }); builder.Build().Run(); ``` The callback receives a `CertificateTrustConfigurationCallbackAnnotationContext` that provides: * `Scope`: The `CertificateTrustScope` for the resource. * `Arguments`: Command line arguments for the resource. Values can be strings or path providers like `CertificateBundlePath` or `CertificateDirectoriesPath`. * `EnvironmentVariables`: Environment variables for configuring certificate trust. The dictionary key is the environment variable name; values can be strings or path providers. By default, includes `SSL_CERT_DIR` and may include `SSL_CERT_FILE` if Override or System scope is configured. * `CertificateBundlePath`: A value provider that resolves to the path of a custom certificate bundle file. * `CertificateDirectoriesPath`: A value provider that resolves to paths containing individual certificates. Default implementations are provided for Node.js, Python, and container resources. Container resources rely on standard OpenSSL configuration options, with default values that support the majority of common Linux distributions. #### Configure container certificate paths [Section titled â€œConfigure container certificate pathsâ€](#configure-container-certificate-paths) For container resources, you can customize where certificates are stored and accessed using `WithContainerCertificatePaths`: AppHost.cs ```csharp var builder = DistributedApplication.CreateBuilder(args); builder.AddContainer("api", "myimage") .WithContainerCertificatePaths( customCertificatesDestination: "/custom/certs/path", defaultCertificateBundlePaths: ["/etc/ssl/certs/ca-certificates.crt"], defaultCertificateDirectoryPaths: ["/etc/ssl/certs"]); builder.Build().Run(); ``` The `WithContainerCertificatePaths` API accepts three optional parameters: * `customCertificatesDestination`: Overrides the base path in the container where custom certificate files are placed. If not set or set to `null`, the default path of `/usr/lib/ssl/aspire` is used. * `defaultCertificateBundlePaths`: Overrides the path(s) in the container where a default certificate authority bundle file is located. When the `CertificateTrustScope` is Override or System, the custom certificate bundle is additionally written to these paths. If not set or set to `null`, a set of default certificate paths for common Linux distributions is used. * `defaultCertificateDirectoryPaths`: Overrides the path(s) in the container where individual trusted certificate files are found. When the `CertificateTrustScope` is Append, these paths are concatenated with the path to the uploaded certificate artifacts. If not set or set to `null`, a set of default certificate paths for common Linux distributions is used. ## Common scenarios [Section titled â€œCommon scenariosâ€](#common-scenarios) This section demonstrates common patterns for configuring HTTPS endpoints and certificate trust together. ### Configure a service with HTTPS and enable dashboard telemetry [Section titled â€œConfigure a service with HTTPS and enable dashboard telemetryâ€](#configure-a-service-with-https-and-enable-dashboard-telemetry) A typical scenario is configuring a Node.js service to serve HTTPS traffic while also enabling it to send telemetry to the dashboard: AppHost.cs ```csharp var builder = DistributedApplication.CreateBuilder(args); // Configure the service to use developer certificate for HTTPS endpoints // and trust the developer certificate for outbound connections (like dashboard telemetry) var frontend = builder.AddNpmApp("frontend", "../frontend") .WithHttpsDeveloperCertificate() // Server cert for HTTPS endpoints .WithDeveloperCertificateTrust(true); // Client trust for dashboard builder.Build().Run(); ``` ### Enable HTTPS with custom certificates [Section titled â€œEnable HTTPS with custom certificatesâ€](#enable-https-with-custom-certificates) When working with corporate or custom CA certificates, you can configure both server and client certificates: AppHost.cs ```csharp using System.Security.Cryptography.X509Certificates; var builder = DistributedApplication.CreateBuilder(args); // Load custom certificates var serverCert = new X509Certificate2("server-cert.pfx", "password"); var customCA = new X509Certificate2Collection(); customCA.Import("corporate-ca.pem"); var caBundle = builder.AddCertificateAuthorityCollection("corporate-certs") .WithCertificates(customCA); // Configure service with custom server cert and CA trust builder.AddContainer("api", "my-api:latest") .WithHttpsCertificate(serverCert) // Server cert for HTTPS .WithCertificateAuthorityCollection(caBundle); // Trust corporate CA builder.Build().Run(); ``` ### Configure Redis with TLS [Section titled â€œConfigure Redis with TLSâ€](#configure-redis-with-tls) Redis resources can be configured to use HTTPS (TLS) for secure connections: AppHost.cs ```csharp var builder = DistributedApplication.CreateBuilder(args); // Configure Redis to use the developer certificate for TLS var redis = builder.AddRedis("cache") .WithHttpsDeveloperCertificate(); // Or disable TLS entirely var redisNoTls = builder.AddRedis("cache-notls") .WithoutHttpsCertificate(); builder.Build().Run(); ``` ### Disable certificate configuration for specific resources [Section titled â€œDisable certificate configuration for specific resourcesâ€](#disable-certificate-configuration-for-specific-resources) To disable both HTTPS endpoint configuration and certificate trust for a resource that manages its own certificates: AppHost.cs ```csharp var builder = DistributedApplication.CreateBuilder(args); // Disable all automatic certificate configuration builder.AddPythonModule("api", "./api", "uvicorn") .WithoutHttpsCertificate() // No server cert config .WithCertificateTrustScope(CertificateTrustScope.None); // No client trust config builder.Build().Run(); ``` ## Limitations [Section titled â€œLimitationsâ€](#limitations) Certificate configuration has the following limitations: * Currently supported only in run mode, not in publish mode * Not all languages and runtimes support all trust scope modes * Python applications donâ€™t natively support Append mode for certificate trust * Custom certificate configuration requires appropriate runtime support within the resource * HTTPS endpoint APIs are marked as experimental (`ASPIRECERTIFICATES001`)

    # AppHost configuration

    > Learn about the Aspire AppHost configuration options.

    The AppHost project configures and starts your distributed application. When a `DistributedApplication` runs it reads configuration from the AppHost. Configuration is loaded from environment variables that are set on the AppHost and `DistributedApplicationOptions`. Configuration includes: * Settings for hosting the resource service, such as the address and authentication options. * Settings used to start the [Aspire dashboard](/dashboard/overview/), such the dashboardâ€™s frontend and OpenTelemetry Protocol (OTLP) addresses. * Internal settings that Aspire uses to run the AppHost. These are set internally but can be accessed by integrations that extend Aspire. AppHost configuration is provided by the AppHost launch profile. The AppHost has a launch settings file call *launchSettings.json* which has a list of launch profiles. Each launch profile is a collection of related options which defines how you would like `dotnet` to start your application. launchSettings.json ```json { "$schema": "https://json.schemastore.org/launchsettings.json", "profiles": { "https": { "commandName": "Project", "dotnetRunMessages": true, "launchBrowser": true, "applicationUrl": "https://localhost:17134;http://localhost:15170", "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development", "DOTNET_ENVIRONMENT": "Development", "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://localhost:21030", "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "https://localhost:22057" } } } } ``` The preceding launch settings file: * Has one launch profile named `https`. * Configures an Aspire AppHost project: * The `applicationUrl` property configures the dashboard launch address (`ASPNETCORE_URLS`). * Environment variables such as `ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL` and `ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL` are set on the AppHost. For more information, see [Launch profiles](/fundamentals/launch-profiles/). ## Common configuration [Section titled â€œCommon configurationâ€](#common-configuration) | Option | Default value | Description | | ---------------------------------- | ------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | | `ASPIRE_ALLOW_UNSECURED_TRANSPORT` | `false` | Allows communication with the AppHost without https. `ASPNETCORE_URLS` (dashboard address) and `ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL` (AppHost resource service address) must be secured with HTTPS unless true. | | `ASPIRE_CONTAINER_RUNTIME` | `docker` | Allows the user of alternative container runtimes for resources backed by containers. Possible values are `docker` (default) or `podman`. | | `ASPIRE_VERSION_CHECK_DISABLED` | `false` | When set to `true`, Aspire doesnâ€™t check for newer versions on startup. | ## Version update notifications [Section titled â€œVersion update notificationsâ€](#version-update-notifications) When an Aspire app starts, it checks if a newer version of Aspire is available on NuGet. If a new version is found, a notification appears in the dashboard with the latest version number, [a link to upgrade instructions](https://aka.ms/dotnet/aspire/update-latest), and button to ignore that version in the future. ![Screenshot of dashboard showing a version update notification with upgrade options.](/_astro/dashboard-update-notification.CbuDufvf_Z2mm2cn.webp) The version check runs only when: * The dashboard is enabled (interaction service is available). * At least 2 days have passed since the last check. * The check hasnâ€™t been disabled via the `ASPIRE_VERSION_CHECK_DISABLED` configuration setting. * The app is not running in publish mode. Updates are manual. You need to edit your project file to upgrade the Aspire SDK and package versions. ## Resource service [Section titled â€œResource serviceâ€](#resource-service) A resource service is hosted by the AppHost. The resource service is used by the dashboard to fetch information about resources which are being orchestrated by Aspire. | Option | Default value | Description | | ----------------------------------------- | ---------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | | `ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL` | `null` | Configures the address of the resource service hosted by the AppHost. Automatically generated with *launchSettings.json* to have a random port on localhost. For example, `https://localhost:17037`. | | `ASPIRE_DASHBOARD_RESOURCESERVICE_APIKEY` | Automatically generated 128-bit entropy token. | The API key used to authenticate requests made to the AppHostâ€™s resource service. The API key is required if the AppHost is in run mode, the dashboard isnâ€™t disabled, and the dashboard isnâ€™t configured to allow anonymous access with `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS`. | ## Dashboard [Section titled â€œDashboardâ€](#dashboard) By default, the dashboard is automatically started by the AppHost. The dashboard supports [its own set of configuration](/dashboard/configuration/), and some settings can be configured from the AppHost. | Option | Default value | Description | | ------------------------------------------- | ----------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | | `ASPNETCORE_URLS` | `null` | Dashboard address. Must be `https` unless `ASPIRE_ALLOW_UNSECURED_TRANSPORT` or `DistributedApplicationOptions.AllowUnsecuredTransport` is true. Automatically generated with *launchSettings.json* to have a random port on localhost. The value in launch settings is set on the `applicationUrls` property. | | `ASPNETCORE_ENVIRONMENT` | `Production` | Configures the environment the dashboard runs as. For more information, see [Use multiple environments in ASP.NET Core](https://learn.microsoft.com/aspnet/core/fundamentals/environments). | | `ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL` | `http://localhost:18889` if no gRPC endpoint is configured. | Configures the dashboard OTLP gRPC address. Used by the dashboard to receive telemetry over OTLP. Set on resources as the `OTEL_EXPORTER_OTLP_ENDPOINT` env var. The `OTEL_EXPORTER_OTLP_PROTOCOL` env var is `grpc`. Automatically generated with *launchSettings.json* to have a random port on localhost. | | `ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL` | `null` | Configures the dashboard OTLP HTTP address. Used by the dashboard to receive telemetry over OTLP. If only `ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL` is configured then it is set on resources as the `OTEL_EXPORTER_OTLP_ENDPOINT` env var. The `OTEL_EXPORTER_OTLP_PROTOCOL` env var is `http/protobuf`. | | `ASPIRE_DASHBOARD_CORS_ALLOWED_ORIGINS` | `null` | Overrides the CORS allowed origins configured in the dashboard. This setting replaces the default behavior of calculating allowed origins based on resource endpoints. | | `ASPIRE_DASHBOARD_FRONTEND_BROWSERTOKEN` | Automatically generated 128-bit entropy token. | Configures the frontend browser token. This is the value that must be entered to access the dashboard when the auth mode is BrowserToken. If no browser token is specified then a new token is generated each time the AppHost is launched. | | `ASPIRE_DASHBOARD_TELEMETRY_OPTOUT` | `false` | Configures the dashboard to never send [usage telemetry](/dashboard/microsoft-collected-dashboard-telemetry/). | | `ASPIRE_DASHBOARD_AI_DISABLED` | `false` | [GitHub Copilot in the dashboard](/dashboard/copilot/) is available when the AppHost is launched by a supported IDE. When set to `true` Copilot is disabled in the dashboard and no Copilot UI is visible. | | `ASPIRE_DASHBOARD_FORWARDEDHEADERS_ENABLED` | `false` | Enables the Forwarded headers middleware that replaces the scheme and host values on the Request context with the values coming from the `X-Forwarded-Proto` and `X-Forwarded-Host` headers. | ## Internal [Section titled â€œInternalâ€](#internal) Internal settings are used by the AppHost and integrations. Internal settings arenâ€™t designed to be configured directly. | Option | Default value | Description | | ---------------------------------- | ----------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | | `AppHost:Directory` | The content root if thereâ€™s no project. | Directory of the project where the AppHost is located. Accessible from the `IDistributedApplicationBuilder.AppHostDirectory`. | | `AppHost:Path` | The directory combined with the application name. | The path to the AppHost. It combines the directory with the application name. | | `AppHost:Sha256` | It is created from the AppHost name when the AppHost is in publish mode. Otherwise it is created from the AppHost path. | Hex encoded hash for the current application. The hash is based on the location of the app on the current machine so it is stable between launches of the AppHost. | | `AppHost:OtlpApiKey` | Automatically generated 128-bit entropy token. | The API key used to authenticate requests sent to the dashboard OTLP service. The value is present if needed: the AppHost is in run mode, the dashboard isnâ€™t disabled, and the dashboard isnâ€™t configured to allow anonymous access with `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS`. | | `AppHost:BrowserToken` | Automatically generated 128-bit entropy token. | The browser token used to authenticate browsing to the dashboard when it is launched by the AppHost. The browser token can be set by `ASPIRE_DASHBOARD_FRONTEND_BROWSERTOKEN`. The value is present if needed: the AppHost is in run mode, the dashboard isnâ€™t disabled, and the dashboard isnâ€™t configured to allow anonymous access with `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS`. | | `AppHost:ResourceService:AuthMode` | `ApiKey`. If `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS` is true then the value is `Unsecured`. | The authentication mode used to access the resource service. The value is present if needed: the AppHost is in run mode and the dashboard isnâ€™t disabled. | | `AppHost:ResourceService:ApiKey` | Automatically generated 128-bit entropy token. | The API key used to authenticate requests made to the AppHostâ€™s resource service. The API key can be set by `ASPIRE_DASHBOARD_RESOURCESERVICE_APIKEY`. The value is present if needed: the AppHost is in run mode, the dashboard isnâ€™t disabled, and the dashboard isnâ€™t configured to allow anonymous access with `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS`. |

    # Docker Compose to Aspire AppHost

    > Quick reference for converting Docker Compose YAML syntax to Aspire C# API calls.

    This reference provides systematic mappings from Docker Compose YAML syntax to equivalent Aspire C# API calls. Use these tables as a quick reference when converting your existing Docker Compose files to Aspire application host configurations. ## Service definitions [Section titled â€œService definitionsâ€](#service-definitions) | Docker Compose | Aspire | Notes | | --------------- | ---------------------------------------------------------- | ------------------------------------------------------------------- | | `services:` | `var builder = DistributedApplication.CreateBuilder(args)` | Root application builder used for adding and representing resources | | `service_name:` | `builder.Add*("service_name")` | Service name becomes resource name | Learn more about [Docker Compose services](https://docs.docker.com/compose/compose-file/05-services/). ## Images and builds [Section titled â€œImages and buildsâ€](#images-and-builds) | Docker Compose | Aspire | Notes | | ------------------------------------- | ---------------------------------------------------------- | ------------------------ | | `image: nginx:latest` | `builder.AddContainer("name", "nginx", "latest")` | Direct image reference | | `build: .` | `builder.AddDockerfile("name", ".")` | Build from Dockerfile | | `build: ./path` | `builder.AddDockerfile("name", "./path")` | Build from specific path | | `build.context: ./app` | `builder.AddDockerfile("name", "./app")` | Build context | | `build.dockerfile: Custom.dockerfile` | `builder.Add*("name").WithDockerfile("Custom.dockerfile")` | Custom Dockerfile name | Learn more about [Docker Compose build reference](https://docs.docker.com/compose/compose-file/build/) and [WithDockerfile](/app-host/withdockerfile/). ## .NET projects [Section titled â€œ.NET projectsâ€](#net-projects) | Docker Compose | Aspire | Notes | | --------------------------- | --------------------------------------------- | ----------------------------- | | `build: ./MyApi` (for .NET) | `builder.AddProject<Projects.MyApi>("myapi")` | Direct .NET project reference | Learn more about [adding .NET projects](/get-started/app-host/). ## Port mappings [Section titled â€œPort mappingsâ€](#port-mappings) | Docker Compose | Aspire | Notes | | -------------------- | ------------------------------------------------ | ----------------------------------------------------------------------------- | | `ports: ["8080:80"]` | `.WithHttpEndpoint(port: 8080, targetPort: 80)` | HTTP endpoint mapping. Ports are optional; dynamic ports are used if omitted | | `ports: ["443:443"]` | `.WithHttpsEndpoint(port: 443, targetPort: 443)` | HTTPS endpoint mapping. Ports are optional; dynamic ports are used if omitted | | `expose: ["8080"]` | `.WithEndpoint(port: 8080)` | Internal port exposure. Ports are optional; dynamic ports are used if omitted | Learn more about [Docker Compose ports](https://docs.docker.com/compose/compose-file/05-services/#ports) and [endpoint configuration](/fundamentals/networking-overview/). ## Environment variables [Section titled â€œEnvironment variablesâ€](#environment-variables) | Docker Compose | Aspire / Notes | | ------------------------------ | ----------------------------------------------------------------------------------------------------------------------- | | `environment: KEY=value` | `.WithEnvironment("KEY", "value")` Static environment variable | | `environment: KEY=${HOST_VAR}` | `.WithEnvironment(context => context.EnvironmentVariables["KEY"] = hostVar)` Environment variable with callback context | | `env_file: .env` | `.ConfigureEnvFile(env => { ... })` Environment file generation (available in 13.1+) | Learn more about [Docker Compose environment](https://docs.docker.com/compose/compose-file/05-services/#environment) and [external parameters](/fundamentals/external-parameters/). ## Volumes and storage [Section titled â€œVolumes and storageâ€](#volumes-and-storage) | Docker Compose | Aspire | Notes | | -------------------------------- | ------------------------------------------------------ | -------------------- | | `volumes: ["data:/app/data"]` | `.WithVolume("data", "/app/data")` | Named volume | | `volumes: ["./host:/container"]` | `.WithBindMount("./host", "/container")` | Bind mount | | `volumes: ["./config:/app:ro"]` | `.WithBindMount("./config", "/app", isReadOnly: true)` | Read-only bind mount | Learn more about [Docker Compose volumes](https://docs.docker.com/compose/compose-file/05-services/#volumes) and [persist container data](/fundamentals/persist-data-volumes/). ## Dependencies and ordering [Section titled â€œDependencies and orderingâ€](#dependencies-and-ordering) | Docker Compose | Aspire | Notes | | -------------------------------------------- | ------------------------ | --------------------------------------------------- | | `depends_on: [db]` | `.WithReference(db)` | Service dependency with connection string injection | | `depends_on: db: condition: service_started` | `.WaitFor(db)` | Wait for service start | | `depends_on: db: condition: service_healthy` | `.WaitForCompletion(db)` | Wait for health check to pass | Learn more about [Docker Compose depends\_on](https://docs.docker.com/compose/compose-file/05-services/#depends_on) and [launch profiles](/fundamentals/launch-profiles/). ## Networks [Section titled â€œNetworksâ€](#networks) | Docker Compose | Aspire | Notes | | --------------------- | ---------- | ----------------------------------------------------- | | `networks: [backend]` | Automatic | Aspire handles networking automatically | | Custom networks | Not needed | Service discovery handles inter-service communication | Learn more about [Docker Compose networks](https://docs.docker.com/compose/compose-file/05-services/#networks) and [service discovery](/fundamentals/service-discovery/). ## Resource limits [Section titled â€œResource limitsâ€](#resource-limits) | Docker Compose | Aspire | Notes | | -------------------------------------- | ------------- | ------------------------------------------ | | `deploy.resources.limits.memory: 512m` | Not supported | Resource limits arenâ€™t supported in Aspire | | `deploy.resources.limits.cpus: 0.5` | Not supported | Resource limits arenâ€™t supported in Aspire | Learn more about [Docker Compose deploy reference](https://docs.docker.com/compose/compose-file/deploy/). ## Health checks [Section titled â€œHealth checksâ€](#health-checks) | Docker Compose | Aspire | Notes | | -------------------------------------------------------------- | --------------------------- | -------------------------------------------------- | | `healthcheck.test: ["CMD", "curl", "http://localhost/health"]` | Built-in for integrations | Aspire integrations include health checks | | `healthcheck.interval: 30s` | Configurable in integration | Health check configuration varies by resource type | Learn more about [Docker Compose healthcheck](https://docs.docker.com/compose/compose-file/05-services/#healthcheck) and [health checks](/fundamentals/health-checks/). ## Restart policies [Section titled â€œRestart policiesâ€](#restart-policies) | Docker Compose | Aspire | Notes | | ------------------------- | ------------- | ------------------------------------------- | | `restart: unless-stopped` | Not supported | Restart policies arenâ€™t supported in Aspire | | `restart: always` | Not supported | Restart policies arenâ€™t supported in Aspire | | `restart: no` | Default | No restart policy | Learn more about [Docker Compose restart](https://docs.docker.com/compose/compose-file/05-services/#restart). ## Logging [Section titled â€œLoggingâ€](#logging) | Docker Compose | Aspire | Notes | | ------------------------------- | ----------------------- | ---------------------------------- | | `logging.driver: json-file` | Built-in | Aspire provides integrated logging | | `logging.options.max-size: 10m` | Dashboard configuration | Managed through Aspire dashboard | Learn more about [Docker Compose logging](https://docs.docker.com/compose/compose-file/05-services/#logging) and [telemetry](/fundamentals/telemetry/). ## Database services [Section titled â€œDatabase servicesâ€](#database-services) | Docker Compose | Aspire | Notes | | --------------------- | ----------------------------- | --------------------------------------- | | `image: postgres:15` | `builder.AddPostgres("name")` | PostgreSQL with automatic configuration | | `image: mysql:8` | `builder.AddMySql("name")` | MySQL with automatic configuration | | `image: redis:7` | `builder.AddRedis("name")` | Redis with automatic configuration | | `image: mongo:latest` | `builder.AddMongoDB("name")` | MongoDB with automatic configuration | Learn more about [Docker Compose services](https://docs.docker.com/compose/compose-file/05-services/) and [database integrations](/integrations/gallery/?search=database). ## See also [Section titled â€œSee alsoâ€](#see-also) * [Migrate from Docker Compose to Aspire](/app-host/migrate-from-docker-compose/) * [AppHost overview](/get-started/app-host/) * [WithDockerfile](/app-host/withdockerfile/)

    # AppHost eventing APIs

    > Learn how to use the Aspire AppHost eventing features.

    In Aspire, eventing allows you to publish and subscribe to events during various AppHost life cycles. Eventing is more flexible than life cycle events. Both let you run arbitrary code during event callbacks, but eventing offers finer control of event timing, publishing, and provides supports for custom events. The eventing mechanisms in Aspire are part of the [ðŸ“¦ Aspire.Hosting](https://www.nuget.org/packages/Aspire.Hosting) NuGet package. This package provides a set of interfaces and classes in the `Aspire.Hosting.Eventing` namespace that you use to publish and subscribe to events in your Aspire AppHost project. Eventing is scoped to the AppHost itself and the resources within. In this article, you learn how to use the eventing features in Aspire. ## AppHost eventing [Section titled â€œAppHost eventingâ€](#apphost-eventing) The following events are available in the AppHost and occur in the following order: 1. `BeforeStartEvent`: This event is raised before the AppHost starts. 2. `ResourceEndpointsAllocatedEvent`: This event is raised per resource after its endpoints are allocated. 3. `AfterResourcesCreatedEvent`: This event is raised after the AppHost created resources. ### Subscribe to AppHost events [Section titled â€œSubscribe to AppHost eventsâ€](#subscribe-to-apphost-events) To subscribe to the built-in AppHost events, use the eventing API. After you have a distributed application builder instance, walk up to the `IDistributedApplicationBuilder.Eventing` property and call the `Subscribe` API. Consider the following sample AppHost *AppHost.cs* file: AppHost.cs ```csharp using Microsoft.Extensions.DependencyInjection; using Microsoft.Extensions.Logging; var builder = DistributedApplication.CreateBuilder(args); var cache = builder.AddRedis("cache"); var apiService = builder.AddProject<Projects.AspireApp_ApiService>("apiservice"); builder.AddProject<Projects.AspireApp_Web>("webfrontend") .WithExternalHttpEndpoints() .WithReference(cache) .WaitFor(cache) .WithReference(apiService) .WaitFor(apiService); builder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>( static (@event, cancellationToken) => { var logger = @event.Services.GetRequiredService<ILogger<Program>>(); logger.LogInformation("2. \"{ResourceName}\" ResourceEndpointsAllocatedEvent", @event.Resource.Name); return Task.CompletedTask; }); builder.Eventing.Subscribe<BeforeStartEvent>( static (@event, cancellationToken) => { var logger = @event.Services.GetRequiredService<ILogger<Program>>(); logger.LogInformation("1. BeforeStartEvent"); return Task.CompletedTask; }); builder.Eventing.Subscribe<AfterResourcesCreatedEvent>( static (@event, cancellationToken) => { var logger = @event.Services.GetRequiredService<ILogger<Program>>(); logger.LogInformation("3. AfterResourcesCreatedEvent"); return Task.CompletedTask; }); builder.Build().Run(); ``` The preceding code is based on the starter template with the addition of the calls to the `Subscribe` API. The `Subscribe<T>` API returns a `DistributedApplicationEventSubscription` instance that you can use to unsubscribe from the event. Itâ€™s common to discard the returned subscriptions, as you donâ€™t usually need to unsubscribe from events as the entire app is torn down when the AppHost is shut down. When the AppHost is run, by the time the Aspire dashboard is displayed, you should see the following log output in the console: ```plaintext info: Program[0] 1. BeforeStartEvent info: Aspire.Hosting.DistributedApplication[0] Aspire version: 13.1.0 info: Aspire.Hosting.DistributedApplication[0] Distributed application starting. info: Aspire.Hosting.DistributedApplication[0] Application host directory is: ../AspireApp/AspireApp.AppHost info: Program[0] 2. "cache" ResourceEndpointsAllocatedEvent info: Program[0] 2. "apiservice" ResourceEndpointsAllocatedEvent info: Program[0] 2. "webfrontend" ResourceEndpointsAllocatedEvent info: Program[0] 2. "aspire-dashboard" ResourceEndpointsAllocatedEvent info: Aspire.Hosting.DistributedApplication[0] Now listening on: https://localhost:17178 info: Aspire.Hosting.DistributedApplication[0] Login to the dashboard at https://localhost:17178/login?t=<YOUR_TOKEN> info: Program[0] 3. AfterResourcesCreatedEvent info: Aspire.Hosting.DistributedApplication[0] Distributed application started. Press Ctrl+C to shut down. ``` The log output confirms that event handlers are executed in the order of the AppHost life cycle events. The subscription order doesnâ€™t affect execution order. The `BeforeStartEvent` is triggered first, followed by each resourceâ€™s `ResourceEndpointsAllocatedEvent`, and finally `AfterResourcesCreatedEvent`. ## Resource eventing [Section titled â€œResource eventingâ€](#resource-eventing) In addition to the AppHost events, you can also subscribe to resource events. Resource events are raised specific to an individual resource. Resource events are defined as implementations of the `IDistributedApplicationResourceEvent` interface. The following resource events are available in the listed order: 1. `InitializeResourceEvent`: Raised by orchestrators to signal to resources that they should initialize themselves. 2. `ResourceEndpointsAllocatedEvent`: Raised when the orchestrator allocates endpoints for a resource. 3. `ConnectionStringAvailableEvent`: Raised when a connection string becomes available for a resource. 4. `BeforeResourceStartedEvent`: Raised before the orchestrator starts a new resource. 5. `ResourceReadyEvent`: Raised when a resource initially transitions to a ready state. ### Subscribe to resource events [Section titled â€œSubscribe to resource eventsâ€](#subscribe-to-resource-events) To subscribe to resource events, use the convenience-based extension methodsâ€”`On*`. After you have a distributed application builder instance, and a resource builder, walk up to the instance and chain a call to the desired `On*` event API. Consider the following sample *AppHost.cs* file: AppHost.cs ```csharp using Microsoft.Extensions.DependencyInjection; using Microsoft.Extensions.Logging; var builder = DistributedApplication.CreateBuilder(args); var cache = builder.AddRedis("cache"); cache.OnResourceReady(static (resource, @event, cancellationToken) => { var logger = @event.Services.GetRequiredService<ILogger<Program>>(); logger.LogInformation("5. OnResourceReady"); return Task.CompletedTask; }); cache.OnInitializeResource( static (resource, @event, cancellationToken) => { var logger = @event.Services.GetRequiredService<ILogger<Program>>(); logger.LogInformation("1. OnInitializeResource"); return Task.CompletedTask; }); cache.OnBeforeResourceStarted( static (resource, @event, cancellationToken) => { var logger = @event.Services.GetRequiredService<ILogger<Program>>(); logger.LogInformation("4. OnBeforeResourceStarted"); return Task.CompletedTask; }); cache.OnResourceEndpointsAllocated( static (resource, @event, cancellationToken) => { var logger = @event.Services.GetRequiredService<ILogger<Program>>(); logger.LogInformation("2. OnResourceEndpointsAllocated"); return Task.CompletedTask; }); cache.OnConnectionStringAvailable( static (resource, @event, cancellationToken) => { var logger = @event.Services.GetRequiredService<ILogger<Program>>(); logger.LogInformation("3. OnConnectionStringAvailable"); return Task.CompletedTask; }); var apiService = builder.AddProject<Projects.AspireApp_ApiService>("apiservice"); builder.AddProject<Projects.AspireApp_Web>("webfrontend") .WithExternalHttpEndpoints() .WithReference(cache) .WaitFor(cache) .WithReference(apiService) .WaitFor(apiService); builder.Build().Run(); ``` The preceding code subscribes to the `InitializeResourceEvent`, `ResourceReadyEvent`, `ResourceEndpointsAllocatedEvent`, `ConnectionStringAvailableEvent`, and `BeforeResourceStartedEvent` events on the `cache` resource. When `AddRedis` is called, it returns an `IResourceBuilder<T>` where `T` is a `RedisResource`. Chain calls to the `On*` methods to subscribe to the events. The `On*` methods return the same `IResourceBuilder<T>` instance, so you can chain multiple calls: * `OnInitializeResource`: Subscribes to the `InitializeResourceEvent`. * `OnResourceEndpointsAllocated`: Subscribes to the `ResourceEndpointsAllocatedEvent` event. * `OnConnectionStringAvailable`: Subscribes to the `ConnectionStringAvailableEvent` event. * `OnBeforeResourceStarted`: Subscribes to the `BeforeResourceStartedEvent` event. * `OnResourceReady`: Subscribes to the `ResourceReadyEvent` event. When the AppHost is run, by the time the Aspire dashboard is displayed, you should see the following log output in the console: ```plaintext info: Aspire.Hosting.DistributedApplication[0] Aspire version: 13.1.0 info: Aspire.Hosting.DistributedApplication[0] Distributed application starting. info: Aspire.Hosting.DistributedApplication[0] Application host directory is: ../AspireApp/AspireApp.AppHost info: Program[0] 1. OnInitializeResource info: Program[0] 2. OnResourceEndpointsAllocated info: Program[0] 3. OnConnectionStringAvailable info: Program[0] 4. OnBeforeResourceStarted info: Aspire.Hosting.DistributedApplication[0] Now listening on: https://localhost:17222 info: Aspire.Hosting.DistributedApplication[0] Login to the dashboard at https://localhost:17222/login?t=<YOUR_TOKEN> info: Program[0] 5. OnResourceReady info: Aspire.Hosting.DistributedApplication[0] Distributed application started. Press Ctrl+C to shut down. ``` ## Publish events [Section titled â€œPublish eventsâ€](#publish-events) When subscribing to any of the built-in events, you donâ€™t need to publish the event yourself as the AppHost orchestrator manages to publish built-in events on your behalf. However, you can publish custom events with the eventing API. To publish an event, you have to first define an event as an implementation of either the `IDistributedApplicationEvent` or `IDistributedApplicationResourceEvent` interface. You need to determine which interface to implement based on whether the event is a global AppHost event or a resource-specific event. Then, you can subscribe and publish the event by calling the either of the following APIs: * `PublishAsync<T>(T, CancellationToken)`: Publishes an event to all subscribes of the specific event type. * `PublishAsync<T>(T, EventDispatchBehavior, CancellationToken)`: Publishes an event to all subscribes of the specific event type with a specified dispatch behavior. ### Provide an `EventDispatchBehavior` [Section titled â€œProvide an EventDispatchBehaviorâ€](#provide-an-eventdispatchbehavior) When events are dispatched, you can control how the events are dispatched to subscribers. The event dispatch behavior is specified with the `EventDispatchBehavior` enum. The following behaviors are available: * `EventDispatchBehavior.BlockingSequential`: Fires events sequentially and blocks until theyâ€™re all processed. * `EventDispatchBehavior.BlockingConcurrent`: Fires events concurrently and blocks until theyâ€™re all processed. * `EventDispatchBehavior.NonBlockingSequential`: Fires events sequentially but doesnâ€™t block. * `EventDispatchBehavior.NonBlockingConcurrent`: Fires events concurrently but doesnâ€™t block. The default behavior is `EventDispatchBehavior.BlockingSequential`. To override this behavior, when calling a publishing API such as `PublishAsync`, provide the desired behavior as an argument. ## Eventing subscribers [Section titled â€œEventing subscribersâ€](#eventing-subscribers) In some cases, such as extension libraries, you may need to access lifecycle events from a service rather than directly from the Aspire application model. You can implement `IDistributedApplicationEventingSubscriber` and register the service with `AddEventingSubscriber` (or `TryAddEventingSubscriber` if you want to avoid duplicate registrations). AppHost.cs ```csharp using Aspire.Hosting.Eventing; using Microsoft.Extensions.DependencyInjection; using Microsoft.Extensions.Logging; var builder = DistributedApplication.CreateBuilder(args); builder.Services.AddEventingSubscriber<LifecycleLoggerSubscriber>(); builder.Build().Run(); internal sealed class LifecycleLoggerSubscriber(ILogger<LifecycleLoggerSubscriber> logger) : IDistributedApplicationEventingSubscriber { public Task SubscribeAsync( IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken) { eventing.Subscribe<BeforeStartEvent>((@event, ct) => { logger.LogInformation("1. BeforeStartEvent"); return Task.CompletedTask; }); eventing.Subscribe<ResourceEndpointsAllocatedEvent>((@event, ct) => { logger.LogInformation("2. {Resource} ResourceEndpointsAllocatedEvent", @event.Resource.Name); return Task.CompletedTask; }); eventing.Subscribe<AfterResourcesCreatedEvent>((@event, ct) => { logger.LogInformation("3. AfterResourcesCreatedEvent"); return Task.CompletedTask; }); return Task.CompletedTask; } } ``` The subscriber approach keeps builder code minimal while still letting you respond to the same lifecycle moments as inline subscriptions: * `AddEventingSubscriber<T>()` (or `TryAddEventingSubscriber()`) ensures the subscriber participates whenever the AppHost starts. * `SubscribeAsync` is called once per AppHost execution, giving you access to `IDistributedApplicationEventing` and the `DistributedApplicationExecutionContext` should you need model- or environment-specific data. * You can register handlers for any built-in event (AppHost or resource) or for your own custom `IDistributedApplicationEvent` types. Use this pattern whenever you previously relied on `IDistributedApplicationLifecycleHook`. The lifecycle hook APIs remain only for backward compatibility and will be removed in a future release.
    """;

    #endregion
}
