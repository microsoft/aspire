// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Resources;
using Microsoft.Extensions.Localization;
using Aspire.Dashboard.Components.Deck;

namespace Aspire.Dashboard.Model.GenAI;

[DebuggerDisplay("Index = {Index}, Type = {Type}, ResourceName = {ResourceName}")]
public class GenAIItemViewModel
{
    private const DeckIconName ToolCallsIcon = DeckIconName.Executable;
    private const DeckIconName MessageIcon = DeckIconName.Mail;
    private const DeckIconName ErrorIcon = DeckIconName.ErrorCircle;
    private const DeckIconName PersonIcon = DeckIconName.Person;
    private const DeckIconName SystemIcon = DeckIconName.AppGeneric;
    // Tool/code messages reuse the executable (angle-brackets) glyph.
    private const DeckIconName ToolIcon = DeckIconName.Executable;
    private const DeckIconName CloudErrorIcon = DeckIconName.ErrorCircle;

    public required int Index { get; set; }
    public required long? InternalId { get; init; }
    public required OtlpSpan Parent { get; init; }
    public required GenAIItemType Type { get; init; }
    public required List<GenAIItemPartViewModel> ItemParts { get; init; } = [];
    public required string ResourceName { get; init; }

    public BadgeDetail GetCategoryBadge(IStringLocalizer<Dialogs> loc)
    {
        if (Type == GenAIItemType.Error)
        {
            return new BadgeDetail(loc[nameof(Dialogs.GenAIMessageCategoryStatus)], "output", ErrorIcon);
        }
        if (Type == GenAIItemType.OutputMessage)
        {
            if (ItemParts.Any(p => p.MessagePart?.Type is MessagePart.ToolCallType or MessagePart.ServerToolCallType))
            {
                return new BadgeDetail(loc[nameof(Dialogs.GenAIMessageCategoryToolCalls)], "output", ToolCallsIcon);
            }
            else
            {
                return new BadgeDetail(loc[nameof(Dialogs.GenAIMessageCategoryOutput)], "output", MessageIcon);
            }
        }
        if (ItemParts.Any(p => p.MessagePart?.Type is MessagePart.ToolCallType or MessagePart.ServerToolCallType))
        {
            return new BadgeDetail(loc[nameof(Dialogs.GenAIMessageCategoryToolCalls)], "tool-calls", ToolCallsIcon);
        }
        if (ItemParts.Any(p => p.MessagePart?.Type is MessagePart.ToolCallResponseType or MessagePart.ServerToolCallResponseType))
        {
            return new BadgeDetail(loc[nameof(Dialogs.GenAIMessageCategoryToolResponse)], "tool-response", MessageIcon);
        }

        return new BadgeDetail(loc[nameof(Dialogs.GenAIMessageCategoryMessage)], "message", MessageIcon);
    }

    public BadgeDetail GetTitleBadge(IStringLocalizer<Dialogs> loc)
    {
        return Type switch
        {
            GenAIItemType.SystemMessage => new BadgeDetail(loc[nameof(Dialogs.GenAIMessageTitleSystem)], "system", SystemIcon),
            GenAIItemType.UserMessage => new BadgeDetail(loc[nameof(Dialogs.GenAIMessageTitleUser)], "user", PersonIcon),
            GenAIItemType.AssistantMessage or GenAIItemType.OutputMessage => new BadgeDetail(loc[nameof(Dialogs.GenAIMessageTitleAssistant)], "assistant", PersonIcon),
            GenAIItemType.ToolMessage => new BadgeDetail(loc[nameof(Dialogs.GenAIMessageTitleTool)], "tool", ToolIcon),
            GenAIItemType.Error => new BadgeDetail(loc[nameof(Dialogs.GenAIMessageTitleError)], "error", CloudErrorIcon),
            _ => throw new InvalidOperationException("Unexpected type: " + Type)
        };
    }
}
