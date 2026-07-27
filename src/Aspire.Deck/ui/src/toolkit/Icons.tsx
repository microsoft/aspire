import type { LucideIcon, LucideProps } from "lucide-react";
import {
  AppWindow,
  ArrowDown,
  ArrowLeft,
  ArrowUp,
  BadgeCheck,
  Bot,
  Box,
  Boxes,
  Braces,
  BrainCircuit,
  Briefcase,
  Calculator,
  Camera,
  ChartLine,
  Check,
  ChevronRight,
  CircleCheck,
  CircleHelp,
  CircleX,
  Clock,
  CloudUpload,
  Code,
  CodeXml,
  Copy,
  Database,
  Download,
  Ellipsis,
  ExternalLink,
  Eye,
  EyeOff,
  FileCode,
  FileText,
  Filter,
  FlaskConical,
  Folder,
  GitFork,
  Globe,
  HeartCrack,
  Info,
  KeyRound,
  LayoutGrid,
  Link,
  Mail,
  Minus,
  Monitor,
  Moon,
  Network,
  PanelsTopLeft,
  Pause,
  Pencil,
  Play,
  Plug,
  Plus,
  RefreshCw,
  RotateCcw,
  RotateCw,
  Route,
  Scan,
  ScrollText,
  Search,
  Send,
  Server,
  Settings,
  SlidersHorizontal,
  Sparkles,
  Square,
  SquareTerminal,
  Sun,
  Table,
  Trash2,
  TriangleAlert,
  UserRoundPlus,
  WrapText,
} from "lucide-react";

export type IconProps = Omit<LucideProps, "size"> & { size?: number };
export type IconVariant = "regular" | "filled";

export interface NamedIconMapping {
  name: string;
  regularComponent: string;
  filledComponent: string;
}

interface IconPair extends NamedIconMapping {
  icon: LucideIcon;
}

function createNamedIcon(name: string, icon: LucideIcon, componentName: string): IconPair {
  return {
    name,
    icon,
    regularComponent: componentName,
    filledComponent: `${componentName}Filled`,
  };
}

