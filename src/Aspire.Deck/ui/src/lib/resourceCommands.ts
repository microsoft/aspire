import type { ResourceCommand } from "../api/types";

const maxHighlightedCommands = 2;

export function partitionResourceCommands(commands: ResourceCommand[]): {
  highlightedCommands: ResourceCommand[];
  menuCommands: ResourceCommand[];
} {
  const visible = commands.filter((command) => command.state !== "hidden");
  const highlighted = visible.filter((command) => command.isHighlighted);
  const highlightedCommands = highlighted.slice(0, maxHighlightedCommands);
  const directNames = new Set(highlightedCommands.map((command) => command.name));
  return {
    highlightedCommands,
    menuCommands: visible.filter((command) => !directNames.has(command.name)),
  };
}
