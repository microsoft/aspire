var builder = DistributedApplication.CreateBuilder(args);

var weatherApi = builder.AddProject<Projects.AspireJavaScript_MinimalApi>("weatherapi")
    .WithExternalHttpEndpoints();

builder.AddJavaScriptApp("angular", "../AspireJavaScript.Angular", runScriptName: "start")
    .WithReference(weatherApi)
    .WaitFor(weatherApi)
    .WithHttpEndpoint(env: "PORT")
    .WithExternalHttpEndpoints()
    .PublishAsDockerFile();

#pragma warning disable ASPIREEXTENSION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
builder.AddJavaScriptApp("react", "../AspireJavaScript.React", runScriptName: "start")
    .WithReference(weatherApi)
    .WaitFor(weatherApi)
    .WithEnvironment("BROWSER", "none") // Disable opening browser on npm start
    .WithHttpEndpoint(env: "PORT")
    .WithExternalHttpEndpoints()
    .PublishAsDockerFile()
    .WithBrowserDebugger();
#pragma warning restore ASPIREEXTENSION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

builder.AddJavaScriptApp("vue", "../AspireJavaScript.Vue")
    .WithRunScript("start")
    .WithNpm(installCommand: "ci") // Use 'npm ci' for clean install, requires lock file
    .WithReference(weatherApi)
    .WaitFor(weatherApi)
    .WithHttpEndpoint(env: "PORT")
    .WithExternalHttpEndpoints()
    .PublishAsDockerFile();

var reactvite = builder.AddViteApp("reactvite", "../AspireJavaScript.Vite")
    .WithReference(weatherApi)
    .WithEnvironment("BROWSER", "none")
    .WithExternalHttpEndpoints();

// Demonstrate the new publish methods:

// PublishAsStaticWebsite: deploys the Vite app as a static site served by YARP.
// With apiPath/apiTarget, YARP reverse-proxies /api/* to the weather API via service discovery — no CORS needed.
#pragma warning disable ASPIREJAVASCRIPT001
builder.AddViteApp("vite-static", "../AspireJavaScript.Vite")
    .WithExternalHttpEndpoints()
    .PublishAsStaticWebsite("/api", weatherApi);
#pragma warning restore ASPIREJAVASCRIPT001

// PublishAsNodeServer: for frameworks that produce a self-contained Node.js server artifact.
// Example: SvelteKit with adapter-node builds to build/index.js, Nuxt/TanStack build to .output/server/index.mjs
// Uncomment the following if you add a SvelteKit or Nuxt app to this playground:
// builder.AddViteApp("sveltekit", "../SvelteKitApp")
//     .PublishAsNodeServer(entryPoint: "build/index.js", outputPath: "build");

// PublishAsNpmScript: for frameworks that need node_modules at runtime.
// Example: Remix needs react-router-serve (an npm dependency), Nuxt needs the full Nitro environment.
// Uncomment the following if you add a Remix or Nuxt app to this playground:
// builder.AddViteApp("remix", "../RemixApp")
//     .PublishAsNpmScript(startScriptName: "start", runScriptArguments: "-- --port $PORT");

builder.AddNodeApp("node", "../AspireJavaScript.NodeApp", "app.js")
    .WithRunScript("dev") // Use 'npm run dev' for development
    .WithHttpEndpoint(env: "PORT")
    .WithExternalHttpEndpoints()
    .PublishAsDockerFile();

weatherApi.PublishWithContainerFiles(reactvite, "./wwwroot");

builder.Build().Run();