const namedIconPairs: readonly IconPair[] = [
  createNamedIcon("Add", Plus, "Plus"),
  createNamedIcon("Agents", Bot, "Bot"),
  createNamedIcon("AgentsAdd", UserRoundPlus, "UserRoundPlus"),
  createNamedIcon("Apps", LayoutGrid, "LayoutGrid"),
  createNamedIcon("ArrowClockwise", RotateCw, "RotateCw"),
  createNamedIcon("ArrowCounterclockwise", RotateCcw, "RotateCcw"),
  createNamedIcon("ArrowDownload", Download, "Download"),
  createNamedIcon("ArrowReset", Scan, "Scan"),
  createNamedIcon("ArrowSync", RefreshCw, "RefreshCw"),
  createNamedIcon("Beaker", FlaskConical, "FlaskConical"),
  createNamedIcon("Box", Box, "Box"),
  createNamedIcon("BoxMultiple", Boxes, "Boxes"),
  createNamedIcon("Braces", Braces, "Braces"),
  createNamedIcon("BrainCircuit", BrainCircuit, "BrainCircuit"),
  createNamedIcon("BranchFork", GitFork, "GitFork"),
  createNamedIcon("Calculator", Calculator, "Calculator"),
  createNamedIcon("Camera", Camera, "Camera"),
  createNamedIcon("Certificate", BadgeCheck, "BadgeCheck"),
  createNamedIcon("ChatSparkle", Sparkles, "Sparkles"),
  createNamedIcon("Clock", Clock, "Clock"),
  createNamedIcon("CheckmarkCircle", CircleCheck, "CircleCheck"),
  createNamedIcon("CloudArrowUp", CloudUpload, "CloudUpload"),
  createNamedIcon("CloudBidirectional", RefreshCw, "RefreshCw"),
  createNamedIcon("CloudDatabase", Database, "Database"),
  createNamedIcon("Code", Code, "Code"),
  createNamedIcon("CodeCircle", CodeXml, "CodeXml"),
  createNamedIcon("CodeCsRectangle", FileCode, "FileCode"),
  createNamedIcon("CodeFsRectangle", FileCode, "FileCode"),
  createNamedIcon("CodeJsRectangle", FileCode, "FileCode"),
  createNamedIcon("CodePyRectangle", FileCode, "FileCode"),
  createNamedIcon("CodeVbRectangle", FileCode, "FileCode"),
  createNamedIcon("ContentView", PanelsTopLeft, "PanelsTopLeft"),
  createNamedIcon("ContentViewGalleryLightning", PanelsTopLeft, "PanelsTopLeft"),
  createNamedIcon("Copy", Copy, "Copy"),
  createNamedIcon("Database", Database, "Database"),
  createNamedIcon("DatabaseArrowRight", Database, "Database"),
  createNamedIcon("DatabaseLightning", Database, "Database"),
  createNamedIcon("DatabaseMultiple", Database, "Database"),
  createNamedIcon("DatabasePlugConnected", Plug, "Plug"),
  createNamedIcon("DatabaseSearch", Search, "Search"),
  createNamedIcon("Delete", Trash2, "Trash2"),
  createNamedIcon("Document", FileText, "FileText"),
  createNamedIcon("Edit", Pencil, "Pencil"),
  createNamedIcon("Folder", Folder, "Folder"),
  createNamedIcon("GlobeArrowForward", Globe, "Globe"),
  createNamedIcon("GlobeDesktop", Monitor, "Monitor"),
  createNamedIcon("HeartBroken", HeartCrack, "HeartCrack"),
  createNamedIcon("Info", Info, "Info"),
  createNamedIcon("Key", KeyRound, "KeyRound"),
  createNamedIcon("LinkMultiple", Link, "Link"),
  createNamedIcon("Mail", Mail, "Mail"),
  createNamedIcon("Open", ExternalLink, "ExternalLink"),
  createNamedIcon("Play", Play, "Play"),
  createNamedIcon("PlugConnectedSettings", Plug, "Plug"),
  createNamedIcon("QuestionCircle", CircleHelp, "CircleHelp"),
  createNamedIcon("Send", Send, "Send"),
  createNamedIcon("Server", Server, "Server"),
  createNamedIcon("Settings", Settings, "Settings"),
  createNamedIcon("SettingsCogMultiple", Settings, "Settings"),
  createNamedIcon("Stop", Square, "Square"),
  createNamedIcon("Subtract", Minus, "Minus"),
  createNamedIcon("TableLightning", Table, "Table"),
  createNamedIcon("TextWrap", WrapText, "WrapText"),
  createNamedIcon("Toolbox", Briefcase, "Briefcase"),
  createNamedIcon("VirtualNetwork", Network, "Network"),
  createNamedIcon("Warning", TriangleAlert, "TriangleAlert"),
  createNamedIcon("Window", AppWindow, "AppWindow"),
  createNamedIcon("WindowConsole", SquareTerminal, "SquareTerminal"),
  createNamedIcon("WindowDatabase", Database, "Database"),
];

const namedIcons: Readonly<Record<string, IconPair>> = Object.fromEntries(
  namedIconPairs.map((pair) => [pair.name.toLowerCase(), pair]),
);

export const namedIconMappings: readonly NamedIconMapping[] = namedIconPairs.map((pair) => ({
  name: pair.name,
  regularComponent: pair.regularComponent,
  filledComponent: pair.filledComponent,
}));

function createIcon(Component: LucideIcon) {
  return function DeckToolkitIcon({ size = 18, ...props }: IconProps) {
    return <Component size={size} {...props} />;
  };
}

