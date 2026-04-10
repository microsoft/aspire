// Aspire Go AppHost - Playground
// For more information, see: https://aspire.dev
//
// To run:
//
//	aspire config set features:experimentalPolyglot:go true --global
//	aspire run
package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder(nil)
	if err != nil {
		log.Fatalf("Failed to create builder: %v", err)
	}

	// Add your resources here, for example:
	// cache := builder.AddRedis("cache", nil, nil)
	// if cache.Err() != nil {
	//     log.Fatalf("Failed to add Redis: %v", cache.Err())
	// }
	// _ = cache

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Failed to build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Failed to run: %v", err)
	}
}
