const RFC3339_PREFIX = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?(?:Z|[+-]\d{2}:?\d{2})?)(?:\s|$)/;

export interface ParsedConsoleLine {
  text: string;
  timestamp: Date | null;
}

export function parseConsoleLine(rawText: string): ParsedConsoleLine {
  const match = RFC3339_PREFIX.exec(rawText);
  if (!match) {
    return { text: rawText, timestamp: null };
  }

  const timestamp = new Date(match[1]!);
  if (Number.isNaN(timestamp.getTime())) {
    return { text: rawText, timestamp: null };
  }

  return {
    text: rawText.slice(match[0].length),
    timestamp,
  };
}

export function formatConsoleTimestamp(timestamp: Date, utc: boolean): string {
  if (utc) {
    return timestamp.toISOString().slice(0, 19) + "Z";
  }

  const year = timestamp.getFullYear().toString().padStart(4, "0");
  const month = (timestamp.getMonth() + 1).toString().padStart(2, "0");
  const day = timestamp.getDate().toString().padStart(2, "0");
  const hours = timestamp.getHours().toString().padStart(2, "0");
  const minutes = timestamp.getMinutes().toString().padStart(2, "0");
  const seconds = timestamp.getSeconds().toString().padStart(2, "0");
  return `${year}-${month}-${day}T${hours}:${minutes}:${seconds}`;
}
