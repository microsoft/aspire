// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
namespace Aspire.Hosting.GitHub;

/// <summary>
/// Generated strongly typed model descriptors for GitHub Models.
/// </summary>
public partial class GitHubModel
{
    /// <summary>
    /// Models published by AI21 Labs.
    /// </summary>
    public static partial class AI21Labs
    {
        /// <summary>
        /// A 398B parameters (94B active) multilingual model, offering a 256K long context window, function calling, structured output, and grounded generation.
        /// </summary>
        public static readonly GitHubModel AI21Jamba15Large = new() { Id = "ai21-labs/ai21-jamba-1.5-large" };
    }

    /// <summary>
    /// Models published by Cohere.
    /// </summary>
    public static partial class Cohere
    {
        /// <summary>
        /// Command A is a highly efficient generative model that excels at agentic and multilingual use cases.
        /// </summary>
        public static readonly GitHubModel CohereCommandA = new() { Id = "cohere/cohere-command-a" };

        /// <summary>
        /// Command R is a scalable generative model targeting RAG and Tool Use to enable production-scale AI for enterprise.
        /// </summary>
        public static readonly GitHubModel CohereCommandR082024 = new() { Id = "cohere/cohere-command-r-08-2024" };

        /// <summary>
        /// Command R+ is a state-of-the-art RAG-optimized model designed to tackle enterprise-grade workloads.
        /// </summary>
        public static readonly GitHubModel CohereCommandRPlus082024 = new() { Id = "cohere/cohere-command-r-plus-08-2024" };
    }

    /// <summary>
    /// Models published by DeepSeek.
    /// </summary>
    public static partial class DeepSeek
    {
        /// <summary>
        /// DeepSeek-R1 excels at reasoning tasks using a step-by-step training process, such as language, scientific reasoning, and coding tasks.
        /// </summary>
        public static readonly GitHubModel DeepSeekR1 = new() { Id = "deepseek/deepseek-r1" };

        /// <summary>
        /// The DeepSeek R1 0528 model has improved reasoning capabilities, this version also offers a reduced hallucination rate, enhanced support for function calling, and better experience for vibe coding.
        /// </summary>
        public static readonly GitHubModel DeepSeekR10528 = new() { Id = "deepseek/deepseek-r1-0528" };

        /// <summary>
        /// DeepSeek-V3-0324 demonstrates notable improvements over its predecessor, DeepSeek-V3, in several key aspects, including enhanced reasoning, improved function calling, and superior code generation capabilities.
        /// </summary>
        public static readonly GitHubModel DeepSeekV30324 = new() { Id = "deepseek/deepseek-v3-0324" };
    }

    /// <summary>
    /// Models published by Meta.
    /// </summary>
    public static partial class Meta
    {
        /// <summary>
        /// Llama 4 Maverick 17B 128E Instruct FP8 is great at precise image understanding and creative writing, offering high quality at a lower price compared to Llama 3.3 70B
        /// </summary>
        public static readonly GitHubModel Llama4Maverick17B128EInstructFP8 = new() { Id = "meta/llama-4-maverick-17b-128e-instruct-fp8" };

        /// <summary>
        /// Llama 4 Scout 17B 16E Instruct is great at multi-document summarization, parsing extensive user activity for personalized tasks, and reasoning over vast codebases.
        /// </summary>
        public static readonly GitHubModel Llama4Scout17B16EInstruct = new() { Id = "meta/llama-4-scout-17b-16e-instruct" };

        /// <summary>
        /// Excels in image reasoning capabilities on high-res images for visual understanding apps.
        /// </summary>
        public static readonly GitHubModel Llama3211BVisionInstruct = new() { Id = "meta/llama-3.2-11b-vision-instruct" };

        /// <summary>
        /// Advanced image reasoning capabilities for visual understanding agentic apps.
        /// </summary>
        public static readonly GitHubModel Llama3290BVisionInstruct = new() { Id = "meta/llama-3.2-90b-vision-instruct" };

        /// <summary>
        /// Llama 3.3 70B Instruct offers enhanced reasoning, math, and instruction following with performance comparable to Llama 3.1 405B.
        /// </summary>
        public static readonly GitHubModel Llama3370BInstruct = new() { Id = "meta/llama-3.3-70b-instruct" };

