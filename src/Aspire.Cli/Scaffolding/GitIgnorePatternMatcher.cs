// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Scaffolding;

/// <summary>
/// Detects overlap between gitignore path patterns.
/// </summary>
internal static class GitIgnorePatternMatcher
{
    private static readonly CharacterSet s_anyCharacter = CharacterSet.Create(
        [new CharacterRange(char.MinValue, char.MaxValue)]);
    private static readonly CharacterSet s_nonSlashCharacter = CharacterSet.Create(
        [
            new CharacterRange(char.MinValue, (char)('/' - 1)),
            new CharacterRange((char)('/' + 1), char.MaxValue)
        ]);
    private static readonly CharacterSet s_slashCharacter = CharacterSet.Create(
        [new CharacterRange('/', '/')]);

    /// <summary>
    /// Determines whether a negation can match a generated directory or anything below it.
    /// </summary>
    internal static bool CanMatchDirectoryOrDescendant(string negationPattern, string directoryPattern)
    {
        ArgumentNullException.ThrowIfNull(negationPattern);
        ArgumentNullException.ThrowIfNull(directoryPattern);

        if (!directoryPattern.EndsWith('/'))
        {
            throw new ArgumentException("The pattern must identify a directory.", nameof(directoryPattern));
        }

        if (Compile(RemoveRoot(negationPattern)) is null)
        {
            return false;
        }

        var negationVariants = CreateVariants(
            negationPattern,
            includeDirectoryVariant: true,
            matchDescendants: false,
            includeUnanchoredPrefix: false);
        string[] directoryVariants;
        if (!IsAnchored(negationPattern))
        {
            // A slashless negation applies to a name at any depth. Compare it with the generated
            // directory's final segment rather than allowing an unrelated name somewhere below it.
            directoryVariants = CreateVariants(
                GetFinalDirectoryPattern(directoryPattern),
                includeDirectoryVariant: false,
                matchDescendants: false,
                includeUnanchoredPrefix: false);
        }
        else
        {
            directoryVariants = CreateVariants(
                directoryPattern,
                includeDirectoryVariant: false,
                matchDescendants: true,
                includeUnanchoredPrefix: true);
        }

        return HaveCommonMatch(negationVariants, directoryVariants);
    }

