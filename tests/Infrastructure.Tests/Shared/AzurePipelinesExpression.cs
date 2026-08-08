// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Infrastructure.Tests;

/// <summary>
/// Evaluator for the small subset of the Azure Pipelines expression language used by the release
/// pipeline's conditional-insertion gates and step conditions, so tests can assert what a given
/// operator parameter recipe actually does instead of only matching the literal expression text.
/// </summary>
/// <remarks>
/// Two syntaxes appear in the pipeline and both are handled here:
/// <list type="bullet">
/// <item><description>
/// Compile-time gates address parameters directly and compare against YAML scalars:
/// <c>and(eq(parameters.DryRun, false), eq(parameters.NpmInternalMirrorAction, 'only'))</c>.
/// </description></item>
/// <item><description>
/// Runtime step conditions substitute the parameter into a string first, so both sides are
/// quoted strings: <c>and(succeeded(), eq('${{ parameters.DryRun }}', 'false'))</c>.
/// </description></item>
/// </list>
/// Comparison follows Azure Pipelines: operands are converted to strings and compared
/// case-insensitively, which makes <c>false</c> and <c>'false'</c> equal.
/// See https://learn.microsoft.com/azure/devops/pipelines/process/expressions.
/// </remarks>
internal static class AzurePipelinesExpression
{
    /// <summary>
    /// Evaluates <paramref name="expression"/> using <paramref name="parameters"/> for
    /// <c>parameters.&lt;name&gt;</c> references. <c>succeeded()</c> evaluates to <see langword="true"/>
    /// because these tests reason about gating, not about upstream step failures.
    /// </summary>
    public static bool Evaluate(string expression, IReadOnlyDictionary<string, string> parameters)
    {
        var reader = new Reader(expression, parameters);
        var value = reader.ReadExpression();
        reader.SkipWhitespace();

        if (!reader.AtEnd)
        {
            throw new FormatException($"Unexpected trailing content in expression '{expression}'.");
        }

        return ToBoolean(value);
    }

    private static bool ToBoolean(string value)
        => value switch
        {
            _ when string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) => true,
            _ when string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) => false,
            _ => throw new FormatException($"Value '{value}' is not a boolean.")
        };

    private sealed class Reader(string expression, IReadOnlyDictionary<string, string> parameters)
    {
        private int _position;

        public bool AtEnd => _position >= expression.Length;

        public string ReadExpression()
        {
            SkipWhitespace();

            if (Peek() == '\'')
            {
                return ReadQuotedString();
            }

            var token = ReadToken();
            SkipWhitespace();

            if (Peek() != '(')
            {
                // Bare operands: `true`, `false`, or `parameters.Name`.
                return token.StartsWith("parameters.", StringComparison.Ordinal)
                    ? LookupParameter(token["parameters.".Length..])
                    : token;
            }

            var arguments = ReadArgumentList();

            return token switch
            {
                "succeeded" => "true",
                "and" => arguments.All(ToBoolean) ? "true" : "false",
                "or" => arguments.Any(ToBoolean) ? "true" : "false",
                "not" => !ToBoolean(arguments.Single()) ? "true" : "false",
                "eq" => AreEqual(arguments) ? "true" : "false",
                "ne" => !AreEqual(arguments) ? "true" : "false",
                _ => throw new FormatException($"Unsupported function '{token}'.")
            };
        }

        public void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(expression[_position]))
            {
                _position++;
            }
        }

        private static bool AreEqual(IReadOnlyList<string> arguments)
        {
            if (arguments.Count != 2)
            {
                throw new FormatException("eq/ne require exactly two arguments.");
            }

            return string.Equals(arguments[0], arguments[1], StringComparison.OrdinalIgnoreCase);
        }

        private List<string> ReadArgumentList()
        {
            Expect('(');
            var arguments = new List<string>();
            SkipWhitespace();

            if (Peek() == ')')
            {
                _position++;
                return arguments;
            }

            while (true)
            {
                arguments.Add(ReadExpression());
                SkipWhitespace();

                var next = Peek();
                _position++;

                if (next == ')')
                {
                    return arguments;
                }

                if (next != ',')
                {
                    throw new FormatException($"Expected ',' or ')' at position {_position} of '{expression}'.");
                }
            }
        }

        private string ReadQuotedString()
        {
            Expect('\'');
            var start = _position;

            while (!AtEnd && expression[_position] != '\'')
            {
                _position++;
            }

            if (AtEnd)
            {
                throw new FormatException($"Unterminated string in expression '{expression}'.");
            }

            var literal = expression[start.._position];
            _position++;

            // Runtime conditions embed the parameter value in the string: '${{ parameters.X }}'.
            const string prefix = "${{ parameters.";
            const string suffix = " }}";
            if (literal.StartsWith(prefix, StringComparison.Ordinal) && literal.EndsWith(suffix, StringComparison.Ordinal))
            {
                return LookupParameter(literal[prefix.Length..^suffix.Length].Trim());
            }

            return literal;
        }

        private string ReadToken()
        {
            var start = _position;

            while (!AtEnd && (char.IsLetterOrDigit(expression[_position]) || expression[_position] is '.' or '_'))
            {
                _position++;
            }

            if (start == _position)
            {
                throw new FormatException($"Expected a token at position {_position} of '{expression}'.");
            }

            return expression[start.._position];
        }

        private string LookupParameter(string name)
            => parameters.TryGetValue(name, out var value)
                ? value
                : throw new KeyNotFoundException($"Expression '{expression}' references undefined parameter '{name}'.");

        private char Peek() => AtEnd ? '\0' : expression[_position];

        private void Expect(char expected)
        {
            if (Peek() != expected)
            {
                throw new FormatException($"Expected '{expected}' at position {_position} of '{expression}'.");
            }

            _position++;
        }
    }
}
