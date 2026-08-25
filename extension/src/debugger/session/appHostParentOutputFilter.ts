export interface AppHostParentOutput {
  output: string;
  category: 'stdout' | 'stderr';
}

export class AppHostParentOutputFilter {
  private _continuingDroppedLog = false;
  private _continuingErrorBlock = false;
  private _lastCategory: string | undefined;

  filter(output: string, category: string | undefined): AppHostParentOutput | undefined {
    // Per the DAP spec the `category` field is optional; clients should treat a
    // missing category as `'console'`. Normalize once at the boundary so state
    // tracking and per-line classification see a consistent value, and so
    // category-less debug-adapter output gets the same suppression as `'console'`
    // instead of being mirrored to the parent debug console as stdout.
    const normalizedCategory = category ?? 'console';

    if (normalizedCategory === 'debug') {
      this.resetState();
      this._lastCategory = normalizedCategory;
      return undefined;
    }

    // Continuation state (dropped log / error block) only makes sense within a single
    // logical stream. When the DAP category changes (e.g. console -> stdout) we are
    // looking at a different stream and previous indented-continuation context no
    // longer applies.
    if (normalizedCategory !== this._lastCategory) {
      this.resetState();
    }
    this._lastCategory = normalizedCategory;

    const segments = output.match(/[^\r\n]*(?:\r\n|\r|\n|$)/g)?.filter(segment => segment.length > 0) ?? [];
    let filteredOutput = '';
    // If the DAP delivered this chunk on stderr, keep the whole emitted message on
    // stderr — the channel itself is authoritative regardless of per-line classification.
    let hasErrorOutput = normalizedCategory === 'stderr';

    for (const segment of segments) {
      const outputCategory = this.getLineCategory(segment, normalizedCategory);
      if (outputCategory) {
        filteredOutput += segment;
        hasErrorOutput ||= outputCategory === 'stderr';
      }
    }

    if (filteredOutput.length === 0) {
      return undefined;
    }

    return {
      output: filteredOutput,
      category: hasErrorOutput ? 'stderr' : 'stdout'
    };
  }

  private getLineCategory(segment: string, category: string): 'stdout' | 'stderr' | undefined {
    const line = segment.replace(/(?:\r\n|\r|\n)$/, '');
    const trimmedLine = line.trim();

    if (trimmedLine.length === 0) {
      return !this._continuingDroppedLog && this.shouldMirrorConsoleOutput(category) ? this.getCurrentCategory(category) : undefined;
    }

    if (this._continuingDroppedLog && isIndentedContinuation(line)) {
      return undefined;
    }

    if (this._continuingErrorBlock && isIndentedContinuation(line)) {
      return 'stderr';
    }

    const logSeverity = getConsoleLogSeverity(trimmedLine);
    if (logSeverity) {
      this._continuingDroppedLog = logSeverity === 'low';
      this._continuingErrorBlock = logSeverity === 'severe';

      return logSeverity === 'low' ? undefined : this.getCurrentCategory(category);
    }

    const isSevereOutput = isSevereRuntimeOutputLine(trimmedLine);
    this._continuingDroppedLog = false;
    this._continuingErrorBlock = isSevereOutput;

    if (category === 'console' && !isSevereOutput) {
      return undefined;
    }

    return this.getCurrentCategory(category);
  }

  private shouldMirrorConsoleOutput(category: string): boolean {
    return category !== 'console' || this._continuingErrorBlock;
  }

  private getCurrentCategory(category: string): 'stdout' | 'stderr' {
    return category === 'stderr' || this._continuingErrorBlock ? 'stderr' : 'stdout';
  }

  private resetState() {
    this._continuingDroppedLog = false;
    this._continuingErrorBlock = false;
  }
}

function getConsoleLogSeverity(line: string): 'low' | 'normal' | 'severe' | undefined {
  const defaultConsoleLogLevel = /^(trce|dbug|info|warn|fail|crit):\s/.exec(line)?.[1];
  if (defaultConsoleLogLevel) {
    return defaultConsoleLogLevel === 'trce' || defaultConsoleLogLevel === 'dbug'
      ? 'low'
      : defaultConsoleLogLevel === 'fail' || defaultConsoleLogLevel === 'crit'
        ? 'severe'
        : 'normal';
  }

  // Microsoft.Extensions.Logging "simple" console formatter emits lines shaped like
  // `<CategoryTypeName>[<EventId>]?: <Level>: <message>`. Real category names are
  // namespaced .NET type names containing at least one dot (e.g.
  // `Aspire.Hosting.Health.ResourceHealthCheckService`). Requiring a dot avoids
  // matching arbitrary user stdout like `"Status: Error: connection refused"`.
  const simpleConsoleLogLevel = /^[A-Za-z_]\w*(?:\.\w+)+(?:\[[^\]]+\])?:\s*(Trace|Debug|Information|Warning|Error|Critical):\s/.exec(line)?.[1];
  if (simpleConsoleLogLevel) {
    return simpleConsoleLogLevel === 'Trace' || simpleConsoleLogLevel === 'Debug'
      ? 'low'
      : simpleConsoleLogLevel === 'Error' || simpleConsoleLogLevel === 'Critical'
        ? 'severe'
        : 'normal';
  }

  return undefined;
}

function isIndentedContinuation(line: string): boolean {
  return /^\s+\S/.test(line);
}

function isSevereRuntimeOutputLine(line: string): boolean {
  // Typed exception — `Namespace.Type.NameException: message` (also matches plain `System.Exception:`).
  return /(?:^|\s)(?:[A-Za-z_][\w`]*\.)+(?:[A-Za-z_][\w`]*Exception|Exception):/.test(line)
    // JavaScript / Node.js error shapes — `Uncaught TypeError: ...`, `Error [CODE]: ...`.
    || /^(?:Uncaught\s+)?(?:[A-Za-z_$][\w$]*Error|Error)(?:\s+\[[^\]]+\])?:/.test(line)
    // Anchored fatal-marker prefixes only — bare word matches like `\bfailed\b` produced
    // false positives on user stdout (`"Failed payment retry queued"`, file paths
    // containing "error", etc.).
    || /^(?:fatal|critical|panic|aborted|segmentation\s+fault|unhandled\s+exception)\b/i.test(line);
}
