import type { FluentIcon, FluentIconsProps } from "@fluentui/react-icons";
import {
  ArrowClockwise24Regular,
  ArrowLeft24Regular,
  Board24Regular,
  Box24Regular,
  ChevronRight24Regular,
  Code24Regular,
  DataTrending24Regular,
  Dismiss24Regular,
  Eye24Regular,
  EyeOff24Regular,
  Grid24Regular,
  Link24Regular,
  Open24Regular,
  Pause24Regular,
  Play24Filled,
  Search24Regular,
  Settings24Regular,
  Stop24Filled,
  TextBulletListSquare24Regular,
  Timeline24Regular,
  WeatherMoon24Regular,
  WeatherSunny24Regular,
  Window24Regular,
  WindowConsole20Regular,
} from "@fluentui/react-icons";

export type IconProps = Omit<FluentIconsProps, "fontSize"> & { size?: number };

function createIcon(Component: FluentIcon) {
  return function DeckToolkitIcon({ size = 18, ...props }: IconProps) {
    return <Component fontSize={size} {...props} />;
  };
}

export const ResourcesIcon = createIcon(Grid24Regular);
export const ParametersIcon = createIcon(Settings24Regular);
export const ConsoleIcon = createIcon(WindowConsole20Regular);
export const LogsIcon = createIcon(TextBulletListSquare24Regular);
export const TracesIcon = createIcon(Timeline24Regular);
export const MetricsIcon = createIcon(DataTrending24Regular);
export const CanvasIcon = createIcon(Board24Regular);
export const ProjectIcon = createIcon(Window24Regular);
export const ContainerIcon = createIcon(Box24Regular);
export const ExecutableIcon = createIcon(Code24Regular);
export const SearchIcon = createIcon(Search24Regular);
export const PlayIcon = createIcon(Play24Filled);
export const PauseIcon = createIcon(Pause24Regular);
export const StopIcon = createIcon(Stop24Filled);
export const RestartIcon = createIcon(ArrowClockwise24Regular);
export const CloseIcon = createIcon(Dismiss24Regular);
export const ExternalIcon = createIcon(Open24Regular);
export const EyeIcon = createIcon(Eye24Regular);
export const EyeOffIcon = createIcon(EyeOff24Regular);
export const SunIcon = createIcon(WeatherSunny24Regular);
export const MoonIcon = createIcon(WeatherMoon24Regular);
export const BackIcon = createIcon(ArrowLeft24Regular);
export const LinkIcon = createIcon(Link24Regular);
export const ChevronIcon = createIcon(ChevronRight24Regular);

export function ResourceTypeIcon({ type, ...props }: { type: string } & IconProps) {
  const normalizedType = type.toLowerCase();
  if (normalizedType.includes("project")) {
    return <ProjectIcon {...props} />;
  }
  if (normalizedType.includes("container")) {
    return <ContainerIcon {...props} />;
  }
  if (normalizedType.includes("executable")) {
    return <ExecutableIcon {...props} />;
  }

  return <ResourcesIcon {...props} />;
}
