// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ClientModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model.Assistant.Ghcp;
using Aspire.Dashboard.Model.Assistant.Markdown;
using Aspire.Dashboard.Model.Assistant.Prompts;
using Aspire.Dashboard.Model.Markdown;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Model.Assistant;

// A copy of the assistant state to use during render.
// Avoids race conditions between rendering and results being return.
public record AssistantChatMessages(List<InitialPrompt> InitialPrompts, List<ChatViewModel> VisibleChatMessages, List<FollowUpPromptViewModel> FollowUpPrompts);

public record ModelViewModel(string Family, string DisplayName)
{
    public string GetValidFamily()
    {
        if (string.IsNullOrEmpty(Family))
        {
            throw new InvalidOperationException($"Model {DisplayName} doesn't haven't a family specified.");
        }

        return Family;
    }
}

public enum AssistantChatDisplayContainer
{
    Sidebar,
    Dialog,
    Switching
}

public enum AssistantChatDisplayState
{
    Uninitialized,
    Chat,
    GhcpDisabled,
    GhcpError
}

public enum ResponseState
{
    Starting,
    ToolCall,
    ResponseText,
    ResponseComplete,
    AddedPlaceHolders,
    Finished
}

public enum KnownLaunchers
{
    VisualStudio,
    VSCode
}

public enum FeedbackType
{
    ThumbsUp,
    ThumbsDown
}

public sealed class AssistantChatState
{
    public List<ChatMessage> ChatMessages { get; } = new();
    public List<ChatViewModel> VisibleChatMessages { get; } = new();
    public List<FollowUpPromptViewModel> FollowUpPrompts { get; } = new();
}

[DebuggerDisplay("Id = {_id}, Disposed = {_cts.IsCancellationRequested}")]
public sealed class AssistantChatViewModel : IDisposable
{
    public const string ChatAssistantContainerId = "chat-assistant-container";

    private static long s_nextId;

    // Large enough to handle any valid response.
    private const int MaximumResponseLength = 1024 * 1024 * 10;
    // Small, cheap, fast model for follow up questions.
    private const string FollowUpQuestionsModel = "gpt-4o-mini";
    private static readonly string[] s_defaultModels = ["gpt-5.4", "gpt-4.1", "gpt-4o"];
    // Older models that VS Code returns as available models. Don't show them in the model selector.
    // There are better, cheaper alternatives that should always be used.
    private static readonly string[] s_oldModels = ["gpt-4", "gpt-4-turbo", "gpt-3.5-turbo"];

    private readonly ILocalStorage _localStorage;
    private readonly IAIContextProvider _aiContextProvider;
    private readonly ChatClientFactory _chatClientFactory;
    private readonly MarkdownProcessor _markdownProcessor;
    private readonly AssistantChatDataContext _dataContext;
    private readonly IServiceProvider _serviceProvider;
    private readonly IStringLocalizer<AIAssistant> _loc;
    private readonly DashboardTelemetryService _telemetryService;
    private readonly ComponentTelemetryContextProvider _componentTelemetryContextProvider;
    private readonly IceBreakersBuilder _iceBreakersBuilder;
    private readonly IOptionsMonitor<DashboardOptions> _dashboardOptions;
    private readonly ILogger<AssistantChatViewModel> _logger;
    private readonly CancellationTokenSource _cts;
    private readonly long _id;
    private readonly object _lock = new object();
    private readonly ComponentTelemetryContext _telemetryContext;
    private readonly ConcurrentDictionary<string, int> _toolCallCounts = new();

