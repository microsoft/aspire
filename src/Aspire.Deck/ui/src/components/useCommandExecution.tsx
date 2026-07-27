import { useCallback, useEffect, useRef, useState } from "react";
import type { Resource, ResourceCommand } from "../api/types";
import { executeCommand } from "../api/deck";
import { Button, IconButton, CloseIcon, TextViewerDialog, type TextViewerRequest } from "../toolkit";

interface CommandFeedback {
  message: string;
  tone: "success" | "error";
  result: TextViewerRequest | null;
}

export function useCommandExecution() {
  const [feedback, setFeedback] = useState<CommandFeedback | null>(null);
  const [viewer, setViewer] = useState<TextViewerRequest | null>(null);
  const timeoutRef = useRef<number | null>(null);

  useEffect(() => () => {
    if (timeoutRef.current !== null) window.clearTimeout(timeoutRef.current);
  }, []);

  const runCommand = useCallback(async (resource: Resource, command: ResourceCommand): Promise<void> => {
    if (timeoutRef.current !== null) window.clearTimeout(timeoutRef.current);
    try {
      const response = await executeCommand({
        resourceName: resource.name,
        resourceType: resource.resourceType,
        commandName: command.name,
      });
      const result = response.result ? {
        title: command.displayName,
        value: response.result.value,
        format: response.result.format,
        downloadFileName: `${command.name}.${response.result.format === "markdown" ? "md" : response.result.format === "json" ? "json" : "txt"}`,
      } satisfies TextViewerRequest : null;
      setFeedback({
        message: response.kind === "succeeded"
          ? `${command.displayName} succeeded`
          : response.message ?? `${command.displayName} ${response.kind}`,
        tone: response.kind === "succeeded" ? "success" : "error",
        result,
      });
      if (result && response.result?.displayImmediately) setViewer(result);
      if (!result) timeoutRef.current = window.setTimeout(() => setFeedback(null), 3200);
    } catch (error) {
      setFeedback({ message: `Command failed: ${String(error)}`, tone: "error", result: null });
      timeoutRef.current = window.setTimeout(() => setFeedback(null), 3200);
    }
  }, []);

  const feedbackUi = (
    <>
      {feedback ? (
        <div className="toast command-feedback" role="status" aria-live="polite">
          <span className={`state__dot ${feedback.tone}`} />
          <span>{feedback.message}</span>
          {feedback.result ? <Button size="small" onClick={() => setViewer(feedback.result)}>View response</Button> : null}
          <IconButton label="Dismiss command result" icon={<CloseIcon size={14} />} onClick={() => setFeedback(null)} />
        </div>
      ) : null}
      <TextViewerDialog request={viewer} onClose={() => setViewer(null)} />
    </>
  );

  return { runCommand, feedbackUi };
}