export const ResourcesIcon = createIcon(LayoutGrid);
export const ParametersIcon = createIcon(SlidersHorizontal);
export const ConsoleIcon = createIcon(SquareTerminal);
export const LogsIcon = createIcon(ScrollText);
export const TracesIcon = createIcon(Route);
export const MetricsIcon = createIcon(ChartLine);
export const CanvasIcon = createIcon(PanelsTopLeft);
export const ProjectIcon = createIcon(AppWindow);
export const ContainerIcon = createIcon(Box);
export const ExecutableIcon = createIcon(Code);
export const SearchIcon = createIcon(Search);
export const PlayIcon = createIcon(Play);
export const PauseIcon = createIcon(Pause);
export const StopIcon = createIcon(Square);
export const RestartIcon = createIcon(RotateCw);
export const CloseIcon = createIcon(CircleX);
export const ExternalIcon = createIcon(ExternalLink);
export const EyeIcon = createIcon(Eye);
export const EyeOffIcon = createIcon(EyeOff);
export const FilterIcon = createIcon(Filter);
export const ZoomInIcon = createIcon(Plus);
export const ZoomOutIcon = createIcon(Minus);
export const ResetViewIcon = createIcon(Scan);
export const CopyIcon = createIcon(Copy);
export const SunIcon = createIcon(Sun);
export const MoonIcon = createIcon(Moon);
export const BackIcon = createIcon(ArrowLeft);
export const SortAscendingIcon = createIcon(ArrowUp);
export const SortDescendingIcon = createIcon(ArrowDown);
export const LinkIcon = createIcon(Link);
export const MoreIcon = createIcon(Ellipsis);
export const ChevronIcon = createIcon(ChevronRight);
export const SuccessIcon = createIcon(Check);
export const WarningIcon = createIcon(TriangleAlert);
export const ErrorIcon = createIcon(CircleX);
export const InfoIcon = createIcon(Info);

export function NamedIcon({
  name,
  variant = "regular",
  fallback = LayoutGrid,
  size = 18,
  ...props
}: {
  name: string | null | undefined;
  variant?: IconVariant | null;
  fallback?: LucideIcon | null;
} & Omit<IconProps, "name">) {
  const pair = name ? namedIcons[name.toLowerCase()] : undefined;
  const resolvedVariant = variant ?? "regular";
  const Component = pair?.icon ?? (name ? fallback : null);
  if (!Component) {
    return null;
  }

  return (
    <Component
      size={size}
      fill={resolvedVariant === "filled" ? "currentColor" : "none"}
      data-icon-name={pair?.name}
      data-icon-variant={pair ? resolvedVariant : undefined}
      data-icon-fallback={pair ? undefined : name ?? undefined}
      {...props}
    />
  );
}

export function ResourceTypeIcon({
  type,
  iconName,
  iconVariant,
  ...props
}: {
  type: string;
  iconName?: string | null;
  iconVariant?: IconVariant | null;
} & IconProps) {
  if (iconName && namedIcons[iconName.toLowerCase()]) {
    return <NamedIcon name={iconName} variant={iconVariant} {...props} />;
  }

  const normalizedType = type.toLowerCase();
  if (normalizedType === "parameter") {
    return <ParametersIcon {...props} data-icon-fallback={iconName ?? undefined} />;
  }
  if (normalizedType === "connectionstring") {
    return <LinkIcon {...props} data-icon-fallback={iconName ?? undefined} />;
  }
  if (normalizedType === "externalservice") {
    return <ExternalIcon {...props} data-icon-fallback={iconName ?? undefined} />;
  }
  if (normalizedType.includes("database")) {
    return <NamedIcon name="Database" variant={iconVariant} {...props} data-icon-fallback={iconName ?? undefined} />;
  }
  if (normalizedType.includes("project")) {
    return <ProjectIcon {...props} data-icon-fallback={iconName ?? undefined} />;
  }
  if (normalizedType.includes("container")) {
    return <ContainerIcon {...props} data-icon-fallback={iconName ?? undefined} />;
  }
  if (normalizedType.includes("executable")) {
    return <ExecutableIcon {...props} data-icon-fallback={iconName ?? undefined} />;
  }

  return <ResourcesIcon {...props} data-icon-fallback={iconName ?? undefined} />;
}
