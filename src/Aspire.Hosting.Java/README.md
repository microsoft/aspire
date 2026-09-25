# Java hosting integration

Use this integration to model, configure, and orchestrate a Java application resource in an Aspire solution.

## Getting started

### Prerequisites

A **JDK** must be available on the PATH of the machine running the AppHost, for every resource that runs a
JVM locally. Aspire does not install one. `AddJavaContainer` is the exception, because it runs a prebuilt
image and the JDK comes from inside that image. Any JDK the application itself targets will do — a service
built for Java 17 needs only a Java 17 JDK.

Writing the **AppHost itself in Java** is a separate requirement: that needs **JDK 25 or later**, because the
AppHost is a compact source file with an instance `main` method, finalized in Java 25 by
[JEP 512](https://openjdk.org/jeps/512), and is compiled with `--release 25`. On an older JDK it fails to
compile with `error: release version 25 not supported`. This applies only to a Java AppHost; the Java
resources it orchestrates are unaffected, and a C#, TypeScript or Python AppHost can host Java resources on
any JDK.

A **Maven or Gradle wrapper** (`mvnw`/`gradlew`) checked into the project is required only by the resources
that invoke one: the `WithMavenGoal`/`WithGradleTask` launch modes, and the `WithMavenBuild`/`WithGradleBuild`
build steps. A resource that runs a prebuilt JAR launches `java -jar` directly and needs no wrapper.

Where a wrapper is used it needs nothing else installed, and Aspire deliberately does not fall back to a
globally installed `mvn` or `gradle`, because the wrapper pins the tool version in the repository so the
AppHost, CI, and the published container image all build with the same one. Add a wrapper with
`mvn -N wrapper:wrapper` or `gradle wrapper`, or select one elsewhere with `WithWrapperPath(...)`.

For VS Code debugging, install
[Language Support for Java](https://marketplace.visualstudio.com/items?itemName=redhat.java) and
[Debugger for Java](https://marketplace.visualstudio.com/items?itemName=vscjava.vscode-java-debug).

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Java` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Java
```

## Usage example

Then, in the AppHost, add a Java application resource and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Detects Maven or Gradle from the build file, builds the app, launches it through the Spring Boot
// plugin, and declares an HTTP endpoint through SERVER_PORT, which is the port Spring Boot listens on.
var catalog = builder.AddSpringBootApp("catalog", "../catalog")
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.Frontend>("frontend")
    .WithReference(catalog)
    .WaitFor(catalog);

builder.Build().Run();
```

**TypeScript**

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

const catalog = await builder.addSpringBootApp("catalog", "../catalog");
await catalog.withExternalHttpEndpoints();

const orders = await builder.addSpringBootApp("orders", "../orders");
await orders.withReference(catalog);
await orders.waitFor(catalog);

await builder.build().run();
```

`appDirectory` is the process working directory and the publish build context, so everything the build
needs must live inside it.

### Spring Boot

`AddSpringBootApp` is `AddJavaApp` with the four calls every Spring Boot service repeats already made.
It reads the build file in the directory to decide between Maven and Gradle, so the AppHost never
restates something the project already declares:

```csharp
builder.AddSpringBootApp("catalog", "../catalog");
```

is the same as:

```csharp
builder.AddJavaApp("catalog", "../catalog")
    .WithMavenBuild("-B", "-ntp", "-DskipTests", "package")
    .WithMavenGoal("spring-boot:run")
    .WithHttpEndpoint(env: "SERVER_PORT");
```

The build skips tests, because it runs every time the AppHost starts and a full suite in front of every
debug session gets old quickly. Call `WithMavenBuild`/`WithGradleBuild` afterwards to choose your own
arguments.

No health check is added: `/actuator/health` only responds when the application depends on
`spring-boot-starter-actuator`, so adding one unconditionally would leave every other application
permanently unhealthy. Add `.WithHttpHealthCheck("/actuator/health")` when the actuator is present.

Use `AddJavaApp` directly for anything else: a different Spring Boot plugin goal, a project laid out so
the build file is not in the app directory, or a framework that is not Spring Boot.

### Quarkus

`AddQuarkusApp` is the Quarkus equivalent, and detects the build tool the same way:

```csharp
builder.AddQuarkusApp("inventory", "../inventory");
```

is the same as:

```csharp
builder.AddJavaApp("inventory", "../inventory")
    .WithMavenBuild("-B", "-ntp", "-DskipTests", "package")
    .WithMavenGoal("quarkus:dev")
    .WithEnvironment("QUARKUS_PROFILE", "dev")
    .WithEnvironment("QUARKUS_OBSERVABILITY_ENABLED", "false")
    .WithHttpEndpoint(env: "QUARKUS_HTTP_PORT");
```

The application runs in Quarkus dev mode, so live coding works while the AppHost is running. Quarkus Dev
Services stay enabled but do not activate for anything Aspire supplies, because a Dev Service only starts
when the configuration it would provide is absent — and `WithReference` supplies it.

The observability Dev Service is the exception, and is turned off. Left on, an application that depends on
`quarkus-opentelemetry` pulls `grafana/otel-lgtm` (roughly 600 MB), starts it through Testcontainers, and
then repoints the exporter at that container — so telemetry never reaches the Aspire dashboard and a
container is left behind. Aspire is already the observability stack, so the Dev Service has nothing to add.

`AddQuarkusApp` also mirrors the OTLP endpoint, protocol, headers and service name onto the
`QUARKUS_OTEL_*` environment variables. `quarkus-opentelemetry` reads its own `quarkus.otel.*`
configuration and ignores the standard `OTEL_*` names, so without this it keeps exporting to its
`localhost:4317` default and every export fails. No Java agent is needed for a Quarkus application, and
`WithOtelAgent` should not be combined with the extension.

`QUARKUS_PROFILE=dev` is set as an environment variable rather than left to the goal, because the VS Code
debugger launches the packaged application directly rather than through `quarkus:dev`; setting it here
means both resolve the same `%dev.` configuration. It is not set when publishing, where the image must
resolve production configuration.

No health check is added, for the same reason as Spring Boot: `/q/health` only responds when the
application depends on `quarkus-smallrye-health`. Add `.WithHttpHealthCheck("/q/health")` when that
extension is present.

A **published** container needs one thing more, in the application's own `application.properties`:

```properties
quarkus.otel.exporter.otlp.endpoint=${OTEL_EXPORTER_OTLP_ENDPOINT:http://localhost:4317}
quarkus.otel.exporter.otlp.protocol=${OTEL_EXPORTER_OTLP_PROTOCOL:grpc}
```

The compute environment supplies `OTEL_*` from a callback it appends while preparing the deployment
target, which is after every callback the AppHost registered, so there is nothing left for the mirroring
to copy. These are [SmallRye config expressions](https://smallrye.io/smallrye-config/Main/config/expressions/),
expanded inside the JVM at startup, so it does not matter when the variable was set. They cannot be passed
as environment variables instead, because Docker Compose interpolates `${...}` in its own file and rejects
this form. The defaults match the extension's own, so the application still runs outside Aspire.

A Quarkus application described with `AddJavaApp` rather than `AddQuarkusApp` gets no mirroring at all and
exports to the extension's `localhost:4317` default. Use `AddQuarkusApp`, or attach the agent with
`WithOtelAgent()`.

### Launch modes

A resource runs in exactly one of three ways, and configuring a second one throws:

| Mode | How to select it | What runs |
| --- | --- | --- |
| Prebuilt JAR | `AddJavaApp(name, appDirectory, jarPath)` | `java -jar <jarPath>` |
| Maven goal | `WithMavenGoal("spring-boot:run")` | `mvnw spring-boot:run` |
| Gradle task | `WithGradleTask("bootRun")` | `gradlew bootRun` |

Arguments passed to `AddJavaApp` or `WithArgs(...)` belong to the application. Arguments for the build
tool are passed to `WithMavenGoal`/`WithGradleTask`, which keeps the two sets separable — the IDE needs
to drop the wrapper's arguments when it launches the JVM directly to debug it.

Prebuilt JAR mode describes how the resource *runs*. It does not by itself mean the JAR is published
as-is: a JAR path in a directory that also has a `pom.xml` or a Gradle build file is built inside the
image and the path selects the artifact. See [Publishing](#publishing).

### Running an image someone else built

When the application ships as a container image — built by a separate pipeline, or by a team that hands
you an image rather than source — use `AddJavaContainer` instead. Aspire runs the image as-is and
never rebuilds it, so the JAR, the JDK, and any OpenTelemetry agent all come from the image:

```csharp
builder.AddJavaContainer("catalog", "mycompany/catalog", "1.4.0")
    .WithHttpEndpoint(targetPort: 8080)
    .WithReference(db)
    .WithJvmArgs("-Xmx512m");
```

No endpoint is declared for you, because the port is a property of the image; 8080 is the default for
Spring Boot and Quarkus. `WithOtelAgent` does not apply here — it copies an agent out of the build
context, and there is no build. If the image already carries the agent, turn it on with
`WithJvmArgs("-javaagent:/app/opentelemetry-javaagent.jar")`.

### Building before running

```csharp
builder.AddJavaApp("worker", "../worker", "target/worker.jar")
    .WithMavenBuild("-B", "-ntp", "-DskipTests", "package");
```

`WithMavenBuild`/`WithGradleBuild` add a child resource that runs before the application starts, and the
application waits for it to succeed. The same arguments also drive the container build during
`aspire publish`, so they should produce the deployable artifact — `build -x test` rather than `classes`
for Gradle, for example.

### Options

| Method | Effect |
| --- | --- |
| `WithMavenGoal(string goal, params string[] args)` | Launches through `mvnw` with the given goal |
| `WithGradleTask(string task, params string[] args)` | Launches through `gradlew` with the given task |
| `WithMavenBuild(params string[] args)` | Builds with Maven before the app runs, and in the published container |
| `WithGradleBuild(params string[] args)` | Builds with Gradle before the app runs, and in the published container |
| `WithWrapperPath(string wrapperPath)` | Selects a custom wrapper path. May be called before or after the build tool is configured. Must stay inside the app directory for an app that will be published, because that directory is the container build context |
| `WithMainClass(string mainClass)` | The fully qualified class the IDE launches when debugging |
| `WithJarArtifact(string jarPath)` | Names the JAR the container build should deploy, when the build produces more than one |
| `WithJvmArgs(params string[] args)` | Appends JVM arguments through `JAVA_TOOL_OPTIONS`. Also available on `AddJavaContainer` |
| `WithOtelAgent(string agentPath)` | Runs the app under the [OpenTelemetry Java agent](https://github.com/open-telemetry/opentelemetry-java-instrumentation). The agent is not downloaded for you: fetch it as a build dependency so it exists before the application starts |
| `WithOtelAgent()` | Same, with the agent at `target/agent/` (Maven) or `build/agent/` (Gradle) |

### Debugging

Debugging is enabled automatically by `AddJavaApp` — use the normal Aspire "Start Debugging" flow in
VS Code. The IDE launches the JVM directly rather than through the wrapper, because `spring-boot:run`
and `bootRun` fork a second JVM that a debugger attached to the wrapper would never see. Set
`WithMainClass(...)` to say which class to launch; for a prebuilt JAR the archive is put on the
debugger's classpath and its manifest's `Main-Class` is launched, which is what `java -jar` does.

### Publishing

`aspire publish` and `aspire deploy` build the app into a container. An app that runs should publish with
no extra configuration: if the app directory contains a `Dockerfile` it is used as-is, otherwise one is
generated that builds the project inside the container. The container runs as an unprivileged numeric
user (`USER 999:999`, with no passwd entry), and the JVM is PID 1 so it receives `SIGTERM` directly and
shutdown hooks run.

The generated build stage reuses the wrapper and the arguments from `WithMavenBuild`/`WithGradleBuild`,
falling back to `package` or `build` when neither is configured. Dependency caches are kept in a BuildKit
cache mount rather than baked into a layer.

A JAR path is not on its own taken to mean the JAR is prebuilt, because it is equally how a Maven or
Gradle application names the artifact its own build produces. So when the app directory also holds a
`pom.xml` or a Gradle build file, the image builds the project and the path selects the artifact out of
the build output. Only a directory with no build file at all — and no `WithMavenBuild`/`WithGradleBuild`
and no `WithMavenGoal`/`WithGradleTask` — publishes as a straight `COPY` of the JAR.

That is deliberately not symmetric with run mode, which execs `java -jar` against whatever is on disk.
Build output directories are conventionally ignored by source control, so copying `target/app.jar` out of
a source checkout would publish a locally built and possibly stale artifact when the developer happens to
have one, and fail the image build outright in a clean clone or on CI where it does not exist yet.

The deployed JAR is selected in this order: an explicit `WithJarArtifact(...)`; for a Quarkus application
whichever artifact `quarkus-artifact.properties` names, together with the dependency directory it needs;
the JAR path given to `AddJavaApp`, when there was one; and otherwise whichever JAR the build produced,
ignoring `-plain`, `-sources`, and `-javadoc` artifacts. Only that last case can be ambiguous — a shade
plugin leaves `original-*.jar` beside the shaded JAR — and then the build fails naming the candidates, so
select one with `WithJarArtifact(...)`.

All three Quarkus packaging types are handled without configuration: `fast-jar` (the default) stages the
whole `target/quarkus-app` directory, `legacy-jar` stages the runner alongside its sibling `target/lib`,
and `uber-jar` stages the single self-contained runner. The first two are useless without the
dependencies beside them, because the runner's manifest `Class-Path` names them relatively.

#### Base images

The Java release is read from `pom.xml` or the Gradle build file, and defaults to 21.

| Stage | Default |
| --- | --- |
| Build | `docker.io/library/eclipse-temurin:{version}-jdk` |
| Runtime | `docker.io/library/eclipse-temurin:{version}-jre` |

A plain JDK image is enough for the build stage because the build always runs through the project's own
wrapper, and the wrapper downloads the Maven or Gradle version the repository pins.

Override both stages in a single call:

```csharp
builder.AddJavaApp("catalog", "../catalog")
    .WithDockerfileBaseImage(
        buildImage: "example/java-build:latest",
        runtimeImage: "example/java-runtime:latest");
```

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/frameworks/java/
* [Aspire documentation](https://aspire.dev/)
* [Maven Wrapper](https://maven.apache.org/wrapper/)
* [Gradle Wrapper](https://docs.gradle.org/current/userguide/gradle_wrapper.html)

## Feedback & contributing

https://github.com/microsoft/aspire

_*Java is a registered trademark of Oracle and/or its affiliates. Apache Maven and Apache Tomcat are trademarks of the Apache Software Foundation. Gradle is a trademark of Gradle, Inc. Spring Boot is a trademark of Broadcom Inc. and/or its subsidiaries. Quarkus is a trademark of Red Hat, Inc._