        /// <summary>
        /// The Llama 3.1 instruction tuned text only models are optimized for multilingual dialogue use cases and outperform many of the available open source and closed chat models on common industry benchmarks.
        /// </summary>
        public static readonly GitHubModel MetaLlama31405BInstruct = new() { Id = "meta/meta-llama-3.1-405b-instruct" };

        /// <summary>
        /// The Llama 3.1 instruction tuned text only models are optimized for multilingual dialogue use cases and outperform many of the available open source and closed chat models on common industry benchmarks.
        /// </summary>
        public static readonly GitHubModel MetaLlama318BInstruct = new() { Id = "meta/meta-llama-3.1-8b-instruct" };
    }

    /// <summary>
    /// Models published by Microsoft.
    /// </summary>
    public static partial class Microsoft
    {
        /// <summary>
        /// MAI-DS-R1 is a DeepSeek-R1 reasoning model that has been post-trained by the Microsoft AI team to fill in information gaps in the previous version of the model and improve its harm protections while maintaining R1 reasoning capabilities.
        /// </summary>
        public static readonly GitHubModel MaiDSR1 = new() { Id = "microsoft/mai-ds-r1" };

        /// <summary>
        /// Phi-4 14B, a highly capable model for low latency scenarios.
        /// </summary>
        public static readonly GitHubModel Phi4 = new() { Id = "microsoft/phi-4" };

        /// <summary>
        /// 3.8B parameters Small Language Model outperforming larger models in reasoning, math, coding, and function-calling
        /// </summary>
        public static readonly GitHubModel Phi4MiniInstruct = new() { Id = "microsoft/phi-4-mini-instruct" };

        /// <summary>
        /// Lightweight math reasoning model optimized for multi-step problem solving
        /// </summary>
        public static readonly GitHubModel Phi4MiniReasoning = new() { Id = "microsoft/phi-4-mini-reasoning" };

        /// <summary>
        /// First small multimodal model to have 3 modality inputs (text, audio, image), excelling in quality and efficiency
        /// </summary>
        public static readonly GitHubModel Phi4MultimodalInstruct = new() { Id = "microsoft/phi-4-multimodal-instruct" };

        /// <summary>
        /// State-of-the-art open-weight reasoning model.
        /// </summary>
        public static readonly GitHubModel Phi4Reasoning = new() { Id = "microsoft/phi-4-reasoning" };
    }

    /// <summary>
    /// Models published by Mistral AI.
    /// </summary>
    public static partial class MistralAI
    {
        /// <summary>
        /// Codestral 25.01 by Mistral AI is designed for code generation, supporting 80+ programming languages, and optimized for tasks like code completion and fill-in-the-middle
        /// </summary>
        public static readonly GitHubModel Codestral2501 = new() { Id = "mistral-ai/codestral-2501" };

        /// <summary>
        /// Ministral 3B is a state-of-the-art Small Language Model (SLM) optimized for edge computing and on-device applications. As it is designed for low-latency and compute-efficient inference, it it also the perfect model for standard GenAI applications that have
        /// </summary>
        public static readonly GitHubModel Ministral3B = new() { Id = "mistral-ai/ministral-3b" };

        /// <summary>
        /// Mistral Medium 3 is an advanced Large Language Model (LLM) with state-of-the-art reasoning, knowledge, coding and vision capabilities.
        /// </summary>
        public static readonly GitHubModel MistralMedium32505 = new() { Id = "mistral-ai/mistral-medium-2505" };

        /// <summary>
        /// Enhanced Mistral Small 3 with multimodal capabilities and a 128k context length.
        /// </summary>
        public static readonly GitHubModel MistralSmall31 = new() { Id = "mistral-ai/mistral-small-2503" };
    }

    /// <summary>
    /// Models published by OpenAI.
    /// </summary>
    public static partial class OpenAI
    {
        /// <summary>
        /// gpt-4.1 outperforms gpt-4o across the board, with major gains in coding, instruction following, and long-context understanding
        /// </summary>
        public static readonly GitHubModel OpenAIGpt41 = new() { Id = "openai/gpt-4.1" };

        /// <summary>
        /// gpt-4.1-mini outperform gpt-4o-mini across the board, with major gains in coding, instruction following, and long-context handling
        /// </summary>
        public static readonly GitHubModel OpenAIGpt41Mini = new() { Id = "openai/gpt-4.1-mini" };

