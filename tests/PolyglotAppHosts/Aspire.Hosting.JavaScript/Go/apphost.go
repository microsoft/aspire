package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder(nil)
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	nodeApp := builder.AddNodeApp("node-app", "./node-app", "server.js")
	nodeApp.WithNpm(&aspire.WithNpmOptions{
		Install:        aspire.BoolPtr(false),
		InstallCommand: aspire.StringPtr("install"),
		InstallArgs:    []string{"--ignore-scripts"},
	})
	nodeApp.WithBun(&aspire.WithBunOptions{
		Install:     aspire.BoolPtr(false),
		InstallArgs: []string{"--frozen-lockfile"},
	})
	nodeApp.WithYarn(&aspire.WithYarnOptions{
		Install:     aspire.BoolPtr(false),
		InstallArgs: []string{"--immutable"},
	})
	nodeApp.WithPnpm(&aspire.WithPnpmOptions{
		Install:     aspire.BoolPtr(false),
		InstallArgs: []string{"--frozen-lockfile"},
	})
	nodeApp.WithBuildScript("build", &aspire.WithBuildScriptOptions{
		Args: []string{"--mode", "production"},
	})
	nodeApp.WithRunScript("dev", &aspire.WithRunScriptOptions{
		Args: []string{"--host", "0.0.0.0"},
	})
	if err = nodeApp.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	javaScriptApp := builder.AddJavaScriptApp("javascript-app", "./javascript-app", &aspire.AddJavaScriptAppOptions{
		RunScriptName: aspire.StringPtr("start"),
	})
	javaScriptApp.WithEnvironment("NODE_ENV", "development")
	if err = javaScriptApp.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	viteApp := builder.AddViteApp("vite-app", "./vite-app", &aspire.AddViteAppOptions{
		RunScriptName: aspire.StringPtr("dev"),
	})
	viteApp.WithViteConfig("./vite.custom.config.ts")
	viteApp.WithPnpm(&aspire.WithPnpmOptions{
		Install:     aspire.BoolPtr(false),
		InstallArgs: []string{"--prod"},
	})
	viteApp.WithBuildScript("build", &aspire.WithBuildScriptOptions{
		Args: []string{"--mode", "production"},
	})
	viteApp.WithRunScript("dev", &aspire.WithRunScriptOptions{
		Args: []string{"--host"},
	})
	if err = viteApp.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
