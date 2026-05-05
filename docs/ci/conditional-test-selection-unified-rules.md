# Unified rules design: minimal rule model

## Purpose

This design keeps the unified model small while still allowing additive
`dotnet-affected` support during evaluation.

The JSON should only need three top-level concepts:

- `ignore`
- `fallbacks`
- `rules`

That is enough to express:

- universal ignores
- critical trigger-all paths
- unmatched fallback
- explicit template-only selection
- direct one-to-one and one-to-many project mappings
- tag-only workflow triggers such as `extension` and `polyglot`
- additive MSBuild/transitive coverage without adding another config section

## Design position

The simplified model intentionally removes:

- `tagCatalog`
- `tagExpansions`
- `claimsFiles`

### Why remove `tagCatalog`

The selector result can just return tag strings directly.

For GitHub summaries and debugging output, the raw tag names are enough:

- `templates`
- `cli`
- `extension`
- `polyglot`
- `integrations`
- `run_all`

If we later need display names or workflow-specific metadata, that can be added back, but it is not needed for the first usable design.

### Why remove `tagExpansions`

Projects should be declared directly on the rule that selected them.

That keeps the rule self-contained:

- what matched
- which tags were emitted
- which projects were selected

For the current cases, a separate `tag -> projects` expansion layer adds indirection without much value.

### Why remove `claimsFiles`

Matching a rule should mean the file is handled by default.

So the default model becomes:

- a file matched by at least one rule is claimed
- a file matched by no rule is unmatched
- unmatched files trigger the top-level fallback

We only need a separate claiming flag if we later introduce observer-only rules, which is not necessary for this design.

## Keep `dotnet-affected`, but as resolver behavior

The unified design should not remove `dotnet-affected`.

It should remove only the need for a separate config language dedicated to generic project discovery.

The cleaner split is:

- rules express repo-specific intent
- `dotnet-affected` supplies additive MSBuild/transitive coverage

That means the selector can:

1. evaluate rules for tags and direct project selection
2. run `dotnet-affected` on the full active changed-file set
3. filter its output to managed test projects
4. union those test projects with the projects selected directly by rules

This keeps the config unified while preserving the main value of `dotnet-affected`.

## Files can be handled in two ways

A non-ignored file is considered explained if either:

- a rule matched it
- or the selector deliberately delegates it to MSBuild/project-graph analysis through `dotnet-affected`

Only files handled by neither path are unmatched.

That distinction matters because `dotnet-affected` should not be treated as a new matching language, but it is still trusted to explain the MSBuild-managed part of the repo.

## Keep `skipWhenRunAll`, but on the project

The policy we need is:

“this project should not be pulled in by run-all unless it was explicitly selected.”

That is still best expressed on the project entry itself:

```json
{
  "path": "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj",
  "skipWhenRunAll": true
}
```

This keeps `Aspire.Templates.Tests` opt-in while preserving conservative fallback for the rest of managed test execution.

## Core semantics

### 1. Every result emits at least one tag

- ignored-only changes emit `ignored_only`
- trigger-all emits `run_all` and `critical_path`
- unmatched fallback emits `run_all` and `unmatched_fallback`
- ordinary matching rules emit at least one semantic tag

### 2. Template tests are explicit-only

Template-related paths emit only the `templates` tag and directly select `Aspire.Templates.Tests`.

Because that project is marked `skipWhenRunAll: true`:

- direct template changes run template tests
- trigger-all does not automatically include template tests
- unmatched fallback does not automatically include template tests
- `dotnet-affected` discovery by itself does not override the explicit-only policy

### 3. CLI only needs one tag

The CLI rule emits only `cli`.

If the workflow needs a boolean like `run_cli_e2e`, it can derive that from the presence of the `cli` tag.

### 4. One Aspire named-mapping rule is enough

The same rule can cover:

- `src/Aspire.{name}/**`
- `src/Components/Aspire.{name}/**`
- `tests/Aspire.{name}.Tests/**`

with exclusions for special cases like:

- `Aspire.ProjectTemplates`
- `Aspire.Cli`
- root `Aspire.Hosting`
- non-integration test buckets

### 5. Unmatched fallback still stays conservative

`dotnet-affected` is additive coverage, not proof that every changed file is safe.

So unmatched fallback should still apply to any non-ignored file that is:

- not matched by a rule
- and not part of the repo surface that the selector intentionally delegates to `dotnet-affected`

This preserves the safety bias for scripts, workflow logic, repo metadata, and other non-MSBuild surfaces.