    private static bool HaveCommonMatch(IEnumerable<string> leftPatterns, IEnumerable<string> rightPatterns)
    {
        foreach (var leftPattern in leftPatterns)
        {
            var leftAutomaton = Compile(leftPattern);
            if (leftAutomaton is null)
            {
                continue;
            }

            foreach (var rightPattern in rightPatterns)
            {
                var rightAutomaton = Compile(rightPattern);
                if (rightAutomaton is not null && HaveCommonMatch(leftAutomaton, rightAutomaton))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string[] CreateVariants(
        string pattern,
        bool includeDirectoryVariant,
        bool matchDescendants,
        bool includeUnanchoredPrefix)
    {
        var isAnchored = IsAnchored(pattern);
        var normalizedPattern = RemoveRoot(pattern);
        var variants = new HashSet<string>(StringComparer.Ordinal);

        AddVariants(normalizedPattern);
        if (includeUnanchoredPrefix && !isAnchored)
        {
            AddVariants("**/" + normalizedPattern);
        }

        return [.. variants];

        void AddVariants(string variant)
        {
            if (matchDescendants)
            {
                variants.Add(variant + "**");
                return;
            }

            variants.Add(variant);
            if (includeDirectoryVariant && !variant.EndsWith('/'))
            {
                // A slashless gitignore pattern can match either a file or a directory.
                variants.Add(variant + '/');
            }
        }
    }

    private static Automaton? Compile(string pattern)
    {
        var automaton = new Automaton();
        var currentState = Automaton.StartState;
        var index = 0;

        // Git uses wildmatch semantics for ?, [], *, and path-aware ** forms. Git can fold
        // ASCII case through core.ignoreCase, but this content-only merger has no repository
        // configuration, so matching both cases conservatively avoids overriding a valid negation.
        // See https://git-scm.com/docs/gitignore and https://github.com/git/git/blob/master/wildmatch.c.
        while (index < pattern.Length)
        {
            var character = pattern[index];
            switch (character)
            {
                case '\\':
                    index++;
                    if (index == pattern.Length)
                    {
                        // Git treats a trailing backslash as an invalid pattern that never matches.
                        return null;
                    }

                    currentState = AddCharacterTransition(
                        automaton,
                        currentState,
                        CharacterSet.Create(
                            [new CharacterRange(pattern[index], pattern[index])],
                            ignoreCase: true));
                    index++;
                    break;

                case '?':
                    currentState = AddCharacterTransition(automaton, currentState, s_nonSlashCharacter);
                    index++;
                    break;

                case '[':
                    if (!TryParseCharacterClass(pattern, index, out var characterSet, out index))
                    {
                        return null;
                    }

                    currentState = AddCharacterTransition(automaton, currentState, characterSet);
                    break;

                case '*':
                    var endOfRun = index + 1;
                    while (endOfRun < pattern.Length && pattern[endOfRun] == '*')
                    {
                        endOfRun++;
                    }

                    var isGlobStar = endOfRun - index > 1 &&
                        (index == 0 || pattern[index - 1] == '/');
                    if (isGlobStar && endOfRun < pattern.Length && pattern[endOfRun] == '/')
                    {
                        currentState = AddDirectoryGlobStar(automaton, currentState);
                        index = endOfRun + 1;
                    }
                    else if (isGlobStar && endOfRun == pattern.Length)
                    {
                        currentState = AddStar(automaton, currentState, s_anyCharacter);
                        index = endOfRun;
                    }
                    else
                    {
                        currentState = AddStar(automaton, currentState, s_nonSlashCharacter);
                        index = endOfRun;
                    }
                    break;

                default:
                    currentState = AddCharacterTransition(
                        automaton,
                        currentState,
                        CharacterSet.Create(
                            [new CharacterRange(character, character)],
                            ignoreCase: true));
                    index++;
                    break;
            }
        }

        automaton.AcceptingState = currentState;
        return automaton;
    }

    private static int AddCharacterTransition(Automaton automaton, int currentState, CharacterSet characterSet)
    {
        var nextState = automaton.AddState();
        automaton.AddTransition(currentState, characterSet, nextState);
        return nextState;
    }

    private static int AddStar(Automaton automaton, int currentState, CharacterSet characterSet)
    {
        var nextState = automaton.AddState();
        automaton.AddEpsilonTransition(currentState, nextState);
        automaton.AddTransition(currentState, characterSet, currentState);
        return nextState;
    }

    private static int AddDirectoryGlobStar(Automaton automaton, int currentState)
    {
        var insideDirectoryState = automaton.AddState();
        var nextState = automaton.AddState();

        // **/ can consume no directory or one or more complete directory segments. Keeping the
        // slash transition separate prevents the automaton from stopping halfway through a name.
        automaton.AddEpsilonTransition(currentState, nextState);
        automaton.AddTransition(currentState, s_nonSlashCharacter, insideDirectoryState);
        automaton.AddTransition(insideDirectoryState, s_nonSlashCharacter, insideDirectoryState);
        automaton.AddTransition(insideDirectoryState, s_slashCharacter, currentState);

        return nextState;
    }

    private static bool TryParseCharacterClass(
        string pattern,
        int openingBracketIndex,
        out CharacterSet characterSet,
        out int nextIndex)
    {
        var ranges = new List<CharacterRange>();
        var index = openingBracketIndex + 1;
        var isNegated = index < pattern.Length && pattern[index] is '!' or '^';
        if (isNegated)
        {
            index++;
        }

        char? pendingCharacter = null;
        var hasContent = false;

        while (index < pattern.Length)
        {
            if (pattern[index] == ']' && hasContent)
            {
                AddPendingCharacter();
                characterSet = CharacterSet.Create(ranges, isNegated, ignoreCase: true).Without('/');
                nextIndex = index + 1;
                return true;
            }

            if (pattern[index] == '[' &&
                index + 1 < pattern.Length &&
                pattern[index + 1] == ':')
            {
                var closingBracket = pattern.IndexOf(']', index + 2);
                if (closingBracket > index + 2 && pattern[closingBracket - 1] == ':')
                {
                    if (!TryAddPosixCharacterClass(pattern[(index + 2)..(closingBracket - 1)], ranges))
                    {
                        characterSet = null!;
                        nextIndex = openingBracketIndex;
                        return false;
                    }

                    AddPendingCharacter();
                    hasContent = true;
                    index = closingBracket + 1;
                    continue;
                }

                // Git treats a malformed POSIX class marker as ordinary bracket-class content.
            }

            if (!TryReadClassCharacter(pattern, ref index, out var currentCharacter))
            {
                characterSet = null!;
                nextIndex = openingBracketIndex;
                return false;
            }

            if (currentCharacter == '-' &&
                pendingCharacter is not null &&
                index < pattern.Length &&
                pattern[index] != ']')
            {
                if (!TryReadClassCharacter(pattern, ref index, out var rangeEnd))
                {
                    characterSet = null!;
                    nextIndex = openingBracketIndex;
                    return false;
                }

                if (pendingCharacter.Value <= rangeEnd)
                {
                    ranges.Add(new CharacterRange(pendingCharacter.Value, rangeEnd));
                }

                pendingCharacter = null;
                hasContent = true;
                continue;
            }

            AddPendingCharacter();
            pendingCharacter = currentCharacter;
            hasContent = true;
        }

        characterSet = null!;
        nextIndex = openingBracketIndex;
        return false;

        void AddPendingCharacter()
        {
            if (pendingCharacter is { } value)
            {
                ranges.Add(new CharacterRange(value, value));
                pendingCharacter = null;
            }
        }
    }

    private static bool TryReadClassCharacter(string pattern, ref int index, out char character)
    {
        character = pattern[index++];
        if (character != '\\')
        {
            return true;
        }

        if (index == pattern.Length)
        {
            return false;
        }

        character = pattern[index++];
        return true;
    }

    private static bool TryAddPosixCharacterClass(string name, List<CharacterRange> ranges)
    {
        switch (name)
        {
            case "alnum":
                ranges.AddRange(
                    [
                        new CharacterRange('0', '9'),
                        new CharacterRange('A', 'Z'),
                        new CharacterRange('a', 'z')
                    ]);
                break;
            case "alpha":
                ranges.AddRange([new CharacterRange('A', 'Z'), new CharacterRange('a', 'z')]);
                break;
            case "blank":
                ranges.AddRange([new CharacterRange('\t', '\t'), new CharacterRange(' ', ' ')]);
                break;
            case "cntrl":
                ranges.AddRange([new CharacterRange('\0', '\u001f'), new CharacterRange('\u007f', '\u007f')]);
                break;
            case "digit":
                ranges.Add(new CharacterRange('0', '9'));
                break;
            case "graph":
                ranges.Add(new CharacterRange('!', '~'));
                break;
            case "lower":
                ranges.Add(new CharacterRange('a', 'z'));
                break;
            case "print":
                ranges.Add(new CharacterRange(' ', '~'));
                break;
            case "punct":
                ranges.AddRange(
                    [
                        new CharacterRange('!', '/'),
                        new CharacterRange(':', '@'),
                        new CharacterRange('[', '`'),
                        new CharacterRange('{', '~')
                    ]);
                break;
            case "space":
                ranges.AddRange([new CharacterRange('\t', '\r'), new CharacterRange(' ', ' ')]);
                break;
            case "upper":
                ranges.Add(new CharacterRange('A', 'Z'));
                break;
            case "xdigit":
                ranges.AddRange(
                    [
                        new CharacterRange('0', '9'),
                        new CharacterRange('A', 'F'),
                        new CharacterRange('a', 'f')
                    ]);
                break;
            default:
                return false;
        }

        return true;
    }

    private static bool HaveCommonMatch(Automaton left, Automaton right)
    {
        var pending = new Queue<(int Left, int Right)>();
        var visited = new HashSet<(int Left, int Right)>();
        Enqueue(Automaton.StartState, Automaton.StartState);

        while (pending.TryDequeue(out var pair))
        {
            if (pair.Left == left.AcceptingState && pair.Right == right.AcceptingState)
            {
                return true;
            }

            foreach (var nextLeft in left.States[pair.Left].EpsilonTransitions)
            {
                Enqueue(nextLeft, pair.Right);
            }

            foreach (var nextRight in right.States[pair.Right].EpsilonTransitions)
            {
                Enqueue(pair.Left, nextRight);
            }

            foreach (var leftTransition in left.States[pair.Left].Transitions)
            {
                foreach (var rightTransition in right.States[pair.Right].Transitions)
                {
                    if (leftTransition.Characters.Intersects(rightTransition.Characters))
                    {
                        Enqueue(leftTransition.TargetState, rightTransition.TargetState);
                    }
                }
            }
        }

        return false;

        void Enqueue(int leftState, int rightState)
        {
            if (visited.Add((leftState, rightState)))
            {
                pending.Enqueue((leftState, rightState));
            }
        }
    }

    private static string RemoveRoot(string pattern)
        => pattern.StartsWith('/') ? pattern[1..] : pattern;

    private static string GetFinalDirectoryPattern(string pattern)
    {
        var withoutDirectoryMarker = RemoveRoot(pattern).TrimEnd('/');
        var finalSlash = withoutDirectoryMarker.LastIndexOf('/');
        return withoutDirectoryMarker[(finalSlash + 1)..] + '/';
    }

    private static bool IsAnchored(string pattern)
    {
        var patternWithoutDirectoryMarker = RemoveRoot(pattern).TrimEnd('/');
        while (!pattern.StartsWith('/') &&
               patternWithoutDirectoryMarker.StartsWith("**/", StringComparison.Ordinal))
        {
            patternWithoutDirectoryMarker = patternWithoutDirectoryMarker[3..];
        }

        return pattern.StartsWith('/') || patternWithoutDirectoryMarker.Contains('/');
    }

    private sealed class Automaton
    {
        internal List<State> States { get; } = [new()];

        internal const int StartState = 0;

        internal int AcceptingState { get; set; }

        internal int AddState()
        {
            States.Add(new State());
            return States.Count - 1;
        }

        internal void AddEpsilonTransition(int sourceState, int targetState)
            => States[sourceState].EpsilonTransitions.Add(targetState);

        internal void AddTransition(int sourceState, CharacterSet characters, int targetState)
            => States[sourceState].Transitions.Add(new Transition(characters, targetState));
    }

    private sealed class State
    {
        internal List<int> EpsilonTransitions { get; } = [];

        internal List<Transition> Transitions { get; } = [];
    }

    private sealed class CharacterSet
    {
        private readonly CharacterRange[] _ranges;

        private CharacterSet(CharacterRange[] ranges)
        {
            _ranges = ranges;
        }

        internal static CharacterSet Create(
            IEnumerable<CharacterRange> ranges,
            bool negate = false,
            bool ignoreCase = false)
        {
            var normalizedRanges = Normalize(ignoreCase ? AddAsciiCaseEquivalents(ranges) : ranges);
            return new CharacterSet(negate ? Complement(normalizedRanges) : normalizedRanges);
        }

        internal CharacterSet Without(char character)
        {
            var ranges = new List<CharacterRange>(_ranges.Length + 1);
            foreach (var range in _ranges)
            {
                if (character < range.Start || character > range.End)
                {
                    ranges.Add(range);
                    continue;
                }

                if (range.Start < character)
                {
                    ranges.Add(new CharacterRange(range.Start, (char)(character - 1)));
                }

                if (range.End > character)
                {
                    ranges.Add(new CharacterRange((char)(character + 1), range.End));
                }
            }

            return new CharacterSet([.. ranges]);
        }

        internal bool Intersects(CharacterSet other)
        {
            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < _ranges.Length && rightIndex < other._ranges.Length)
            {
                var left = _ranges[leftIndex];
                var right = other._ranges[rightIndex];
                if (left.End < right.Start)
                {
                    leftIndex++;
                }
                else if (right.End < left.Start)
                {
                    rightIndex++;
                }
                else
                {
                    return true;
                }
            }

            return false;
        }

        private static CharacterRange[] Normalize(IEnumerable<CharacterRange> ranges)
        {
            var orderedRanges = ranges.OrderBy(range => range.Start).ThenBy(range => range.End).ToArray();
            if (orderedRanges.Length == 0)
            {
                return [];
            }

            var normalizedRanges = new List<CharacterRange>(orderedRanges.Length);
            var current = orderedRanges[0];
            foreach (var next in orderedRanges.AsSpan(1))
            {
                if (next.Start <= current.End || (int)next.Start == current.End + 1)
                {
                    current = new CharacterRange(current.Start, (char)Math.Max(current.End, next.End));
                }
                else
                {
                    normalizedRanges.Add(current);
                    current = next;
                }
            }

            normalizedRanges.Add(current);
            return [.. normalizedRanges];
        }

        private static IEnumerable<CharacterRange> AddAsciiCaseEquivalents(IEnumerable<CharacterRange> ranges)
        {
            foreach (var range in ranges)
            {
                yield return range;

                var upperStart = Math.Max(range.Start, 'A');
                var upperEnd = Math.Min(range.End, 'Z');
                if (upperStart <= upperEnd)
                {
                    yield return new CharacterRange(
                        (char)(upperStart + ('a' - 'A')),
                        (char)(upperEnd + ('a' - 'A')));
                }

                var lowerStart = Math.Max(range.Start, 'a');
                var lowerEnd = Math.Min(range.End, 'z');
                if (lowerStart <= lowerEnd)
                {
                    yield return new CharacterRange(
                        (char)(lowerStart - ('a' - 'A')),
                        (char)(lowerEnd - ('a' - 'A')));
                }
            }
        }

        private static CharacterRange[] Complement(CharacterRange[] ranges)
        {
            var complement = new List<CharacterRange>(ranges.Length + 1);
            var nextStart = (int)char.MinValue;
            foreach (var range in ranges)
            {
                if (nextStart < range.Start)
                {
                    complement.Add(new CharacterRange((char)nextStart, (char)(range.Start - 1)));
                }

                nextStart = range.End + 1;
            }

            if (nextStart <= char.MaxValue)
            {
                complement.Add(new CharacterRange((char)nextStart, char.MaxValue));
            }

            return [.. complement];
        }
    }

    private readonly record struct CharacterRange(char Start, char End);

    private readonly record struct Transition(CharacterSet Characters, int TargetState);
}
