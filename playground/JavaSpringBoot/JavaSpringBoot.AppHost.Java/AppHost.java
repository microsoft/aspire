import aspire.*;

// The same three services the C# AppHost next door orchestrates, written in Java. Running either one
// against this directory produces the same application, which is the point: the AppHost language is a
// choice about what the team already writes, not about what Aspire can express.
void main() throws Exception {
    var builder = DistributedApplication.CreateBuilder();

    // Matches the C# AppHost next door, so `aspire publish` emits the same Docker Compose artifacts
    // from either language. Without a compute environment the publish pipeline has nothing to write.
    builder.addDockerComposeEnvironment("compose");

    // Maven, detected from the pom.xml in the directory. addSpringBootApp builds the application,
    // launches it through spring-boot:run, and declares an HTTP endpoint through SERVER_PORT.
    var catalog = builder.addSpringBootApp("catalog", "../catalog")
        // Reads target/agent/opentelemetry-javaagent.jar, which the POM copies there during the build.
        .withOtelAgentDefaultPath()
        .withHttpHealthCheck(new WithHttpHealthCheckOptions().path("/actuator/health"))
        .withExternalHttpEndpoints();

    // Gradle, detected from build.gradle. Identical AppHost code to the Maven service above, because
    // the build tool is a property of the project rather than something the AppHost restates.
    builder.addSpringBootApp("orders", "../orders")
        .withOtelAgentDefaultPath()
        .withHttpHealthCheck(new WithHttpHealthCheckOptions().path("/actuator/health"))
        .withExternalHttpEndpoints()
        // Projects the catalog endpoint as services__catalog__http__0 and holds orders back until
        // catalog reports healthy, so its first request cannot race the other service's startup.
        .withReference(catalog)
        .waitFor(catalog);

    // A plain JAR with no framework, built by Maven before it runs.
    builder.addJavaAppWithJar("worker", "../worker", "target/worker-0.0.1-SNAPSHOT.jar",
            new String[] { "--interval-seconds", "10" })
        .withMavenBuild(new String[] { "-B", "-ntp", "-DskipTests", "package" })
        // Publishing has to know which JAR is the application, or the container build finds both
        // worker-0.0.1-SNAPSHOT.jar and any classifier artifacts and refuses to guess.
        .withJarArtifact("target/worker-0.0.1-SNAPSHOT.jar")
        .withJvmArgs(new String[] { "-Xmx128m" });

    builder.build().run();
}