## Resolver pipeline

The evaluation pipeline becomes:

1. filter ignored files
2. check trigger-all fallback
3. evaluate rules and collect:
   - matched files
   - emitted tags
   - `ruleSelectedProjects`
4. determine whether all remaining active files are explained by either:
   - a matching rule
   - or delegated MSBuild/project-graph analysis
5. if any active file is explained by neither path, emit unmatched fallback
6. run `dotnet-affected` over the active changed files
7. filter `dotnet-affected` output to managed test projects
8. union `ruleSelectedProjects` with `dotnetAffectedProjects`
9. de-duplicate and emit the final `selectedTestProjects`

In this design:

- tags come from rules, not from `dotnet-affected`
- project selection is additive
- unmatched fallback remains independent of `dotnet-affected` success

## Selection behavior matrix

| Scenario | Rule hit | `dotnet-affected` contributes projects | `runAll` | Notes |
|---|---:|---:|---:|---|
| Template-specific file | Yes | Optional | No | Explicit rule selects `Aspire.Templates.Tests` |
| MSBuild-managed transitive file | No | Yes | No | Valid selective case; tags may remain empty |
| Trigger-all only | No | No | Yes | Default run-all coverage applies |
| Trigger-all plus explicit template file | Yes | Optional | Yes | Explicit-only project is still added on top |
| Non-MSBuild file with no rule | No | No | Yes | Unmatched fallback protects against under-selection |

## Proposed JSON shape

```json
{
  "version": 1,
  "ignore": {
    "globs": [],
    "regexes": [],
    "tagsWhenIgnoredOnly": ["ignored_only"]
  },
  "fallbacks": {
    "triggerAll": {
      "paths": [],
      "tags": ["run_all"]
    },
    "unmatched": {
      "mode": "run_all",
      "tags": ["run_all", "unmatched_fallback"]
    }
  },
  "rules": []
}
```

## Rule shape

```json
{
  "id": "template-explicit",
  "description": "Template paths explicitly opt into Aspire.Templates.Tests.",
  "include": [
    "src/Aspire.ProjectTemplates/**"
  ],
  "exclude": [],
  "tags": [
    "templates"
  ],
  "projects": [
    {
      "path": "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj",
      "skipWhenRunAll": true
    }
  ],
  "projectTemplates": []
}
```

Notes:

- `projects` contains explicit project paths
- `projectTemplates` contains placeholder-based project paths such as `tests/Aspire.{name}.Tests/Aspire.{name}.Tests.csproj`
- matching any rule implies the file is claimed
- a file may also be handled by `dotnet-affected` without a direct rule hit if it is part of the MSBuild-managed surface

Additional matching semantics:

- multi-rule matches are additive: all matching rules contribute tags and projects
- `{name}` captures the full variable portion between the fixed prefix and suffix of a pattern
- if a rule matches and a `projectTemplates` entry resolves to a missing path with `ifExists: true`, the file still counts as claimed; the unresolved project is simply not emitted
- test-project filtering for `dotnet-affected` may remain a small selector setting or convention, but it is not a separate path-matching primitive

## Concrete draft rules file

The draft file is:

- `eng/scripts/test-selection-rules.unified.json`

It covers the requested cases:

- template-specific changes run `Aspire.Templates.Tests`, but only explicitly
- simple `Aspire.{name}` and `Components/Aspire.{name}` mappings resolve to matching test projects
- extension and polyglot emit tags only
- universal ignores mix globs and regexes
- critical paths and unmatched files trigger run-all

## Concrete draft JSON

