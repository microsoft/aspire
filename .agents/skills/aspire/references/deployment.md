# Deployment

Use this when the task is about deployment artifacts, deployment execution, or named pipeline steps.

## Scenario: Regenerate Deployment Artifacts And Redeploy

Use these commands when the task is to build fresh deployment artifacts and run the full deployment flow.

```bash
aspire publish
aspire deploy
aspire deploy --clear-cache
```

Keep these points in mind:

- Use `aspire publish` when artifact generation is part of the request.
- Use `aspire deploy` when the goal is the full deployment flow, not just one step inside it.
- Use `aspire deploy --clear-cache` when cached deployment state is stale or stuck.

## Scenario: Run One Named Deployment Step Instead Of The Whole Deployment

Use these commands when the deployment pipeline already exists and the user wants only one step, such as seeding data, running diagnostics, or pushing containers/images.

```bash
aspire do seed-data
aspire do push-containers   # if the app defines this step
aspire do diagnostics       # if the app defines this step
```

Keep these points in mind:

- Use `aspire do <step>` when the request is specifically about one named pipeline step.
- Common scenarios include seeding data, running a diagnostics step, or pushing containers/images, but the step names are app-defined.
- Do not substitute `aspire deploy` when the request is to rerun only one step from the deployment pipeline.
