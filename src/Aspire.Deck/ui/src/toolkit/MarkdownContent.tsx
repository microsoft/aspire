import { Fragment, type ReactNode } from "react";

export interface MarkdownContentProps {
  markdown: string;
  enabled?: boolean;
  className?: string;
  id?: string;
  onLinkClick?: (url: string) => void;
}

export function MarkdownContent({
  markdown,
  enabled = true,
  className,
  id,
  onLinkClick,
}: MarkdownContentProps) {
  const classes = ["markdown-content", className].filter(Boolean).join(" ");
  if (!enabled) {
    return <span id={id} className={classes}>{markdown}</span>;
  }

  return <div id={id} className={classes}>{renderBlocks(markdown, onLinkClick)}</div>;
}

function renderBlocks(markdown: string, onLinkClick?: (url: string) => void): ReactNode[] {
  const lines = markdown.replaceAll("\r\n", "\n").replaceAll("\r", "\n").split("\n");
  const blocks: ReactNode[] = [];
  let index = 0;

  while (index < lines.length) {
    const line = lines[index]!;
    if (!line.trim()) {
      index++;
      continue;
    }

    if (line.startsWith("```")) {
      const language = line.slice(3).trim();
      const code: string[] = [];
      index++;
      while (index < lines.length && !lines[index]!.startsWith("```")) {
        code.push(lines[index++]!);
      }
      if (index < lines.length) index++;
      blocks.push(<pre key={`code-${index}`}><code data-language={language || undefined}>{code.join("\n")}</code></pre>);
      continue;
    }

    const heading = /^(#{1,6})\s+(.+)$/.exec(line);
    if (heading) {
      const level = heading[1]!.length;
      const content = renderInline(heading[2]!, onLinkClick);
      blocks.push(level <= 2
        ? <h3 key={`heading-${index}`}>{content}</h3>
        : <h4 key={`heading-${index}`}>{content}</h4>);
      index++;
      continue;
    }

    if (/^\s*[-*]\s+/.test(line)) {
      const items: ReactNode[] = [];
      while (index < lines.length && /^\s*[-*]\s+/.test(lines[index]!)) {
        const content = lines[index]!.replace(/^\s*[-*]\s+/, "");
        items.push(<li key={`item-${index}`}>{renderInline(content, onLinkClick)}</li>);
        index++;
      }
      blocks.push(<ul key={`list-${index}`}>{items}</ul>);
      continue;
    }

    if (/^\s*\d+\.\s+/.test(line)) {
      const items: ReactNode[] = [];
      while (index < lines.length && /^\s*\d+\.\s+/.test(lines[index]!)) {
        const content = lines[index]!.replace(/^\s*\d+\.\s+/, "");
        items.push(<li key={`item-${index}`}>{renderInline(content, onLinkClick)}</li>);
        index++;
      }
      blocks.push(<ol key={`list-${index}`}>{items}</ol>);
      continue;
    }

    if (line.startsWith("> ")) {
      const quote: string[] = [];
      while (index < lines.length && lines[index]!.startsWith("> ")) {
        quote.push(lines[index++]!.slice(2));
      }
      blocks.push(<blockquote key={`quote-${index}`}>{renderInline(quote.join(" "), onLinkClick)}</blockquote>);
      continue;
    }

    const paragraph: string[] = [];
    while (index < lines.length && lines[index]!.trim() && !isBlockStart(lines[index]!)) {
      paragraph.push(lines[index++]!);
    }
    if (paragraph.length === 0) {
      paragraph.push(lines[index++]!);
    }
    blocks.push(
      <p key={`paragraph-${index}`}>
        {paragraph.map((value, lineIndex) => (
          <Fragment key={lineIndex}>
            {lineIndex > 0 ? <br /> : null}
            {renderInline(value, onLinkClick)}
          </Fragment>
        ))}
      </p>,
    );
  }

  return blocks;
}

function isBlockStart(line: string): boolean {
  return line.startsWith("```") || /^(#{1,6})\s+/.test(line) || /^\s*[-*]\s+/.test(line) || /^\s*\d+\.\s+/.test(line) || line.startsWith("> ");
}

function renderInline(markdown: string, onLinkClick?: (url: string) => void): ReactNode[] {
  const pattern = /(`[^`]+`|\*\*[^*]+\*\*|\*[^*]+\*|\[[^\]]+\]\([^)]+\))/g;
  const nodes: ReactNode[] = [];
  let offset = 0;
  let match: RegExpExecArray | null;

  while ((match = pattern.exec(markdown)) !== null) {
    if (match.index > offset) nodes.push(markdown.slice(offset, match.index));
    const token = match[0];
    if (token.startsWith("`")) {
      nodes.push(<code key={match.index}>{token.slice(1, -1)}</code>);
    } else if (token.startsWith("**")) {
      nodes.push(<strong key={match.index}>{renderInline(token.slice(2, -2), onLinkClick)}</strong>);
    } else if (token.startsWith("*")) {
      nodes.push(<em key={match.index}>{renderInline(token.slice(1, -1), onLinkClick)}</em>);
    } else {
      const link = /^\[([^\]]+)\]\(([^)]+)\)$/.exec(token)!;
      const label = link[1]!;
      const url = link[2]!;
      if (isSafeUrl(url)) {
        nodes.push(
          <a
            key={match.index}
            href={url}
            target="_blank"
            rel="noopener noreferrer"
            onClick={onLinkClick ? (event) => { event.preventDefault(); onLinkClick(url); } : undefined}
          >
            {label}
          </a>,
        );
      } else {
        nodes.push(<span key={match.index}>{label} ({url})</span>);
      }
    }
    offset = match.index + token.length;
  }
  if (offset < markdown.length) nodes.push(markdown.slice(offset));
  return nodes;
}

function isSafeUrl(url: string): boolean {
  if (url.startsWith("/") || url.startsWith("#")) return true;
  try {
    return ["http:", "https:", "mailto:"].includes(new URL(url).protocol);
  } catch {
    return false;
  }
}