```json
{
  "version": 1,
  "description": "Draft unified rules model for conditional test selection.",
  "ignore": {
    "globs": [
      ".editorconfig",
      ".gitignore",
      "**/*.md",
      "docs/**",
      "eng/pipelines/**",
      "eng/test-configuration.json",
      "eng/testing/**",
      "eng/scripts/test-selection-rules.json",
      "eng/scripts/test-selection-rules.schema.json",
      "eng/scripts/test-selection-rules.audit.json",
      "eng/scripts/test-selection-rules.unified.json",
      "eng/scripts/test-selection-rules.hybrid.json",
      ".github/actions/**",
      ".github/instructions/**",
      ".github/skills/**",
      "tests/agent-scenarios/**",
      "tests/Aspire.Infrastructure.Tests/**",
      "src/Grafana/**",
      "src/Schema/**"
    ],
    "regexes": [
      "^\\.github/workflows/(?!polyglot-validation(?:\\.yml|/)).*$"
    ],
    "tagsWhenIgnoredOnly": [
      "ignored_only"
    ]
  },
  "fallbacks": {
    "triggerAll": {
      "paths": [
        "global.json",
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "NuGet.config",
        "tests/Directory.Build.props",
        "tests/Directory.Build.targets",
        "*.sln",
        "*.slnx",
        "src/Aspire.Hosting/**"
      ],
      "tags": [
        "run_all",
        "critical_path"
      ]
    },
    "unmatched": {
      "mode": "run_all",
      "tags": [
        "run_all",
        "unmatched_fallback"
      ]
    }
  },
  "rules": [
    {
      "id": "template-explicit",
      "description": "Template paths explicitly opt into Aspire.Templates.Tests.",
      "include": [
        "src/Aspire.ProjectTemplates/**",
        "tests/Aspire.Templates.Tests/**",
        "tests/Shared/TemplatesTesting/**",
        "tests/workloads.proj"
      ],
      "tags": [
        "templates"
      ],
      "projects": [
        {
          "path": "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj",
          "skipWhenRunAll": true
        }
      ]
    },
    {
      "id": "cli",
      "description": "CLI changes select CLI E2E coverage and emit the cli tag.",
      "include": [
        "src/Aspire.Cli/**",
        "eng/clipack/**",
        "tests/Aspire.Cli.EndToEnd.Tests/**"
      ],
      "tags": [
        "cli"
      ],
      "projects": [
        {
          "path": "tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj"
        }
      ]
    },
    {
      "id": "extension",
      "description": "Extension paths emit the extension tag for workflow consumers.",
      "include": [
        "extension/**"
      ],
      "tags": [
        "extension"
      ]
    },
    {
      "id": "polyglot",
      "description": "Polyglot workflow paths emit the polyglot tag.",
      "include": [
        ".github/workflows/polyglot-validation/**",
        ".github/workflows/polyglot-validation.yml"
      ],
      "tags": [
        "polyglot"
      ]
    },
    {
      "id": "aspire-named",
      "description": "Simple named mapping for Aspire sources and matching test projects.",
      "include": [
        "src/Aspire.{name}/**",
        "src/Components/Aspire.{name}/**",
        "tests/Aspire.{name}.Tests/**"
      ],
      "exclude": [
        "src/Aspire.ProjectTemplates/**",
        "src/Aspire.Cli/**",
        "src/Aspire.Hosting/**",
        "tests/Aspire.Templates.Tests/**",
        "tests/Aspire.Cli.EndToEnd.Tests/**",
        "tests/Aspire.EndToEnd.Tests/**"
      ],
      "tags": [
        "integrations"
      ],
      "projectTemplates": [
        {
          "path": "tests/Aspire.{name}.Tests/Aspire.{name}.Tests.csproj",
          "ifExists": true
        }
      ]
    }
  ]
}
```

## Simulated output JSON

The selector output should preserve provenance for both selection sources:

- `ruleSelectedProjects`
- `dotnetAffectedProjects`
- `selectedTestProjects`

### 1. Template change

Changed file:

- `src/Aspire.ProjectTemplates/templates/aspire-starter/Program.cs`

```json
{
  "runAll": false,
  "reason": "selective",
  "selectedTags": [
    "templates"
  ],
  "ruleSelectedProjects": [
    "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"
  ],
  "dotnetAffectedProjects": [],
  "selectedTestProjects": [
    "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"
  ],
  "ruleHits": [
    {
      "ruleId": "template-explicit",
      "matchedFiles": [
        "src/Aspire.ProjectTemplates/templates/aspire-starter/Program.cs"
      ],
      "emittedTags": [
        "templates"
      ],
      "emittedProjects": [
        "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"
      ]
    }
  ],
  "ignoredFiles": [],
  "unmatchedFiles": []
}
```

### 2. Extension change

Changed file:

- `extension/src/extension.ts`

```json
{
  "runAll": false,
  "reason": "selective",
  "selectedTags": [
    "extension"
  ],
  "ruleSelectedProjects": [],
  "dotnetAffectedProjects": [],
  "selectedTestProjects": [],
  "ruleHits": [
    {
      "ruleId": "extension",
      "matchedFiles": [
        "extension/src/extension.ts"
      ],
      "emittedTags": [
        "extension"
      ],
      "emittedProjects": []
    }
  ],
  "ignoredFiles": [],
  "unmatchedFiles": []
}
```

### 3. Polyglot workflow change

Changed file:

