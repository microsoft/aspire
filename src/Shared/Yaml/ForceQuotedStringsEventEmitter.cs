// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if KUBERNETES // This namespace is only available in the Kubernetes integration
using Aspire.Hosting.Kubernetes.Extensions;
#endif
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace Aspire.Hosting.Yaml;

/// <summary>
/// A custom event emitter that forces string scalar values to use double-quoted style when serialized in YAML.
/// </summary>
/// <remarks>
/// This class extends the <see cref="ChainedEventEmitter"/> to modify the behavior of how scalar values are emitted.
/// It ensures that all string values are wrapped in double quotes, which can be useful for preserving string formatting
/// or compatibility with specific YAML consumers.
/// </remarks>
internal sealed class ForceQuotedStringsEventEmitter : ChainedEventEmitter
{
    private readonly Stack<EmitterState> _state = new();
    private readonly Func<string, (bool, ScalarStyle?)> _shouldApply;
   

    /// <summary>
    /// A custom event emitter that forces string scalar values to use double-quoted style when serialized in YAML.
    /// </summary>
    /// <remarks>
    /// This class extends the <see cref="ChainedEventEmitter"/> to modify the behavior of how scalar values are emitted.
    /// It ensures that all string values are wrapped in double quotes, which can be useful for preserving string formatting
    /// or compatibility with specific YAML consumers.
    /// </remarks>
    public ForceQuotedStringsEventEmitter(
        IEventEmitter nextEmitter,
        Func<string, (bool, ScalarStyle?)>? shouldApply = null
    ) : base(nextEmitter)
    {
        _state.Push(new(EmitterState.EventType.Root));
        _shouldApply = shouldApply ?? AlwaysTrue;
    }

    /// <summary>
    /// Emits a scalar event in YAML serialization, with customization to enforce double-quoted style
    /// for string scalar values when applicable.
    /// </summary>
    /// <param name="eventInfo">
    /// Metadata about the scalar value being emitted, such as its type and style details.
    /// </param>
    /// <param name="emitter">
    /// The emitter responsible for outputting the scalar event to the YAML stream.
    /// </param>
    public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
    {
        var item = _state.Peek();
        item.Move();

        if (item.ShouldApply() && eventInfo.Source.Type == typeof(string))
        {
            eventInfo = new(eventInfo.Source)
            {
                Style = eventInfo.Source.Value is string value
                        && _shouldApply(value) is (false, var style)
                      ? style ?? throw new InvalidOperationException("Style cannot be null when not applying double quotes.")
                      : ScalarStyle.DoubleQuoted
            };
        }

        base.Emit(eventInfo, emitter);
    }

    /// <summary>
    /// Emits a YAML scalar event while ensuring string scalar values are double-quoted if applicable.
    /// </summary>
    /// <param name="eventInfo">
    /// An instance of <see cref="ScalarEventInfo"/> that contains information about the scalar event.
    /// </param>
    /// <param name="emitter">
    /// An instance of <see cref="IEmitter"/> used to write the serialized YAML data.
    /// </param>
    public override void Emit(MappingStartEventInfo eventInfo, IEmitter emitter)
    {
        _state.Peek().Move();
        _state.Push(new(EmitterState.EventType.Mapping));
        base.Emit(eventInfo, emitter);
    }

    /// <summary>
    /// Emits a YAML scalar event, forcing string scalar values to use double-quoted style when necessary.
    /// </summary>
    /// <param name="eventInfo">
    /// Information about the scalar event being emitted, including the source value and its metadata.
    /// </param>
    /// <param name="emitter">
    /// The emitter used to write the YAML events.
    /// </param>
    /// <remarks>
    /// This method checks the current state of the emitter and applies double-quoted style
    /// to string scalar events if required by the emitter's state logic.
    /// It overrides the functionality of the base event emitter to enforce custom serialization rules.
    /// </remarks>
    public override void Emit(MappingEndEventInfo eventInfo, IEmitter emitter)
    {
        var item = _state.Pop();
        if (item.Type != EmitterState.EventType.Mapping)
        {
            throw new YamlException("Invalid state: expected MappingEndEventInfo.");
        }

        base.Emit(eventInfo, emitter);
    }

    /// <summary>
    /// Forces string scalar values to use double-quoted style when serialized in YAML.
    /// </summary>
    /// <param name="eventInfo">The scalar event information to be emitted, containing details about the value and its metadata.</param>
    /// <param name="emitter">The YAML emitter responsible for handling the event and producing output.</param>
    public override void Emit(SequenceStartEventInfo eventInfo, IEmitter emitter)
    {
        _state.Peek().Move();
        _state.Push(new(EmitterState.EventType.Sequence));
        base.Emit(eventInfo, emitter);
    }

    /// <summary>
    /// Emits a YAML event with support for forcing string scalar values into double-quoted style when applicable.
    /// </summary>
    /// <param name="eventInfo">The metadata of the scalar event to be emitted, including type and style information.</param>
    /// <param name="emitter">The emitter responsible for writing the event to the YAML output.</param>
    public override void Emit(SequenceEndEventInfo eventInfo, IEmitter emitter)
    {
        var item = _state.Pop();
        if (item.Type != EmitterState.EventType.Sequence)
        {
            throw new YamlException("Invalid state: expected SequenceEndEventInfo.");
        }

        base.Emit(eventInfo, emitter);
    }

    private sealed class EmitterState(
        EmitterState.EventType eventType
    )
    {
        public EventType Type { get; } = eventType;

        private int _currentIndex;

        public void Move()
        {
            _currentIndex++;
        }

        public bool ShouldApply() => Type switch
        {
            EventType.Mapping => _currentIndex % 2 == 0,
            EventType.Sequence => true,
            _ => false,
        };

        public enum EventType : byte
        {
            Root,
            Mapping,
            Sequence,
        }
    }

    private static (bool, ScalarStyle?) AlwaysTrue(string _) => (true, null);
}
