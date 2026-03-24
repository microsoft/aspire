// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Aspire.Hosting.Analyzers;

public partial class AspireExportAnalyzer
{
    internal static class Diagnostics
    {
        private const string ExportMethodMustBeStaticId = "ASPIREEXPORT001";
        internal static readonly DiagnosticDescriptor s_exportMethodMustBeStatic = new(
            id: ExportMethodMustBeStaticId,
            title: "AspireExport method must be static",
            messageFormat: "Method '{0}' marked with [AspireExport] must be static",
            category: "Design",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: $"https://aka.ms/aspire/diagnostics/{ExportMethodMustBeStaticId}");

        private const string InvalidExportIdFormatId = "ASPIREEXPORT002";
        internal static readonly DiagnosticDescriptor s_invalidExportIdFormat = new(
            id: InvalidExportIdFormatId,
            title: "Invalid AspireExport ID format",
            messageFormat: "Export ID '{0}' is not a valid method name. Use a valid identifier (e.g., 'addRedis', 'withEnvironment').",
            category: "Design",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: $"https://aka.ms/aspire/diagnostics/{InvalidExportIdFormatId}");

        private const string ReturnTypeMustBeAtsCompatibleId = "ASPIREEXPORT003";
        internal static readonly DiagnosticDescriptor s_returnTypeMustBeAtsCompatible = new(
            id: ReturnTypeMustBeAtsCompatibleId,
            title: "AspireExport return type must be ATS-compatible",
            messageFormat: "Method '{0}' has return type '{1}' which is not ATS-compatible. Use void, Task, Task<T>, or a supported Aspire type.",
            category: "Design",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: $"https://aka.ms/aspire/diagnostics/{ReturnTypeMustBeAtsCompatibleId}");

        private const string ParameterTypeMustBeAtsCompatibleId = "ASPIREEXPORT004";
        internal static readonly DiagnosticDescriptor s_parameterTypeMustBeAtsCompatible = new(
            id: ParameterTypeMustBeAtsCompatibleId,
            title: "AspireExport parameter type must be ATS-compatible",
            messageFormat: "Parameter '{0}' of type '{1}' in method '{2}' is not ATS-compatible. Use primitive types, enums, or supported Aspire types.",
            category: "Design",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: $"https://aka.ms/aspire/diagnostics/{ParameterTypeMustBeAtsCompatibleId}");

        private const string UnionRequiresAtLeastTwoTypesId = "ASPIREEXPORT005";
        internal static readonly DiagnosticDescriptor s_unionRequiresAtLeastTwoTypes = new(
            id: UnionRequiresAtLeastTwoTypesId,
            title: "AspireUnion requires at least 2 types",
            messageFormat: "[AspireUnion] requires at least 2 types, but {0} was specified",
            category: "Design",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            helpLinkUri: $"https://aka.ms/aspire/diagnostics/{UnionRequiresAtLeastTwoTypesId}");

        private const string UnionTypeMustBeAtsCompatibleId = "ASPIREEXPORT006";
        internal static readonly DiagnosticDescriptor s_unionTypeMustBeAtsCompatible = new(
            id: UnionTypeMustBeAtsCompatibleId,
            title: "AspireUnion type must be ATS-compatible",
            messageFormat: "Type '{0}' in [AspireUnion] is not ATS-compatible. Use primitive types, enums, or supported Aspire types.",
            category: "Design",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            helpLinkUri: $"https://aka.ms/aspire/diagnostics/{UnionTypeMustBeAtsCompatibleId}");

        private const string DuplicateExportIdId = "ASPIREEXPORT007";
        internal static readonly DiagnosticDescriptor s_duplicateExportId = new(
            id: DuplicateExportIdId,
            title: "Duplicate AspireExport ID for same target type",
            messageFormat: "Export ID '{0}' is already defined for target type '{1}'. Each export ID must be unique per target type.",
            category: "Design",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            helpLinkUri: $"https://aka.ms/aspire/diagnostics/{DuplicateExportIdId}",
            customTags: [WellKnownDiagnosticTags.CompilationEnd]);

        private const string MissingExportAttributeId = "ASPIREEXPORT008";
        internal static readonly DiagnosticDescriptor s_missingExportAttribute = new(
            id: MissingExportAttributeId,
            title: "Extension method on exported type missing AspireExport or AspireExportIgnore attribute",
            messageFormat: "Extension method '{0}' on exported type is missing [AspireExport] or [AspireExportIgnore]: {1}",
            category: "Design",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            helpLinkUri: $"https://aka.ms/aspire/diagnostics/{MissingExportAttributeId}");

        private const string ExportNameShouldBeUniqueId = "ASPIREEXPORT009";
        internal static readonly DiagnosticDescriptor s_exportNameShouldBeUnique = new(
            id: ExportNameShouldBeUniqueId,
            title: "Export name should be unique for methods targeting a specific resource type",
            messageFormat: "Export name '{0}' on method '{1}' may collide across integrations because it targets IResourceBuilder<{2}>. Use a unique name like '{3}'.",
            category: "Design",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            helpLinkUri: $"https://aka.ms/aspire/diagnostics/{ExportNameShouldBeUniqueId}");

        private const string ExportedSyncDelegateInvokedInlineId = "ASPIREEXPORT010";
        internal static readonly DiagnosticDescriptor s_exportedSyncDelegateInvokedInline = new(
            id: ExportedSyncDelegateInvokedInlineId,
            title: "Exported synchronous callback should not be invoked inline",
            messageFormat: "Exported builder method '{0}' directly invokes synchronous delegate parameter '{1}'. Defer the callback, expose an async delegate, or set RunSyncOnBackgroundThread = true to avoid polyglot deadlocks.",
            category: "Design",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            helpLinkUri: $"https://aka.ms/aspire/diagnostics/{ExportedSyncDelegateInvokedInlineId}");

        public static readonly ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics = ImmutableArray.Create(
            s_exportMethodMustBeStatic,
            s_invalidExportIdFormat,
            s_returnTypeMustBeAtsCompatible,
            s_parameterTypeMustBeAtsCompatible,
            s_unionRequiresAtLeastTwoTypes,
            s_unionTypeMustBeAtsCompatible,
            s_duplicateExportId,
            s_missingExportAttribute,
            s_exportNameShouldBeUnique,
            s_exportedSyncDelegateInvokedInline
        );
    }
}
