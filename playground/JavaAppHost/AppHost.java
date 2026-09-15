import aspire.*;

void main(String[] args) throws Exception {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

        // A plain JAR with no web framework, built by its own checked-in Maven wrapper before it runs.
        // The JAR path is repeated in withJarArtifact because publishing has to be told which artifact is
        // the application; the container build otherwise has to guess among everything under target/.
        JavaAppResource forecast = builder.addJavaAppWithJar("forecast", "./forecast",
                "target/forecast-1.0.0-SNAPSHOT.jar", new String[0]);
        forecast.withMavenBuild(new String[] { "-B", "-ntp", "-DskipTests", "package" });
        forecast.withJarArtifact("target/forecast-1.0.0-SNAPSHOT.jar");
        // Reads target/agent/opentelemetry-javaagent.jar, which the POM copies there during the build.
        forecast.withOtelAgentDefaultPath();
        forecast.withHttpEndpoint(new WithHttpEndpointOptions().env("PORT"));
        forecast.withHttpHealthCheck(new WithHttpHealthCheckOptions().path("/health"));

        NodeAppResource app = builder.addNodeApp("app", "./api", "src/index.ts");
        app.withHttpEndpoint(new WithHttpEndpointOptions().env("PORT"));
        app.withExternalHttpEndpoints();
        // Projects the Java endpoint as services__forecast__http__0, which the API reads to call it, and
        // holds the API back until the JVM reports healthy so its first request cannot race the build.
        app.withReference(forecast);
        app.waitFor(forecast);

        ViteAppResource frontend = builder.addViteApp("frontend", "./frontend");
        frontend.withReference(app);
        frontend.waitFor(app);

        app.publishWithContainerFiles(frontend, "./static");

        builder.build().run();
    }
