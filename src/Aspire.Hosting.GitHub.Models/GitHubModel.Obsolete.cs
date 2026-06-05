// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;

namespace Aspire.Hosting.GitHub;

// This file contains obsolete elements kept for backward compatibility.

public partial class GitHubModel
{
    public static partial class AI21Labs
    {
        /// <summary>
        /// A 52B parameters (12B active) multilingual model, offering a 256K long context window, function calling, structured output, and grounded generation.
        /// </summary>
        [Obsolete("This model has been removed from GitHub Models.")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static readonly GitHubModel AI21Jamba15Mini = new() { Id = "ai21-labs/ai21-jamba-1.5-mini" };
    }

    public static partial class Cohere
    {
        /// <summary>
        /// Cohere Embed English is the market's leading text representation model used for semantic search, retrieval-augmented generation (RAG), classification, and clustering.
        /// </summary>
        [Obsolete("This model has been removed from GitHub Models.")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static readonly GitHubModel CohereEmbedV3English = new() { Id = "cohere/cohere-embed-v3-english" };

        /// <summary>
        /// Cohere Embed Multilingual is the market's leading text representation model used for semantic search, retrieval-augmented generation (RAG), classification, and clustering.
        /// </summary>
        [Obsolete("This model has been removed from GitHub Models.")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static readonly GitHubModel CohereEmbedV3Multilingual = new() { Id = "cohere/cohere-embed-v3-multilingual" };
    }

    /// <summary>
    /// Models published by Core42.
    /// </summary>
    public static partial class Core42
    {
        /// <summary>
        /// JAIS 30b Chat is an auto-regressive bilingual LLM for Arabic &amp; English with state-of-the-art capabilities in Arabic.
        /// </summary>
        [Obsolete("This model has been removed from GitHub Models.")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static readonly GitHubModel Jais30bChat = new() { Id = "core42/jais-30b-chat" };
    }

    public static partial class MistralAI
    {
        /// <summary>
        /// Mistral Large 24.11 offers enhanced system prompts, advanced reasoning and function calling capabilities.
        /// </summary>
        [Obsolete("This model has been removed from GitHub Models.")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static readonly GitHubModel MistralLarge2411 = new() { Id = "mistral-ai/mistral-large-2411" };

        /// <summary>
        /// Mistral Nemo is a cutting-edge Language Model (LLM) boasting state-of-the-art reasoning, world knowledge, and coding capabilities within its size category.
        /// </summary>
        [Obsolete("This model has been removed from GitHub Models.")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static readonly GitHubModel MistralNemo = new() { Id = "mistral-ai/mistral-nemo" };
    }
}
