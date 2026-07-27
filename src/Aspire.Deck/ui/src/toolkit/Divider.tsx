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

  if (orientation === "horizontal") {
    return <hr className={classes} aria-orientation="horizontal" aria-label={label} />;
  }

  return (
    <div
      className={classes}
      role="separator"
      aria-orientation="vertical"
      aria-label={label}
    />
  );
}
