import aspire.*;

// A resource's launch mode is exclusive — a prebuilt JAR, a Maven goal, or a Gradle task — and
// the two build steps exclude each other, so the exported surface is spread across four apps.
void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();

        // Maven-launched app with an explicit main class and JVM tuning
        var catalog = builder.addJavaApp("catalog", "../java-catalog")
            .withMavenGoal("spring-boot:run", new String[] { "-Dspring-boot.run.profiles=dev" })
            .withMainClass("com.example.catalog.CatalogApplication")
            .withJvmArgs(new String[] { "-Xmx512m", "-XX:+UseZGC" });

        // Prebuilt JAR produced by a Maven build, instrumented with the OpenTelemetry Java agent
        var worker = builder.addJavaAppWithJar("worker", "../java-worker", "target/worker.jar",
                new String[] { "--spring.profiles.active=ci" })
            .withMavenBuild(new String[] { "clean", "package", "-DskipTests" })
            .withJarArtifact("target/worker.jar")
            .withOtelAgent("../agents/opentelemetry-javaagent.jar");

        // Gradle-launched app whose wrapper lives above the app directory
        var gateway = builder.addJavaApp("gateway", "../java-gateway")
            .withGradleTask("bootRun", new String[] { "--args=--server.port=0" })
            .withWrapperPath("../gradlew");

        // Prebuilt JAR produced by a Gradle build, so the task only has to assemble it
        var reports = builder.addJavaAppWithJar("reports", "../java-reports", "build/libs/reports.jar", new String[0])
            .withGradleBuild(new String[] { "clean", "bootJar" });

        builder.build().run();
    }