        /// <summary>
        /// gpt-4.1-nano provides gains in coding, instruction following, and long-context handling along with lower latency and cost
        /// </summary>
        public static readonly GitHubModel OpenAIGpt41Nano = new() { Id = "openai/gpt-4.1-nano" };

        /// <summary>
        /// OpenAI&apos;s most advanced multimodal model in the gpt-4o family. Can handle both text and image inputs.
        /// </summary>
        public static readonly GitHubModel OpenAIGpt4o = new() { Id = "openai/gpt-4o" };

        /// <summary>
        /// An affordable, efficient AI solution for diverse text and image tasks.
        /// </summary>
        public static readonly GitHubModel OpenAIGpt4oMini = new() { Id = "openai/gpt-4o-mini" };

        /// <summary>
        /// gpt-5 is designed for logic-heavy and multi-step tasks.
        /// </summary>
        public static readonly GitHubModel OpenAIGpt5 = new() { Id = "openai/gpt-5" };

        /// <summary>
        /// gpt-5-chat (preview) is an advanced, natural, multimodal, and context-aware conversations for enterprise applications.
        /// </summary>
        public static readonly GitHubModel OpenAIGpt5ChatPreview = new() { Id = "openai/gpt-5-chat" };

        /// <summary>
        /// gpt-5-mini is a lightweight version for cost-sensitive applications.
        /// </summary>
        public static readonly GitHubModel OpenAIGpt5Mini = new() { Id = "openai/gpt-5-mini" };

        /// <summary>
        /// gpt-5-nano is optimized for speed, ideal for applications requiring low latency.
        /// </summary>
        public static readonly GitHubModel OpenAIGpt5Nano = new() { Id = "openai/gpt-5-nano" };

        /// <summary>
        /// Focused on advanced reasoning and solving complex problems, including math and science tasks. Ideal for applications that require deep contextual understanding and agentic workflows.
        /// </summary>
        public static readonly GitHubModel OpenAIO1 = new() { Id = "openai/o1" };

        /// <summary>
        /// Smaller, faster, and 80% cheaper than o1-preview, performs well at code generation and small context operations.
        /// </summary>
        public static readonly GitHubModel OpenAIO1Mini = new() { Id = "openai/o1-mini" };

        /// <summary>
        /// Focused on advanced reasoning and solving complex problems, including math and science tasks. Ideal for applications that require deep contextual understanding and agentic workflows.
        /// </summary>
        public static readonly GitHubModel OpenAIO1Preview = new() { Id = "openai/o1-preview" };

        /// <summary>
        /// o3 includes significant improvements on quality and safety while supporting the existing features of o1 and delivering comparable or better performance.
        /// </summary>
        public static readonly GitHubModel OpenAIO3 = new() { Id = "openai/o3" };

        /// <summary>
        /// o3-mini includes the o1 features with significant cost-efficiencies for scenarios requiring high performance.
        /// </summary>
        public static readonly GitHubModel OpenAIO3Mini = new() { Id = "openai/o3-mini" };

        /// <summary>
        /// o4-mini includes significant improvements on quality and safety while supporting the existing features of o3-mini and delivering comparable or better performance.
        /// </summary>
        public static readonly GitHubModel OpenAIO4Mini = new() { Id = "openai/o4-mini" };

        /// <summary>
        /// Text-embedding-3 series models are the latest and most capable embedding model from OpenAI.
        /// </summary>
        public static readonly GitHubModel OpenAITextEmbedding3Large = new() { Id = "openai/text-embedding-3-large" };

        /// <summary>
        /// Text-embedding-3 series models are the latest and most capable embedding model from OpenAI.
        /// </summary>
        public static readonly GitHubModel OpenAITextEmbedding3Small = new() { Id = "openai/text-embedding-3-small" };
    }

    /// <summary>
    /// Models published by xAI.
    /// </summary>
    public static partial class XAI
    {
        /// <summary>
        /// Grok 3 is xAI&apos;s debut model, pretrained by Colossus at supermassive scale to excel in specialized domains like finance, healthcare, and the law.
        /// </summary>
        public static readonly GitHubModel Grok3 = new() { Id = "xai/grok-3" };

