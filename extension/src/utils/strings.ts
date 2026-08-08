const emojiMap: { [key: string]: string; } = {
  ':ice:': '🧊',
  ':rocket:': '🚀',
  ':bug:': '🐛',
  ':microscope:': '🔬',
  ':linked_paperclips:': '🔗',
  ':chart_increasing:': '📈',
  ':chart_decreasing:': '📉',
  ':locked_with_key:': '🔒',
  ':play_button:': '▶️',
  ':check_mark:': '✅',
  ':cross_mark:': '❌',
  ':hammer_and_wrench:': '🛠️'
};

/**
 * Formats a string by replacing emoji codes (such as :ice:) with their corresponding Unicode characters.
 */
export function formatText(str: string): string {
  return str.replace(/:[a-z]+(?:_[a-z]+)*:/g, match => emojiMap[match] || match);
}

export function removeTrailingNewline(str: string): string {
  return str.replace(/(\r\n|\n)$/, '');
}

/**
 * Collapses runs of whitespace, including newlines, into single spaces. CLI status text can span
 * multiple lines, which neither a single-line VS Code label nor a screen reader renders well.
 */
export function collapseWhitespace(str: string): string {
  return str.replace(/\s+/g, ' ').trim();
}

/**
 * Escapes `$(name)` codicon syntax so text the extension does not control cannot inject icons into
 * VS Code surfaces that render them, such as the status bar and window progress. A leading
 * backslash is the escape VS Code itself honours.
 * See `escapeIcons` in https://github.com/microsoft/vscode/blob/main/src/vs/base/common/iconLabels.ts.
 */
export function escapeCodicons(str: string): string {
  return str.replace(/(\\)?\$\(/g, (match, alreadyEscaped) => alreadyEscaped ? match : '\\$(');
}

export function applyTextStyle(text: string, style: string | null | undefined): string {
  if (!style) {
    return text;
  }

  return `${style}${text}\x1b[0m`;
}