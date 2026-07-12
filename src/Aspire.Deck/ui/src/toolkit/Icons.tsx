import type { FluentIcon, FluentIconsProps } from "@fluentui/react-icons";
import {
  AddFilled,
  AddRegular,
  AgentsAddFilled,
  AgentsAddRegular,
  AgentsFilled,
  AgentsRegular,
  AppsFilled,
  AppsRegular,
  ArrowCounterclockwiseFilled,
  ArrowCounterclockwiseRegular,
  ArrowDownloadFilled,
  ArrowDownloadRegular,
  ArrowSortDown24Regular,
  ArrowSortUp24Regular,
  ArrowClockwise24Regular,
  ArrowClockwiseFilled,
  ArrowClockwiseRegular,
  ArrowLeft24Regular,
  ArrowResetFilled,
  ArrowResetRegular,
  ArrowSyncFilled,
  ArrowSyncRegular,
  BeakerFilled,
  BeakerRegular,
  Board24Regular,
  BoxFilled,
  BoxMultipleFilled,
  BoxMultipleRegular,
  BoxRegular,
  Box24Regular,
  BracesFilled,
  BracesRegular,
  BrainCircuitFilled,
  BrainCircuitRegular,
  BranchForkFilled,
  BranchForkRegular,
  CalculatorFilled,
  CalculatorRegular,
  CameraFilled,
  CameraRegular,
  CertificateFilled,
  CertificateRegular,
  ChatSparkleFilled,
  ChatSparkleRegular,
  ClockFilled,
  ClockRegular,
  ChevronRight24Regular,
  CheckmarkCircleFilled,
  CheckmarkCircleRegular,
  CheckmarkCircle24Regular,
  CloudArrowUpFilled,
  CloudArrowUpRegular,
  CloudBidirectionalFilled,
  CloudBidirectionalRegular,
  CloudDatabaseFilled,
  CloudDatabaseRegular,
  CodeCircleFilled,
  CodeCircleRegular,
  CodeCsRectangle16Filled,
  CodeCsRectangle16Regular,
  CodeFilled,
  CodeFsRectangle16Filled,
  CodeFsRectangle16Regular,
  CodeJsRectangle16Filled,
  CodeJsRectangle16Regular,
  CodePyRectangle16Filled,
  CodePyRectangle16Regular,
  CodeRegular,
  CodeVbRectangle16Filled,
  CodeVbRectangle16Regular,
  Code24Regular,
  ContentViewFilled,
  ContentViewRegular,
  ContentViewGalleryLightningFilled,
  ContentViewGalleryLightningRegular,
  CopyFilled,
  CopyRegular,
  DatabaseArrowRightFilled,
  DatabaseArrowRightRegular,
  DatabaseFilled,
  DatabaseLightningFilled,
  DatabaseLightningRegular,
  DatabaseMultipleFilled,
  DatabaseMultipleRegular,
  DatabasePlugConnectedFilled,
  DatabasePlugConnectedRegular,
  DatabaseRegular,
  DatabaseSearchFilled,
  DatabaseSearchRegular,
  DataTrending24Regular,
  DeleteFilled,
  DeleteRegular,
  Dismiss24Regular,
  DocumentFilled,
  DocumentRegular,
  EditFilled,
  EditRegular,
  ErrorCircle24Regular,
  Eye24Regular,
  EyeOff24Regular,
  Filter24Regular,
  FolderFilled,
  FolderRegular,
  GlobeArrowForwardFilled,
  GlobeArrowForwardRegular,
  GlobeDesktopFilled,
  GlobeDesktopRegular,
  Grid24Regular,
  HeartBrokenFilled,
  HeartBrokenRegular,
  InfoFilled,
  InfoRegular,
  Info24Regular,
  KeyFilled,
  KeyRegular,
  Link24Regular,
  LinkMultipleFilled,
  LinkMultipleRegular,
  MailFilled,
  MailRegular,
  MoreHorizontal24Regular,
  OpenFilled,
  OpenRegular,
  Open24Regular,
  Pause24Regular,
  PlayFilled,
  PlayRegular,
  Play24Filled,
  PlugConnectedSettingsFilled,
  PlugConnectedSettingsRegular,
  QuestionCircleFilled,
  QuestionCircleRegular,
  SendFilled,
  SendRegular,
  ServerFilled,
  ServerRegular,
  SettingsCogMultipleFilled,
  SettingsCogMultipleRegular,
  SettingsFilled,
  SettingsRegular,
  Search24Regular,
  Settings24Regular,
  StopFilled,
  StopRegular,
  Stop24Filled,
  SubtractFilled,
  SubtractRegular,
  TableLightningFilled,
  TableLightningRegular,
  TextBulletListSquare24Regular,
  TextWrapFilled,
  TextWrapRegular,
  Timeline24Regular,
  ToolboxFilled,
  ToolboxRegular,
  VirtualNetworkFilled,
  VirtualNetworkRegular,
  WarningFilled,
  WarningRegular,
  WeatherMoon24Regular,
  WeatherSunny24Regular,
  Warning24Regular,
  WindowConsoleFilled,
  WindowConsoleRegular,
  WindowDatabaseFilled,
  WindowDatabaseRegular,
  WindowFilled,
  WindowRegular,
  Window24Regular,
  WindowConsole20Regular,
} from "@fluentui/react-icons";

