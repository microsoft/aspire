import type { CSSProperties, ReactNode } from "react";

interface IconProps {
  size?: number;
  className?: string;
  style?: CSSProperties;
}

// Minimal inline icon set (stroke-based, currentColor) so we avoid shipping an
// icon font/library. Each icon is a 24x24 viewBox.
function svg(path: ReactNode, props: IconProps) {
  const { size = 18, className, style } = props;
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.8}
      strokeLinecap="round"
      strokeLinejoin="round"
      className={className}
      style={style}
      aria-hidden="true"
    >
      {path}
    </svg>
  );
}

export const ResourcesIcon = (p: IconProps) =>
  svg(
    <>
      <rect x="3" y="3" width="7" height="7" rx="1.5" />
      <rect x="14" y="3" width="7" height="7" rx="1.5" />
      <rect x="3" y="14" width="7" height="7" rx="1.5" />
      <rect x="14" y="14" width="7" height="7" rx="1.5" />
    </>,
    p,
  );

export const ConsoleIcon = (p: IconProps) =>
  svg(
    <>
      <rect x="3" y="4" width="18" height="16" rx="2" />
      <path d="m7 9 3 3-3 3" />
      <path d="M13 15h4" />
    </>,
    p,
  );

export const LogsIcon = (p: IconProps) =>
  svg(
    <>
      <path d="M4 6h16" />
      <path d="M4 12h16" />
      <path d="M4 18h10" />
    </>,
    p,
  );

export const TracesIcon = (p: IconProps) =>
  svg(
    <>
      <path d="M4 5h16" />
      <path d="M4 5v14" />
      <path d="M8 9h9" />
      <path d="M8 13h6" />
      <path d="M8 17h11" />
    </>,
    p,
  );

export const MetricsIcon = (p: IconProps) =>
  svg(
    <>
      <path d="M4 19V5" />
      <path d="M4 15l4-5 4 3 7-8" />
    </>,
    p,
  );

export const CanvasIcon = (p: IconProps) =>
  svg(
    <>
      <rect x="3" y="3" width="18" height="18" rx="2" />
      <path d="M3 9h18" />
      <path d="M9 21V9" />
    </>,
    p,
  );

export const ProjectIcon = (p: IconProps) =>
  svg(
    <>
      <rect x="3" y="4" width="18" height="16" rx="2" />
      <path d="M3 9h18" />
      <circle cx="6.5" cy="6.5" r="0.6" fill="currentColor" />
    </>,
    p,
  );

export const ContainerIcon = (p: IconProps) =>
  svg(
    <>
      <path d="M12 3 3 7.5v9L12 21l9-4.5v-9z" />
      <path d="M3 7.5 12 12l9-4.5" />
      <path d="M12 12v9" />
    </>,
    p,
  );

export const ExecutableIcon = (p: IconProps) =>
  svg(
    <>
      <path d="m8 8-4 4 4 4" />
      <path d="m16 8 4 4-4 4" />
    </>,
    p,
  );

export const SearchIcon = (p: IconProps) =>
  svg(
    <>
      <circle cx="11" cy="11" r="7" />
      <path d="m20 20-3-3" />
    </>,
    p,
  );

export const PlayIcon = (p: IconProps) => svg(<path d="m6 4 14 8-14 8z" />, p);

export const StopIcon = (p: IconProps) =>
  svg(<rect x="5" y="5" width="14" height="14" rx="2" />, p);

export const RestartIcon = (p: IconProps) =>
  svg(
    <>
      <path d="M21 12a9 9 0 1 1-3-6.7" />
      <path d="M21 3v5h-5" />
    </>,
    p,
  );

export const CloseIcon = (p: IconProps) =>
  svg(
    <>
      <path d="m6 6 12 12" />
      <path d="m18 6-12 12" />
    </>,
    p,
  );

export const ExternalIcon = (p: IconProps) =>
  svg(
    <>
      <path d="M14 4h6v6" />
      <path d="M20 4 10 14" />
      <path d="M19 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1h5" />
    </>,
    p,
  );

export const EyeIcon = (p: IconProps) =>
  svg(
    <>
      <path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7z" />
      <circle cx="12" cy="12" r="2.5" />
    </>,
    p,
  );

export const EyeOffIcon = (p: IconProps) =>
  svg(
    <>
      <path d="M10.6 6.2A9.6 9.6 0 0 1 12 6c6.5 0 10 6 10 6a16 16 0 0 1-3 3.6" />
      <path d="M6.2 6.2A16 16 0 0 0 2 12s3.5 7 10 7a9.6 9.6 0 0 0 4-.9" />
      <path d="m2 2 20 20" />
    </>,
    p,
  );

export const SunIcon = (p: IconProps) =>
  svg(
    <>
      <circle cx="12" cy="12" r="4" />
      <path d="M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" />
    </>,
    p,
  );

export const MoonIcon = (p: IconProps) =>
  svg(<path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z" />, p);

export const BackIcon = (p: IconProps) =>
  svg(
    <>
      <path d="m15 18-6-6 6-6" />
    </>,
    p,
  );

export const LinkIcon = (p: IconProps) =>
  svg(
    <>
      <path d="M9 12h6" />
      <path d="M10 17H7a5 5 0 0 1 0-10h3" />
      <path d="M14 7h3a5 5 0 0 1 0 10h-3" />
    </>,
    p,
  );

// Maps a resource type to a representative icon.
export function ResourceTypeIcon({ type, ...rest }: { type: string } & IconProps) {
  const lower = type.toLowerCase();
  if (lower.includes("project")) {
    return <ProjectIcon {...rest} />;
  }
  if (lower.includes("container")) {
    return <ContainerIcon {...rest} />;
  }
  if (lower.includes("executable")) {
    return <ExecutableIcon {...rest} />;
  }
  return <ResourcesIcon {...rest} />;
}
