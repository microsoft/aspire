// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREGITHUBPAGES001
#pragma warning disable ASPIRECOMPUTE002
#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREJAVASCRIPT001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Tests.Publishing;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Aspire.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.GitHubPages.Tests;

public class GitHubPagesHostingTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("owner/repository", "https://owner.github.io/repository/", "https://owner.github.io", "/repository/")]
    [InlineData("Owner/Owner.github.io", "https://owner.github.io/", "https://owner.github.io", "/")]
    [InlineData("owner/my-site", "https://owner.github.io/my-site/", "https://owner.github.io", "/my-site/")]
    public void DefaultAddresses(string repository, string url, string origin, string basePath)
    {
        var environment = new GitHubPagesEnvironmentResource("pages", repository);

        Assert.Equal(url, environment.SiteUrl.AbsoluteUri);
        Assert.Equal(origin, environment.Origin);
        Assert.Equal(basePath, environment.BasePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("owner")]
    [InlineData("owner/repo/extra")]
    [InlineData("owner/..")]
    [InlineData("-owner/repo")]
    [InlineData("owner_/repo")]
    [InlineData("owner/repo?query")]
    [InlineData("owner/repo\\other")]
    public void InvalidRepositoriesAreRejected(string repository)
    {
        Assert.Throws<ArgumentException>(() => new GitHubPagesEnvironmentResource("pages", repository));
    }

    [Fact]
    public void CustomDomainSetsCanonicalOriginAndRootPath()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repo")
            .WithCustomDomain("APP.example.com");

        Assert.Equal("app.example.com", pages.Resource.CustomDomain);
        Assert.Equal("https://app.example.com", pages.Resource.Origin);
        Assert.Equal("/", pages.Resource.BasePath);
        Assert.Equal("https://app.example.com/", pages.Resource.SiteUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://app.example.com")]
    [InlineData("app.example.com/path")]
    [InlineData("app.example.com:443")]
    [InlineData("*.example.com")]
    [InlineData("app.example.com.")]
    [InlineData("app..example.com")]
    [InlineData("-app.example.com")]
    [InlineData("app_.example.com")]
    [InlineData("localhost")]
    public void InvalidDomainsAreRejected(string domain)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repo");

        Assert.Throws<ArgumentException>(() => pages.WithCustomDomain(domain));
    }

    [Fact]
    public void RunModeKeepsViteWorkloadAndOmitsEnvironment()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repo");
        var vite = builder.AddViteApp("frontend", ".").WithComputeEnvironment(pages);

        Assert.Same(vite.Resource, Assert.Single(builder.Resources.OfType<ViteAppResource>()));
        Assert.Empty(builder.Resources.OfType<GitHubPagesEnvironmentResource>());
        Assert.Empty(vite.Resource.Annotations.OfType<DeploymentTargetAnnotation>());
        Assert.Equal([], vite.Resource.Annotations.OfType<JavaScriptBuildScriptAnnotation>().Last().Args);
        Assert.Empty(pages.Resource.Annotations.OfType<PipelineStepAnnotation>());
    }

    [Fact]
    public void PublishModeDoesNotRegisterManifestPublishing()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repo");

        Assert.Same(pages.Resource, Assert.Single(builder.Resources.OfType<GitHubPagesEnvironmentResource>()));
        Assert.Empty(pages.Resource.Annotations.OfType<ManifestPublishingCallbackAnnotation>());
    }

    [Theory]
    [InlineData(EndpointProperty.Url, "https://owner.github.io/repo/")]
    [InlineData(EndpointProperty.Host, "owner.github.io")]
    [InlineData(EndpointProperty.HostAndPort, "owner.github.io:443")]
    [InlineData(EndpointProperty.Port, "443")]
    [InlineData(EndpointProperty.Scheme, "https")]
    [InlineData(EndpointProperty.TlsEnabled, "True")]
    public async Task DeployedEndpointIgnoresLocalSchemeAndPort(EndpointProperty property, string expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repo");
        var source = builder.AddContainer("frontend", "image")
            .WithHttpEndpoint(port: 5123, targetPort: 5173)
            .WithComputeEnvironment(pages);

        var expression = pages.Resource.GetEndpointPropertyExpression(source.GetEndpoint("http").Property(property));

        Assert.Equal(expected, await expression.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void UnsupportedAndForeignEndpointsAreRejected()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repo");
        var other = builder.AddGitHubPagesEnvironment("other", "owner/other");
        var source = builder.AddContainer("source", "image").WithHttpEndpoint().WithComputeEnvironment(other);

        Assert.Throws<InvalidOperationException>(() => pages.Resource.GetHostAddressExpression(source.GetEndpoint("http")));
        Assert.Throws<InvalidOperationException>(() => other.Resource.GetEndpointPropertyExpression(source.GetEndpoint("http").Property(EndpointProperty.TargetPort)));
    }

    [Theory]
    [InlineData(null, "--base=/repo/")]
    [InlineData("app.example.com", "--base=/")]
    public async Task PublishPreparesViteTargetAndBuildPath(string? domain, string expectedBaseArgument)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path, step: "bundle-frontend");
        var imageBuilder = new MockImageBuilder();
        builder.Services.AddSingleton<IResourceContainerImageManager>(imageBuilder);
        var runtime = CreateRuntimeWithBundle();
        builder.Services.AddSingleton<IContainerRuntimeResolver>(runtime);
        var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repo");
        if (domain is not null)
        {
            pages.WithCustomDomain(domain);
        }
        var vite = builder.AddViteApp("frontend", workspace.Path).WithComputeEnvironment(pages);

        await using var app = builder.Build();
        await app.RunAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var target = Assert.IsType<GitHubPagesSiteResource>(vite.Resource.GetDeploymentTargetAnnotation()?.DeploymentTarget);
        Assert.Same(pages.Resource, target.Environment);
        Assert.Equal("bundle-frontend", target.BundleStepName);
        Assert.Equal(["--", expectedBaseArgument], vite.Resource.Annotations.OfType<JavaScriptBuildScriptAnnotation>().Last().Args);
        var consumer = Assert.Single(pages.Resource.Annotations.OfType<ContainerFilesDestinationAnnotation>());
        Assert.Equal("frontend", consumer.Source.Name);
        Assert.Equal("frontend", Assert.Single(imageBuilder.BuildImageResources).Name);
        Assert.Empty(imageBuilder.PushImageCalls);
        Assert.Single(runtime.CopyFilesFromImageCalls);
        Assert.True(File.Exists(Path.Combine(workspace.Path, "github-pages", "pages", "dist", "index.html")));
    }

    [Fact]
    public async Task ViteDockerfileUsesPagesBaseAndPreparationIsIdempotent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repo");
        var frontend = builder.AddViteApp("frontend", workspace.Path);
        await using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        GitHubPagesHostingExtensions.PrepareTarget(pages.Resource, model);
        var target = frontend.Resource.GetDeploymentTargetAnnotation()?.DeploymentTarget;
        GitHubPagesHostingExtensions.PrepareTarget(pages.Resource, model);

        Assert.Same(target, frontend.Resource.GetDeploymentTargetAnnotation()?.DeploymentTarget);
        Assert.Single(frontend.Resource.Annotations.OfType<DeploymentTargetAnnotation>());
        Assert.Single(pages.Resource.Annotations.OfType<ContainerFilesDestinationAnnotation>());
        Assert.Equal(["--", "--base=/repo/"], frontend.Resource.Annotations.OfType<JavaScriptBuildScriptAnnotation>().Last().Args);
        var container = Assert.Single(model.Resources.OfType<ContainerResource>());
        var dockerfile = container.Annotations.OfType<DockerfileBuildAnnotation>().Last();
        var content = await dockerfile.DockerfileFactory!(new DockerfileFactoryContext
        {
            Resource = container,
            Services = app.Services,
            CancellationToken = TestContext.Current.CancellationToken
        });
        await Verify(content);
    }

    [Fact]
    public async Task SelectingBundleBuildsOnlyItsOwnSource()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path, step: "bundle-frontend");
        var imageBuilder = new MockImageBuilder();
        builder.Services.AddSingleton<IResourceContainerImageManager>(imageBuilder);
        var runtime = CreateRuntimeWithBundle();
        builder.Services.AddSingleton<IContainerRuntimeResolver>(runtime);
        var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repo");
        var other = builder.AddGitHubPagesEnvironment("other", "owner/other");
        builder.AddViteApp("frontend", workspace.Path).WithComputeEnvironment(pages);
        builder.AddViteApp("unrelated", workspace.Path).WithComputeEnvironment(other);
        await using var app = builder.Build();

        await app.RunAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal("frontend", Assert.Single(imageBuilder.BuildImageResources).Name);
        Assert.Empty(imageBuilder.PushImageCalls);
        Assert.Single(runtime.CopyFilesFromImageCalls);
        Assert.Equal(["pages"], Directory.GetDirectories(Path.Combine(workspace.Path, "github-pages")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task DeployFailsRatherThanPretendingToPublish()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path, step: "deploy");
        var runtime = CreateRuntimeWithBundle();
        builder.Services.AddSingleton<IContainerRuntimeResolver>(runtime);
        var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repo");
        builder.AddContainer("frontend", "static-image")
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/dist" })
            .WithComputeEnvironment(pages);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ExecutePipelineAsync(app));

        Assert.Contains("GitHub Pages deployment is not implemented", exception.ToString());
        Assert.Empty(runtime.CopyFilesFromImageCalls);
    }

    [Fact]
    public async Task CustomViteDockerfileIsRejected()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "Dockerfile"), "FROM scratch");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repo");
        builder.AddViteApp("frontend", workspace.Path).WithComputeEnvironment(pages);
        await using var app = builder.Build();

        var exception = Assert.Throws<DistributedApplicationException>(() =>
            GitHubPagesHostingExtensions.PrepareTarget(pages.Resource, app.Services.GetRequiredService<DistributedApplicationModel>()));

        Assert.Contains("uses a custom Dockerfile", exception.Message);
    }

    [Fact]
    public async Task ExplicitViteBaseArgumentIsRejected()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repo");
        builder.AddViteApp("frontend", ".").WithBuildScript("build", ["--base=/wrong/"]).WithComputeEnvironment(pages);
        await using var app = builder.Build();

        var exception = Assert.Throws<DistributedApplicationException>(() =>
            GitHubPagesHostingExtensions.PrepareTarget(pages.Resource, app.Services.GetRequiredService<DistributedApplicationModel>()));

        Assert.Contains("GitHub Pages controls the base path", exception.Message);
    }

    [Fact]
    public async Task EmptyEnvironmentIsRejected()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repo");
        await using var app = builder.Build();

        var exception = Assert.Throws<DistributedApplicationException>(() =>
            GitHubPagesHostingExtensions.PrepareTarget(pages.Resource, app.Services.GetRequiredService<DistributedApplicationModel>()));

        Assert.Contains("has no static website", exception.Message);
    }

    [Fact]
    public async Task BundleWritesCompleteOutputAndReplacesOldFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputPath = Path.Combine(workspace.Path, "output");
        var oldBundle = Path.Combine(outputPath, "github-pages", "pages", "dist");
        Directory.CreateDirectory(oldBundle);
        await File.WriteAllTextAsync(Path.Combine(oldBundle, "old.txt"), "previous deployment");
        using var builder = CreateStaticBuilder(outputPath, CreateRuntimeWithBundle());

        await using var app = builder.Build();
        await app.RunAsync().WaitAsync(TimeSpan.FromSeconds(60));

        await VerifyDirectory(Path.Combine(outputPath, "github-pages"));
    }

    [Fact]
    public async Task FailedExtractionPreservesExistingBundle()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var oldBundle = Path.Combine(workspace.Path, "github-pages", "pages", "dist");
        Directory.CreateDirectory(oldBundle);
        await File.WriteAllTextAsync(Path.Combine(oldBundle, "index.html"), "previous deployment");
        var runtime = new FakeContainerRuntime
        {
            CopyFilesFromImageAsyncCallback = (_, _, _, _) => throw new DistributedApplicationException("copy failed")
        };
        using var builder = CreateStaticBuilder(workspace.Path, runtime);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ExecutePipelineAsync(app));

        Assert.Contains("copy failed", exception.ToString());
        Assert.Equal("previous deployment", await File.ReadAllTextAsync(Path.Combine(oldBundle, "index.html")));
        Assert.Equal(["index.html"], Directory.GetFiles(oldBundle).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("dist")]
    [InlineData("/app/../dist")]
    public async Task InvalidSourcePathsFailBeforeExtraction(string path)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var runtime = CreateRuntimeWithBundle();
        using var builder = CreateStaticBuilder(workspace.Path, runtime, path);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => app.RunAsync().WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.Contains("exactly one absolute container files directory", exception.ToString());
        Assert.Empty(runtime.CopyFilesFromImageCalls);
    }

    [Fact]
    public async Task MultipleWorkloadsAreRejected()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repo");
        builder.AddViteApp("first", ".").WithComputeEnvironment(pages);
        builder.AddViteApp("second", ".").WithComputeEnvironment(pages);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => app.RunAsync().WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.Contains("can host only one static website", exception.ToString());
    }

    [Fact]
    public async Task DuplicateRepositoryEnvironmentsAreRejected()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var first = builder.AddGitHubPagesEnvironment("first", "owner/repo");
        var second = builder.AddGitHubPagesEnvironment("second", "OWNER/REPO");
        builder.AddViteApp("one", ".").WithComputeEnvironment(first);
        builder.AddViteApp("two", ".").WithComputeEnvironment(second);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => app.RunAsync().WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.Contains("Multiple GitHub Pages environments target repository", exception.ToString());
    }

    [Fact]
    public async Task ServerPublishingIsRejected()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repo");
        builder.AddViteApp("frontend", ".").PublishAsNodeServer("server.js").WithComputeEnvironment(pages);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => app.RunAsync().WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.Contains("not a server or SSR container", exception.ToString());
    }

    [Fact]
    public void MissingIndexIsRejected()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var exception = Assert.Throws<DistributedApplicationException>(() =>
            GitHubPagesHostingExtensions.ValidateBundle(workspace.Path, TestContext.Current.CancellationToken));

        Assert.Equal("The static website bundle must contain index.html at its root.", exception.Message);
    }

    [Fact]
    public async Task SymbolicLinksAreRejected()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "index.html"), "index");
        var assets = workspace.CreateDirectory("assets");
        try
        {
            File.CreateSymbolicLink(Path.Combine(assets.FullName, "link.html"), Path.Combine(workspace.Path, "index.html"));
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("Symbolic link creation requires Windows Developer Mode or elevated permissions.");
        }

        var exception = Assert.Throws<DistributedApplicationException>(() =>
            GitHubPagesHostingExtensions.ValidateBundle(workspace.Path, TestContext.Current.CancellationToken));

        Assert.Equal("GitHub Pages bundles cannot contain symbolic links: 'link.html'.", exception.Message);
    }

    [Fact]
    public void BundleValidationHonorsCancellation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "index.html"), "index");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => GitHubPagesHostingExtensions.ValidateBundle(workspace.Path, cancellation.Token));
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime | TestFeature.ContainerImageBuild)]
    public async Task BundleBuildsAndExtractsRealStaticImage()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var source = workspace.CreateDirectory("source");
        const string html = "<html><h1>GitHub Pages static fixture</h1></html>";
        await File.WriteAllTextAsync(Path.Combine(source.FullName, "index.html"), html);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path, step: "bundle-frontend");
        var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repo");
        var imageName = $"aspire-pages-test-{Guid.NewGuid():N}";
        var frontend = builder.AddDockerfileFactory("frontend", source.FullName, _ => "FROM scratch\nCOPY index.html /app/dist/index.html\n")
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/dist" })
            .WithComputeEnvironment(pages);
        var dockerfile = frontend.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Last();
        dockerfile.HasEntrypoint = false;
        dockerfile.ImageName = imageName;
        dockerfile.ImageTag = "latest";
        await using var app = builder.Build();
        var runtime = await app.Services.GetRequiredService<IContainerRuntimeResolver>().ResolveAsync(TestContext.Current.CancellationToken);

        try
        {
            await ExecutePipelineAsync(app);

            Assert.Equal(html, await File.ReadAllTextAsync(Path.Combine(workspace.Path, "github-pages", "pages", "dist", "index.html")));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if ((await runtime.InspectImageConfigAsync($"{imageName}:latest", cleanup.Token)).Status == ContainerImageInspectionStatus.Succeeded)
            {
                await runtime.RemoveImageAsync($"{imageName}:latest", cleanup.Token);
            }
        }
    }

    private static Task ExecutePipelineAsync(DistributedApplication app)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        foreach (var environment in model.Resources.OfType<GitHubPagesEnvironmentResource>())
        {
            GitHubPagesHostingExtensions.PrepareTarget(environment, model);
        }
        var context = new PipelineContext(
            model,
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        return app.Services.GetRequiredService<IDistributedApplicationPipeline>().ExecuteAsync(context).WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static FakeContainerRuntime CreateRuntimeWithBundle() => new()
    {
        CopyFilesFromImageAsyncCallback = async (_, _, destination, token) =>
        {
            Directory.CreateDirectory(Path.Combine(destination, "assets"));
            await File.WriteAllTextAsync(Path.Combine(destination, "index.html"), "<html><script src=\"/repo/assets/app.js\"></script></html>", token);
            await File.WriteAllTextAsync(Path.Combine(destination, "assets", "app.js"), "console.log('static website');", token);
        }
    };

    private static IDistributedApplicationTestingBuilder CreateStaticBuilder(string outputPath, FakeContainerRuntime runtime, string sourcePath = "/app/dist")
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath, step: "bundle-frontend");
        builder.Services.AddSingleton<IContainerRuntimeResolver>(runtime);
        var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repo");
        builder.AddContainer("frontend", "static-image")
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = sourcePath })
            .WithComputeEnvironment(pages);
        return builder;
    }
}