export type IconProps = Omit<FluentIconsProps, "fontSize"> & { size?: number };
export type FluentIconVariant = "regular" | "filled";

export interface NamedIconMapping {
  name: string;
  regularComponent: string;
  filledComponent: string;
}

interface IconPair extends NamedIconMapping {
  regular: FluentIcon;
  filled: FluentIcon;
}

function createNamedIcon(
  name: string,
  regular: FluentIcon,
  filled: FluentIcon,
  componentStem = name,
): IconPair {
  return {
    name,
    regular,
    filled,
    regularComponent: `${componentStem}Regular`,
    filledComponent: `${componentStem}Filled`,
  };
}

const namedIconPairs: readonly IconPair[] = [
  createNamedIcon("Add", AddRegular, AddFilled),
  createNamedIcon("Agents", AgentsRegular, AgentsFilled),
  createNamedIcon("AgentsAdd", AgentsAddRegular, AgentsAddFilled),
  createNamedIcon("Apps", AppsRegular, AppsFilled),
  createNamedIcon("ArrowClockwise", ArrowClockwiseRegular, ArrowClockwiseFilled),
  createNamedIcon("ArrowCounterclockwise", ArrowCounterclockwiseRegular, ArrowCounterclockwiseFilled),
  createNamedIcon("ArrowDownload", ArrowDownloadRegular, ArrowDownloadFilled),
  createNamedIcon("ArrowReset", ArrowResetRegular, ArrowResetFilled),
  createNamedIcon("ArrowSync", ArrowSyncRegular, ArrowSyncFilled),
  createNamedIcon("Beaker", BeakerRegular, BeakerFilled),
  createNamedIcon("Box", BoxRegular, BoxFilled),
  createNamedIcon("BoxMultiple", BoxMultipleRegular, BoxMultipleFilled),
  createNamedIcon("Braces", BracesRegular, BracesFilled),
  createNamedIcon("BrainCircuit", BrainCircuitRegular, BrainCircuitFilled),
  createNamedIcon("BranchFork", BranchForkRegular, BranchForkFilled),
  createNamedIcon("Calculator", CalculatorRegular, CalculatorFilled),
  createNamedIcon("Camera", CameraRegular, CameraFilled),
  createNamedIcon("Certificate", CertificateRegular, CertificateFilled),
  createNamedIcon("ChatSparkle", ChatSparkleRegular, ChatSparkleFilled),
  createNamedIcon("Clock", ClockRegular, ClockFilled),
  createNamedIcon("CheckmarkCircle", CheckmarkCircleRegular, CheckmarkCircleFilled),
  createNamedIcon("CloudArrowUp", CloudArrowUpRegular, CloudArrowUpFilled),
  createNamedIcon("CloudBidirectional", CloudBidirectionalRegular, CloudBidirectionalFilled),
  createNamedIcon("CloudDatabase", CloudDatabaseRegular, CloudDatabaseFilled),
  createNamedIcon("Code", CodeRegular, CodeFilled),
  createNamedIcon("CodeCircle", CodeCircleRegular, CodeCircleFilled),
  createNamedIcon("CodeCsRectangle", CodeCsRectangle16Regular, CodeCsRectangle16Filled, "CodeCsRectangle16"),
  createNamedIcon("CodeFsRectangle", CodeFsRectangle16Regular, CodeFsRectangle16Filled, "CodeFsRectangle16"),
  createNamedIcon("CodeJsRectangle", CodeJsRectangle16Regular, CodeJsRectangle16Filled, "CodeJsRectangle16"),
  createNamedIcon("CodePyRectangle", CodePyRectangle16Regular, CodePyRectangle16Filled, "CodePyRectangle16"),
  createNamedIcon("CodeVbRectangle", CodeVbRectangle16Regular, CodeVbRectangle16Filled, "CodeVbRectangle16"),
  createNamedIcon("ContentView", ContentViewRegular, ContentViewFilled),
  createNamedIcon("ContentViewGalleryLightning", ContentViewGalleryLightningRegular, ContentViewGalleryLightningFilled),
  createNamedIcon("Copy", CopyRegular, CopyFilled),
  createNamedIcon("Database", DatabaseRegular, DatabaseFilled),
  createNamedIcon("DatabaseArrowRight", DatabaseArrowRightRegular, DatabaseArrowRightFilled),
  createNamedIcon("DatabaseLightning", DatabaseLightningRegular, DatabaseLightningFilled),
  createNamedIcon("DatabaseMultiple", DatabaseMultipleRegular, DatabaseMultipleFilled),
  createNamedIcon("DatabasePlugConnected", DatabasePlugConnectedRegular, DatabasePlugConnectedFilled),
  createNamedIcon("DatabaseSearch", DatabaseSearchRegular, DatabaseSearchFilled),
  createNamedIcon("Delete", DeleteRegular, DeleteFilled),
  createNamedIcon("Document", DocumentRegular, DocumentFilled),
  createNamedIcon("Edit", EditRegular, EditFilled),
  createNamedIcon("Folder", FolderRegular, FolderFilled),
  createNamedIcon("GlobeArrowForward", GlobeArrowForwardRegular, GlobeArrowForwardFilled),
  createNamedIcon("GlobeDesktop", GlobeDesktopRegular, GlobeDesktopFilled),
  createNamedIcon("HeartBroken", HeartBrokenRegular, HeartBrokenFilled),
  createNamedIcon("Info", InfoRegular, InfoFilled),
  createNamedIcon("Key", KeyRegular, KeyFilled),
  createNamedIcon("LinkMultiple", LinkMultipleRegular, LinkMultipleFilled),
  createNamedIcon("Mail", MailRegular, MailFilled),
  createNamedIcon("Open", OpenRegular, OpenFilled),
  createNamedIcon("Play", PlayRegular, PlayFilled),
  createNamedIcon("PlugConnectedSettings", PlugConnectedSettingsRegular, PlugConnectedSettingsFilled),
  createNamedIcon("QuestionCircle", QuestionCircleRegular, QuestionCircleFilled),
  createNamedIcon("Send", SendRegular, SendFilled),
  createNamedIcon("Server", ServerRegular, ServerFilled),
  createNamedIcon("Settings", SettingsRegular, SettingsFilled),
  createNamedIcon("SettingsCogMultiple", SettingsCogMultipleRegular, SettingsCogMultipleFilled),
  createNamedIcon("Stop", StopRegular, StopFilled),
  createNamedIcon("Subtract", SubtractRegular, SubtractFilled),
  createNamedIcon("TableLightning", TableLightningRegular, TableLightningFilled),
  createNamedIcon("TextWrap", TextWrapRegular, TextWrapFilled),
  createNamedIcon("Toolbox", ToolboxRegular, ToolboxFilled),
  createNamedIcon("VirtualNetwork", VirtualNetworkRegular, VirtualNetworkFilled),
  createNamedIcon("Warning", WarningRegular, WarningFilled),
  createNamedIcon("Window", WindowRegular, WindowFilled),
  createNamedIcon("WindowConsole", WindowConsoleRegular, WindowConsoleFilled),
  createNamedIcon("WindowDatabase", WindowDatabaseRegular, WindowDatabaseFilled),
];

