// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only.

internal sealed class RoguelikeInteraction
{
    private const int MapWidth = 15;
    private const int MapHeight = 10;
    private const int MaxHealth = 10;
    private static readonly TimeSpan s_deathMessageDelay = TimeSpan.FromSeconds(3);
    private const string CssRoute = "roguelike-styles.css";
    private const string JsRoute = "roguelike-controls.js";

    private readonly object _sessionsLock = new();
    private readonly Dictionary<string, GameSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _resourceColors = new(StringComparer.OrdinalIgnoreCase);

    private IDistributedApplicationBuilder? _builder;
    private ResourceCommandService? _commandService;
    private ResourceNotificationService? _resourceNotificationService;

    public void Register(IDistributedApplicationBuilder builder)
    {
        _builder = builder;
        AssignResourceColors(builder);

        builder.OnBeforeStart((@event, ct) =>
        {
            var interactionService = @event.Services.GetRequiredService<IInteractionService>();
            _commandService = @event.Services.GetRequiredService<ResourceCommandService>();
            _resourceNotificationService = @event.Services.GetRequiredService<ResourceNotificationService>();
            RegisterPage(interactionService);

            return Task.CompletedTask;
        });
    }

    private void RegisterPage(IInteractionService interactionService)
    {
        var css = LoadEmbeddedTextResource("RoguelikeStyles.css");
        interactionService.RegisterAsset(CssRoute, "text/css", Encoding.UTF8.GetBytes(css));

        var js = LoadEmbeddedTextResource("RoguelikeControls.js");
        interactionService.RegisterAsset(JsRoute, "application/javascript", Encoding.UTF8.GetBytes(js));

        interactionService.RegisterPage("roguelike", new ContentPageOptions
        {
            Title = "Roguelike",
            EnableHtml = true,
            StyleIncludes = [CssRoute],
            ScriptIncludes = [JsRoute],
            Actions = new Dictionary<string, Func<ActionContext, Task>>(StringComparer.OrdinalIgnoreCase)
            {
                ["move-up"] = context => Move(context, 0, -1),
                ["move-down"] = context => Move(context, 0, 1),
                ["move-left"] = context => Move(context, -1, 0),
                ["move-right"] = context => Move(context, 1, 0),
                ["new-run"] = async context =>
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    if (TryGetSession(context, out var game))
                    {
                        StartNewRun(game, notify: true);
                    }

                    await StartAllResourcesAsync(context.CancellationToken).ConfigureAwait(false);
                }
            },
            OnVisit = async visitContext =>
            {
                var game = new GameSession();
                StartNewRun(game, notify: false);

                lock (_sessionsLock)
                {
                    _sessions[visitContext.SessionId] = game;
                }

                try
                {
                    while (!visitContext.CancellationToken.IsCancellationRequested)
                    {
                        var (html, version) = BuildHtml(game);
                        await visitContext.RenderAsync(html, visitContext.CancellationToken);
                        await game.WaitForChangeAsync(version, visitContext.CancellationToken);
                    }
                }
                finally
                {
                    lock (_sessionsLock)
                    {
                        _sessions.Remove(visitContext.SessionId);
                    }
                }
            }
        });

