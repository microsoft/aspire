package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	project := builder.AddDotnetProject("project", "./src/Project/Project.csproj")
	_, _ = project.Name()
	_, _ = project.Command()
	_, _ = project.WorkingDirectory()
	if err = project.Err(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatal(aspire.FormatError(err))
	}
}
