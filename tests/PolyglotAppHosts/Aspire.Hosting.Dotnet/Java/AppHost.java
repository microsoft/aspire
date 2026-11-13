import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();

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