    public AssistantChatViewModel(
        TelemetryRepository telemetryRepository,
        IConfiguration configuration,
        ILocalStorage localStorage,
        ILoggerFactory loggerFactory,
        IAIContextProvider aiContextProvider,
        ChatClientFactory chatClientFactory,
        AssistantChatDataContext dataContext,
        IServiceProvider serviceProvider,
        IStringLocalizer<AIAssistant> loc,
        IStringLocalizer<ControlsStrings> controlLoc,
        DashboardTelemetryService telemetryService,
        ComponentTelemetryContextProvider componentTelemetryContextProvider,
        IceBreakersBuilder iceBreakersBuilder,
        IOptionsMonitor<DashboardOptions> dashboardOptions)
    {
        _localStorage = localStorage;
        _logger = loggerFactory.CreateLogger<AssistantChatViewModel>();
        _aiContextProvider = aiContextProvider;
        _chatClientFactory = chatClientFactory;
        _markdownProcessor = new MarkdownProcessor(controlLoc, safeUrlSchemes: Aspire.Dashboard.Model.Markdown.MarkdownHelpers.SafeUrlSchemes, [new AspireEnrichmentExtension(new AspireEnrichmentOptions
        {
            DataContext = dataContext
        })]);
        _dataContext = dataContext;
        _serviceProvider = serviceProvider;
        _loc = loc;
        _componentTelemetryContextProvider = componentTelemetryContextProvider;
        _iceBreakersBuilder = iceBreakersBuilder;
        _dashboardOptions = dashboardOptions;
        _telemetryService = telemetryService;
        _dataContext.OnToolInvokedCallback = InvokeToolCallbackAsync;

        // Place the context on the VM instead of the component because the AI assistant view can switch between sidebar and dialog.
        _telemetryContext = new ComponentTelemetryContext(ComponentType.Page, "AssistantChat");

        _cts = new CancellationTokenSource();
        _id = Interlocked.Increment(ref s_nextId);

        _aiTools =
        [
            AIFunctionFactory.Create(dataContext.GetResourceGraphAsync),
            AIFunctionFactory.Create(dataContext.GetConsoleLogsAsync),
            AIFunctionFactory.Create(dataContext.GetTraceAsync),
            AIFunctionFactory.Create(dataContext.GetStructuredLogsAsync),
            AIFunctionFactory.Create(dataContext.GetTracesAsync),
            AIFunctionFactory.Create(dataContext.GetTraceStructuredLogsAsync)
        ];

        // UI to access this should be hidden when AI is disabled or there isn't a debug session. Just in case the user finds a way to reach this constructor.
        _aiContextProvider.EnsureEnabled();
    }

    public MarkdownProcessor MarkdownProcessor => _markdownProcessor;
    public AssistantChatDataContext DataContext => _dataContext;
    public string? UserMessage { get; set; }
    public bool ResponseInProgress { get; private set; }
    public List<ModelViewModel> Models { get; private set; } = new();
    public KnownLaunchers Launcher { get; private set; }
    public ModelViewModel? SelectedModel { get; private set; }
    public AssistantChatDisplayState DisplayState { get; set; }
    public AssistantChatDisplayContainer DisplayContainer { get; set; }

    public void SetSelectedModel(ModelViewModel model)
    {
        if (model != SelectedModel)
        {
            SelectedModel = model;
            _client = _chatClientFactory.CreateClient(SelectedModel.GetValidFamily());
        }
    }

    private readonly List<InitialPrompt> _initialPrompts = new List<InitialPrompt>();
    private readonly AIFunction[] _aiTools;
    private AssistantChatState _chatState = new();
    private int _followUpPromptsPageIndex;

    private ChatViewModel? _currentAssistantResponse;
    private ResponseState _responseState;
    private IChatClient _client = null!;
    private IChatClient _followUpClient = null!;
    private CancellationTokenSource? _currentResponseCts;
    private Task? _currentCallTask;
    private bool _disposed;

    public Func<string, string, CancellationToken, Task>? OnToolInvokedCallback { get; set; }
    public Func<ChatViewModel?, ResponseState, CancellationToken, Task>? OnConversationChangedCallback { get; set; }
    public Func<CancellationToken, Task>? OnContextChangedCallback { get; set; }
    public Func<Task>? OnDisconnectCallback { get; set; }
    public Func<CancellationToken, Task>? OnInitializeCallback { get; set; }

    public bool NewSession { get; set; } = true;

    private bool _isInitialized;
    private IDisposable? _contextProviderSubscription;
    private const int FollowUpPromptsCount = 2;
    private const int FollowUpPromptsPageSize = 2;
    public bool FollowUpPromptsHasPages => FollowUpPromptsCount > FollowUpPromptsPageSize;

    public AssistantChatMessages GetChatMessages()
    {
        lock (_lock)
        {
            var currentFollowUpPrompts = _chatState.FollowUpPrompts
                .Skip(_followUpPromptsPageIndex * FollowUpPromptsPageSize)
                .Take(FollowUpPromptsPageSize)
                .ToList();

            return new(
                _initialPrompts.ToList(),
                _chatState.VisibleChatMessages.ToList(),
                currentFollowUpPrompts);
        }
    }

