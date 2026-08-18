from aspire_app import create_builder


# A resource's launch mode is exclusive - a prebuilt JAR, a Maven goal, or a Gradle task - and
# the two build steps exclude each other, so the exported surface is spread across four apps.
with create_builder() as builder:
    # Maven-launched app with an explicit main class and JVM tuning
    catalog = builder.add_java_app("catalog", "../java-catalog")
    catalog.with_maven_goal("spring-boot:run", ["-Dspring-boot.run.profiles=dev"])
    catalog.with_main_class("com.example.catalog.CatalogApplication")
    catalog.with_jvm_args(["-Xmx512m", "-XX:+UseZGC"])

    # Prebuilt JAR produced by a Maven build, instrumented with the OpenTelemetry Java agent
    worker = builder.add_java_app_with_jar("worker", "../java-worker", "target/worker.jar",
                                           args=["--spring.profiles.active=ci"])
    worker.with_maven_build(["clean", "package", "-DskipTests"])
    worker.with_jar_artifact("target/worker.jar")
    worker.with_otel_agent("../agents/opentelemetry-javaagent.jar")

    # Gradle-launched app whose wrapper lives above the app directory
    gateway = builder.add_java_app("gateway", "../java-gateway")
    gateway.with_gradle_task("bootRun", ["--args=--server.port=0"])
    gateway.with_wrapper_path("../gradlew")

    # Prebuilt JAR produced by a Gradle build, so the task only has to assemble it
    reports = builder.add_java_app_with_jar("reports", "../java-reports", "build/libs/reports.jar")
    reports.with_gradle_build(["clean", "bootJar"])

    builder.run()
