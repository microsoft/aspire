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

	// ── 1. addAzureContainerAppEnvironment + fluent chain ────────────────────
	env := builder.AddAzureContainerAppEnvironment("myenv")
	env.
		WithAzdResourceNaming().
		WithCompactResourceNaming().
		WithDashboardWithOpts(&aspire.WithDashboardOptions{Enable: aspire.BoolPtr(true)}).
		WithHttpsUpgradeWithOpts(&aspire.WithHttpsUpgradeOptions{Upgrade: aspire.BoolPtr(false)})
	if err = env.Err(); err != nil {
		log.Fatalf("env: %v", err)
	}

	// ── 2. simple withDashboard / withHttpsUpgrade (no args) ─────────────────
	env2 := builder.AddAzureContainerAppEnvironment("myenv2")
	env2.WithDashboard()
	env2.WithHttpsUpgrade()

	// ── 3. withAzureLogAnalyticsWorkspace ────────────────────────────────────
	laws := builder.AddAzureLogAnalyticsWorkspace("laws")
	env3 := builder.AddAzureContainerAppEnvironment("myenv3")
	env3.WithAzureLogAnalyticsWorkspace(laws)

	// ── 4. parameters ────────────────────────────────────────────────────────
	customDomain := builder.AddParameter("customDomain")
	certificateName := builder.AddParameter("certificateName")

	// ── 5. publishAsAzureContainerApp (with callback) ────────────────────────
	web := builder.AddContainer("web", "myregistry/web:latest")
	web.PublishAsAzureContainerApp(func(_ *aspire.AzureResourceInfrastructure, _ *aspire.ContainerApp) {
		// ContainerApp configuration — sdk exposes handle wrapper only
		_ = customDomain
		_ = certificateName
	})

	// ── 6. publishAsAzureContainerAppJob (parameterless) ─────────────────────
	api := builder.AddExecutable("api", "dotnet", ".", []string{"run"})
	_, _ = api.PublishAsAzureContainerAppJob()

	worker := builder.AddContainer("worker", "myregistry/worker:latest")
	_, _ = worker.PublishAsAzureContainerAppJob()

	// ── 7. publishAsConfiguredAzureContainerAppJob (with callback) ───────────
	processor := builder.AddContainer("processor", "myregistry/processor:latest")
	_, _ = processor.PublishAsConfiguredAzureContainerAppJob(
		func(_ *aspire.AzureResourceInfrastructure, _ *aspire.ContainerAppJob) {})

	// ── 8. publishAsScheduledAzureContainerAppJob (cron, no callback) ────────
	scheduler := builder.AddContainer("scheduler", "myregistry/scheduler:latest")
	_, _ = scheduler.PublishAsScheduledAzureContainerAppJob("0 0 * * *")

	// ── 9. publishAsConfiguredScheduledAzureContainerAppJob (cron + callback) ─
	reporter := builder.AddContainer("reporter", "myregistry/reporter:latest")
	_, _ = reporter.PublishAsConfiguredScheduledAzureContainerAppJob(
		"0 */6 * * *",
		func(_ *aspire.AzureResourceInfrastructure, _ *aspire.ContainerAppJob) {})

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
