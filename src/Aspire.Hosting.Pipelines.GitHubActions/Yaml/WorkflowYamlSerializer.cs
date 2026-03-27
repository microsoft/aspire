// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;

namespace Aspire.Hosting.Pipelines.GitHubActions.Yaml;

/// <summary>
/// Serializes <see cref="WorkflowYaml"/> to a YAML string.
/// </summary>
internal static class WorkflowYamlSerializer
{
    public static string Serialize(WorkflowYaml workflow)
    {
        var sb = new StringBuilder();

        sb.AppendLine(CultureInfo.InvariantCulture, $"name: {workflow.Name}");
        sb.AppendLine();

        WriteOn(sb, workflow.On);

        if (workflow.Permissions is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("permissions:");
            foreach (var (key, value) in workflow.Permissions)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {key}: {value}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("jobs:");

        var firstJob = true;
        foreach (var (jobId, job) in workflow.Jobs)
        {
            if (!firstJob)
            {
                sb.AppendLine();
            }
            firstJob = false;

            WriteJob(sb, jobId, job);
        }

        return sb.ToString();
    }

    private static void WriteOn(StringBuilder sb, WorkflowTriggers triggers)
    {
        sb.AppendLine("on:");

        if (triggers.WorkflowDispatch)
        {
            sb.AppendLine("  workflow_dispatch:");
        }

        if (triggers.Push is not null)
        {
            sb.AppendLine("  push:");
            if (triggers.Push.Branches.Count > 0)
            {
                sb.AppendLine("    branches:");
                foreach (var branch in triggers.Push.Branches)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"      - {branch}");
                }
            }
        }
    }

    private static void WriteJob(StringBuilder sb, string jobId, JobYaml job)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"  {jobId}:");

        if (job.Name is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    name: {YamlQuote(job.Name)}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"    runs-on: {job.RunsOn}");

        if (job.If is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    if: {job.If}");
        }

        if (job.Environment is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    environment: {job.Environment}");
        }

        if (job.Needs is { Count: > 0 })
        {
            if (job.Needs.Count == 1)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    needs: {job.Needs[0]}");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    needs: [{string.Join(", ", job.Needs)}]");
            }
        }

        if (job.Concurrency is not null)
        {
            sb.AppendLine("    concurrency:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"      group: {job.Concurrency.Group}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"      cancel-in-progress: {(job.Concurrency.CancelInProgress ? "true" : "false")}");
        }

        if (job.Permissions is { Count: > 0 })
        {
            sb.AppendLine("    permissions:");
            foreach (var (key, value) in job.Permissions)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"      {key}: {value}");
            }
        }

        if (job.Env is { Count: > 0 })
        {
            sb.AppendLine("    env:");
            foreach (var (key, value) in job.Env)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"      {key}: {YamlQuote(value)}");
            }
        }

        if (job.Steps.Count > 0)
        {
            sb.AppendLine("    steps:");
            foreach (var step in job.Steps)
            {
                WriteStep(sb, step);
            }
        }
    }

    private static void WriteStep(StringBuilder sb, StepYaml step)
    {
        // First property determines the leading dash
        if (step.Name is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"      - name: {YamlQuote(step.Name)}");
        }
        else if (step.Uses is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"      - uses: {step.Uses}");
        }
        else if (step.Run is not null)
        {
            WriteRunStep(sb, step, leadWithDash: true);
            return;
        }
        else
        {
            return;
        }

        if (step.Id is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"        id: {step.Id}");
        }

        if (step.Uses is not null && step.Name is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"        uses: {step.Uses}");
        }

        if (step.With is { Count: > 0 })
        {
            sb.AppendLine("        with:");
            foreach (var (key, value) in step.With)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"          {key}: {YamlQuote(value)}");
            }
        }

        if (step.Env is { Count: > 0 })
        {
            sb.AppendLine("        env:");
            foreach (var (key, value) in step.Env)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"          {key}: {YamlQuote(value)}");
            }
        }

        if (step.Run is not null)
        {
            WriteRunStep(sb, step, leadWithDash: false);
        }
    }

    private static void WriteRunStep(StringBuilder sb, StepYaml step, bool leadWithDash)
    {
        var indent = leadWithDash ? "      " : "        ";
        var prefix = leadWithDash ? "- " : "";

        if (step.Run!.Contains('\n'))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{prefix}run: |");
            foreach (var line in step.Run.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    sb.AppendLine();
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}  {line}");
                }
            }
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{prefix}run: {step.Run}");
        }
    }

    private static string YamlQuote(string value)
    {
        if (value.Contains('\'') || value.Contains('"') || value.Contains(':') ||
            value.Contains('#') || value.Contains('{') || value.Contains('}') ||
            value.Contains('[') || value.Contains(']') || value.Contains('&') ||
            value.Contains('*') || value.Contains('!') || value.Contains('|') ||
            value.Contains('>') || value.Contains('%') || value.Contains('@'))
        {
            return $"'{value.Replace("'", "''")}'";
        }

        return value;
    }
}