        /// <summary>
        /// Grok 3 Mini is a lightweight model that thinks before responding. Trained on mathematic and scientific problems, it is great for logic-based tasks.
        /// </summary>
        public static readonly GitHubModel Grok3Mini = new() { Id = "xai/grok-3-mini" };
    }

    /// <summary>
    /// Gets the model identifier string for the specified <see cref="GitHubModelName"/>.
    /// </summary>
    internal static string GetModelId(GitHubModelName name) => name switch
    {
        GitHubModelName.AI21Jamba15Large => "ai21-labs/ai21-jamba-1.5-large",
        GitHubModelName.CohereCommandA => "cohere/cohere-command-a",
        GitHubModelName.CohereCommandR082024 => "cohere/cohere-command-r-08-2024",
        GitHubModelName.CohereCommandRPlus082024 => "cohere/cohere-command-r-plus-08-2024",
        GitHubModelName.DeepSeekR1 => "deepseek/deepseek-r1",
        GitHubModelName.DeepSeekR10528 => "deepseek/deepseek-r1-0528",
        GitHubModelName.DeepSeekV30324 => "deepseek/deepseek-v3-0324",
        GitHubModelName.Llama4Maverick17B128EInstructFP8 => "meta/llama-4-maverick-17b-128e-instruct-fp8",
        GitHubModelName.Llama4Scout17B16EInstruct => "meta/llama-4-scout-17b-16e-instruct",
        GitHubModelName.Llama3211BVisionInstruct => "meta/llama-3.2-11b-vision-instruct",
        GitHubModelName.Llama3290BVisionInstruct => "meta/llama-3.2-90b-vision-instruct",
        GitHubModelName.Llama3370BInstruct => "meta/llama-3.3-70b-instruct",
        GitHubModelName.MetaLlama31405BInstruct => "meta/meta-llama-3.1-405b-instruct",
        GitHubModelName.MetaLlama318BInstruct => "meta/meta-llama-3.1-8b-instruct",
        GitHubModelName.MaiDSR1 => "microsoft/mai-ds-r1",
        GitHubModelName.Phi4 => "microsoft/phi-4",
        GitHubModelName.Phi4MiniInstruct => "microsoft/phi-4-mini-instruct",
        GitHubModelName.Phi4MiniReasoning => "microsoft/phi-4-mini-reasoning",
        GitHubModelName.Phi4MultimodalInstruct => "microsoft/phi-4-multimodal-instruct",
        GitHubModelName.Phi4Reasoning => "microsoft/phi-4-reasoning",
        GitHubModelName.Codestral2501 => "mistral-ai/codestral-2501",
        GitHubModelName.Ministral3B => "mistral-ai/ministral-3b",
        GitHubModelName.MistralMedium32505 => "mistral-ai/mistral-medium-2505",
        GitHubModelName.MistralSmall31 => "mistral-ai/mistral-small-2503",
        GitHubModelName.OpenAIGpt41 => "openai/gpt-4.1",
        GitHubModelName.OpenAIGpt41Mini => "openai/gpt-4.1-mini",
        GitHubModelName.OpenAIGpt41Nano => "openai/gpt-4.1-nano",
        GitHubModelName.OpenAIGpt4o => "openai/gpt-4o",
        GitHubModelName.OpenAIGpt4oMini => "openai/gpt-4o-mini",
        GitHubModelName.OpenAIGpt5 => "openai/gpt-5",
        GitHubModelName.OpenAIGpt5ChatPreview => "openai/gpt-5-chat",
        GitHubModelName.OpenAIGpt5Mini => "openai/gpt-5-mini",
        GitHubModelName.OpenAIGpt5Nano => "openai/gpt-5-nano",
        GitHubModelName.OpenAIO1 => "openai/o1",
        GitHubModelName.OpenAIO1Mini => "openai/o1-mini",
        GitHubModelName.OpenAIO1Preview => "openai/o1-preview",
        GitHubModelName.OpenAIO3 => "openai/o3",
        GitHubModelName.OpenAIO3Mini => "openai/o3-mini",
        GitHubModelName.OpenAIO4Mini => "openai/o4-mini",
        GitHubModelName.OpenAITextEmbedding3Large => "openai/text-embedding-3-large",
        GitHubModelName.OpenAITextEmbedding3Small => "openai/text-embedding-3-small",
        GitHubModelName.Grok3 => "xai/grok-3",
        GitHubModelName.Grok3Mini => "xai/grok-3-mini",
        _ => throw new System.ArgumentOutOfRangeException(nameof(name), name, "Unknown GitHub model name.")
    };
}