        interactionService.RegisterMenuButton(new MenuButtonOptions
        {
            IconName = "Shield",
            Text = "Roguelike",
            Url = "/pages/roguelike"
        });
    }

    private bool TryGetSession(ActionContext context, out GameSession game)
    {
        lock (_sessionsLock)
        {
            if (_sessions.TryGetValue(context.SessionId, out game!))
            {
                return true;
            }
        }

        game = null!;
        return false;
    }

    private Task Move(ActionContext context, int dx, int dy)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        if (!TryGetSession(context, out var game))
        {
            return Task.CompletedTask;
        }

        int? deathSequenceId = null;

        lock (game.Lock)
        {
            if (game.Health <= 0)
            {
                AddLog(game, "💀 You are dead. Start a new run to try again.");
            }
            else
            {
                game.Turn++;
                var target = new Cell(game.Player.X + dx, game.Player.Y + dy);

                if (!IsInBounds(target) || game.Tiles[target.X, target.Y] == Tile.Wall)
                {
                    AddLog(game, "🧱 You bump into a wall.");
                }
                else if (FindMonster(game, target) is { } monster)
                {
                    Attack(game, monster);
                }
                else
                {
                    game.Player = target;

                    if (game.Potion == game.Player)
                    {
                        game.Health = Math.Min(MaxHealth, game.Health + 5);
                        game.Potion = null;
                        AddLog(game, "💖 You drink a glowing potion and recover five hearts.");
                    }

                    if (game.Tiles[game.Player.X, game.Player.Y] == Tile.Stairs)
                    {
                        if (game.Monsters.Count == 0)
                        {
                            game.Level++;
                            game.Health = Math.Min(MaxHealth, game.Health + 1);
                            GenerateMap(game);
                            AddLog(game, "🚪 You descend to the next dungeon level.");
                        }
                        else
                        {
                            AddLog(game, "🔒 The exit is sealed while monsters remain.");
                        }
                    }
                }

                // Monsters take their turn after the player (if still alive).
                if (game.Health > 0)
                {
                    deathSequenceId = MoveMonsters(game);
                }
            }
        }

        game.NotifyChanged();

        if (deathSequenceId is { } sequenceId)
        {
            _ = RevealDeathMessageAsync(game, sequenceId);
        }

        return Task.CompletedTask;
    }

    private void Attack(GameSession game, Monster monster)
    {
        var damage = game.Random.Next(2, 5);
        monster.Health -= damage;
        RequestMapShake(game);
        AddMonsterLog(game, monster, $"⚔️ You hit the {monster.Name} for {damage}.");

        if (monster.Health <= 0)
        {
            game.Monsters.Remove(monster);
            AddMonsterLog(game, monster, $"☠️ The {monster.Name} falls.");

            // If this monster is bound to a resource, stop that resource.
            if (monster.ResourceName is not null)
            {
                _ = StopResourceAsync(monster.ResourceName);
            }

            if (game.Monsters.Count == 0)
            {
                AddLog(game, "✨ The dungeon goes quiet. Find the door to descend.");
            }
        }
    }

    private async Task StopResourceAsync(string resourceName)
    {
        if (_commandService is { } commandService)
        {
            await commandService.ExecuteCommandAsync(resourceName, KnownResourceCommands.StopCommand).ConfigureAwait(false);
        }
    }

    private async Task StartAllResourcesAsync(CancellationToken cancellationToken)
    {
        if (_commandService is not { } commandService || _resourceNotificationService is not { } resourceNotificationService)
        {
            return;
        }

        var builder = GetBuilder();
        var resourceIds = builder.Resources
            .Where(static r => !r.Name.StartsWith("aspire", StringComparison.OrdinalIgnoreCase)
                && !r.Annotations.Any(a => a.GetType().Name == "HiddenAnnotation"))
            .Select(static r => r.Name)
            .Where(resourceId => CanStartResource(resourceNotificationService, resourceId))
            .ToList();

        await Task.WhenAll(resourceIds.Select(resourceId => StartResourceAsync(commandService, resourceId, cancellationToken))).ConfigureAwait(false);
    }

    private static bool CanStartResource(ResourceNotificationService resourceNotificationService, string resourceId)
    {
        return resourceNotificationService.TryGetCurrentState(resourceId, out var resourceEvent)
            && resourceEvent.Snapshot.Commands.Any(static command => command.Name == KnownResourceCommands.StartCommand && command.State == ResourceCommandState.Enabled);
    }

    private static async Task StartResourceAsync(ResourceCommandService commandService, string resourceId, CancellationToken cancellationToken)
    {
        try
        {
            await commandService.ExecuteCommandAsync(resourceId, KnownResourceCommands.StartCommand, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Some resources may not expose a start command in their current state; New Game is best-effort.
        }
    }

    private int? MoveMonsters(GameSession game)
    {
        // Iterate over a snapshot so removals during iteration are safe.
        foreach (var monster in game.Monsters.ToArray())
        {
            var dist = Math.Abs(monster.Position.X - game.Player.X) + Math.Abs(monster.Position.Y - game.Player.Y);

            Cell target;
            if (dist <= 2)
            {
                // Adjacent or nearly adjacent — move towards the player.
                var stepX = Math.Sign(game.Player.X - monster.Position.X);
                var stepY = Math.Sign(game.Player.Y - monster.Position.Y);

                // Prefer the axis with the larger gap; if equal, pick one randomly.
                var dx = Math.Abs(game.Player.X - monster.Position.X);
                var dy = Math.Abs(game.Player.Y - monster.Position.Y);
                if (dx > dy || (dx == dy && game.Random.Next(2) == 0))
                {
                    target = new Cell(monster.Position.X + stepX, monster.Position.Y);
                }
                else
                {
                    target = new Cell(monster.Position.X, monster.Position.Y + stepY);
                }
            }
            else
            {
                // Random movement — pick a cardinal direction.
                var direction = game.Random.Next(4);
                target = direction switch
                {
                    0 => new Cell(monster.Position.X, monster.Position.Y - 1),
                    1 => new Cell(monster.Position.X, monster.Position.Y + 1),
                    2 => new Cell(monster.Position.X - 1, monster.Position.Y),
                    _ => new Cell(monster.Position.X + 1, monster.Position.Y)
                };
            }

            if (!IsInBounds(target) || game.Tiles[target.X, target.Y] == Tile.Wall)
            {
                continue;
            }

            if (target == game.Player)
            {
                // Monster attacks the player.
                game.Health -= monster.Attack;
                RequestMapShake(game);
                AddMonsterLog(game, monster, $"🩸 The {monster.Name} attacks you for {monster.Attack}.");

                if (game.Health <= 0)
                {
                    game.Health = 0;
                    AddLog(game, "💀 You were killed.");
                    return StartDeathSequence(game, monster);
                }
            }
            else if (FindMonster(game, target) is null)
            {
                monster.Position = target;
            }
        }

        return null;
    }

    private static int StartDeathSequence(GameSession game, Monster monster)
    {
        game.DeathMessageVisible = false;
        game.KillerEmoji = monster.Emoji;
        game.KillerName = monster.Name;
        game.KillerResourceName = monster.ResourceName;
        return ++game.DeathSequenceId;
    }

    private static async Task RevealDeathMessageAsync(GameSession game, int deathSequenceId)
    {
        await Task.Delay(s_deathMessageDelay).ConfigureAwait(false);

        var changed = false;
        lock (game.Lock)
        {
            if (game.Health <= 0 && game.DeathSequenceId == deathSequenceId && !game.DeathMessageVisible)
            {
                game.DeathMessageVisible = true;
                changed = true;
            }
        }

        if (changed)
        {
            game.NotifyChanged();
        }
    }

    private void StartNewRun(GameSession game, bool notify)
    {
        lock (game.Lock)
        {
            game.MapShakeRequested = false;
            game.DeathSequenceId++;
            game.DeathMessageVisible = false;
            game.KillerEmoji = null;
            game.KillerName = null;
            game.KillerResourceName = null;
            game.Level = 1;
            game.Turn = 0;
            game.Health = MaxHealth;
            game.CombatLog.Clear();
            GenerateMap(game);
            AddLog(game, "🎮 A new dungeon opens before you.");
        }

        if (notify)
        {
            game.NotifyChanged();
        }
    }

    private void GenerateMap(GameSession game)
    {
        game.Monsters.Clear();
        game.Potion = null;

        for (var y = 0; y < MapHeight; y++)
        {
            for (var x = 0; x < MapWidth; x++)
            {
                var border = x == 0 || y == 0 || x == MapWidth - 1 || y == MapHeight - 1;
                game.Tiles[x, y] = border || game.Random.NextDouble() < 0.13 ? Tile.Wall : Tile.Floor;
            }
        }

        game.Player = new Cell(1, 1);
        game.Tiles[game.Player.X, game.Player.Y] = Tile.Floor;

        var stairs = PickOpenCell(game);
        game.Tiles[stairs.X, stairs.Y] = Tile.Stairs;

        game.Potion = PickOpenCell(game);

        var builder = GetBuilder();
        // Get resource names to use as monster labels.
        // Exclude aspire-prefixed resources and hidden resources.
        var resourceNames = builder.Resources
            .Where(r => !r.Name.StartsWith("aspire", StringComparison.OrdinalIgnoreCase)
                && !r.Annotations.Any(a => a.GetType().Name == "HiddenAnnotation"))
            .Select(r => r.Name)
            .ToList();

        // Shuffle resource names so different ones appear each run.
        for (var i = resourceNames.Count - 1; i > 0; i--)
        {
            var j = game.Random.Next(i + 1);
            (resourceNames[i], resourceNames[j]) = (resourceNames[j], resourceNames[i]);
        }

        var resourceIndex = 0;
        var monsterCount = Math.Min(6, 3 + game.Level);
        for (var i = 0; i < monsterCount; i++)
        {
            var template = MonsterTemplate.All[game.Random.Next(MonsterTemplate.All.Length)];
            string? resourceName = null;
            string displayName;

            if (resourceIndex < resourceNames.Count)
            {
                resourceName = resourceNames[resourceIndex++];
                displayName = $"{template.Name} ({resourceName})";
            }
            else
            {
                displayName = template.Name;
            }

            game.Monsters.Add(new Monster(displayName, template.Emoji, template.Health + (game.Level / 3), template.Attack, PickOpenCell(game), resourceName));
        }
    }

    private static Cell PickOpenCell(GameSession game)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var cell = new Cell(game.Random.Next(1, MapWidth - 1), game.Random.Next(1, MapHeight - 1));
            if (game.Tiles[cell.X, cell.Y] == Tile.Floor && cell != game.Player && game.Potion != cell && FindMonster(game, cell) is null)
            {
                return cell;
            }
        }

        for (var y = 1; y < MapHeight - 1; y++)
        {
            for (var x = 1; x < MapWidth - 1; x++)
            {
                var cell = new Cell(x, y);
                if (game.Tiles[x, y] == Tile.Floor && cell != game.Player && FindMonster(game, cell) is null)
                {
                    return cell;
                }
            }
        }

        return game.Player;
    }

    private (string Html, int Version) BuildHtml(GameSession game)
    {
        lock (game.Lock)
        {
            var sb = new StringBuilder();
            var mapFrameClass = game.MapShakeRequested ? "roguelike-map-frame roguelike-map-shake" : "roguelike-map-frame";
            game.MapShakeRequested = false;

            sb.AppendLine("## 🗡️ Roguelike Dungeon");
            sb.Append("<div class=\"roguelike\">");
            sb.Append("<div class=\"roguelike-layout\">");

            // Map (top-left)
            sb.Append(CultureInfo.InvariantCulture, $"<div class=\"{mapFrameClass}\">");
            sb.Append(CultureInfo.InvariantCulture, $"<div class=\"roguelike-map\" style=\"{GetMapStyle(game)}\">");
            for (var y = 0; y < MapHeight; y++)
            {
                sb.Append("<div class=\"row\">");
                for (var x = 0; x < MapWidth; x++)
                {
                    sb.Append(GetCellEmoji(game, new Cell(x, y)));
                }
                sb.Append("</div>");
            }
            sb.Append("</div>");

            if (game.DeathMessageVisible)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<div class=\"roguelike-death-message\"><div>💀 You were killed by a {game.KillerEmoji} {RenderMonsterNameHtml(game.KillerName ?? "a monster", game.KillerResourceName)}</div></div>");
            }

            sb.Append("</div>");

            // Sidebar
            sb.Append("<div class=\"roguelike-sidebar\">");
            sb.Append(CultureInfo.InvariantCulture, $"<div><span class=\"label\">Health:</span> <span class=\"hearts\">{RenderHearts(game)}</span></div>");
            sb.Append(CultureInfo.InvariantCulture, $"<div><span class=\"label\">Level:</span> {game.Level}</div>");
            sb.Append(CultureInfo.InvariantCulture, $"<div><span class=\"label\">Turn:</span> {game.Turn}</div>");
            sb.Append(CultureInfo.InvariantCulture, $"<div><span class=\"label\">Enemies:</span> {game.Monsters.Count}</div>");
            sb.Append("<hr/>");
            sb.Append("<div class=\"legend\">🧙 you &nbsp; 🧱 wall &nbsp; 🚪 exit &nbsp; 💖 potion</div>");
            sb.Append("<hr/>");

            if (game.Monsters.Count > 0)
            {
                var nearest = game.Monsters
                    .OrderBy(m => Math.Abs(m.Position.X - game.Player.X) + Math.Abs(m.Position.Y - game.Player.Y))
                    .First();
                sb.Append(CultureInfo.InvariantCulture, $"<div><span class=\"label\">Nearest:</span> {nearest.Emoji} {RenderMonsterNameHtml(nearest)} ({nearest.Health} hp)</div>");
            }
            else
            {
                sb.Append("<div><strong>All clear</strong> — find the 🚪 exit</div>");
            }

            sb.Append("<hr/>");
            sb.Append("<div class=\"label\">Combat log:</div>");
            sb.Append("<div class=\"roguelike-log\">");
            foreach (var entry in game.CombatLog)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<div class=\"entry\">{entry}</div>");
            }
            sb.Append("</div>");
            sb.Append("</div>"); // sidebar

            // Controls (bottom-left)
            sb.Append("<div class=\"roguelike-controls\">");
            sb.Append("<span class=\"empty\"></span>");
            sb.Append("<a data-action=\"move-up\">⬆️</a>");
            sb.Append("<span class=\"empty\"></span>");
            sb.Append("<a data-action=\"move-left\">⬅️</a>");
            sb.Append("<a data-action=\"move-down\">⬇️</a>");
            sb.Append("<a data-action=\"move-right\">➡️</a>");
            sb.Append("</div>");

            // New Game button (bottom-right)
            sb.Append("<div class=\"roguelike-newgame\">");
            sb.Append("<a data-action=\"new-run\" class=\"newgame-btn\">🔄 New Game</a>");
            sb.Append("</div>");

            sb.Append("</div>"); // layout
            sb.Append("</div>"); // roguelike
            return (sb.ToString(), game.Version);
        }
    }

    private static string GetMapStyle(GameSession game)
    {
        if (game.Health > 0)
        {
            return "opacity: 1";
        }

        if (!game.DeathMessageVisible)
        {
            return "opacity: 1; --roguelike-map-death-opacity: 0.2; animation: roguelike-map-death-fade 3s ease forwards";
        }

        return "opacity: 0.2";
    }

    private static string GetCellEmoji(GameSession game, Cell cell)
    {
        if (cell == game.Player)
        {
            return game.Health <= 0 ? "💀" : "🧙";
        }

        if (FindMonster(game, cell) is { } monster)
        {
            return monster.Emoji;
        }

        if (game.Potion == cell)
        {
            return "💖";
        }

        return game.Tiles[cell.X, cell.Y] switch
        {
            Tile.Wall => "🧱",
            Tile.Stairs => "🚪",
            _ => "⬜"
        };
    }

    private static string RenderHearts(GameSession game)
    {
        return string.Concat(Enumerable.Repeat("❤️", game.Health)) +
               string.Concat(Enumerable.Repeat("🖤", MaxHealth - game.Health));
    }

    private static Monster? FindMonster(GameSession game, Cell cell)
    {
        return game.Monsters.FirstOrDefault(m => m.Position == cell);
    }

    private static bool IsInBounds(Cell cell)
    {
        return cell.X >= 0 && cell.X < MapWidth && cell.Y >= 0 && cell.Y < MapHeight;
    }

    private static void AddLog(GameSession game, string message)
    {
        game.CombatLog.Insert(0, HtmlEncode(message));
        if (game.CombatLog.Count > 5)
        {
            game.CombatLog.RemoveAt(game.CombatLog.Count - 1);
        }
    }

    private static void RequestMapShake(GameSession game)
    {
        game.MapShakeRequested = true;
    }

    /// <summary>
    /// Adds a combat log entry that includes the monster's name rendered with a colored resource badge.
    /// The message should contain the monster's plain-text Name; it will be replaced with the HTML version.
    /// </summary>
    private void AddMonsterLog(GameSession game, Monster monster, string message)
    {
        var htmlName = RenderMonsterNameHtml(monster);
        var htmlMessage = HtmlEncode(message).Replace(HtmlEncode(monster.Name), htmlName, StringComparison.Ordinal);
        game.CombatLog.Insert(0, htmlMessage);
        if (game.CombatLog.Count > 5)
        {
            game.CombatLog.RemoveAt(game.CombatLog.Count - 1);
        }
    }

    private static string HtmlEncode(string value)
    {
        return System.Net.WebUtility.HtmlEncode(value);
    }

    /// <summary>
    /// Renders a monster's name as HTML. If the monster is bound to a resource,
    /// the resource name is displayed in a badge with the resource's dashboard color.
    /// </summary>
    private string RenderMonsterNameHtml(Monster monster)
    {
        return RenderMonsterNameHtml(monster.Name, monster.ResourceName);
    }

    private string RenderMonsterNameHtml(string name, string? resourceName)
    {
        if (resourceName is null)
        {
            return HtmlEncode(name);
        }

        // Name format is "creature (resourceName)" — render the resource part as a colored badge.
        var resourceStart = name.IndexOf('(');
        var creatureName = resourceStart > 1 ? name.AsSpan(0, resourceStart - 1).ToString() : name;
        var color = _resourceColors.GetValueOrDefault(resourceName, "var(--accent-teal)");
        return string.Create(CultureInfo.InvariantCulture, $"{HtmlEncode(creatureName)} <span class=\"resource-badge\" style=\"background:{color}\">{HtmlEncode(resourceName)}</span>");
    }

    /// <summary>
    /// Assigns colors to resource names using the same palette order as the dashboard's ColorGenerator.
    /// </summary>
    private IDistributedApplicationBuilder GetBuilder()
    {
        return _builder ?? throw new InvalidOperationException("Roguelike interaction must be registered before use.");
    }

    private void AssignResourceColors(IDistributedApplicationBuilder builder)
    {
        // Same palette used by Aspire.Dashboard's ColorGenerator for visual consistency.
        string[] palette =
        [
            "var(--accent-teal)", "var(--accent-marigold)", "var(--accent-brass)",
            "var(--accent-peach)", "var(--accent-coral)", "var(--accent-royal-blue)",
            "var(--accent-orchid)", "var(--accent-brand-blue)", "var(--accent-seafoam)",
            "var(--accent-mink)", "var(--accent-cyan)", "var(--accent-gold)",
            "var(--accent-bronze)", "var(--accent-orange)", "var(--accent-rust)",
            "var(--accent-navy)", "var(--accent-berry)", "var(--accent-ocean)",
            "var(--accent-jade)", "var(--accent-olive)"
        ];

        var names = builder.Resources
            .Where(r => !r.Annotations.Any(a => a.GetType().Name == "HiddenAnnotation"))
            .Select(r => r.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < names.Count; i++)
        {
            _resourceColors[names[i]] = palette[i % palette.Length];
        }
    }

    private static string LoadEmbeddedTextResource(string fileName)
    {
        var assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
        var resourceName = $"{assemblyName}.Resources.{fileName}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"{fileName} embedded resource not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private enum Tile
    {
        Floor,
        Wall,
        Stairs
    }

    private readonly record struct Cell(int X, int Y);

    private sealed class GameSession
    {
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public object Lock { get; } = new();
        public Random Random { get; } = new();
        public Tile[,] Tiles { get; } = new Tile[MapWidth, MapHeight];
        public List<Monster> Monsters { get; } = [];
        public List<string> CombatLog { get; } = [];
        public Cell Player { get; set; }
        public Cell? Potion { get; set; }
        public int Health { get; set; }
        public int Level { get; set; }
        public int Turn { get; set; }
        public bool DeathMessageVisible { get; set; }
        public bool MapShakeRequested { get; set; }
        public int DeathSequenceId { get; set; }
        public string? KillerEmoji { get; set; }
        public string? KillerName { get; set; }
        public string? KillerResourceName { get; set; }
        public int Version { get; private set; }

        public Task WaitForChangeAsync(int observedVersion, CancellationToken cancellationToken)
        {
            lock (Lock)
            {
                return Version != observedVersion
                    ? Task.CompletedTask
                    : _changed.Task.WaitAsync(cancellationToken);
            }
        }

        public void NotifyChanged()
        {
            TaskCompletionSource changed;

            lock (Lock)
            {
                Version++;
                changed = _changed;
                _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            changed.TrySetResult();
        }
    }

    private sealed class Monster(string name, string emoji, int health, int attack, Cell position, string? resourceName)
    {
        public string Name { get; } = name;
        public string Emoji { get; } = emoji;
        public int Attack { get; } = attack;
        public Cell Position { get; set; } = position;
        public int Health { get; set; } = health;
        public string? ResourceName { get; } = resourceName;
    }

    private sealed record MonsterTemplate(string Name, string Emoji, int Health, int Attack)
    {
        public static readonly MonsterTemplate[] All =
        [
            new("goblin", "👺", 3, 1),
            new("bat", "🦇", 2, 1),
            new("slime", "🟢", 2, 1),
            new("orc", "👹", 4, 2),
            new("spider", "🕷️", 2, 1),
            new("snake", "🐍", 3, 1),
            new("ghost", "👻", 3, 1),
            new("wolf", "🐺", 3, 2),
            new("rat", "🐀", 2, 1),
            new("dragon", "🐉", 6, 3),
            new("crocodile", "🐊", 3, 2),
            new("bug", "🐛", 1, 1),
            new("scorpion", "🦂", 3, 1),
            new("lizard", "🦎", 3, 1),
            new("eagle", "🦅", 3, 1),
            new("bear", "🐻", 5, 2),
            new("demon", "😈", 5, 3),
            new("alien", "👾", 3, 2),
            new("troll", "🧌", 6, 2),
            new("zombie", "🧟", 4, 2),
            new("vampire", "🧛", 4, 2),
            new("beetle", "🪲", 2, 1),
            new("crow", "🐦‍⬛", 2, 1),
            new("boar", "🐗", 4, 2)
        ];
    }
}
