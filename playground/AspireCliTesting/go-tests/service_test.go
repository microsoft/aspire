// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

package playground

import (
	"io"
	"net/http"
	"os"
	"strings"
	"testing"
	"time"
)

func TestServiceResponse(t *testing.T) {
	if body := getService(t, "/"); body != "Aspire CLI testing" {
		t.Fatalf("Unexpected service response: %q", body)
	}
}

func TestServiceHealth(t *testing.T) {
	getService(t, "/health")
}

func TestControlledFailure(t *testing.T) {
	if strings.EqualFold(os.Getenv("GO_PLAYGROUND_FAIL"), "true") {
		t.Fatal("Opt-in Go playground failure.")
	}
}

func TestSkippedExample(t *testing.T) {
	t.Skip("Demonstrates a skipped Go result in the common summary.")
}

func getService(t *testing.T, path string) string {
	t.Helper()
	endpoint := os.Getenv("GO_SERVICE_URL")
	if endpoint == "" {
		t.Fatal("GO_SERVICE_URL must contain the shared service endpoint.")
	}
	request, err := http.NewRequestWithContext(t.Context(), http.MethodGet, strings.TrimRight(endpoint, "/")+path, nil)
	if err != nil {
		t.Fatal(err)
	}
	client := &http.Client{Timeout: 30 * time.Second}
	response, err := client.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	body, err := io.ReadAll(response.Body)
	if err != nil {
		t.Fatal(err)
	}
	if response.StatusCode != http.StatusOK {
		t.Fatalf("GET %s returned %d: %s", path, response.StatusCode, body)
	}
	return string(body)
}
