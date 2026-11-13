import aspire.*;

final class AppHost {
    void main() throws Exception {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        // The Spring Boot API in ./api. Gradle is detected from its build.gradle, so the AppHost never
        // names a build tool: addSpringBootApp builds the project, launches it through
        // bootRun, and declares an HTTP endpoint by setting SERVER_PORT.
        JavaAppResource api = builder.addSpringBootApp("api", "./api");

        // Attaches build/agent/opentelemetry-javaagent.jar, which Gradle downloads during the build.
        // The agent instruments Spring's web and HTTP client layers, so traces, metrics, and
        // logs reach the Aspire dashboard without any code in the service.
        api.withOtelAgentDefaultPath();

        // Holds anything that waits on the API back until Spring reports it can serve traffic,
        // rather than merely until the process exists.
        api.withHttpHealthCheck(new WithHttpHealthCheckOptions().path("/actuator/health"));
        api.withExternalHttpEndpoints();

        ViteAppResource frontend = builder.addViteApp("frontend", "./frontend");
        frontend.withReference(api);
        frontend.waitFor(api);

        // In publish and deploy mode the frontend's build output is copied into the API container at
        // ./static, which application.properties adds to the Spring resource locations. That puts the
        // SPA and /api/weatherforecast on one origin. During local development this does nothing and
        // Vite serves the frontend, proxying /api to the API instead.
        api.publishWithContainerFiles(frontend, "./static");

        builder.build().run();
    }
}
