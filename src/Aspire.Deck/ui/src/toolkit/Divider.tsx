export function Divider({
  orientation = "vertical",
  label,
  className,
}: {
  orientation?: "horizontal" | "vertical";
  label?: string;
  className?: string;
}) {
  const classes = [
    "deck-divider",
    orientation === "vertical" ? "deck-divider--vertical" : "",
    className,
  ]
    .filter(Boolean)
    .join(" ");

  return (
    <Separator
      className={classes}
      orientation={orientation}
      decorative={!label}
      aria-label={label}
      aria-orientation={orientation}
    />
  );
}
import { Separator } from "@/components/ui/separator";