/// <summary>
/// Enumerates known GitHub model names for polyglot app host support.
/// </summary>
internal enum GitHubModelName
{
    /// <summary>
    /// A 398B parameters (94B active) multilingual model, offering a 256K long context window, function calling, structured output, and grounded generation.
    /// </summary>
    AI21Jamba15Large,

    /// <summary>
    /// Command A is a highly efficient generative model that excels at agentic and multilingual use cases.
    /// </summary>
    CohereCommandA,

    /// <summary>
    /// Command R is a scalable generative model targeting RAG and Tool Use to enable production-scale AI for enterprise.
    /// </summary>
    CohereCommandR082024,

    /// <summary>
    /// Command R+ is a state-of-the-art RAG-optimized model designed to tackle enterprise-grade workloads.
    /// </summary>
    CohereCommandRPlus082024,

    /// <summary>
    /// DeepSeek-R1 excels at reasoning tasks using a step-by-step training process, such as language, scientific reasoning, and coding tasks.
    /// </summary>
    DeepSeekR1,

    /// <summary>
    /// The DeepSeek R1 0528 model has improved reasoning capabilities, this version also offers a reduced hallucination rate, enhanced support for function calling, and better experience for vibe coding.
    /// </summary>
    DeepSeekR10528,

    /// <summary>
    /// DeepSeek-V3-0324 demonstrates notable improvements over its predecessor, DeepSeek-V3, in several key aspects, including enhanced reasoning, improved function calling, and superior code generation capabilities.
    /// </summary>
    DeepSeekV30324,

    /// <summary>
    /// Llama 4 Maverick 17B 128E Instruct FP8 is great at precise image understanding and creative writing, offering high quality at a lower price compared to Llama 3.3 70B
    /// </summary>
    Llama4Maverick17B128EInstructFP8,

    /// <summary>
    /// Llama 4 Scout 17B 16E Instruct is great at multi-document summarization, parsing extensive user activity for personalized tasks, and reasoning over vast codebases.
    /// </summary>
    Llama4Scout17B16EInstruct,

    /// <summary>
    /// Excels in image reasoning capabilities on high-res images for visual understanding apps.
    /// </summary>
    Llama3211BVisionInstruct,

    /// <summary>
    /// Advanced image reasoning capabilities for visual understanding agentic apps.
    /// </summary>
    Llama3290BVisionInstruct,

    /// <summary>
    /// Llama 3.3 70B Instruct offers enhanced reasoning, math, and instruction following with performance comparable to Llama 3.1 405B.
    /// </summary>
    Llama3370BInstruct,

    /// <summary>
    /// The Llama 3.1 instruction tuned text only models are optimized for multilingual dialogue use cases and outperform many of the available open source and closed chat models on common industry benchmarks.
    /// </summary>
    MetaLlama31405BInstruct,

    /// <summary>
    /// The Llama 3.1 instruction tuned text only models are optimized for multilingual dialogue use cases and outperform many of the available open source and closed chat models on common industry benchmarks.
    /// </summary>
    MetaLlama318BInstruct,

    /// <summary>
    /// MAI-DS-R1 is a DeepSeek-R1 reasoning model that has been post-trained by the Microsoft AI team to fill in information gaps in the previous version of the model and improve its harm protections while maintaining R1 reasoning capabilities.
    /// </summary>
    MaiDSR1,

    /// <summary>
    /// Phi-4 14B, a highly capable model for low latency scenarios.
    /// </summary>
    Phi4,

    /// <summary>
    /// 3.8B parameters Small Language Model outperforming larger models in reasoning, math, coding, and function-calling
    /// </summary>
    Phi4MiniInstruct,

    /// <summary>
    /// Lightweight math reasoning model optimized for multi-step problem solving
    /// </summary>
    Phi4MiniReasoning,

    /// <summary>
    /// First small multimodal model to have 3 modality inputs (text, audio, image), excelling in quality and efficiency
    /// </summary>
    Phi4MultimodalInstruct,

    /// <summary>
    /// State-of-the-art open-weight reasoning model.
    /// </summary>
    Phi4Reasoning,

