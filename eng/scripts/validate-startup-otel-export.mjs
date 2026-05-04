// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import fs from 'node:fs';
import path from 'node:path';

function getSpanAttributeValue(span, key) {
  for (const attribute of span.attributes ?? []) {
    if (attribute.key !== key) {
      continue;
    }

    const value = attribute.value ?? {};
    for (const propertyName of ['stringValue', 'intValue', 'doubleValue', 'boolValue']) {
      if (Object.prototype.hasOwnProperty.call(value, propertyName)) {
        return value[propertyName];
      }
    }
  }

  return null;
}

function groupBy(values, selector) {
  const groups = new Map();
  for (const value of values) {
    const key = selector(value);
    if (!groups.has(key)) {
      groups.set(key, []);
    }

    groups.get(key).push(value);
  }

  return groups;
}

function fail(message) {
  throw new Error(message);
}

function getOptionalPathList(directory, extension) {
  return directory && fs.existsSync(directory)
    ? fs.readdirSync(directory).filter(fileName => fileName.endsWith(extension)).sort().map(fileName => path.join(directory, fileName))
    : [];
}

const exportDir = process.env.EXPORT_DIR;
const tracesDir = path.join(exportDir, 'traces');
const traceFiles = fs.existsSync(tracesDir)
  ? fs.readdirSync(tracesDir).filter(fileName => fileName.endsWith('.json'))
  : [];

const spans = [];
for (const traceFile of traceFiles) {
  const tracePath = path.join(tracesDir, traceFile);
  const document = JSON.parse(fs.readFileSync(tracePath, 'utf8'));

  // Dashboard trace export files use the OTLP JSON shape:
  // { resourceSpans: [{ scopeSpans: [{ scope: { name }, spans: [...] }] }] }
  for (const resourceSpan of document.resourceSpans ?? []) {
    for (const scopeSpan of resourceSpan.scopeSpans ?? []) {
      for (const span of scopeSpan.spans ?? []) {
        spans.push({
          File: traceFile,
          Scope: scopeSpan.scope?.name ?? null,
          Name: span.name ?? null,
          TraceId: span.traceId ?? null,
          SpanId: span.spanId ?? null,
          ParentSpanId: span.parentSpanId ?? null,
          StartupOperationId: getSpanAttributeValue(span, 'aspire.startup.operation_id'),
          CommandName: getSpanAttributeValue(span, 'aspire.cli.command.name'),
          ProcessId: getSpanAttributeValue(span, 'process.pid'),
          DcpCreateObjectId: getSpanAttributeValue(span, 'aspire.hosting.dcp.create_object.id'),
          DcpCreateObjectKind: getSpanAttributeValue(span, 'aspire.hosting.dcp.create_object.kind'),
          DcpCreateObjectName: getSpanAttributeValue(span, 'aspire.hosting.dcp.create_object.name'),
          DcpCreateObjectSpanId: getSpanAttributeValue(span, 'aspire.hosting.dcp.create_object.span_id'),
          LinkSpanIds: (span.links ?? []).map(link => link.spanId).filter(Boolean),
          EventNames: (span.events ?? []).map(event => event.name).filter(Boolean)
        });
      }
    }
  }
}

fs.writeFileSync(process.env.SPAN_SUMMARY_PATH, JSON.stringify(spans, null, 2));

const startupGroups = groupBy(spans.filter(span => span.StartupOperationId), span => span.StartupOperationId);
if (startupGroups.size === 0) {
  fail(`No exported spans contained aspire.startup.operation_id. See ${process.env.SPAN_SUMMARY_PATH}`);
}

let validStartupOperationId = null;
let validTraceId = null;
let validTraceSpans = null;
const requireDcpSpans = process.env.REQUIRE_DCP_SPANS === 'true';