const namedIcons: Readonly<Record<string, IconPair>> = Object.fromEntries(
  namedIconPairs.map((pair) => [pair.name.toLowerCase(), pair]),
);

export const namedIconMappings: readonly NamedIconMapping[] = namedIconPairs.map((pair) => ({
  name: pair.name,
  regularComponent: pair.regularComponent,
  filledComponent: pair.filledComponent,
}));

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
export const FilterIcon = createIcon(Filter24Regular);
export const ZoomInIcon = createIcon(AddRegular);
export const ZoomOutIcon = createIcon(SubtractRegular);
export const ResetViewIcon = createIcon(ArrowResetRegular);
export const SunIcon = createIcon(WeatherSunny24Regular);
export const MoonIcon = createIcon(WeatherMoon24Regular);
export const BackIcon = createIcon(ArrowLeft24Regular);
export const SortAscendingIcon = createIcon(ArrowSortUp24Regular);
export const SortDescendingIcon = createIcon(ArrowSortDown24Regular);
export const LinkIcon = createIcon(Link24Regular);
export const MoreIcon = createIcon(MoreHorizontal24Regular);
export const ChevronIcon = createIcon(ChevronRight24Regular);
export const SuccessIcon = createIcon(CheckmarkCircle24Regular);
export const WarningIcon = createIcon(Warning24Regular);
export const ErrorIcon = createIcon(ErrorCircle24Regular);
export const InfoIcon = createIcon(Info24Regular);

export function NamedIcon({
  name,
  variant = "regular",
  fallback = AppsRegular,
  size = 18,
  ...props
}: {
  name: string | null | undefined;
  variant?: FluentIconVariant | null;
  fallback?: FluentIcon | null;
} & Omit<IconProps, "name">) {
  const pair = name ? namedIcons[name.toLowerCase()] : undefined;
  const resolvedVariant = variant ?? "regular";
  const Component = pair?.[resolvedVariant] ?? (name ? fallback : null);
  if (!Component) {
    return null;
  }

  return (
    <Component
      fontSize={size}
      data-icon-name={pair ? name : undefined}
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
  iconVariant?: FluentIconVariant | null;
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
