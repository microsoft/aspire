import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();

        var tool = builder.addDotnetCliTool("tool", "dotnet-ef");
        tool.withToolPackage("dotnet-ef");
        tool.withToolVersion("10.0.*");
        tool.withToolPrerelease();
        tool.withToolSource("https://example.invalid/feed");
        tool.withToolIgnoreExistingFeeds();
        tool.withToolIgnoreFailedSources();

        var options = new DotnetProjectOptions();
        options.setLaunchProfileName("https");
        var project = builder.addDotnetProject("project", "./src/Project/Project.csproj", options);
        project.withReplicas(2);
        project.disableForwardedHeaders();
        project.withEndpointsInEnvironment(new String[] { "http" });
        project.name();
        project.command();
        project.workingDirectory();

        builder.build().run();
    }
