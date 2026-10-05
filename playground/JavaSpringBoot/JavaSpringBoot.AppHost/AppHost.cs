// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

// Gives `aspire publish` somewhere to publish to. Each Java resource turns into a container image built
// from a Dockerfile that Aspire generates from the resource's build tool and target Java release.
builder.AddDockerComposeEnvironment("compose");

// Maven, detected from the pom.xml in the directory. AddSpringBootApp builds the application, launches it
// through spring-boot:run, and declares an HTTP endpoint through SERVER_PORT.
var catalog = builder.AddSpringBootApp("catalog", "../catalog")
    // Reads target/agent/opentelemetry-javaagent.jar, which the POM copies there during the build. The
    // agent is ~25 MB and is not committed, so it does not exist on a fresh clone. Asking for a
    // build-produced agent is therefore what makes Aspire add the catalog-maven-build resource this
    // waits for: JAVA_TOOL_OPTIONS reaches the wrapper's own JVM, so without a build that has already
    // written the agent, that JVM dies at VM initialization before spring-boot:run ever starts.
    .WithOtelAgent()
    .WithHttpHealthCheck("/actuator/health")
    .WithExternalHttpEndpoints();

// Gradle, detected from build.gradle in the directory. Identical AppHost code to the Maven service above,
// which is the point: the build tool is a property of the project, not something the AppHost restates.
var orders = builder.AddSpringBootApp("orders", "../orders")
    .WithOtelAgent()
    .WithHttpHealthCheck("/actuator/health")
    .WithExternalHttpEndpoints()
    // Projects the catalog endpoint as services__catalog__http__0 and holds orders back until catalog
    // reports healthy, so its first request cannot race the other service's startup.
    .WithReference(catalog)
    .WaitFor(catalog);

// A plain JAR with no framework, built by Maven before it runs. Its wrapper lives in the module rather
// than being borrowed from a sibling: publishing uploads only the application directory to the daemon,
// so a wrapper outside it would exist on the host and not in the image.
builder.AddJavaApp("worker", "../worker", "target/worker-0.0.1-SNAPSHOT.jar", ["--interval-seconds", "10"])
    .WithMavenBuild("-B", "-ntp", "-DskipTests", "package")
    // Publishing has to know which JAR is the application. Without this the container build would find
    // both worker-0.0.1-SNAPSHOT.jar and any classifier artifacts and refuse to guess.
    .WithJarArtifact("target/worker-0.0.1-SNAPSHOT.jar")
    .WithJvmArgs("-Xmx128m");

#if !SKIP_DASHBOARD_REFERENCE
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();
