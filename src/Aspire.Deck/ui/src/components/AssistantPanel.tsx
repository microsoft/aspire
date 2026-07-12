import { useEffect, useRef, useState } from "react";
import type { AssistantMessage, AssistantModel } from "../api/types";
import { getAssistantInfo, streamAssistantChat } from "../api/deck";
import { Button, Drawer, IconButton, MarkdownContent, NamedIcon, Select } from "../toolkit";

export function AssistantPanel({ initialPrompt, onClose }: { initialPrompt?: string | null; onClose: () => void }) {
  const [messages, setMessages] = useState<AssistantMessage[]>([]);
  const [models, setModels] = useState<AssistantModel[]>([]);
  const [model, setModel] = useState("");
  const [draft, setDraft] = useState("");
  const [expanded, setExpanded] = useState(false);
  const [responding, setResponding] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const responseController = useRef<AbortController | null>(null);
  const submittedInitialPrompt = useRef<string | null>(null);

  useEffect(() => {
    void getAssistantInfo()
      .then((info) => {
        setModels(info.models);
        setModel((current) => current || info.models[0]?.family || "");
      })
      .catch((reason) => setError(reason instanceof Error ? reason.message : String(reason)));
    return () => responseController.current?.abort();
  }, []);

  const newChat = (): void => {
    responseController.current?.abort();
    responseController.current = null;
    setResponding(false);
    setMessages([]);
    setDraft("");
    setError(null);
  };

  const sendPrompt = async (promptValue: string): Promise<void> => {
    const prompt = promptValue.trim();
    if (!prompt || responding) return;
    const userMessage: AssistantMessage = { role: "user", content: prompt };
    const requestMessages = [...messages, userMessage];
    setMessages([...requestMessages, { role: "assistant", content: "" }]);
    setDraft("");
    setError(null);
    setResponding(true);
    const controller = new AbortController();
    responseController.current = controller;
    try {
      await streamAssistantChat(
        { messages: requestMessages, model: model || null },
        (event) => {
          if (event.type === "content" && event.content) {
            setMessages((current) => current.map((message, index) =>
              index === current.length - 1 && message.role === "assistant"
                ? { ...message, content: message.content + event.content }
                : message));
          } else if (event.type === "error") {
            setError(event.message || "The assistant could not complete the response.");
          }
        },
        controller.signal,
      );
    } catch (reason) {
      if (!(reason instanceof DOMException && reason.name === "AbortError")) {
        setError(reason instanceof Error ? reason.message : String(reason));
      }
    } finally {
      if (responseController.current === controller) {
        responseController.current = null;
        setResponding(false);
      }
    }
  };

  useEffect(() => {
    if (!initialPrompt || !model || submittedInitialPrompt.current === initialPrompt) return;
    submittedInitialPrompt.current = initialPrompt;
    void sendPrompt(initialPrompt);
  }, [initialPrompt, model]);

  return (
    <Drawer
      title="Assistant"
      leading={<NamedIcon name="ChatSparkle" size={20} />}
      closeLabel="Close assistant"
      className={expanded ? "assistant-panel assistant-panel--expanded" : "assistant-panel"}
      size={expanded ? 960 : 480}
      onClose={onClose}
      headerActions={(
        <>
          <IconButton label="New chat" icon={<NamedIcon name="Add" size={16} />} onClick={newChat} />
          <IconButton label={expanded ? "Collapse assistant" : "Expand assistant"} icon={<NamedIcon name="Open" size={16} />} onClick={() => setExpanded((current) => !current)} />
        </>
      )}
      footer={(
        <div className="assistant-composer">
          <label className="assistant-composer__label" htmlFor="assistant-message">Message the assistant</label>
          <textarea
            id="assistant-message"
            rows={2}
            value={draft}
            placeholder="Ask about resources, logs, or traces"
            onChange={(event) => setDraft(event.currentTarget.value)}
            onKeyDown={(event) => {
              if (event.key === "Enter" && !event.shiftKey) {
                event.preventDefault();
                void sendPrompt(draft);
              }
            }}
          />
          <div className="assistant-composer__tools">
            <Select ariaLabel="Assistant model" value={model} options={models.map((item) => ({ value: item.family, label: item.displayName }))} disabled={responding || models.length === 0} onValueChange={setModel} />
            {responding ? (
              <Button onClick={() => responseController.current?.abort()}><NamedIcon name="Stop" size={16} /> Stop</Button>
            ) : (
              <Button variant="primary" disabled={!draft.trim() || models.length === 0} onClick={() => void sendPrompt(draft)}><NamedIcon name="Send" size={16} /> Send</Button>
            )}
          </div>
        </div>
      )}
    >
      <div className="assistant-conversation" role="log" aria-label="Assistant conversation" aria-live="polite">
        {messages.length === 0 ? (
          <div className="assistant-empty"><NamedIcon name="ChatSparkle" size={32} /><div>Ask the assistant to investigate your distributed application.</div></div>
        ) : messages.map((message, index) => (
          <article className={`assistant-message assistant-message--${message.role}`} key={index}>
            <div className="assistant-message__role">{message.role === "user" ? "You" : "Assistant"}</div>
            {message.role === "assistant" && !message.content && responding
              ? <div className="assistant-thinking">Thinking...</div>
              : <MarkdownContent markdown={message.content} />}
          </article>
        ))}
        {error ? <div className="assistant-error" role="alert">{error}</div> : null}
      </div>
    </Drawer>
  );
}
