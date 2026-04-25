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

	builder.AddPythonApp("python-script", ".", "main.py")
	builder.AddPythonModule("python-module", ".", "uvicorn")
	builder.AddPythonExecutable("python-executable", ".", "pytest")

	uvicorn := builder.AddUvicornApp("python-uvicorn", ".", "main:app")
	uvicorn.WithVirtualEnvironment(".venv", &aspire.WithVirtualEnvironmentOptions{
		CreateIfNotExists: aspire.BoolPtr(false),
	})
	uvicorn.WithDebugging()
	uvicorn.WithEntrypoint(aspire.EntrypointTypeModule, "uvicorn")
	uvicorn.WithPip(&aspire.WithPipOptions{
		Install:     aspire.BoolPtr(true),
		InstallArgs: []string{"install", "-r", "requirements.txt"},
	})
	uvicorn.WithUv(&aspire.WithUvOptions{
		Install: aspire.BoolPtr(false),
		Args:    []string{"sync"},
	})
	if err = uvicorn.Err(); err != nil {
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
