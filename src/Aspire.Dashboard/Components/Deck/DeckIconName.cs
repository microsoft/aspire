// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Components.Deck;

/// <summary>
/// Names for the inline Deck icon set rendered by <see cref="DeckIcon"/>. Mirrors the icons
/// in <c>src/Aspire.Deck/ui/src/components/Icons.tsx</c>.
/// </summary>
public enum DeckIconName
{
    /// <summary>Grid of four squares; the default/fallback icon.</summary>
    Resources,
    /// <summary>Sliders; used for the Parameters nav entry.</summary>
    Parameters,
    /// <summary>Terminal window; used for the Console nav entry.</summary>
    Console,
    /// <summary>Stacked lines; used for the Structured Logs nav entry.</summary>
    Logs,
    /// <summary>Tree/branch; used for the Traces nav entry.</summary>
    Traces,
    /// <summary>Line chart; used for the Metrics nav entry.</summary>
    Metrics,
    /// <summary>Connected nodes; used for the Graph nav entry.</summary>
    Graph,
    /// <summary>Window with title bar; represents a project resource.</summary>
    Project,
    /// <summary>Cube; represents a container resource.</summary>
    Container,
    /// <summary>Angle brackets; represents an executable resource.</summary>
    Executable,
    /// <summary>Magnifying glass.</summary>
    Search,
    /// <summary>Funnel (filter).</summary>
    Filter,
    /// <summary>Play triangle.</summary>
    Play,
    /// <summary>Pause bars.</summary>
    Pause,
    /// <summary>Stop square.</summary>
    Stop,
    /// <summary>Circular restart arrow.</summary>
    Restart,
    /// <summary>Close cross.</summary>
    Close,
    /// <summary>External link arrow.</summary>
    External,
    /// <summary>Eye (reveal).</summary>
    Eye,
    /// <summary>Eye with slash (hide).</summary>
    EyeOff,
    /// <summary>Sun (light theme).</summary>
    Sun,
    /// <summary>Moon (dark theme).</summary>
    Moon,
    /// <summary>Back chevron.</summary>
    Back,
    /// <summary>Link/relationship.</summary>
    Link,
    /// <summary>Right chevron (expand/collapse).</summary>
    Chevron,
    /// <summary>Gear (settings).</summary>
    Settings,
    /// <summary>Bell (notifications).</summary>
    Bell,
    /// <summary>Question mark (help).</summary>
    Help,
    /// <summary>GitHub mark.</summary>
    GitHub,
    /// <summary>Triangle with exclamation (warning).</summary>
    Warning,
    /// <summary>Circle with exclamation (error).</summary>
    ErrorCircle,
    /// <summary>Four-pointed sparkle(s); AI/GenAI affordances.</summary>
    Sparkle,
    /// <summary>Bar chart with multiple columns.</summary>
    ChartMultiple,
    /// <summary>Horizontal gantt/timeline bars.</summary>
    GanttChart,
    /// <summary>Document with an exclamation (error/exception).</summary>
    DocumentError,
    /// <summary>Panel with a magnifier (slide/text search).</summary>
    SlideSearch,
    /// <summary>Generic app window.</summary>
    AppGeneric,
    /// <summary>Map pin.</summary>
    Pin,
    /// <summary>Down arrow.</summary>
    ArrowDown,
    /// <summary>Arrow turning down then right (nested item).</summary>
    ArrowTurnDownRight,
    /// <summary>List of items/apps.</summary>
    AppsList,
    /// <summary>Server racks.</summary>
    Server,
    /// <summary>Envelope (mail/messaging).</summary>
    Mail,
    /// <summary>Database cylinder; represents a database resource.</summary>
    Database,
    /// <summary>Heart; a healthy health status.</summary>
    Heart,
    /// <summary>Heart with a crack; a degraded/unhealthy health status.</summary>
    HeartBroken,
    /// <summary>Hollow circle; an unknown/indeterminate health status.</summary>
    CircleHint,
    /// <summary>Circle with an "i"; informational.</summary>
    Info,
    /// <summary>Curly braces; structured/JSON content.</summary>
    Braces,
    /// <summary>Document with text lines.</summary>
    DocumentText,
    /// <summary>Toolbox; resource commands.</summary>
    Toolbox,
    /// <summary>Downward chevron; a dropdown affordance.</summary>
    ChevronDown,
    /// <summary>Three horizontal dots; an overflow/more menu.</summary>
    MoreHorizontal,
    /// <summary>Horizontal sliders; an options menu.</summary>
    Options,
    /// <summary>Trash can; a delete/clear action.</summary>
    Delete,
    /// <summary>Checkmark; a selected/applied item.</summary>
    Checkmark,
    /// <summary>Double down chevron; expand-all.</summary>
    ExpandAll,
    /// <summary>Double up chevron; collapse-all.</summary>
    CollapseAll,
    /// <summary>Down arrow into a tray; download/save.</summary>
    Download,
    /// <summary>Clock; time/timestamp.</summary>
    Clock,
    /// <summary>Checked checkbox.</summary>
    CheckboxChecked,
    /// <summary>Empty checkbox.</summary>
    CheckboxUnchecked,
    /// <summary>Checkbox with a dash; indeterminate/partial selection.</summary>
    CheckboxIndeterminate,
    /// <summary>Wrapping arrow; toggle line wrapping.</summary>
    TextWrap,
    /// <summary>Overlapping pages; copy.</summary>
    Copy,
    /// <summary>Stacked layers.</summary>
    Stack,
    /// <summary>Person silhouette.</summary>
    Person,
    /// <summary>Checkmark inside a circle; success.</summary>
    CheckmarkCircle,
    /// <summary>Up arrow inside a circle; value increased.</summary>
    ArrowCircleUp,
    /// <summary>Down arrow inside a circle; value decreased.</summary>
    ArrowCircleDown,
    /// <summary>Right arrow inside a circle; value unchanged.</summary>
    ArrowCircleRight,
    /// <summary>Paper plane; send a message.</summary>
    Send,
    /// <summary>Lab flask/beaker.</summary>
    Beaker,
    /// <summary>Medical briefcase; health check.</summary>
    BriefcaseMedical,
    /// <summary>Gauge/speedometer.</summary>
    Gauge,
    /// <summary>
    /// Special value: resolves to a project/container/executable icon based on the
    /// <see cref="DeckIcon.ResourceType"/> string.
    /// </summary>
    ResourceType,
}
