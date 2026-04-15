package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder(nil)
	if err != nil {
		log.Fatalf("CreateBuilder: %v", err)
	}

	nodeApp := builder.AddNodeApp("node-app", "./node-app", "server.js")
	nodeApp.WithNpmWithOpts(&aspire.WithNpmOptions{
		Install:        aspire.BoolPtr(false),
		InstallCommand: aspire.StringPtr("install"),
		InstallArgs:    []string{"--ignore-scripts"},
	})
	nodeApp.WithBunWithOpts(&aspire.WithBunOptions{
		Install:     aspire.BoolPtr(false),
		InstallArgs: []string{"--frozen-lockfile"},
	})
	nodeApp.WithYarnWithOpts(&aspire.WithYarnOptions{
		Install:     aspire.BoolPtr(false),
		InstallArgs: []string{"--immutable"},
	})
	nodeApp.WithPnpmWithOpts(&aspire.WithPnpmOptions{
		Install:     aspire.BoolPtr(false),
		InstallArgs: []string{"--frozen-lockfile"},
	})
	nodeApp.WithBuildScriptWithOpts("build", &aspire.WithBuildScriptOptions{
		Args: []string{"--mode", "production"},
	})
	nodeApp.WithRunScriptWithOpts("dev", &aspire.WithRunScriptOptions{
		Args: []string{"--host", "0.0.0.0"},
	})
	if err = nodeApp.Err(); err != nil {
		log.Fatalf("nodeApp: %v", err)
	}

	javaScriptApp := builder.AddJavaScriptAppWithOpts("javascript-app", "./javascript-app", &aspire.AddJavaScriptAppOptions{
		RunScriptName: aspire.StringPtr("start"),
	})
	javaScriptApp.WithEnvironment("NODE_ENV", "development")
	if err = javaScriptApp.Err(); err != nil {
		log.Fatalf("javaScriptApp: %v", err)
	}

	viteApp := builder.AddViteAppWithOpts("vite-app", "./vite-app", &aspire.AddViteAppOptions{
		RunScriptName: aspire.StringPtr("dev"),
	})
	viteApp.WithViteConfig("./vite.custom.config.ts")
	viteApp.WithPnpmWithOpts(&aspire.WithPnpmOptions{
		Install:     aspire.BoolPtr(false),
		InstallArgs: []string{"--prod"},
	})
	viteApp.WithBuildScriptWithOpts("build", &aspire.WithBuildScriptOptions{
		Args: []string{"--mode", "production"},
	})
	viteApp.WithRunScriptWithOpts("dev", &aspire.WithRunScriptOptions{
		Args: []string{"--host"},
	})
	if err = viteApp.Err(); err != nil {
		log.Fatalf("viteApp: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
