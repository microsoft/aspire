import { useEffect, useState } from "react";
import {
  Dialog as ShadcnDialog,
  DialogContent,
  DialogTitle,
} from "@/components/ui/dialog";
import { ScrollArea } from "@/components/ui/scroll-area";
import { Button } from "./Button";
import { NamedIcon } from "./Icons";
import { MarkdownContent } from "./MarkdownContent";

export interface TextViewerRequest {
  title: string;
  value: string;
  format?: "text" | "json" | "markdown";
  downloadFileName?: string;
}

export function TextViewerDialog({
  request,
  onClose,
}: {
  request: TextViewerRequest | null;
  onClose: () => void;
}) {
  const [copyStatus, setCopyStatus] = useState("");

  useEffect(() => {
    setCopyStatus("");
  }, [request?.value]);

  if (request === null) {
    return null;
  }

  const copy = async (): Promise<void> => {
    try {
      await navigator.clipboard.writeText(request.value);
      setCopyStatus("Copied");
    } catch {
      setCopyStatus("Copy failed");
    }
  };

  const download = (): void => {
    const extension = request.format === "json" ? "json" : request.format === "markdown" ? "md" : "txt";
    const href = URL.createObjectURL(new Blob([request.value], { type: "text/plain;charset=utf-8" }));
    const anchor = document.createElement("a");
    anchor.href = href;
    anchor.download = request.downloadFileName ?? `command-result.${extension}`;
    anchor.click();
    URL.revokeObjectURL(href);
  };

  const format = request.format ?? "text";
  let displayValue = request.value;
  if (format === "json") {
    try {
      displayValue = JSON.stringify(JSON.parse(request.value), null, 2);
    } catch {
      // Preserve malformed JSON exactly so command output is never discarded.
    }
  }

  return (
    <ShadcnDialog open onOpenChange={(open) => {
      if (!open) onClose();
    }}>
      <DialogContent className="modal text-viewer" closeLabel="Close visualizer">
        <DialogTitle className="modal__title">{request.title}</DialogTitle>
        <ScrollArea className="text-viewer__content">
          {format === "markdown" ? (
            <div className="text-viewer__content--markdown">
              <MarkdownContent markdown={request.value} />
            </div>
          ) : (
            <pre data-format={format}>
              <code>{displayValue}</code>
            </pre>
          )}
        </ScrollArea>
        <div className="modal__actions">
          <span className="text-viewer__status" role="status" aria-live="polite">{copyStatus}</span>
          <Button onClick={download}>
            <NamedIcon name="ArrowDownload" size={16} />
            Download
          </Button>
          <Button onClick={() => void copy()}>
            <NamedIcon name="Copy" size={16} />
            Copy
          </Button>
          <Button variant="primary" onClick={onClose}>Close</Button>
        </div>
      </DialogContent>
    </ShadcnDialog>
  );
}