for (const [startupOperationId, startupSpans] of startupGroups) {
  const traceGroups = groupBy(startupSpans.filter(span => span.TraceId), span => span.TraceId);

  for (const [traceId, traceSpans] of traceGroups) {
    const hasStartCommandSpan = traceSpans.some(span =>
      span.Scope === 'Aspire.Cli.Profiling' &&
      span.Name === 'aspire/cli/start_apphost.spawn_child');
    const hasChildDiagnosticSpan = traceSpans.some(span =>
      span.Scope === 'Aspire.Cli.Profiling' &&
      [
        'aspire/cli/apphost.ensure_dev_certificates',
        'aspire/cli/backchannel.connect',
        'aspire/cli/backchannel.get_dashboard_urls',
        'aspire/cli/dotnet.build',
        'aspire/cli/run'
      ].includes(span.Name));
    const hasHostingDcpSpan = traceSpans.some(span =>
      span.Scope === 'Aspire.Hosting.Profiling' &&
      ['aspire.hosting.dcp.run_application', 'aspire.hosting.dcp.create_rendered_resources', 'aspire.hosting.dcp.allocate_service_addresses'].includes(span.Name));
    const hasResourceCreateSpan = traceSpans.some(span =>
      span.Scope === 'Aspire.Hosting.Profiling' && span.Name === 'aspire.hosting.resource.create');
    const hasResourceWaitSpan = traceSpans.some(span =>
      span.Scope === 'Aspire.Hosting.Profiling' &&
      ['aspire.hosting.resource.before_start_wait', 'aspire.hosting.resource.wait_for_dependencies', 'aspire.hosting.resource.wait_for_dependency'].includes(span.Name));
    const hasDcpProcessSpan = traceSpans.some(span =>
      span.Scope === 'dcp.startup' &&
      ['dcp.command', 'dcp.start_apiserver', 'dcp.start_apiserver.fork', 'dcp.run', 'dcp.apiserver.start', 'dcp.hosted_services.start', 'dcp.controllers.run', 'dcp.controllers.create_manager'].includes(span.Name));
    const hasDcpResourceSpan = traceSpans.some(span =>
      span.Scope === 'dcp.startup' &&
      ['dcp.controller.reconcile', 'dcp.executable.manage', 'dcp.service.ensure_effective_address', 'dcp.container.manage'].includes(span.Name));
    const hasDcpResourceObservedSpan = traceSpans.some(span =>
      span.Scope === 'Aspire.Hosting.Profiling' &&
      span.Name === 'aspire.hosting.dcp.resource_observed');
    const hasDcpCreateObjectLink = traceSpans.some(span =>
      span.Scope === 'dcp.startup' &&
      span.DcpCreateObjectId &&
      span.DcpCreateObjectSpanId &&
      span.LinkSpanIds.includes(span.DcpCreateObjectSpanId));
    const hasResourceWaitEvents = traceSpans.some(span =>
      span.Scope === 'Aspire.Hosting.Profiling' &&
      span.EventNames.includes('aspire.resource.wait.observed') &&
      span.EventNames.includes('aspire.resource.wait.completed'));
    const hasRequiredDcpSpans = !requireDcpSpans || (hasDcpProcessSpan && hasDcpResourceSpan);

    if (hasStartCommandSpan &&
        hasChildDiagnosticSpan &&
        hasHostingDcpSpan &&
        hasResourceCreateSpan &&
        hasResourceWaitSpan &&
        hasDcpResourceObservedSpan &&
        hasDcpCreateObjectLink &&
        hasResourceWaitEvents &&
        hasRequiredDcpSpans) {
      validStartupOperationId = startupOperationId;
      validTraceId = traceId;
      validTraceSpans = traceSpans;
      break;
    }
  }

  if (validStartupOperationId !== null) {
    break;
  }
}

if (validStartupOperationId === null) {
  const dcpRequirement = requireDcpSpans ? ', DCP process, and DCP resource/controller' : '';
  fail(`No startup operation contained correlated CLI, Hosting DCP resource creation, DCP resource observation, resource wait events, Hosting-to-DCP links${dcpRequirement} spans in one trace. See ${process.env.SPAN_SUMMARY_PATH}`);
}

const startJson = JSON.parse(fs.readFileSync(process.env.START_JSON_PATH, 'utf8'));
const summary = {
  RunRoot: process.env.RUN_ROOT,
  TargetAspirePath: process.env.TARGET_ASPIRE_PATH,
  ProfilerAspirePath: process.env.PROFILER_ASPIRE_PATH,
  LayoutPath: process.env.LAYOUT_PATH || null,
  DcpPath: process.env.DCP_PATH || null,
  PostStartDelaySeconds: Number(process.env.POST_START_DELAY_SECONDS ?? '0'),
  RequireDcpSpans: requireDcpSpans,
  DashboardUrl: process.env.DASHBOARD_URL,
  OtlpGrpcUrl: process.env.OTLP_GRPC_URL,
  OtlpHttpUrl: process.env.OTLP_HTTP_URL,
  AppHostPath: process.env.APPHOST_PATH,
  StartedDashboardUrl: startJson.dashboardUrl ?? null,
  ExportZip: process.env.EXPORT_ZIP,
  DotnetTraceDirectory: process.env.DOTNET_TRACE_DIR || null,
  DotnetTraceFiles: getOptionalPathList(process.env.DOTNET_TRACE_DIR, '.nettrace'),
  DotnetBinlogDirectory: process.env.DOTNET_BINLOG_DIR || null,
  DotnetBinlogFiles: getOptionalPathList(process.env.DOTNET_BINLOG_DIR, '.binlog'),
  SpanSummary: process.env.SPAN_SUMMARY_PATH,
  StartupOperationId: validStartupOperationId,
  CorrelatedSpanCount: validTraceSpans.length,
  TraceId: validTraceId
};

fs.writeFileSync(path.join(process.env.RUN_ROOT, 'summary.json'), JSON.stringify(summary, null, 2));
console.log(JSON.stringify(summary, null, 2));
