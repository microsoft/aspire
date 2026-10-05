// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

#if INCLUDE_SQL_QUERY_PARSER
using System.Diagnostics;
#endif
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
#if INCLUDE_SQL_QUERY_PARSER
using OpenTelemetry.Instrumentation;
using OpenTelemetry.Trace;
#endif

namespace OpenTelemetry.Internal;

/// <summary>
/// Helper class for Database Semantic Conventions.
/// </summary>
/// <remarks>
/// Due to a breaking change in the semantic convention, affected instrumentation libraries
/// must inspect an environment variable to determine which attributes to emit.
/// This is expected to be removed when the instrumentation libraries reach Stable.
/// <see href="https://github.com/open-telemetry/semantic-conventions/blob/v1.28.0/docs/database/database-spans.md"/>.
/// </remarks>
internal static class DatabaseSemanticConventionHelper
{
    internal const string SemanticConventionOptInKeyName = "OTEL_SEMCONV_STABILITY_OPT_IN";
    internal static readonly char[] Separator = [',', ' '];

    [Flags]
    internal enum DatabaseSemanticConvention
    {
        /// <summary>
        /// Instructs an instrumentation library to emit the old experimental Database attributes.
        /// </summary>
        Old = 0x1,

        /// <summary>
        /// Instructs an instrumentation library to emit the new, v1.28.0 Database attributes.
        /// </summary>
        New = 0x2,

        /// <summary>
        /// Instructs an instrumentation library to emit both the old and new attributes.
        /// </summary>
        Dupe = Old | New,
    }

    public static DatabaseSemanticConvention GetSemanticConventionOptIn(IConfiguration configuration)
    {
        if (TryGetConfiguredValues(configuration, out var values))
        {
            if (values.Contains("database/dup"))
            {
                return DatabaseSemanticConvention.Dupe;
            }
            else if (values.Contains("database"))
            {
                return DatabaseSemanticConvention.New;
            }
        }

        return DatabaseSemanticConvention.Old;
    }

#if INCLUDE_SQL_QUERY_PARSER
    public static void ApplyConventionsForQueryText(
        Activity activity,
        string? commandText,
        bool emitOldAttributes,
        bool emitNewAttributes,
        bool sanitizeQuery = true)
    {
        var queryText = commandText ?? string.Empty;
        var querySummary = string.Empty;

        if (sanitizeQuery)
        {
            var sqlStatementInfo = SqlProcessor.GetSanitizedSql(commandText);

            queryText = sqlStatementInfo.SanitizedSql;
            querySummary = sqlStatementInfo.DbQuerySummary;
        }

        if (emitOldAttributes)
        {
            activity.SetTag(SemanticConventions.AttributeDbStatement, queryText);
        }

        if (emitNewAttributes)
        {
            activity.SetTag(SemanticConventions.AttributeDbQueryText, queryText);

            if (!string.IsNullOrEmpty(querySummary))
            {
                activity.SetTag(SemanticConventions.AttributeDbQuerySummary, querySummary);
                activity.DisplayName = querySummary;
            }
        }
    }

    public static void AddTagsForSamplingAndUpdateActivityNameForQueryText(
        ref TagList tagList,
        string? commandText,
        ref string activityName,
        bool sanitizeQuery = true)
    {
        var queryText = commandText ?? string.Empty;
        var querySummary = string.Empty;

        if (sanitizeQuery)
        {
            var sqlStatementInfo = SqlProcessor.GetSanitizedSql(commandText);
            queryText = sqlStatementInfo.SanitizedSql;
            querySummary = sqlStatementInfo.DbQuerySummary;
        }

        tagList.Add(SemanticConventions.AttributeDbQueryText, queryText);

        if (!string.IsNullOrEmpty(querySummary))
        {
            tagList.Add(SemanticConventions.AttributeDbQuerySummary, querySummary);
            activityName = querySummary;
        }
    }

    public static void ApplyConventionsForStoredProcedure(
        Activity activity,
        string? commandText,
        bool emitOldAttributes,
        bool emitNewAttributes)
    {
        if (emitOldAttributes)
        {
            activity.SetTag(SemanticConventions.AttributeDbStatement, commandText);
        }

        if (emitNewAttributes)
        {
            activity.SetTag(SemanticConventions.AttributeDbOperationName, "EXECUTE");
            activity.SetTag(SemanticConventions.AttributeDbStoredProcedureName, commandText);
            var dbQuerySummary = $"EXECUTE {commandText}";
            activity.SetTag(SemanticConventions.AttributeDbQuerySummary, dbQuerySummary);
            activity.DisplayName = dbQuerySummary;
        }
    }

    public static void AddTagsForSamplingAndUpdateActivityNameForStoredProcedure(
        ref TagList tagsList,
        string? commandText,
        ref string activityName)
    {
        tagsList.Add(SemanticConventions.AttributeDbOperationName, "EXECUTE");
        tagsList.Add(SemanticConventions.AttributeDbStoredProcedureName, commandText);
        var dbQuerySummary = $"EXECUTE {commandText}";
        tagsList.Add(SemanticConventions.AttributeDbQuerySummary, dbQuerySummary);
        activityName = dbQuerySummary;
    }
#endif

    private static bool TryGetConfiguredValues(IConfiguration configuration, [NotNullWhen(true)] out HashSet<string>? values)
    {
        try
        {
            var stringValue = configuration[SemanticConventionOptInKeyName];

            if (string.IsNullOrWhiteSpace(stringValue))
            {
                values = null;
                return false;
            }

#pragma warning disable IDE0370 // Suppression is unnecessary
            var stringValues = stringValue!.Split(separator: Separator, options: StringSplitOptions.RemoveEmptyEntries);
#pragma warning restore IDE0370 // Suppression is unnecessary
            values = new HashSet<string>(stringValues, StringComparer.OrdinalIgnoreCase);
            return true;
        }
        catch
        {
            values = null;
            return false;
        }
    }
}
