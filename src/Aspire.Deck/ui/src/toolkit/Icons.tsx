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

interface IconPair {
  regular: FluentIcon;
  filled: FluentIcon;
}

const namedIcons: Readonly<Record<string, IconPair>> = {
  add: { regular: AddRegular, filled: AddFilled },
  agents: { regular: AgentsRegular, filled: AgentsFilled },
  agentsadd: { regular: AgentsAddRegular, filled: AgentsAddFilled },
  apps: { regular: AppsRegular, filled: AppsFilled },
  arrowclockwise: { regular: ArrowClockwiseRegular, filled: ArrowClockwiseFilled },
  arrowcounterclockwise: { regular: ArrowCounterclockwiseRegular, filled: ArrowCounterclockwiseFilled },
  arrowreset: { regular: ArrowResetRegular, filled: ArrowResetFilled },
  arrowsync: { regular: ArrowSyncRegular, filled: ArrowSyncFilled },
  beaker: { regular: BeakerRegular, filled: BeakerFilled },
  box: { regular: BoxRegular, filled: BoxFilled },
  boxmultiple: { regular: BoxMultipleRegular, filled: BoxMultipleFilled },
  braincircuit: { regular: BrainCircuitRegular, filled: BrainCircuitFilled },
  branchfork: { regular: BranchForkRegular, filled: BranchForkFilled },
  calculator: { regular: CalculatorRegular, filled: CalculatorFilled },
  camera: { regular: CameraRegular, filled: CameraFilled },
  certificate: { regular: CertificateRegular, filled: CertificateFilled },
  chatsparkle: { regular: ChatSparkleRegular, filled: ChatSparkleFilled },
  checkmarkcircle: { regular: CheckmarkCircleRegular, filled: CheckmarkCircleFilled },
  cloudarrowup: { regular: CloudArrowUpRegular, filled: CloudArrowUpFilled },
  cloudbidirectional: { regular: CloudBidirectionalRegular, filled: CloudBidirectionalFilled },
  clouddatabase: { regular: CloudDatabaseRegular, filled: CloudDatabaseFilled },
  code: { regular: CodeRegular, filled: CodeFilled },
  codecircle: { regular: CodeCircleRegular, filled: CodeCircleFilled },
  codecsrectangle: { regular: CodeCsRectangle16Regular, filled: CodeCsRectangle16Filled },
  codefsrectangle: { regular: CodeFsRectangle16Regular, filled: CodeFsRectangle16Filled },
  codejsrectangle: { regular: CodeJsRectangle16Regular, filled: CodeJsRectangle16Filled },
  codepyrectangle: { regular: CodePyRectangle16Regular, filled: CodePyRectangle16Filled },
  codevbrectangle: { regular: CodeVbRectangle16Regular, filled: CodeVbRectangle16Filled },
  contentviewgallerylightning: { regular: ContentViewGalleryLightningRegular, filled: ContentViewGalleryLightningFilled },
  contentview: { regular: ContentViewRegular, filled: ContentViewFilled },
  database: { regular: DatabaseRegular, filled: DatabaseFilled },
  databasearrowright: { regular: DatabaseArrowRightRegular, filled: DatabaseArrowRightFilled },
  databaselightning: { regular: DatabaseLightningRegular, filled: DatabaseLightningFilled },
  databasemultiple: { regular: DatabaseMultipleRegular, filled: DatabaseMultipleFilled },
  databaseplugconnected: { regular: DatabasePlugConnectedRegular, filled: DatabasePlugConnectedFilled },
  databasesearch: { regular: DatabaseSearchRegular, filled: DatabaseSearchFilled },
  delete: { regular: DeleteRegular, filled: DeleteFilled },
  document: { regular: DocumentRegular, filled: DocumentFilled },
  edit: { regular: EditRegular, filled: EditFilled },
  folder: { regular: FolderRegular, filled: FolderFilled },
  globearrowforward: { regular: GlobeArrowForwardRegular, filled: GlobeArrowForwardFilled },
  globedesktop: { regular: GlobeDesktopRegular, filled: GlobeDesktopFilled },
  heartbroken: { regular: HeartBrokenRegular, filled: HeartBrokenFilled },
  info: { regular: InfoRegular, filled: InfoFilled },
  key: { regular: KeyRegular, filled: KeyFilled },
  linkmultiple: { regular: LinkMultipleRegular, filled: LinkMultipleFilled },
  mail: { regular: MailRegular, filled: MailFilled },
  open: { regular: OpenRegular, filled: OpenFilled },
  play: { regular: PlayRegular, filled: PlayFilled },
  plugconnectedsettings: { regular: PlugConnectedSettingsRegular, filled: PlugConnectedSettingsFilled },
  send: { regular: SendRegular, filled: SendFilled },
  server: { regular: ServerRegular, filled: ServerFilled },
  settings: { regular: SettingsRegular, filled: SettingsFilled },
  settingscogmultiple: { regular: SettingsCogMultipleRegular, filled: SettingsCogMultipleFilled },
  stop: { regular: StopRegular, filled: StopFilled },
  subtract: { regular: SubtractRegular, filled: SubtractFilled },
  tablelightning: { regular: TableLightningRegular, filled: TableLightningFilled },
  toolbox: { regular: ToolboxRegular, filled: ToolboxFilled },
  virtualnetwork: { regular: VirtualNetworkRegular, filled: VirtualNetworkFilled },
  warning: { regular: WarningRegular, filled: WarningFilled },
  window: { regular: WindowRegular, filled: WindowFilled },
  windowconsole: { regular: WindowConsoleRegular, filled: WindowConsoleFilled },
  windowdatabase: { regular: WindowDatabaseRegular, filled: WindowDatabaseFilled },
};

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