- `.github/workflows/polyglot-validation.yml`

```json
{
  "runAll": false,
  "reason": "selective",
  "selectedTags": [
    "polyglot"
  ],
  "ruleSelectedProjects": [],
  "dotnetAffectedProjects": [],
  "selectedTestProjects": [],
  "ruleHits": [
    {
      "ruleId": "polyglot",
      "matchedFiles": [
        ".github/workflows/polyglot-validation.yml"
      ],
      "emittedTags": [
        "polyglot"
      ],
      "emittedProjects": []
    }
  ],
  "ignoredFiles": [],
  "unmatchedFiles": []
}
```

### 4. Simple Aspire source change

Changed file:

- `src/Aspire.Dashboard/Model/ResourceViewModel.cs`

```json
{
  "runAll": false,
  "reason": "selective",
  "selectedTags": [
    "integrations"
  ],
  "ruleSelectedProjects": [
    "tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj"
  ],
  "dotnetAffectedProjects": [
    "tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj"
  ],
  "selectedTestProjects": [
    "tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj"
  ],
  "ruleHits": [
    {
      "ruleId": "aspire-named",
      "matchedFiles": [
        "src/Aspire.Dashboard/Model/ResourceViewModel.cs"
      ],
      "emittedTags": [
        "integrations"
      ],
      "emittedProjects": [
        "tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj"
      ]
    }
  ],
  "ignoredFiles": [],
  "unmatchedFiles": []
}
```

Even though the final test project set is the same here, the provenance shows that:

- the rule selected the test directly
- `dotnet-affected` also independently reached the same test project through MSBuild analysis

### 5. Transitive-only coverage from `dotnet-affected`

Changed file:

- `tests/Aspire.TestUtilities/SomeSharedHelper.cs`

```json
{
  "runAll": false,
  "reason": "selective",
  "selectedTags": [],
  "ruleSelectedProjects": [],
  "dotnetAffectedProjects": [
    "tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj",
    "tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj"
  ],
  "selectedTestProjects": [
    "tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj",
    "tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj"
  ],
  "ruleHits": [],
  "ignoredFiles": [],
  "unmatchedFiles": []
}
```

This is the important additive case:

- no unified rule had to name every downstream test project
- `dotnet-affected` contributes the transitive MSBuild coverage
- the final result is still explainable because the output keeps source provenance

## Known coverage gaps in the minimal draft

The draft JSON is intentionally minimal and does not yet add explicit rules for every current repo-specific surface.

Examples that still need an explicit design choice before rollout:

- `playground/**`
- `tools/TestSelector/**`
- `src/Tools/ConfigurationSchemaGenerator/**`
- `src/Shared/**`
- `src/Vendoring/**`
- `tests/Aspire.EndToEnd.Tests/**`

For each of these, the repo should choose one of:

- add an explicit unified rule
- rely on delegated MSBuild/project-graph analysis
- intentionally ignore the path
- intentionally allow unmatched fallback to trigger run-all

### 6. Trigger-all path

Changed file:

- `Directory.Packages.props`

```json
{
  "runAll": true,
  "reason": "trigger_all",
  "selectedTags": [
    "run_all",
    "critical_path"
  ],
  "ruleSelectedProjects": [],
  "dotnetAffectedProjects": [],
  "selectedTestProjects": [],
  "ruleHits": [],
  "trigger": {
    "file": "Directory.Packages.props",
    "pattern": "Directory.Packages.props"
  },
  "ignoredFiles": [],
  "unmatchedFiles": []
}
```

`Aspire.Templates.Tests` is still absent here because its project entry is marked `skipWhenRunAll: true`.

### 7. Unmatched fallback

Changed file:

- `tools/NewArea/new-script.ts`

```json
{
  "runAll": true,
  "reason": "unmatched",
  "selectedTags": [
    "run_all",
    "unmatched_fallback"
  ],
  "ruleSelectedProjects": [],
  "dotnetAffectedProjects": [],
  "selectedTestProjects": [],
  "ruleHits": [],
  "ignoredFiles": [],
  "unmatchedFiles": [
    "tools/NewArea/new-script.ts"
  ]
}
```

## Recommendation

This smaller model is the right baseline:

- top-level `ignore`
- top-level `fallbacks`
- direct `rules`

It is expressive enough for the current cases without introducing extra layers that the workflow does not yet need.

The key addition is in selector behavior, not config structure:

- rules provide direct, explainable project selection and job tags
- `dotnet-affected` adds transitive MSBuild coverage
- final project selection is the union of both
