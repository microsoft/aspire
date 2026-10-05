import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

// A resource's launch mode is exclusive - a prebuilt JAR, a Maven goal, or a Gradle task - and
// the two build steps exclude each other, so the exported surface is spread across four apps.

// Maven-launched app with an explicit main class and JVM tuning
const catalog = await builder.addJavaApp('catalog', '../java-catalog');
await catalog.withMavenGoal('spring-boot:run', ['-Dspring-boot.run.profiles=dev']);
await catalog.withMainClass('com.example.catalog.CatalogApplication');
await catalog.withJvmArgs(['-Xmx512m', '-XX:+UseZGC']);

// Prebuilt JAR produced by a Maven build, instrumented with the OpenTelemetry Java agent
const worker = await builder.addJavaAppWithJar('worker', '../java-worker', 'target/worker.jar', ['--spring.profiles.active=ci']);
await worker.withMavenBuild(['clean', 'package', '-DskipTests']);
await worker.withJarArtifact('target/worker.jar');
await worker.withOtelAgent('../agents/opentelemetry-javaagent.jar');

// Gradle-launched app whose wrapper lives above the app directory
const gateway = await builder.addJavaApp('gateway', '../java-gateway');
await gateway.withGradleTask('bootRun', ['--args=--server.port=0']);
await gateway.withWrapperPath('../gradlew');

// Prebuilt JAR produced by a Gradle build, so the task only has to assemble it
const reports = await builder.addJavaAppWithJar('reports', '../java-reports', 'build/libs/reports.jar', []);
await reports.withGradleBuild(['clean', 'bootJar']);

await builder.build().run();
