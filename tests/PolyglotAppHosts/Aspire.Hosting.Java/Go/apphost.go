package main

import (
	"log"

	"apphost/modules/aspire"
)

// A resource's launch mode is exclusive — a prebuilt JAR, a Maven goal, or a Gradle task — and
// the two build steps exclude each other, so the exported surface is spread across four apps.
func main() {
	builder, err := aspire.CreateBuilder()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	// Maven-launched app with an explicit main class and JVM tuning
	catalog := builder.AddJavaApp("catalog", "../java-catalog").
		WithMavenGoal("spring-boot:run", []string{"-Dspring-boot.run.profiles=dev"}).
		WithMainClass("com.example.catalog.CatalogApplication").
		WithJvmArgs([]string{"-Xmx512m", "-XX:+UseZGC"})
	if err = catalog.Err(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	// Prebuilt JAR produced by a Maven build, instrumented with the OpenTelemetry Java agent
	worker := builder.AddJavaAppWithJar("worker", "../java-worker", "target/worker.jar", []string{"--spring.profiles.active=ci"}).
		WithMavenBuild([]string{"clean", "package", "-DskipTests"}).
		WithJarArtifact("target/worker.jar").
		WithOtelAgent("../agents/opentelemetry-javaagent.jar")
	if err = worker.Err(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	// Gradle-launched app whose wrapper lives above the app directory
	gateway := builder.AddJavaApp("gateway", "../java-gateway").
		WithGradleTask("bootRun", []string{"--args=--server.port=0"}).
		WithWrapperPath("../gradlew")
	if err = gateway.Err(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	// Prebuilt JAR produced by a Gradle build, so the task only has to assemble it
	reports := builder.AddJavaAppWithJar("reports", "../java-reports", "build/libs/reports.jar", []string{}).
		WithGradleBuild([]string{"clean", "bootJar"})
	if err = reports.Err(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}
}
