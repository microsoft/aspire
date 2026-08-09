package aspire;

final class AppHost {
    void main() throws Exception {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        NodeAppResource app = builder.addNodeApp("app", "./api", "src/index.ts");
        app.withHttpEndpoint(new WithHttpEndpointOptions().env("PORT"));
        app.withExternalHttpEndpoints();

        ViteAppResource frontend = builder.addViteApp("frontend", "./frontend");
        frontend.withReference(app);
        frontend.waitFor(app);

        app.publishWithContainerFiles(frontend, "./static");

        builder.build().run();
    }
}
