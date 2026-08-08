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

export function applyTextStyle(text: string, style: string | null | undefined): string {
  if (!style) {
    return text;
  }

  return `${style}${text}\x1b[0m`;
}

/**
 * Standard SGR codes the debug console and the Aspire terminal render through the
 * workbench ANSI palette, so they follow the active color theme. 256-color and
 * truecolor escapes would not.
 *
 * These live here rather than next to the terminal provider because that module
 * imports `vscode`, and the debug console log formatting is host-free so it can be
 * exercised directly under Node.
 */
export const enum AnsiColors {
  Dim = '\x1b[2m',
  Green = '\x1b[32m',
  Yellow = '\x1b[33m',
  Blue = '\x1b[34m',
}
