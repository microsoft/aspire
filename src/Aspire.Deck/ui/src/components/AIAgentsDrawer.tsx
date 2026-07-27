import { openExternal } from "../api/deck";
import { Button, Drawer, MarkdownContent, NamedIcon } from "../toolkit";

export function AIAgentsDrawer({ markdown, onClose }: { markdown: string; onClose: () => void }) {
  return (
    <Drawer
      title="AI agents"
      leading={<NamedIcon name="ChatSparkle" size={20} />}
      closeLabel="Close AI agents"
      onClose={onClose}
      footer={<Button onClick={onClose}>Close</Button>}
    >
      <MarkdownContent
        markdown={markdown}
        className="ai-agents"
        onLinkClick={(url) => void openExternal(url)}
      />
    </Drawer>
  );
}
