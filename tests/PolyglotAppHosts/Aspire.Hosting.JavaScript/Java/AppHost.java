import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var nodeApp = builder.addNodeApp("node-app", "./node-app", "server.js");
        nodeApp.withNpm(new WithNpmOptions().install(false).installCommand("install").installArgs(new String[] { "--ignore-scripts" }));
        nodeApp.withBun(new WithBunOptions().install(false).installArgs(new String[] { "--frozen-lockfile" }));
        nodeApp.withYarn(new WithYarnOptions().install(false).installArgs(new String[] { "--immutable" }));
        nodeApp.withPnpm(new WithPnpmOptions().install(false).installArgs(new String[] { "--frozen-lockfile" }));
        nodeApp.withBuildScript("build", new String[] { "--mode", "production" });
        nodeApp.withRunScript("dev", new String[] { "--host", "0.0.0.0" });
        var javaScriptApp = builder.addJavaScriptApp("javascript-app", "./javascript-app", "start");
        javaScriptApp.withEnvironment("NODE_ENV", "development");
        var viteApp = builder.addViteApp("vite-app", "./vite-app", "dev");
        viteApp.withViteConfig("./vite.custom.config.ts");
        viteApp.withPnpm(new WithPnpmOptions().install(false).installArgs(new String[] { "--prod" }));
        viteApp.withBuildScript("build", new String[] { "--mode", "production" });
        viteApp.withRunScript("dev", new String[] { "--host" });
        builder.build().run();
    }