    private async Task InvokeToolCallbackAsync(string toolName, string message, CancellationToken cancellationToken)
    {
        _toolCallCounts.AddOrUpdate(toolName, 1, (_, count) => count + 1);
        UpdateTelemetryProperties();

        if (OnToolInvokedCallback is { } callback)
        {
            if (_currentAssistantResponse is not null)
            {
                lock (_lock)
                {
                    EnsureNoResponsePlaceholder();

                    // End tool call message with new lines so following content starts a new paragraph.
                    var resolvedMessage = message + Environment.NewLine + Environment.NewLine;

                    // Start tool call message with new lines so tool call message starts a new paragraph.
                    if (_responseState == ResponseState.ResponseText)
                    {
                        resolvedMessage = Environment.NewLine + Environment.NewLine + resolvedMessage;
                    }

                    UpdateAssistantResponseHtml(resolvedMessage, inCompleteDocument: false);
                    _responseState = ResponseState.ToolCall;
                }

                await callback(toolName, message, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task InvokeConversationChangedCallbackAsync(ChatViewModel? chatViewModel, ResponseState responseState, CancellationToken cancellationToken)
    {
        if (responseState == ResponseState.Finished)
        {
            UpdateTelemetryProperties();
        }

        if (OnConversationChangedCallback is { } callback)
        {
            await callback(chatViewModel, responseState, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InvokeInitializedCallbackAsync()
    {
        UpdateTelemetryProperties();

        if (OnInitializeCallback is { } callback)
        {
            await callback(_cts.Token).ConfigureAwait(false);
        }
    }

    public async Task InitializeAsync()
    {
        if (await InitializeCoreAsync().ConfigureAwait(false))
        {
            DisplayState = AssistantChatDisplayState.Chat;
        }

        await InvokeInitializedCallbackAsync().ConfigureAwait(false);
    }

    public async Task InitializeWithPreviousStateAsync(AssistantChatState state)
    {
        _chatState = state;

        if (await InitializeCoreAsync().ConfigureAwait(false))
        {
            DisplayState = AssistantChatDisplayState.Chat;
        }

        await InvokeInitializedCallbackAsync().ConfigureAwait(false);
    }

    public async Task InitializeWithInitialPromptAsync(Func<Task> addInitialPrompt)
    {
        if (await InitializeCoreAsync().ConfigureAwait(false))
        {
            await addInitialPrompt().ConfigureAwait(false);

            // Wait until after initial prompt is added to change state from initializing to chat.
            DisplayState = AssistantChatDisplayState.Chat;

            await InvokeInitializedCallbackAsync().ConfigureAwait(false);

            await CallServiceAsync().ConfigureAwait(false);
        }
        else
        {
            await InvokeInitializedCallbackAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> InitializeCoreAsync()
    {
        if (_isInitialized)
        {
            throw new InvalidOperationException("InitializeAsync should only be called once.");
        }

        _isInitialized = true;

        _componentTelemetryContextProvider.Initialize(_telemetryContext);

        var assistantSettingsTask = _localStorage.GetUnprotectedAsync<AssistantChatAssistantSettings>(BrowserStorageKeys.AssistantChatAssistantSettings);
        var getInfoTask = _aiContextProvider.GetInfoAsync(_cts.Token);
        GhcpInfoResponse? response = null;
        try
        {
            response = await getInfoTask.ConfigureAwait(false);

            // Note that the launcher property isn't returned by VS yet so the property is null. Assume that a null launcher value is VS for now.
            Launcher = response.Launcher?.ToLowerInvariant() switch
            {
                "vscode" => KnownLaunchers.VSCode,
                _ => KnownLaunchers.VisualStudio
            };

            if (response.State != GhcpState.Enabled)
            {
                DisplayState = AssistantChatDisplayState.GhcpDisabled;
                return false;
            }
            else if (response.Models == null || response.Models.Count == 0)
            {
                throw new InvalidOperationException("GHCP is enabled but no models are available.");
            }
        }
        catch (Exception ex)
        {
            DisplayState = AssistantChatDisplayState.GhcpError;

            _logger.LogError(ex, "Error getting GHCP info.");
            return false;
        }

        // Multiple models with the same display name could be returned.
        // For example:
        // DisplayName = GPT-4o, Name = gpt-4o-2024-04-06
        // DisplayName = GPT-4o, Name = gpt-4o-2023-12-12
        //
        // In the situation where multiple models with the same family are returned, order the results by name and get the first to choose the model with the later date
        // The only data pull off the latest model is the display name. The actual value passed to the model is the family.
        var models = response.Models
            .Where(m => !s_oldModels.Contains(m.Family!))
            .Where(m => FilterUnsupportedModels(m.Family!, Launcher))
            .GroupBy(m => m.Family)
            .Select(g => g.OrderByDescending(m => m.Name).First())
            .Select(m => new ModelViewModel(m.Family!, m.DisplayName!))
            .OrderBy(m => m.DisplayName)
            .ToList();

        // Match IDE behavior and remove gpt-4o-mini from model selector.
        if (models.Count >= 2 && models.FirstOrDefault(m => m.Family == FollowUpQuestionsModel) is { } followUpQuestionsModel)
        {
            models.Remove(followUpQuestionsModel);
        }

        Models.AddRange(models);

        var assistantSettingsResult = await assistantSettingsTask.ConfigureAwait(false);
        AssistantChatAssistantSettings assistantSettings;

        if (assistantSettingsResult.Success)
        {
            assistantSettings = assistantSettingsResult.Value;
        }
        else
        {
            assistantSettings = new AssistantChatAssistantSettings(null);
        }

        // Use the model saved to settings. If there is no model, or the model is unknown/unsupported, then fallback to a default model.
        if (assistantSettings.Model is { } model && Models.FirstOrDefault(m => m.Family == model) is { } modelVM)
        {
            SelectedModel = modelVM;
        }

        _contextProviderSubscription = _aiContextProvider.OnContextChanged(async () =>
        {
            var context = _aiContextProvider.GetContext();
            PopulatePrompts(context);

            if (OnContextChangedCallback is { } callback)
            {
                await callback(_cts.Token).ConfigureAwait(false);
            }
        });

        PopulatePrompts(_aiContextProvider.GetContext());

        SelectedModel ??= s_defaultModels.Select(defaultModel => Models.FirstOrDefault(m => m.Family == defaultModel)).FirstOrDefault()
            ?? Models.FirstOrDefault()
            ?? throw new InvalidOperationException("No models available");

        _client = _chatClientFactory.CreateClient(SelectedModel.GetValidFamily());
        _followUpClient = _chatClientFactory.CreateClient(FollowUpQuestionsModel);

        return true;
    }

    private static bool FilterUnsupportedModels(string family, KnownLaunchers launcher)
    {
        // VS Code has issues with Claude models:
        // 3.5 errors when calling a function without parameters.
        // 3.7 isn't supported at all via language model API.
        //
        // Don't show them in the model selector for VS Code.
        if (launcher == KnownLaunchers.VSCode && family.Contains("claude", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void PopulatePrompts(AIContext? context)
    {
        lock (_lock)
        {
            _initialPrompts.Clear();

            var builder = context?.BuildIceBreakers;
            if (builder == null)
            {
                builder = (b, c) => b.Default(c);
            }

            var builderContext = new BuildIceBreakersContext(_initialPrompts);

            builder(_iceBreakersBuilder, builderContext);
        }
    }

    public async Task CallServiceAsync()
    {
        // If a new call starts while the old one is in progress then cancel the old one.
        if (_currentCallTask != null && !_currentCallTask.IsCompleted)
        {
            _currentResponseCts?.Cancel();
        }

        var currentResponseCts = _currentResponseCts = _currentResponseCts?.TryReset() ?? false
            ? _currentResponseCts
            : CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

        if (!Debugger.IsAttached)
        {
            // Maximum time for a call to complete end-to-end.
            currentResponseCts.CancelAfter(AIHelpers.CompleteMessageTimeout);
        }

        try
        {
            _currentCallTask = CallServiceCoreAsync(currentResponseCts);
            await _currentCallTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (currentResponseCts.Token.IsCancellationRequested)
        {
            // Ignore errors after cancellation.
            _logger.LogTrace(ex, "Error getting response after cancellation.");
        }
    }

    private async Task CallServiceCoreAsync(CancellationTokenSource responseCts)
    {
        ResponseInProgress = true;
        var responseText = new StringBuilder();
        ChatViewModel? callAssistantResponse = null;
        var updateFollowUpPromptsTask = Task.CompletedTask;
        var cancellationToken = responseCts.Token;

        try
        {
            List<ChatMessage> chatMessages;
            lock (_lock)
            {
                _currentAssistantResponse = callAssistantResponse = new ChatViewModel(isUserMessage: false);
                _currentAssistantResponse.AppendMarkdown(_loc[nameof(AIAssistant.ChatThinkingText)], _markdownProcessor);
                _responseState = ResponseState.Starting;
                _chatState.VisibleChatMessages.Add(_currentAssistantResponse);
                _chatState.FollowUpPrompts.Clear();

                chatMessages = _chatState.VisibleChatMessages.SelectMany(m => m.GetChatMessages()).ToList();
            }

            await InvokeConversationChangedCallbackAsync(_currentAssistantResponse, _responseState, cancellationToken).ConfigureAwait(false);

            await AIHelpers.ExecuteStreamingCallAsync(
                _client,
                chatMessages,
                async responseTextChunk =>
                {
                    responseText.Append(responseTextChunk);

                    lock (_lock)
                    {
                        _currentAssistantResponse.PromptText = responseText.ToString();

                        EnsureNoResponsePlaceholder();
                        UpdateAssistantResponseHtml(responseTextChunk, inCompleteDocument: true);
                        _responseState = ResponseState.ResponseText;
                    }

                    await InvokeConversationChangedCallbackAsync(_currentAssistantResponse, _responseState, cancellationToken).ConfigureAwait(false);
                },
                messages =>
                {
                    AddChatMessages(messages);
                    return Task.CompletedTask;
                },
                MaximumResponseLength,
                _aiTools,
                responseCts).ConfigureAwait(false);

            List<ChatMessage>? followUpMessages = null;
            var conversationChangedTask = Task.CompletedTask;
            lock (_lock)
            {
                _responseState = ResponseState.ResponseComplete;

                if (_currentAssistantResponse != null)
                {
                    _currentAssistantResponse.IsComplete = true;
                    ResponseInProgress = false;

                    if (responseText.Length > 0)
                    {
                        // Handle complete response.
                        // Don't pass incomplete flag when generating final HTML.
                        UpdateAssistantResponseHtml(responseTextChunk: null, inCompleteDocument: false);

                        var assistantMessage = new ChatMessage(ChatRole.Assistant, responseText.ToString());
                        AddChatMessages([assistantMessage]);

                        followUpMessages = _chatState.VisibleChatMessages.SelectMany(m => m.GetChatMessages()).ToList();
                        followUpMessages.Add(KnownChatMessages.General.CreateFollowUpMessage(questionCount: FollowUpPromptsCount));

                        conversationChangedTask = InvokeConversationChangedCallbackAsync(chatViewModel: null, _responseState, cancellationToken);
                    }
                }
            }

            await conversationChangedTask.ConfigureAwait(false);

            updateFollowUpPromptsTask = UpdateFollowUpPromptsAsync(followUpMessages, responseCts);
        }
        catch (Exception ex)
        {
            Debug.Assert(_currentAssistantResponse != null);

            if (cancellationToken.IsCancellationRequested)
            {
                // Ignore errors after cancellation.
                _logger.LogTrace(ex, "Error getting response after cancellation.");

                lock (_lock)
                {
                    // Display cancellation error to the user.
                    if (_currentResponseCts?.IsCancellationRequested ?? false)
                    {
                        EnsureNoResponsePlaceholder();

                        _currentAssistantResponse.ErrorMessage = _loc[nameof(AIAssistant.ChatRequestErrorCanceled)];

                        responseText.AppendLine();
                        responseText.AppendLine();
                        responseText.Append(_loc[nameof(AIAssistant.ChatRequestErrorCanceled)]);

                        var assistantMessage = new ChatMessage(ChatRole.Assistant, responseText.ToString());
                        AddChatMessages([assistantMessage]);
                    }

                    _responseState = ResponseState.ResponseComplete;
                }
            }
            else
            {
                _logger.LogError(ex, "Error getting response.");

                lock (_lock)
                {
                    _responseState = ResponseState.ResponseComplete;

                    EnsureNoResponsePlaceholder();

                    // If the GHCP account has reached its allowed limits then it returns date text in the response and then aborts the response.
                    // Special case this situation and read the incoming date and display a good error to the user.
                    if (ex is HttpIOException httpIOException &&
                        httpIOException.HttpRequestError == HttpRequestError.ResponseEnded &&
                        DateTime.TryParse(_currentAssistantResponse.PromptText, CultureInfo.InvariantCulture, out var dateTime))
                    {
                        _currentAssistantResponse.Html = string.Empty;
                        _currentAssistantResponse.ErrorMessage = _loc[nameof(AIAssistant.ChatRequestErrorReachedLimit)];
                        _currentAssistantResponse.LimitResetDate = dateTime;
                    }
                    else if (ex is ClientResultException clientResultEx)
                    {
                        string errorMessage;
                        var isForbidden = false;

                        switch (clientResultEx.Status)
                        {
                            case 0:
                                errorMessage = _loc[nameof(AIAssistant.ChatRequestErrorUnknown)];
                                break;
                            case 500:
                                if (clientResultEx.GetRawResponse() is { } response)
                                {
                                    try
                                    {
                                        // Display server error as error message.
                                        // This is so that the reach limits error message in VS Code is displayed in the UI.
                                        const string Prefix = "Request generateResponse failed with message: ";
                                        var errorResponse = response.Content.ToObjectFromJson<ErrorResponse>();
                                        if (errorResponse?.Error?.StartsWith(Prefix, StringComparison.InvariantCulture) ?? false)
                                        {
                                            errorMessage = errorResponse.Error[Prefix.Length..];
                                            break;
                                        }
                                    }
                                    catch (Exception responseEx)
                                    {
                                        _logger.LogError(responseEx, "Error when parsing error response.");
                                    }
                                }

                                errorMessage = _loc[nameof(AIAssistant.ChatRequestErrorUnknown)];
                                break;
                            case 403:
                                errorMessage = _loc[nameof(AIAssistant.ChatRequestErrorForbidden)];
                                isForbidden = true;
                                break;
                            default:
                                errorMessage = _loc.GetString(nameof(AIAssistant.ChatRequestErrorStatusCode), clientResultEx.Status);
                                break;
                        }

                        _currentAssistantResponse.ErrorMessage = errorMessage;
                        _currentAssistantResponse.IsForbidden = isForbidden;
                    }
                    else
                    {
                        _currentAssistantResponse.ErrorMessage = _loc[nameof(AIAssistant.ChatRequestErrorUnknown)];
                    }
                }
            }
        }
        finally
        {
            Debug.Assert(_currentAssistantResponse != null);

            lock (_lock)
            {
                // Don't clear UserMessage here. It was done when the call started.
                // Also, the text area isn't disabled. The user can type a follow-up message while call is in progress.
                _currentAssistantResponse.IsComplete = true;
                ResponseInProgress = false;

                _currentAssistantResponse = null;
                _responseState = ResponseState.Finished;
            }

            try
            {
                await updateFollowUpPromptsTask.ConfigureAwait(false);
                await InvokeConversationChangedCallbackAsync(callAssistantResponse, _responseState, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error getting follow up prompts.");
            }
        }

        void AddChatMessages(IList<ChatMessage> messages)
        {
            lock (_lock)
            {
                foreach (var message in messages)
                {
                    _currentAssistantResponse.AddChatMessage(message);
                }
            }
        }
    }

    private async Task UpdateFollowUpPromptsAsync(List<ChatMessage>? followUpMessages, CancellationTokenSource responseCts)
    {
        var conversationChangedTask = Task.CompletedTask;

        if (followUpMessages != null)
        {
            var inProgressFollowUpPrompts = new List<FollowUpPromptViewModel>();
            var completeFollowUpResponse = new StringBuilder();
            await AIHelpers.ExecuteStreamingCallAsync(
                _followUpClient,
                followUpMessages,
                async responseTextChunk =>
                {
                    completeFollowUpResponse.Append(responseTextChunk);

                    if (FollowUpPromptViewModel.ParseResponseText(_markdownProcessor, inProgressFollowUpPrompts, completeFollowUpResponse.ToString(), inProgress: true))
                    {
                        var conversationChangedTask = Task.CompletedTask;
                        var addInitialPrompts = false;
                        lock (_lock)
                        {
                            addInitialPrompts = (inProgressFollowUpPrompts.Count >= 2 && _chatState.FollowUpPrompts.Count == 0);

                            if (addInitialPrompts)
                            {
                                AddFollowUpPromptsToDisplay(inProgressFollowUpPrompts);
                                _responseState = ResponseState.AddedPlaceHolders;
                                conversationChangedTask = InvokeConversationChangedCallbackAsync(chatViewModel: null, _responseState, responseCts.Token);
                            }
                        }

                        await conversationChangedTask.ConfigureAwait(false);
                    }
                },
                messages => Task.CompletedTask,
                MaximumResponseLength,
                _aiTools,
                responseCts).ConfigureAwait(false);

            if (completeFollowUpResponse.Length > 0)
            {
                FollowUpPromptViewModel.ParseResponseText(_markdownProcessor, inProgressFollowUpPrompts, completeFollowUpResponse.ToString(), inProgress: false);

                lock (_lock)
                {
                    _chatState.FollowUpPrompts.Clear();
                    _responseState = ResponseState.AddedPlaceHolders;
                    AddFollowUpPromptsToDisplay(inProgressFollowUpPrompts);
                    conversationChangedTask = InvokeConversationChangedCallbackAsync(chatViewModel: null, _responseState, responseCts.Token);
                }
            }
        }

        await conversationChangedTask.ConfigureAwait(false);

        void AddFollowUpPromptsToDisplay(List<FollowUpPromptViewModel> inProgressFollowUpPrompts)
        {
            Debug.Assert(Monitor.IsEntered(_lock));

            // Add each "page" of prompts to the collection. The shortest prompt test is displayed last in the page
            // to make it more likely the refresh button can be displayed on the same line.
            for (var i = 0; i < inProgressFollowUpPrompts.Count / FollowUpPromptsPageSize; i++)
            {
                foreach (var promptToDisplay in inProgressFollowUpPrompts.Skip(i * FollowUpPromptsPageSize).Take(2).OrderByDescending(p => p.Text.Length))
                {
                    _chatState.FollowUpPrompts.Add(promptToDisplay);
                }
            }
        }
    }

    private class ErrorResponse
    {
        [JsonPropertyName("error")]
        public string? Error { get; set; }
        [JsonPropertyName("stacktrace")]
        public string? StackTrace { get; set; }
    }

    private void EnsureNoResponsePlaceholder()
    {
        Debug.Assert(_currentAssistantResponse != null);
        Debug.Assert(Monitor.IsEntered(_lock));

        if (_responseState == ResponseState.Starting)
        {
            // Remove Thinking... placeholder
            _currentAssistantResponse.ClearMarkdown();
        }
    }

    private void UpdateAssistantResponseHtml(string? responseTextChunk, bool inCompleteDocument)
    {
        Debug.Assert(_currentAssistantResponse != null);
        Debug.Assert(Monitor.IsEntered(_lock));

        if (!string.IsNullOrEmpty(responseTextChunk))
        {
            _currentAssistantResponse.AppendMarkdown(responseTextChunk, _markdownProcessor, inCompleteDocument: inCompleteDocument);
        }
    }

    public void RefreshFollowUpPrompts()
    {
        // Page index can either be 0 or 1;
        _followUpPromptsPageIndex = (_followUpPromptsPageIndex + 1) % 2;
    }

    internal async Task AddFollowUpPromptAsync(string displayText, string promptText, bool fromFollowUpPrompt)
    {
        var vm = new ChatViewModel(isUserMessage: true)
        {
            PromptText = promptText
        };

        if (fromFollowUpPrompt)
        {
            vm.AppendMarkdown(displayText, _markdownProcessor, suppressSurroundingParagraph: true);
        }
        else
        {
            vm.SetText(displayText);
        }

        lock (_lock)
        {
            AddUserPrompt(vm, promptText, _chatState.VisibleChatMessages.Count == 0);
            _chatState.VisibleChatMessages.Add(vm);
        }
        UserMessage = string.Empty;

        await InvokeConversationChangedCallbackAsync(vm, ResponseState.Starting, _cts.Token).ConfigureAwait(false);
    }

    internal async Task AddFollowUpPromptAsync(ChatViewModel chatViewModel)
    {
        lock (_lock)
        {
            AddUserPrompt(chatViewModel, chatViewModel.PromptText, _chatState.VisibleChatMessages.Count == 0);
            _chatState.VisibleChatMessages.Add(chatViewModel);
        }
        UserMessage = string.Empty;

        await InvokeConversationChangedCallbackAsync(chatViewModel, ResponseState.Starting, _cts.Token).ConfigureAwait(false);
    }

    private void AddUserPrompt(ChatViewModel chatViewModel, string promptText, bool isFirst)
    {
        if (isFirst)
        {
            chatViewModel.AddChatMessage(KnownChatMessages.General.CreateSystemMessage());

            // IMPORTANT: This message is recreated each time is fetched.
            // This is done so that resources are up to date with the current state of the app when a request begins.
            // This technically breaks conversation flow, e.g. an answer may discuss a resource that isn't healthy but later becomes healthy.
            // It appears that models use the resource graph data in the initial message as the source of truth rather than becoming confused.
            chatViewModel.AddChatMessage(() => KnownChatMessages.General.CreateInitialMessage(promptText, _dataContext.ApplicationName, _dataContext.GetResources().ToList(), _dashboardOptions.CurrentValue));
        }
        else
        {
            chatViewModel.AddChatMessage(new ChatMessage(ChatRole.User, promptText));
        }
    }

    internal async Task DisconnectAsync()
    {
        OnToolInvokedCallback = null;
        OnConversationChangedCallback = null;
        OnContextChangedCallback = null;
        OnInitializeCallback = null;

        if (OnDisconnectCallback is { } disconnectCallback)
        {
            await disconnectCallback().ConfigureAwait(false);
        }

        OnDisconnectCallback = null;
    }

    internal void CancelResponseInProgress()
    {
        _currentResponseCts?.Cancel();
    }

    public async Task UpdateSettingsAsync()
    {
        await _localStorage.SetUnprotectedAsync(
            BrowserStorageKeys.AssistantChatAssistantSettings,
            new AssistantChatAssistantSettings(SelectedModel?.Family)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _contextProviderSubscription?.Dispose();
            _cts.Cancel();
            _aiContextProvider.ChatState = _chatState;
            _telemetryContext.Dispose();
            _disposed = true;
        }
    }

    public async Task ComponentInitialize()
    {
        if (NewSession)
        {
            NewSession = false;
        }
        else
        {
            // If the VM is displayed in a component and it's not new then raise change event to scroll to end of content.
            // Also initializes any client logic (code highlighting) if a message is being received.
            //
            // Pass response state of starting so the conversation is scrolled to the end.
            ChatViewModel? currentChatViewModel;
            lock (_lock)
            {
                currentChatViewModel = _chatState.VisibleChatMessages.LastOrDefault();
            }
            await InvokeConversationChangedCallbackAsync(currentChatViewModel, ResponseState.Starting, _cts.Token).ConfigureAwait(false);
        }
    }

    internal async Task InitialPromptSelectedAsync(InitialPrompt item)
    {
        var chatBuilder = new ChatViewModelBuilder(_markdownProcessor);

        await item.CreatePrompt(new InitializePromptContext(chatBuilder, _dataContext, _serviceProvider, _dashboardOptions.CurrentValue)).ConfigureAwait(false);

        await AddFollowUpPromptAsync(chatBuilder.Build()).ConfigureAwait(false);

        await CallServiceAsync().ConfigureAwait(false);
    }

    internal async Task RetryChatMessageAsync(ChatViewModel chat)
    {
        // TODO: This only removes the last response. There are cases where it would be useful to refresh the request message.
        // For example, if the user asks about the state of a resource, the resources's state in the initial prompt should be updated.
        lock (_lock)
        {
            _chatState.VisibleChatMessages.Remove(chat);
        }

        await CallServiceAsync().ConfigureAwait(false);
    }

    internal void PostFeedback(FeedbackType feedbackType)
    {
        // TODO: This sends feedback immediately. Could improve this to wait to send feedback until the user sends another prompt or closes the dialog.
        // This change would give people the chance to change their mind about the feedback they sent.
        _telemetryService.PostUserTask(
            TelemetryEventKeys.AIAssistantFeedback,
            TelemetryResult.Success,
            properties: new Dictionary<string, AspireTelemetryProperty>
            {
                { TelemetryPropertyKeys.AIAssistantFeedbackType, new AspireTelemetryProperty(feedbackType.ToString()) }
            });
    }

    public void UpdateTelemetryProperties()
    {
        int messageCount;
        lock (_lock)
        {
            messageCount = _chatState.VisibleChatMessages.Sum(m => m.ChatMessageCount);
        }

        _telemetryContext.UpdateTelemetryProperties([
            new ComponentTelemetryProperty(TelemetryPropertyKeys.AIAssistantEnabled, new AspireTelemetryProperty(DisplayState == AssistantChatDisplayState.Chat)),
            new ComponentTelemetryProperty(TelemetryPropertyKeys.AIAssistantChatMessageCount, new AspireTelemetryProperty(messageCount)),
            new ComponentTelemetryProperty(TelemetryPropertyKeys.AIAssistantSelectedModel, new AspireTelemetryProperty(SelectedModel?.Family ?? string.Empty)),
            new ComponentTelemetryProperty(TelemetryPropertyKeys.AIAssistantToolCalls, new AspireTelemetryProperty(_toolCallCounts.Keys.Order().ToList())),
        ], _logger);
    }

    internal string GetLauncherDisplayName()
    {
        // It's ok that these product names aren't localized.
        return Launcher switch
        {
            KnownLaunchers.VisualStudio => "Visual Studio",
            KnownLaunchers.VSCode => "Visual Studio Code",
            _ => "IDE"
        };
    }
}
