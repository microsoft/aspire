// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

// This file contains obsolete elements kept for backward compatibility.

public partial class AIFoundryModel
{
    /// <summary>
    /// Obsolete Microsoft models that have been removed or replaced.
    /// </summary>
    public static partial class Microsoft
    {
        /// <summary>
        /// Azure AI Language service.
        /// </summary>
        [Obsolete("Azure AI Language has been replaced with more granular services. Use AzureLanguageLanguageDetection, AzureLanguageTextPiiRedaction, or other specific services instead.")]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public static readonly AIFoundryModel AzureAILanguage = new() { Name = "Azure-AI-Language", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Azure AI Translator service.
        /// </summary>
        [Obsolete("Azure AI Translator has been replaced with more granular services. Use AzureTranslatorTextTranslation or AzureTranslatorDocumentTranslation instead.")]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public static readonly AIFoundryModel AzureAITranslator = new() { Name = "Azure-AI-Translator", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Azure Language Text PII service.
        /// </summary>
        [Obsolete("Use AzureLanguageTextPiiRedaction instead.")]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public static readonly AIFoundryModel TextPii = new() { Name = "Text-PII", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Azure Language Detection service.
        /// </summary>
        [Obsolete("Use AzureLanguageLanguageDetection instead.")]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public static readonly AIFoundryModel LanguageDetection = new() { Name = "Language-Detection", Version = "1", Format = "Microsoft" };
    }

    /// <summary>
    /// Obsolete local models that have been removed.
    /// </summary>
    public static partial class Local
    {
        /// <summary>
        /// Qwen 2.5 1.5B Instruct model for AMD NPUs (test variant).
        /// </summary>
        [Obsolete("This test variant is no longer available. Use Qwen2515b instead.")]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public static readonly AIFoundryModel Qwen2515bInstructTestVitisNpu = new() { Name = "qwen2.5-1.5b-instruct-test-vitis-npu", Version = "1", Format = "Microsoft" };
    }
}
