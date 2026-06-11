// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;

namespace Aspire.Dashboard.Model.GenAI;

public static class GenAIHelpers
{
    public const string GenAISystem = "gen_ai.system";
    public const string GenAIProviderName = "gen_ai.provider.name";
    public const string GenAIEventContent = "gen_ai.event.content";
    public const string GenAISystemInstructions = "gen_ai.system_instructions";
    public const string GenAIInputMessages = "gen_ai.input.messages";
    public const string GenAIOutputInstructions = "gen_ai.output.messages";
    public const string GenAIResponseModel = "gen_ai.response.model";
    public const string GenAIUsageInputTokens = "gen_ai.usage.input_tokens";
    public const string GenAIUsageOutputTokens = "gen_ai.usage.output_tokens";
    public const string GenAIToolDefinitions = "gen_ai.tool.definitions";

    // LangSmith OpenTelemetry genai standard attributes (flattened format)
    public const string GenAIPromptPrefix = "gen_ai.prompt.";
    public const string GenAICompletionPrefix = "gen_ai.completion.";

    // Event names
    public const string GenAIEvaluationResultEventName = "gen_ai.evaluation.result";

    // Evaluation result attributes (per OpenTelemetry specification)
    public const string GenAIEvaluationName = "gen_ai.evaluation.name";
    public const string GenAIEvaluationScoreLabel = "gen_ai.evaluation.score.label";
    public const string GenAIEvaluationScoreValue = "gen_ai.evaluation.score.value";
    public const string GenAIEvaluationExplanation = "gen_ai.evaluation.explanation";
    public const string GenAIResponseId = "gen_ai.response.id";

    public const string ErrorType = "error.type";

    public static bool HasGenAIAttribute(KeyValuePair<string, string>[] attributes)
    {
        return attributes.GetValueWithFallback(GenAISystem, GenAIProviderName) is { Length: > 0 };
    }
}