    /// <summary>
    /// Codestral 25.01 by Mistral AI is designed for code generation, supporting 80+ programming languages, and optimized for tasks like code completion and fill-in-the-middle
    /// </summary>
    Codestral2501,

    /// <summary>
    /// Ministral 3B is a state-of-the-art Small Language Model (SLM) optimized for edge computing and on-device applications. As it is designed for low-latency and compute-efficient inference, it it also the perfect model for standard GenAI applications that have
    /// </summary>
    Ministral3B,

    /// <summary>
    /// Mistral Medium 3 is an advanced Large Language Model (LLM) with state-of-the-art reasoning, knowledge, coding and vision capabilities.
    /// </summary>
    MistralMedium32505,

    /// <summary>
    /// Enhanced Mistral Small 3 with multimodal capabilities and a 128k context length.
    /// </summary>
    MistralSmall31,

    /// <summary>
    /// gpt-4.1 outperforms gpt-4o across the board, with major gains in coding, instruction following, and long-context understanding
    /// </summary>
    OpenAIGpt41,

    /// <summary>
    /// gpt-4.1-mini outperform gpt-4o-mini across the board, with major gains in coding, instruction following, and long-context handling
    /// </summary>
    OpenAIGpt41Mini,

    /// <summary>
    /// gpt-4.1-nano provides gains in coding, instruction following, and long-context handling along with lower latency and cost
    /// </summary>
    OpenAIGpt41Nano,

    /// <summary>
    /// OpenAI&apos;s most advanced multimodal model in the gpt-4o family. Can handle both text and image inputs.
    /// </summary>
    OpenAIGpt4o,

    /// <summary>
    /// An affordable, efficient AI solution for diverse text and image tasks.
    /// </summary>
    OpenAIGpt4oMini,

    /// <summary>
    /// gpt-5 is designed for logic-heavy and multi-step tasks.
    /// </summary>
    OpenAIGpt5,

    /// <summary>
    /// gpt-5-chat (preview) is an advanced, natural, multimodal, and context-aware conversations for enterprise applications.
    /// </summary>
    OpenAIGpt5ChatPreview,

    /// <summary>
    /// gpt-5-mini is a lightweight version for cost-sensitive applications.
    /// </summary>
    OpenAIGpt5Mini,

    /// <summary>
    /// gpt-5-nano is optimized for speed, ideal for applications requiring low latency.
    /// </summary>
    OpenAIGpt5Nano,

    /// <summary>
    /// Focused on advanced reasoning and solving complex problems, including math and science tasks. Ideal for applications that require deep contextual understanding and agentic workflows.
    /// </summary>
    OpenAIO1,

    /// <summary>
    /// Smaller, faster, and 80% cheaper than o1-preview, performs well at code generation and small context operations.
    /// </summary>
    OpenAIO1Mini,

    /// <summary>
    /// Focused on advanced reasoning and solving complex problems, including math and science tasks. Ideal for applications that require deep contextual understanding and agentic workflows.
    /// </summary>
    OpenAIO1Preview,

    /// <summary>
    /// o3 includes significant improvements on quality and safety while supporting the existing features of o1 and delivering comparable or better performance.
    /// </summary>
    OpenAIO3,

    /// <summary>
    /// o3-mini includes the o1 features with significant cost-efficiencies for scenarios requiring high performance.
    /// </summary>
    OpenAIO3Mini,

    /// <summary>
    /// o4-mini includes significant improvements on quality and safety while supporting the existing features of o3-mini and delivering comparable or better performance.
    /// </summary>
    OpenAIO4Mini,

    /// <summary>
    /// Text-embedding-3 series models are the latest and most capable embedding model from OpenAI.
    /// </summary>
    OpenAITextEmbedding3Large,

    /// <summary>
    /// Text-embedding-3 series models are the latest and most capable embedding model from OpenAI.
    /// </summary>
    OpenAITextEmbedding3Small,

    /// <summary>
    /// Grok 3 is xAI&apos;s debut model, pretrained by Colossus at supermassive scale to excel in specialized domains like finance, healthcare, and the law.
    /// </summary>
    Grok3,

    /// <summary>
    /// Grok 3 Mini is a lightweight model that thinks before responding. Trained on mathematic and scientific problems, it is great for logic-based tasks.
    /// </summary>
    Grok3Mini,
}
