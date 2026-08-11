// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Foundry;

// This file is a placeholder for obsolete model entries kept for backward compatibility.
//
// When a model that has already shipped in a stable release is removed from the catalog,
// do NOT delete its entry from the generated files. Instead, move it here and mark it as
// obsolete so existing consumers still compile. Use the format below: an [Obsolete] message
// pointing at the replacement (or stating it is no longer available), plus
// [EditorBrowsable(Never)] so it stays out of IntelliSense and the ATS export.
//
// Group entries under the same partial class (publisher) they originally belonged to, e.g.:
//
//     public partial class FoundryModel
//     {
//         public static partial class Microsoft
//         {
//             /// <summary>
//             /// Azure AI Language service.
//             /// </summary>
//             [Obsolete("Azure AI Language has been replaced with more granular services. Use AzureLanguageLanguageDetection instead.")]
//             [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
//             public static readonly FoundryModel AzureAILanguage = new() { Name = "Azure-AI-Language", Version = "1", Format = "Microsoft" };
//         }
//     }
//
// There are currently no obsolete entries. This file is intentionally kept (with the example
// above commented out) so the formatting is ready to use for the next release.
