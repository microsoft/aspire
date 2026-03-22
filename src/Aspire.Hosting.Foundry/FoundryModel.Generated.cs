// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
namespace Aspire.Hosting.Foundry;

/// <summary>
/// Generated strongly typed model descriptors for Microsoft Foundry.
/// </summary>
public partial class FoundryModel
{
    /// <summary>
    /// Models published by AI21 Labs.
    /// </summary>
    public static partial class AI21Labs
    {
        /// <summary>
        /// A 398B parameters (94B active) multilingual model, offering a 256K long context window, function calling, structured output, and grounded generation.
        /// </summary>
        public static readonly FoundryModel AI21Jamba15Large = new() { Name = "AI21-Jamba-1.5-Large", Version = "1", Format = "AI21 Labs" };

        /// <summary>
        /// A 52B parameters (12B active) multilingual model, offering a 256K long context window, function calling, structured output, and grounded generation.
        /// </summary>
        public static readonly FoundryModel AI21Jamba15Mini = new() { Name = "AI21-Jamba-1.5-Mini", Version = "1", Format = "AI21 Labs" };
    }

    /// <summary>
    /// Models published by Anthropic.
    /// </summary>
    public static partial class Anthropic
    {
        /// <summary>
        /// Claude Haiku 4.5 delivers near-frontier performance for a wide range of use cases, and stands out as one of the best coding and agent models – with the right speed and cost to power free products and scaled sub-agents.
        /// </summary>
        public static readonly FoundryModel ClaudeHaiku45 = new() { Name = "claude-haiku-4-5", Version = "20251001", Format = "Anthropic" };

        /// <summary>
        /// Claude Opus 4.1 is an industry leader for coding. It delivers sustained performance on long-running tasks that require focused effort and thousands of steps, significantly expanding what AI agents can solve.
        /// </summary>
        public static readonly FoundryModel ClaudeOpus41 = new() { Name = "claude-opus-4-1", Version = "20250805", Format = "Anthropic" };

        /// <summary>
        /// Claude Opus 4.5 is Anthropic’s most intelligent model, and an industry leader across coding, agents, computer use, and enterprise workflows. With a 200K token context window and 64K max output, Opus 4.5 is ideal for production code, sophisticated agents, o
        /// </summary>
        public static readonly FoundryModel ClaudeOpus45 = new() { Name = "claude-opus-4-5", Version = "20251101", Format = "Anthropic" };

        /// <summary>
        /// Claude Opus 4.6 is the latest version of Anthropic's most intelligent model, and the world's best model for coding, enterprise agents, and professional work. With a 1M token context window (beta) and 128K max output, Opus 4.6 is ideal for production code,
        /// </summary>
        public static readonly FoundryModel ClaudeOpus46 = new() { Name = "claude-opus-4-6", Version = "1", Format = "Anthropic" };

        /// <summary>
        /// Claude Sonnet 4.5 is Anthropic's most capable model for complex agents and an industry leader for coding and computer use.
        /// </summary>
        public static readonly FoundryModel ClaudeSonnet45 = new() { Name = "claude-sonnet-4-5", Version = "20250929", Format = "Anthropic" };

        /// <summary>
        /// Claude Sonnet 4.6 delivers frontier intelligence at scale—built for coding, agents, and enterprise workflows. With a 1M token context window (beta) and 128K max output, Sonnet 4.6 is ideal for coding, agents, office tasks, financial analysis, cybersecurity
        /// </summary>
        public static readonly FoundryModel ClaudeSonnet46 = new() { Name = "claude-sonnet-4-6", Version = "1", Format = "Anthropic" };
    }

    /// <summary>
    /// Models published by Black Forest Labs.
    /// </summary>
    public static partial class BlackForestLabs
    {
        /// <summary>
        /// Generate images with amazing image quality, prompt adherence, and diversity at blazing fast speeds. FLUX1.1 [pro] delivers six times faster image generation and achieved the highest Elo score on Artificial Analysis benchmarks when launched, surpassing all
        /// </summary>
        public static readonly FoundryModel Flux11Pro = new() { Name = "FLUX-1.1-pro", Version = "1", Format = "Black Forest Labs" };

        /// <summary>
        /// Generate and edit images through both text and image prompts. FLUX.1 Kontext is a multimodal flow matching model that enables both text-to-image generation and in-context image editing. Modify images while maintaining character consistency and performing l
        /// </summary>
        public static readonly FoundryModel Flux1KontextPro = new() { Name = "FLUX.1-Kontext-pro", Version = "1", Format = "Black Forest Labs" };

        /// <summary>
        /// Professional text-to-image generation with adjustable inference steps and guidance scale for fine-tuned control. Features superior typography and text rendering, supports up to 4MP resolution, 32K prompt tokens, and up to 8 reference images. Ideal for deve
        /// </summary>
        public static readonly FoundryModel Flux2Flex = new() { Name = "FLUX.2-flex", Version = "1", Format = "Black Forest Labs" };

        /// <summary>
        /// Generate and edit images through both text and image prompts. FLUX.2-pro is a multimodal flow matching model that enables both text-to-image generation and in-context image editing. Modify images while maintaining character consistency and performing local
        /// </summary>
        public static readonly FoundryModel Flux2Pro = new() { Name = "FLUX.2-pro", Version = "1", Format = "Black Forest Labs" };
    }

    /// <summary>
    /// Models published by Cohere.
    /// </summary>
    public static partial class Cohere
    {
        /// <summary>
        /// Command A is a highly efficient generative model that excels at agentic and multilingual use cases.
        /// </summary>
        public static readonly FoundryModel CohereCommandA = new() { Name = "cohere-command-a", Version = "4", Format = "Cohere" };

        /// <summary>
        /// Command R is a scalable generative model targeting RAG and Tool Use to enable production-scale AI for enterprise.
        /// </summary>
        public static readonly FoundryModel CohereCommandR = new() { Name = "Cohere-command-r", Version = "1", Format = "Cohere" };

        /// <summary>
        /// Command R is a scalable generative model targeting RAG and Tool Use to enable production-scale AI for enterprise.
        /// </summary>
        public static readonly FoundryModel CohereCommandR082024 = new() { Name = "Cohere-command-r-08-2024", Version = "1", Format = "Cohere" };

        /// <summary>
        /// Command R+ is a state-of-the-art RAG-optimized model designed to tackle enterprise-grade workloads.
        /// </summary>
        public static readonly FoundryModel CohereCommandRPlus = new() { Name = "Cohere-command-r-plus", Version = "1", Format = "Cohere" };

        /// <summary>
        /// Command R+ is a state-of-the-art RAG-optimized model designed to tackle enterprise-grade workloads.
        /// </summary>
        public static readonly FoundryModel CohereCommandRPlus082024 = new() { Name = "Cohere-command-r-plus-08-2024", Version = "1", Format = "Cohere" };

        /// <summary>
        /// Cohere Embed English is the market's leading text representation model used for semantic search, retrieval-augmented generation (RAG), classification, and clustering.
        /// </summary>
        public static readonly FoundryModel CohereEmbedV3English = new() { Name = "Cohere-embed-v3-english", Version = "1", Format = "Cohere" };

        /// <summary>
        /// Cohere Embed Multilingual is the market's leading text representation model used for semantic search, retrieval-augmented generation (RAG), classification, and clustering.
        /// </summary>
        public static readonly FoundryModel CohereEmbedV3Multilingual = new() { Name = "Cohere-embed-v3-multilingual", Version = "1", Format = "Cohere" };

        /// <summary>
        /// Rerank improves search systems by sorting documents based on their semantic similarity to a query
        /// </summary>
        public static readonly FoundryModel CohereRerankV40Fast = new() { Name = "Cohere-rerank-v4.0-fast", Version = "2", Format = "Cohere" };

        /// <summary>
        /// Rerank improves search systems by sorting documents based on their semantic similarity to a query
        /// </summary>
        public static readonly FoundryModel CohereRerankV40Pro = new() { Name = "Cohere-rerank-v4.0-pro", Version = "1", Format = "Cohere" };

        /// <summary>
        /// Embed 4 transforms texts and images into numerical vectors
        /// </summary>
        public static readonly FoundryModel EmbedV40 = new() { Name = "embed-v-4-0", Version = "6", Format = "Cohere" };
    }

    /// <summary>
    /// Models published by DeepSeek.
    /// </summary>
    public static partial class DeepSeek
    {
        /// <summary>
        /// DeepSeek-R1 excels at reasoning tasks using a step-by-step training process, such as language, scientific reasoning, and coding tasks.
        /// </summary>
        public static readonly FoundryModel DeepSeekR1 = new() { Name = "DeepSeek-R1", Version = "1", Format = "DeepSeek" };

        /// <summary>
        /// The DeepSeek R1 0528 model has improved reasoning capabilities, this version also offers a reduced hallucination rate, enhanced support for function calling, and better experience for vibe coding.
        /// </summary>
        public static readonly FoundryModel DeepSeekR10528 = new() { Name = "DeepSeek-R1-0528", Version = "1", Format = "DeepSeek" };

        /// <summary>
        /// A strong Mixture-of-Experts (MoE) language model with 671B total parameters with 37B activated for each token.
        /// </summary>
        public static readonly FoundryModel DeepSeekV3 = new() { Name = "DeepSeek-V3", Version = "1", Format = "DeepSeek" };

        /// <summary>
        /// DeepSeek-V3-0324 demonstrates notable improvements over its predecessor, DeepSeek-V3, in several key aspects, including enhanced reasoning, improved function calling, and superior code generation capabilities.
        /// </summary>
        public static readonly FoundryModel DeepSeekV30324 = new() { Name = "DeepSeek-V3-0324", Version = "1", Format = "DeepSeek" };

        /// <summary>
        /// DeepSeek-V3.1 is a hybrid model that enhances tool usage, thinking efficiency, and supports both thinking and non-thinking modes via chat template switching
        /// </summary>
        public static readonly FoundryModel DeepSeekV31 = new() { Name = "DeepSeek-V3.1", Version = "1", Format = "DeepSeek" };

        /// <summary>
        /// DeepSeek-V3.2, a model that harmonizes high computational efficiency with superior reasoning and agent performance
        /// </summary>
        public static readonly FoundryModel DeepSeekV32 = new() { Name = "DeepSeek-V3.2", Version = "1", Format = "DeepSeek" };

        /// <summary>
        /// DeepSeek-V3.2 Speciale, a model that harmonizes high computational efficiency with superior reasoning and agent performance
        /// </summary>
        public static readonly FoundryModel DeepSeekV32Speciale = new() { Name = "DeepSeek-V3.2-Speciale", Version = "1", Format = "DeepSeek" };
    }

    /// <summary>
    /// Models published by Meta.
    /// </summary>
    public static partial class Meta
    {
        /// <summary>
        /// Excels in image reasoning capabilities on high-res images for visual understanding apps.
        /// </summary>
        public static readonly FoundryModel Llama3211BVisionInstruct = new() { Name = "Llama-3.2-11B-Vision-Instruct", Version = "6", Format = "Meta" };

        /// <summary>
        /// Advanced image reasoning capabilities for visual understanding agentic apps.
        /// </summary>
        public static readonly FoundryModel Llama3290BVisionInstruct = new() { Name = "Llama-3.2-90B-Vision-Instruct", Version = "5", Format = "Meta" };

        /// <summary>
        /// Llama 3.3 70B Instruct offers enhanced reasoning, math, and instruction following with performance comparable to Llama 3.1 405B.
        /// </summary>
        public static readonly FoundryModel Llama3370BInstruct = new() { Name = "Llama-3.3-70B-Instruct", Version = "9", Format = "Meta" };

        /// <summary>
        /// Llama 4 Maverick 17B 128E Instruct FP8 is great at precise image understanding and creative writing, offering high quality at a lower price compared to Llama 3.3 70B
        /// </summary>
        public static readonly FoundryModel Llama4Maverick17B128EInstructFP8 = new() { Name = "Llama-4-Maverick-17B-128E-Instruct-FP8", Version = "3", Format = "Meta" };

        /// <summary>
        /// Llama 4 Scout 17B 16E Instruct is great at multi-document summarization, parsing extensive user activity for personalized tasks, and reasoning over vast codebases.
        /// </summary>
        public static readonly FoundryModel Llama4Scout17B16EInstruct = new() { Name = "Llama-4-Scout-17B-16E-Instruct", Version = "2", Format = "Meta" };

        /// <summary>
        /// A powerful 70-billion parameter model excelling in reasoning, coding, and broad language applications.
        /// </summary>
        public static readonly FoundryModel MetaLlama370BInstruct = new() { Name = "Meta-Llama-3-70B-Instruct", Version = "9", Format = "Meta" };

        /// <summary>
        /// A versatile 8-billion parameter model optimized for dialogue and text generation tasks.
        /// </summary>
        public static readonly FoundryModel MetaLlama38BInstruct = new() { Name = "Meta-Llama-3-8B-Instruct", Version = "9", Format = "Meta" };

        /// <summary>
        /// The Llama 3.1 instruction tuned text only models are optimized for multilingual dialogue use cases and outperform many of the available open source and closed chat models on common industry benchmarks.
        /// </summary>
        public static readonly FoundryModel MetaLlama31405BInstruct = new() { Name = "Meta-Llama-3.1-405B-Instruct", Version = "1", Format = "Meta" };

        /// <summary>
        /// The Llama 3.1 instruction tuned text only models are optimized for multilingual dialogue use cases and outperform many of the available open source and closed chat models on common industry benchmarks.
        /// </summary>
        public static readonly FoundryModel MetaLlama3170BInstruct = new() { Name = "Meta-Llama-3.1-70B-Instruct", Version = "4", Format = "Meta" };

        /// <summary>
        /// The Llama 3.1 instruction tuned text only models are optimized for multilingual dialogue use cases and outperform many of the available open source and closed chat models on common industry benchmarks.
        /// </summary>
        public static readonly FoundryModel MetaLlama318BInstruct = new() { Name = "Meta-Llama-3.1-8B-Instruct", Version = "6", Format = "Meta" };
    }

    /// <summary>
    /// Models published by Microsoft.
    /// </summary>
    public static partial class Microsoft
    {
        /// <summary>
        ///   <para>
        ///     <b>Azure AI Content Safety</b>
        ///   </para>
        ///   <para>
        ///     <b>Introduction</b>
        ///   </para>
        ///   <para>Azure AI Content Safety is a safety system for monitoring content generated by both foundation models and humans. Detect and block potential risks, threats, and quality problems. You can build an advanced safety system for foundation models to detect and mitigate harmful content and risks in user prompts and AI-generated outputs. Use Prompt Shields to detect and block prompt injection attacks, groundedness detection to pinpoint ungrounded or hallucinated materials, and protected material detection to identify copyrighted or owned content.</para>
        ///   <para>
        ///     <b>Core Features</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Block harmful input and output</b>
        ///         </para>
        ///         <list type="bullet">
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Description</b>: Detect and block violence, hate, sexual, and self-harm content for both text, images and multimodal. Configure severity thresholds for your specific use case and adhere to your responsible AI policies.</para>
        ///             </description>
        ///           </item>
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Key Features</b>: Violence, hate, sexual, and self-harm content detection. Custom blocklist.</para>
        ///             </description>
        ///           </item>
        ///         </list>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Policy customization with custom categories</b>
        ///         </para>
        ///         <list type="bullet">
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Description</b>: Create unique content filters tailored to your requirements using custom categories. Quickly train a new custom category by providing examples of content you need to block.</para>
        ///             </description>
        ///           </item>
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Key Features</b>: Custom categories</para>
        ///             </description>
        ///           </item>
        ///         </list>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Identify the security risks</b>
        ///         </para>
        ///         <list type="bullet">
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Description</b>: Safeguard your AI applications against prompt injection attacks and jailbreak attempts. Identify and mitigate both direct and indirect threats with prompt shields.</para>
        ///             </description>
        ///           </item>
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Key Features</b>: Direct jailbreak attack, indirect prompt injection from docs.</para>
        ///             </description>
        ///           </item>
        ///         </list>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Detect and correct Gen AI hallucinations</b>
        ///         </para>
        ///         <list type="bullet">
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Description</b>: Identify and correct generative AI hallucinations and ensure outputs are reliable, accurate, and grounded in data with groundedness detection.</para>
        ///             </description>
        ///           </item>
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Key Features</b>: Groundedness detection, reasoning, and correction.</para>
        ///             </description>
        ///           </item>
        ///         </list>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Identify protected material</b>
        ///         </para>
        ///         <list type="bullet">
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Description</b>: Pinpoint copyrighted content and provide sources for preexisting text and code with protected material detection.</para>
        ///             </description>
        ///           </item>
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Key Features</b>: Protected material for code, protected material for text</para>
        ///             </description>
        ///           </item>
        ///         </list>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Use Cases</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Generative AI services screen user-submitted prompts and generated outputs to ensure safe and appropriate content.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Online marketplaces monitor and filter product listings and other user-generated content to prevent harmful or inappropriate material.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Gaming platforms manage and moderate user-created game content and in-game communication to maintain a safe environment.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Social media platforms review and regulate user-uploaded images and posts to enforce community standards and prevent harmful content.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Enterprise media companies implement centralized content moderation systems to ensure the safety and appropriateness of their published materials.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>K-12 educational technology providers filter out potentially harmful or inappropriate content to create a safe learning environment for students and educators.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Benefits</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>No ML experience required</b>: Incorporate content safety features into your projects with no machine learning experience required.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Effortlessly customize your RAI policies</b>: Customizing your content safety classifiers can be done with one line of description, a few samples using Custom Categories.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>State of the art models</b>: ready for use APIs, SOTA models, and flexible deployment options reduce the need for ongoing manual training or extensive customization. Microsoft has a science team and policy experts working on the frontier of Gen AI to constantly improve the safety and security models to ensure our customers can develop and deploy generative AI safely and responsibly.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Global Reach</b>: Support more than 100 languages, enabling businesses to communicate effectively with customers, partners, and employees worldwide.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Scalable and Reliable</b>: Built on Azure’s cloud infrastructure, the Azure AI Content Safety service scales automatically to meet demand, from small business applications to global enterprise workloads.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Security and Compliance</b>: Azure AI Content Safety runs on Azure’s secure cloud infrastructure, ensuring data privacy and compliance with global standards. User data is not stored after the translation process.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Flexible deployment</b>: Azure AI Content Safety can be deployed on cloud, on premises and on devices.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Technical Details</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Deployment</b>
        ///         </para>
        ///         <list type="bullet">
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Container for on-premise deployment</b>: <see href="https://learn.microsoft.com/azure/ai-services/content-safety/how-to/containers/container-overview">Content safety containers overview - Azure AI Content Safety - Azure AI services | Microsoft Learn</see></para>
        ///             </description>
        ///           </item>
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Embedded Content Safety</b>: <see href="https://learn.microsoft.com/azure/ai-services/content-safety/how-to/embedded-content-safety?tabs=windows-target%2Ctext">Embedded Content Safety - Azure AI Content Safety - Azure AI services | Microsoft Learn</see></para>
        ///             </description>
        ///           </item>
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Cloud</b>: <see href="https://learn.microsoft.com/azure/ai-services/content-safety/">Azure AI Content Safety documentation - Quickstarts, Tutorials, API Reference - Azure AI services | Microsoft Learn</see></para>
        ///             </description>
        ///           </item>
        ///         </list>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Requirements</b>: Requirements vary feature by feature, for more details, refer to the Azure AI Content Safety documentation: <see href="https://learn.microsoft.com/azure/ai-services/content-safety/">Azure AI Content Safety documentation - Quickstarts, Tutorials, API Reference - Azure AI services | Microsoft Learn</see>.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Support</b>: Azure AI Content Safety is part of Azure AI Services. Support options for AI Services can be found here: <see href="https://learn.microsoft.com/azure/ai-services/cognitive-services-support-options?context=%2Fazure%2Fai-services%2Fcontent-safety%2Fcontext%2Fcontext">Azure AI services support and help options - Azure AI services | Microsoft Learn</see>.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>Explore pricing options here: <see href="https://azure.microsoft.com/pricing/details/cognitive-services/content-safety/">Azure AI Content Safety - Pricing | Microsoft Azure</see>.</para>
        /// </summary>
        public static readonly FoundryModel AzureAIContentSafety = new() { Name = "Azure-AI-Content-Safety", Version = "1", Format = "Microsoft" };

        /// <summary>
        ///   <para>
        ///     <b>Azure AI Content Understanding</b>
        ///   </para>
        ///   <para>
        ///     <b>Introduction</b>
        ///   </para>
        ///   <para>Azure AI Content Understanding empowers you to transform unstructured multimodal data—such as text, images, audio, and video—into structured, actionable insights. By streamlining content processing with advanced AI techniques like schema extraction and grounding, it delivers accurate structured data for downstream applications. Offering prebuilt templates for common use cases and customizable models, it helps you unify diverse data types into a single, efficient pipeline, optimizing workflows and accelerating time to value.</para>
        ///   <para>
        ///     <b>Core Features</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Multimodal data ingestion</b>
        ///           <br />Ingest a range of modalities such as documents, images, audio, or video. Use a variety of AI models to convert the input data into a structured format that can be easily processed and analyzed by downstream services or applications.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Customizable output schemas</b>
        ///           <br />Customize the schemas of extracted results to meet your specific needs. Tailor the format and structure of summaries, insights, or features to include only the most relevant details—such as key points or timestamps—from video or audio files.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Confidence scores</b> Leverage confidence scores to minimize human intervention and continuously improve accuracy through user feedback.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Output ready for downstream applications</b>
        ///           <br />Automate business processes by building enterprise AI apps or agentic workflows. Use outputs that downstream applications can consume for reasoning with retrieval-augmented generation (RAG).</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Grounding</b> Ensure the information extracted, inferred, or abstracted is represented in the underlying content.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Automatic labeling</b> Save time and effort on manual annotation and create models quicker by using large language models (LLMs) to extract fields from various document types.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Use Cases</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Post-call analytics for call centers</b>: Generate insights from call recordings, track key performance indicators (KPIs), and answer customer questions more accurately and efficiently.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Tax process automation</b>: Streamline the tax return process by extracting data from tax forms to create a consolidated view of information across various documents.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Media asset management</b>: Extract features from images and videos to provide richer tools for targeted content and enhance media asset management solutions.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Chart understanding</b>: Enhance chart understanding by automating the analysis and interpretation of various types of charts and diagrams using Content Understanding.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Benefits</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Streamline workflows</b>: Azure AI Content Understanding standardizes the extraction of content, structure, and insights from various content types into a unified process.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Simplify field extraction</b>: Field extraction in Content Understanding makes it easier to generate structured output from unstructured content. Define a schema to extract, classify, or generate field values with no complex prompt engineering.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Enhance accuracy</b>: Content Understanding employs multiple AI models to analyze and cross-validate information simultaneously, resulting in more accurate and reliable results.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Confidence scores &amp; grounding</b>: Content Understanding ensures the accuracy of extracted values while minimizing the cost of human review.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Technical Details</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Deployment</b>: Deployment options may vary by service, reference the following docs for more information: <see href="https://learn.microsoft.com/en-us/azure/ai-services/content-understanding/how-to/create-multi-service-resource">Create a Microsoft Foundry resource</see>.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Requirements</b>: Requirements may vary depending on the input data you are analyzing, reference the following docs for more information: <see href="https://learn.microsoft.com/en-us/azure/ai-services/content-understanding/service-limits">Service quotas and limits</see>.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Support</b>: Support options for AI Services can be found here: <see href="https://learn.microsoft.com/en-us/azure/ai-services/cognitive-services-support-options?view=doc-intel-4.0.0">Azure AI services support and help options</see>.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>View up-to-date pay-as-you-go pricing details here: <see href="https://azure.microsoft.com/en-us/pricing/details/content-understanding/">Azure AI Content Understanding pricing</see>.</para>
        /// </summary>
        public static readonly FoundryModel AzureAIContentUnderstanding = new() { Name = "Azure-AI-Content-Understanding", Version = "1", Format = "Microsoft" };

        /// <summary>
        ///   <para>
        ///     <b>Azure AI Document Intelligence</b>
        ///   </para>
        ///   <para>Document Intelligence is a cloud-based service that enables you to build intelligent document processing solutions. Massive amounts of data, spanning a wide variety of data types, are stored in forms and documents. Document Intelligence enables you to effectively manage the velocity at which data is collected and processed and is key to improved operations, informed data-driven decisions, and enlightened innovation.</para>
        ///   <para>
        ///     <b>Core Features</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>General extraction models</b>
        ///         </para>
        ///         <list type="bullet">
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Description</b>: General extraction models enable text extraction from forms and documents and return structured business-ready content ready for your organization's action, use, or development.</para>
        ///             </description>
        ///           </item>
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Key Features</b>
        ///               </para>
        ///               <list type="bullet">
        ///                 <item>
        ///                   <description>
        ///                     <para>Read model allows you to extract written or printed text liens, words, locations, and detected languages.</para>
        ///                   </description>
        ///                 </item>
        ///                 <item>
        ///                   <description>
        ///                     <para>Layout model, on top of text extraction, extracts structural information like tables, selection marks, paragraphs, titles, headings, and subheadings. Layout model can also output the extraction results in a Markdown format, enabling you to define your semantic chunking strategy based on provided building blocks, allowing for easier RAG (Retrieval Augmented Generation).</para>
        ///                   </description>
        ///                 </item>
        ///               </list>
        ///             </description>
        ///           </item>
        ///         </list>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Prebuilt models</b>
        ///         </para>
        ///         <list type="bullet">
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Description</b>: Prebuilt models enable you to add intelligent document processing to your apps and flows without having to train and build your own models. Prebuilt models extract a pre-defined set of fields depending on the document type.</para>
        ///             </description>
        ///           </item>
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Key Features</b>
        ///               </para>
        ///               <list type="bullet">
        ///                 <item>
        ///                   <description>
        ///                     <para>
        ///                       <b>Financial Services and Legal Documents</b>: Credit Cards, Bank Statement, Pay Slip, Check, Invoices, Receipts, Contracts.</para>
        ///                   </description>
        ///                 </item>
        ///                 <item>
        ///                   <description>
        ///                     <para>
        ///                       <b>US Tax Documents</b>: Unified Tax, W-2, 1099 Combo, 1040 (multiple variations), 1098 (multiple variations), 1099 (multiple variations).</para>
        ///                   </description>
        ///                 </item>
        ///                 <item>
        ///                   <description>
        ///                     <para>
        ///                       <b>US Mortgage Documents</b>: 1003, 1004, 1005, 1008, Closing Disclosure.</para>
        ///                   </description>
        ///                 </item>
        ///                 <item>
        ///                   <description>
        ///                     <para>
        ///                       <b>Personal Identification Documents</b>: Identity Documents, Health Insurance Cards, Marriage Certificates.</para>
        ///                   </description>
        ///                 </item>
        ///               </list>
        ///             </description>
        ///           </item>
        ///         </list>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Custom models</b>
        ///         </para>
        ///         <list type="bullet">
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Description</b>: Custom models are trained using your labeled datasets to extract distinct data from forms and documents, specific to your use cases. Standalone custom models can be combined to create composed models.</para>
        ///             </description>
        ///           </item>
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Key Features</b>
        ///               </para>
        ///               <list type="bullet">
        ///                 <item>
        ///                   <description>
        ///                     <para>
        ///                       <b>Document field extraction models</b>
        ///                     </para>
        ///                     <list type="bullet">
        ///                       <item>
        ///                         <description>
        ///                           <para>
        ///                             <b>Custom generative</b>: Build a custom extraction model using generative AI for documents with unstructured format and varying templates.</para>
        ///                         </description>
        ///                       </item>
        ///                       <item>
        ///                         <description>
        ///                           <para>
        ///                             <b>Custom neural</b>: Extract data from mixed-type documents.</para>
        ///                         </description>
        ///                       </item>
        ///                       <item>
        ///                         <description>
        ///                           <para>
        ///                             <b>Custom template</b>: Extract data from static layouts.</para>
        ///                         </description>
        ///                       </item>
        ///                       <item>
        ///                         <description>
        ///                           <para>
        ///                             <b>Custom composed</b>: Extract data using a collection of models. Explicitly choose the classifier and enable confidence-based routing based on the threshold you set.</para>
        ///                         </description>
        ///                       </item>
        ///                     </list>
        ///                   </description>
        ///                 </item>
        ///                 <item>
        ///                   <description>
        ///                     <para>
        ///                       <b>Custom classification models</b>
        ///                     </para>
        ///                     <list type="bullet">
        ///                       <item>
        ///                         <description>
        ///                           <para>
        ///                             <b>Custom classifier</b>: Identify designated document types (classes) before invoking an extraction model.</para>
        ///                         </description>
        ///                       </item>
        ///                     </list>
        ///                   </description>
        ///                 </item>
        ///               </list>
        ///             </description>
        ///           </item>
        ///         </list>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Add-on capabilities</b>
        ///         </para>
        ///         <list type="bullet">
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Description</b>: Use the add-on features to extend the results to include more features extracted from your documents. Some add-on features incur an extra cost. These optional features can be enabled and disabled depending on the scenario of the document extraction.</para>
        ///             </description>
        ///           </item>
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Key Features</b>
        ///               </para>
        ///               <list type="bullet">
        ///                 <item>
        ///                   <description>
        ///                     <para>High resolution extraction</para>
        ///                   </description>
        ///                 </item>
        ///                 <item>
        ///                   <description>
        ///                     <para>Formula extraction</para>
        ///                   </description>
        ///                 </item>
        ///                 <item>
        ///                   <description>
        ///                     <para>Font extraction</para>
        ///                   </description>
        ///                 </item>
        ///                 <item>
        ///                   <description>
        ///                     <para>Barcode extraction</para>
        ///                   </description>
        ///                 </item>
        ///                 <item>
        ///                   <description>
        ///                     <para>Language detection</para>
        ///                   </description>
        ///                 </item>
        ///                 <item>
        ///                   <description>
        ///                     <para>Searchable PDF output</para>
        ///                   </description>
        ///                 </item>
        ///               </list>
        ///             </description>
        ///           </item>
        ///         </list>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Use Cases</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Accounts payable</b>: A company can increase the efficiency of its accounts payable clerks by using the prebuilt invoice model and custom forms to speed up invoice data entry with a human in the loop. The prebuilt invoice model can extract key fields, such as Invoice Total and Shipping Address.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Insurance form processing</b>: A customer can train a model by using custom forms to extract a key-value pair in insurance forms and then feeds the data to their business flow to improve the accuracy and efficiency of their process. For their unique forms, customers can build their own model that extracts key values by using custom forms. These extracted values then become actionable data for various workflows within their business.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Bank form processing</b>: A bank can use the prebuilt ID model and custom forms to speed up the data entry for "know your customer" documentation, or to speed up data entry for a mortgage packet. If a bank requires their customers to submit personal identification as part of a process, the prebuilt ID model can extract key values, such as Name and Document Number, speeding up the overall time for data entry.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Robotic process automation (RPA)</b>: Using the custom extraction model, customers can extract specific data needed from distinct types of documents. The key-value pair extracted can then be entered into various systems such as databases, or CRM systems, through RPA, replacing manual data entry. Customers can also use custom classification model to categorize documents based on their content and file them in proper location. As such, an organized set of data extracted from the custom model can be an essential first step to document RPA scenarios for businesses that manage large volumes of documents regularly.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Benefits</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>No experience required</b>: Incorporate Document Intelligence features into your projects with no machine learning experience required.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Effortlessly customize your models</b>: Training your own custom extraction and classification model can be done with as little as one document labeled, making it easy to train your own models.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>State of the art models</b>: ready for use APIs, constantly enhanced models, and flexible deployment options reduce the need for ongoing manual training or extensive customization.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Technical Details:</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Deployment</b>: Deployment options may vary by service, reference the following docs for more information: <see href="https://learn.microsoft.com/azure/ai-services/document-intelligence/how-to-guides/use-sdk-rest-api?view=doc-intel-3.1.0&amp;tabs=linux&amp;pivots=programming-language-rest-api">Use Document Intelligence models</see> and <see href="https://learn.microsoft.com/azure/ai-services/document-intelligence/containers/install-run?view=doc-intel-4.0.0&amp;tabs=read">Install and run containers</see>.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Requirements</b>: Requirements may vary slightly depending on the model you are using to analyze the documents. Reference the following docs for more information: <see href="https://learn.microsoft.com/azure/ai-services/document-intelligence/service-limits?view=doc-intel-4.0.0">Service quotas and limits</see>.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Support</b>: Support options for AI Services can be found here: <see href="https://learn.microsoft.com/azure/ai-services/cognitive-services-support-options?context=%2Fazure%2Fai-services%2Fdocument-intelligence%2Fcontext%2Fcontext&amp;view=doc-intel-4.0.0">Azure AI services support and help options - Azure AI services | Microsoft Learn</see>.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>View up-to-date pricing information for the pay-as-you-go pricing model here: <see href="https://azure.microsoft.com/pricing/details/ai-document-intelligence/">Azure AI Document Intelligence pricing</see>.</para>
        /// </summary>
        public static readonly FoundryModel AzureAIDocumentIntelligence = new() { Name = "Azure-AI-Document-Intelligence", Version = "1", Format = "Microsoft" };

        /// <summary>
        ///   <para>
        ///     <b>Azure AI Vision</b>
        ///   </para>
        ///   <para>
        ///     <b>Introduction</b>
        ///   </para>
        ///   <para>The Azure AI Vision service gives you access to advanced algorithms that process images and videos and return insights based on the visual features and content you are interested in. Azure AI Vision can power a diverse set of scenarios, including digital asset management, video content search &amp; summary, identity verification, generating accessible alt-text for images, and many more. The key product categories for Azure AI Vision include Video Analysis, Image Analysis, Face, and Optical Character Recognition.</para>
        ///   <para>
        ///     <b>Core Features</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Video analysis</b>
        ///         </para>
        ///         <list type="bullet">
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Description</b>: Video Analysis includes video-related features like Spatial Analysis and Video Retrieval. Spatial Analysis analyzes the presence and movement of people on a video feed and produces events that other systems can respond to. Video Retrieval lets you create an index of videos that you can search in your natural language.</para>
        ///             </description>
        ///           </item>
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Key Features</b>: Video retrieval, spatial analysis, person counting, person in a zone, person crossing a line, person distance</para>
        ///             </description>
        ///           </item>
        ///         </list>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Face</b>
        ///         </para>
        ///         <list type="bullet">
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Description</b>: The Face service provides AI algorithms that detect, recognize, and analyze human faces in images. Facial recognition software is important in many different scenarios, such as identification, touchless access control, and face blurring for privacy.</para>
        ///             </description>
        ///           </item>
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Key Features</b>: Face detection and analysis, face liveness, face identification, face verification</para>
        ///             </description>
        ///           </item>
        ///         </list>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Image analysis</b>
        ///         </para>
        ///         <list type="bullet">
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Description</b>: The Image Analysis service extracts many visual features from images, such as objects, faces, adult content, and auto-generated text descriptions.</para>
        ///             </description>
        ///           </item>
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Key Features</b>: Image tagging, image classification, object detection, image captioning, dense captioning, face detection, optical character recognition, image embeddings, and image search</para>
        ///             </description>
        ///           </item>
        ///         </list>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Optical character recognition</b>
        ///         </para>
        ///         <list type="bullet">
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Description</b>: The Optical Character Recognition (OCR) service extracts text from images. You can use the Read API to extract printed and handwritten text from photos and documents. It uses deep-learning-based models and works with text on various surfaces and backgrounds. These include business documents, invoices, receipts, posters, business cards, letters, and whiteboards. The OCR APIs support extracting printed text in several languages.</para>
        ///             </description>
        ///           </item>
        ///           <item>
        ///             <description>
        ///               <para>
        ///                 <b>Key Features</b>: OCR</para>
        ///             </description>
        ///           </item>
        ///         </list>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Use Cases</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Boost content discovery with image analysis</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Verify identities with the Face service</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Search content in videos</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Benefits</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>No experience required</b>: Incorporate vision features into your projects with no machine learning experience required.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Effortlessly customize your models</b>: Customizing your image classification and object detection models can be done with as little as one image per tag, making it easy to train your own models.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>State of the art models</b>: Ready to use APIs, constantly enhanced models, and flexible deployment options reduce the need for ongoing manual training or extensive customization.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Technical Details</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Deployment</b>: Deployment options may vary by service, reference the following docs for more information: <see href="https://learn.microsoft.com/azure/ai-services/computer-vision/overview-image-analysis?tabs=4-0">Image Analysis Overview</see>, <see href="https://learn.microsoft.com/azure/ai-services/computer-vision/overview-ocr">Optical Character Recognition Overview</see>, <see href="https://learn.microsoft.com/azure/ai-services/computer-vision/intro-to-spatial-analysis-public-preview?tabs=sa">Video Analysis Overview</see>, and <see href="https://learn.microsoft.com/azure/ai-services/computer-vision/overview-identity">Face Overview</see>.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Requirements</b>: Requirements may very slightly depending on the data you are analyzing, reference the following docs for more information: <see href="https://learn.microsoft.com/azure/ai-services/computer-vision/overview-image-analysis?tabs=4-0">Image Analysis Overview</see>, <see href="https://learn.microsoft.com/azure/ai-services/computer-vision/overview-ocr">Optical Character Recognition Overview</see>, <see href="https://learn.microsoft.com/azure/ai-services/computer-vision/intro-to-spatial-analysis-public-preview?tabs=sa">Video Analysis Overview</see>, and <see href="https://learn.microsoft.com/azure/ai-services/computer-vision/overview-identity">Face Overview</see>.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Support</b>: Support options for AI Services can be found here: <see href="https://learn.microsoft.com/azure/ai-services/cognitive-services-support-options?context=%2Fazure%2Fai-services%2Fcomputer-vision%2Fcontext%2Fcontext">Azure AI services support and help options - Azure AI services | Microsoft Learn</see>.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>View up-to-date pricing information for the pay-as-you-go pricing model here: <see href="https://azure.microsoft.com/pricing/details/cognitive-services/computer-vision">Azure AI Vision pricing</see>.</para>
        /// </summary>
        public static readonly FoundryModel AzureAIVision = new() { Name = "Azure-AI-Vision", Version = "1", Format = "Microsoft" };

        /// <summary>
        ///   <para>
        ///     <b>Azure Content Understanding - Layout</b>
        ///   </para>
        ///   <para>Content Understanding Layout offers rich, structure‑aware extraction that captures text, formatting, tables, figures, and geometric layout details. It’s designed for complex document understanding workflows that require positional accuracy and deeper structural insights.</para>
        ///   <para>
        ///     <b>Azure Content Understanding</b>
        ///   </para>
        ///   <para>Azure Content Understanding uses generative AI to process/ingest content of many types (documents, images, videos, and audio) into a user-defined output format. It offers a streamlined process to reason over large amounts of unstructured data, accelerating time-to-value by generating an output that can be integrated into automation and analytical workflows.</para>
        ///   <para>
        ///     <b>Key capabilities</b>
        ///   </para>
        ///   <para>
        ///     <b>About this model</b>
        ///   </para>
        ///   <para>The <b>Layout</b> model offers rich, structure‑aware analysis for documents that require deeper understanding of formatting, hierarchy, and spatial relationships. It combines textual extraction with geometric layout detection to support advanced automation and content reasoning.</para>
        ///   <para>
        ///     <b>Key model capabilities</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Extracts detailed content and layout elements such as <b>words</b>, <b>paragraphs</b>, <b>tables</b>, <b>figures</b>, and <b>sections</b></para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Identifies <b>document structure</b>, formatting patterns, and hierarchical organization</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Extracts <b>hyperlinks</b> embedded in documents</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Captures <b>annotations</b> such as highlights, underlines, and strikethroughs in digital PDFs</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Provides <b>precise positional information</b> for all extracted elements</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Detects all figure types—<b>charts</b>, <b>diagrams</b>, <b>pictures</b>, <b>icons</b>, and other images—with bounding box details (<b>PDF only</b>)</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Suitable for advanced workflows such as <b>document automation</b>, <b>RAG indexing</b>, <b>semantic</b> search, and any process demanding fine‑grained layout understanding</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>View up-to-date pay-as-you-go pricing details here: <see href="https://azure.microsoft.com/en-us/pricing/details/content-understanding/">Azure AI Content Understanding pricing</see>.</para>
        ///   <para>
        ///     <b>Technical details</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Deployment</b>: Deployment options may vary by service, reference the following docs for more information: <see href="https://learn.microsoft.com/en-us/azure/ai-services/content-understanding/how-to/create-multi-service-resource">Create a Microsoft Foundry resource</see>.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Requirements</b>: Requirements may vary depending on the input data you are analyzing, reference the following docs for more information: <see href="https://learn.microsoft.com/en-us/azure/ai-services/content-understanding/service-limits">Service quotas and limits</see>.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Support</b>: Support options for AI Services can be found here: <see href="https://learn.microsoft.com/en-us/azure/ai-services/cognitive-services-support-options?view=doc-intel-4.0.0">Azure AI services support and help options</see>.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>More information</b>
        ///   </para>
        ///   <para>Learn more in the full <see href="https://aka.ms/content-understanding-doc">Azure AI Content Understanding documentation</see>.</para>
        /// </summary>
        public static readonly FoundryModel AzureContentUnderstandingLayout = new() { Name = "Azure-Content-Understanding-Layout", Version = "1", Format = "Microsoft" };

        /// <summary>
        ///   <para>
        ///     <b>Azure Content Understanding - Read</b>
        ///   </para>
        ///   <para>Content Understanding Read provides fast, reliable extraction of text and basic content elements from documents, enabling simple ingestion workflows without layout interpretation. It’s ideal for scenarios where clean text output is needed for downstream automation, classification, or search.</para>
        ///   <para>
        ///     <b>Azure Content Understanding</b>
        ///   </para>
        ///   <para>Azure Content Understanding uses generative AI to process/ingest content of many types (documents, images, videos, and audio) into a user-defined output format. It offers a streamlined process to reason over large amounts of unstructured data, accelerating time-to-value by generating an output that can be integrated into automation and analytical workflows.</para>
        ///   <para>
        ///     <b>Key capabilities</b>
        ///   </para>
        ///   <para>
        ///     <b>About this model</b>
        ///   </para>
        ///   <para>The <b>Read</b> model provides foundational text extraction capabilities for simple, fast, and reliable ingestion of document content. It focuses on capturing textual elements without performing layout or structural analysis, making it ideal for lightweight processing and downstream text-based workflows.</para>
        ///   <para>
        ///     <b>Key model capabilities</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Extracts fundamental content elements such as <b>words</b>, <b>lines</b>, <b>paragraphs</b>, <b>formulas</b>, and <b>barcodes</b></para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Provides <b>basic OCR</b> functionality for a wide range of document types</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Returns text results <b>without layout interpretation</b></para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Best suited for scenarios requiring <b>quick ingestion</b>, metadata extraction, transcription, or feeding clean text into analytic or search pipelines</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>View up-to-date pay-as-you-go pricing details here: <see href="https://azure.microsoft.com/en-us/pricing/details/content-understanding/">Azure AI Content Understanding pricing</see>.</para>
        ///   <para>
        ///     <b>Technical details</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Deployment</b>: Deployment options may vary by service, reference the following docs for more information: <see href="https://learn.microsoft.com/en-us/azure/ai-services/content-understanding/how-to/create-multi-service-resource">Create a Microsoft Foundry resource</see>.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Requirements</b>: Requirements may vary depending on the input data you are analyzing, reference the following docs for more information: <see href="https://learn.microsoft.com/en-us/azure/ai-services/content-understanding/service-limits">Service quotas and limits</see>.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Support</b>: Support options for AI Services can be found here: <see href="https://learn.microsoft.com/en-us/azure/ai-services/cognitive-services-support-options?view=doc-intel-4.0.0">Azure AI services support and help options</see>.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>More information</b>
        ///   </para>
        ///   <para>Learn more in the full <see href="https://aka.ms/content-understanding-doc">Azure AI Content Understanding documentation</see>.</para>
        /// </summary>
        public static readonly FoundryModel AzureContentUnderstandingRead = new() { Name = "Azure-Content-Understanding-Read", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Language detection quickly and accurately identifies the language of any text, supporting over 100 languages and dialects, including the ISO 15924 standard for a select number of languages.
        /// </summary>
        public static readonly FoundryModel AzureLanguageLanguageDetection = new() { Name = "Azure-Language-Language-detection", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// PII Redaction for Text automatically detects and masks sensitive information such as names, addresses, phone numbers, credit card details, and other personally identifiable information (PII) in unstructured text.
        /// </summary>
        public static readonly FoundryModel AzureLanguageTextPiiRedaction = new() { Name = "Azure-Language-Text-PII-redaction", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Transcribes streaming or recorded audio into readable text across 140+ languages and dialects. Accuracy can be further optimized with custom models for your specialized use cases.
        /// </summary>
        public static readonly FoundryModel AzureSpeechSpeechToText = new() { Name = "Azure-Speech-Speech-to-text", Version = "1", Format = "Microsoft" };

        /// <summary>
        ///   <para>Text-to-speech enables your applications, tools, or devices to convert text into natural synthesized speech. It leverages advanced out-of-the-box [prebuilt neural voices](<see href="https://learn.microsoft.com/en-us/azure/ai-services/speech-service/language-support?t">https://learn.microsoft.com/en-us/azure/ai-services/speech-service/language-support?t</see></para>
        /// </summary>
        public static readonly FoundryModel AzureSpeechTextToSpeech = new() { Name = "Azure-Speech-Text-to-speech", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Text to speech avatar converts text into a digital video of a human (either a standard avatar or a custom text to speech avatar) speaking with a natural-sounding voice. The text to speech avatar video can be synthesized asynchronously or in real time. Deve
        /// </summary>
        public static readonly FoundryModel AzureSpeechTextToSpeechAvatar = new() { Name = "Azure-Speech-Text-to-speech-Avatar", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Voice Live API is a single unified API that enables low-latency, high-quality speech to speech interactions for voice agents.
        /// </summary>
        public static readonly FoundryModel AzureSpeechVoiceLive = new() { Name = "Azure-Speech-Voice-Live", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Document translation is a cloud-based, multilingual service that uses AI to translate documents from one language to another while preserving the document layout.
        /// </summary>
        public static readonly FoundryModel AzureTranslatorDocumentTranslation = new() { Name = "Azure-Translator-Document-translation", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Text translation is a cloud-based, multilingual service that uses neural machine translation models (NMT) and/or large language models (LLM) to translate text from one language to another, supporting 135 languages.
        /// </summary>
        public static readonly FoundryModel AzureTranslatorTextTranslation = new() { Name = "Azure-Translator-Text-translation", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// MAI-DS-R1 is a DeepSeek-R1 reasoning model that has been post-trained by the Microsoft AI team to fill in information gaps in the previous version of the model and improve its harm protections while maintaining R1 reasoning capabilities.
        /// </summary>
        public static readonly FoundryModel MaiDSR1 = new() { Name = "MAI-DS-R1", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Model router is a deployable AI model that is trained to select the most suitable large language model (LLM) for a given prompt.
        /// </summary>
        public static readonly FoundryModel ModelRouter = new() { Name = "model-router", Version = "2025-11-18", Format = "Microsoft" };

        /// <summary>
        /// Same Phi-3-medium model, but with a larger context size for RAG or few shot prompting.
        /// </summary>
        public static readonly FoundryModel Phi3Medium128kInstruct = new() { Name = "Phi-3-medium-128k-instruct", Version = "7", Format = "Microsoft" };

        /// <summary>
        /// A 14B parameters model, proves better quality than Phi-3-mini, with a focus on high-quality, reasoning-dense data.
        /// </summary>
        public static readonly FoundryModel Phi3Medium4kInstruct = new() { Name = "Phi-3-medium-4k-instruct", Version = "6", Format = "Microsoft" };

        /// <summary>
        /// Same Phi-3-mini model, but with a larger context size for RAG or few shot prompting.
        /// </summary>
        public static readonly FoundryModel Phi3Mini128kInstruct = new() { Name = "Phi-3-mini-128k-instruct", Version = "13", Format = "Microsoft" };

        /// <summary>
        /// Tiniest member of the Phi-3 family. Optimized for both quality and low latency.
        /// </summary>
        public static readonly FoundryModel Phi3Mini4kInstruct = new() { Name = "Phi-3-mini-4k-instruct", Version = "15", Format = "Microsoft" };

        /// <summary>
        /// Same Phi-3-small model, but with a larger context size for RAG or few shot prompting.
        /// </summary>
        public static readonly FoundryModel Phi3Small128kInstruct = new() { Name = "Phi-3-small-128k-instruct", Version = "5", Format = "Microsoft" };

        /// <summary>
        /// A 7B parameters model, proves better quality than Phi-3-mini, with a focus on high-quality, reasoning-dense data.
        /// </summary>
        public static readonly FoundryModel Phi3Small8kInstruct = new() { Name = "Phi-3-small-8k-instruct", Version = "6", Format = "Microsoft" };

        /// <summary>
        /// Refresh of Phi-3-mini model.
        /// </summary>
        public static readonly FoundryModel Phi35MiniInstruct = new() { Name = "Phi-3.5-mini-instruct", Version = "6", Format = "Microsoft" };

        /// <summary>
        /// A new mixture of experts model
        /// </summary>
        public static readonly FoundryModel Phi35MoEInstruct = new() { Name = "Phi-3.5-MoE-instruct", Version = "5", Format = "Microsoft" };

        /// <summary>
        /// Refresh of Phi-3-vision model.
        /// </summary>
        public static readonly FoundryModel Phi35VisionInstruct = new() { Name = "Phi-3.5-vision-instruct", Version = "2", Format = "Microsoft" };

        /// <summary>
        /// Phi-4 14B, a highly capable model for low latency scenarios.
        /// </summary>
        public static readonly FoundryModel Phi4 = new() { Name = "Phi-4", Version = "7", Format = "Microsoft" };

        /// <summary>
        /// 3.8B parameters Small Language Model outperforming larger models in reasoning, math, coding, and function-calling
        /// </summary>
        public static readonly FoundryModel Phi4MiniInstruct = new() { Name = "Phi-4-mini-instruct", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Lightweight math reasoning model optimized for multi-step problem solving
        /// </summary>
        public static readonly FoundryModel Phi4MiniReasoning = new() { Name = "Phi-4-mini-reasoning", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// First small multimodal model to have 3 modality inputs (text, audio, image), excelling in quality and efficiency
        /// </summary>
        public static readonly FoundryModel Phi4MultimodalInstruct = new() { Name = "Phi-4-multimodal-instruct", Version = "2", Format = "Microsoft" };

        /// <summary>
        /// State-of-the-art open-weight reasoning model.
        /// </summary>
        public static readonly FoundryModel Phi4Reasoning = new() { Name = "Phi-4-reasoning", Version = "1", Format = "Microsoft" };
    }

    /// <summary>
    /// Models published by Mistral AI.
    /// </summary>
    public static partial class MistralAI
    {
        /// <summary>
        /// Codestral 25.01 by Mistral AI is designed for code generation, supporting 80+ programming languages, and optimized for tasks like code completion and fill-in-the-middle
        /// </summary>
        public static readonly FoundryModel Codestral2501 = new() { Name = "Codestral-2501", Version = "2", Format = "Mistral AI" };

        /// <summary>
        /// Ministral 3B is a state-of-the-art Small Language Model (SLM) optimized for edge computing and on-device applications. As it is designed for low-latency and compute-efficient inference, it it also the perfect model for standard GenAI applications that have
        /// </summary>
        public static readonly FoundryModel Ministral3B = new() { Name = "Ministral-3B", Version = "1", Format = "Mistral AI" };

        /// <summary>
        /// Document conversion to markdown with interleaved images and text
        /// </summary>
        public static readonly FoundryModel MistralDocumentAi2505 = new() { Name = "mistral-document-ai-2505", Version = "1", Format = "Mistral AI" };

        /// <summary>
        /// Document conversion to markdown with interleaved images and text
        /// </summary>
        public static readonly FoundryModel MistralDocumentAi2512 = new() { Name = "mistral-document-ai-2512", Version = "1", Format = "Mistral AI" };

        /// <summary>
        /// Mistral Large (2407) is an advanced Large Language Model (LLM) with state-of-the-art reasoning, knowledge and coding capabilities.
        /// </summary>
        public static readonly FoundryModel MistralLarge2407 = new() { Name = "Mistral-large-2407", Version = "1", Format = "Mistral AI" };

        /// <summary>
        /// Mistral Large 24.11 offers enhanced system prompts, advanced reasoning and function calling capabilities.
        /// </summary>
        public static readonly FoundryModel MistralLarge2411 = new() { Name = "Mistral-Large-2411", Version = "2", Format = "Mistral AI" };

        /// <summary>
        /// Mistral Large 3 is a state-of-the-art General-purpose Multimodal granular Mixture-of-Experts model with 39B active parameters, 673B total parameters featuring 128 experts per layer and Multi-Latent attention.
        /// </summary>
        public static readonly FoundryModel MistralLarge3 = new() { Name = "Mistral-Large-3", Version = "1", Format = "Mistral AI" };

        /// <summary>
        /// Mistral Medium 3 is an advanced Large Language Model (LLM) with state-of-the-art reasoning, knowledge, coding and vision capabilities.
        /// </summary>
        public static readonly FoundryModel MistralMedium2505 = new() { Name = "mistral-medium-2505", Version = "1", Format = "Mistral AI" };

        /// <summary>
        /// Mistral Nemo is a cutting-edge Language Model (LLM) boasting state-of-the-art reasoning, world knowledge, and coding capabilities within its size category.
        /// </summary>
        public static readonly FoundryModel MistralNemo = new() { Name = "Mistral-Nemo", Version = "1", Format = "Mistral AI" };

        /// <summary>
        /// Mistral Small can be used on any language-based task that requires high efficiency and low latency.
        /// </summary>
        public static readonly FoundryModel MistralSmall = new() { Name = "Mistral-small", Version = "1", Format = "Mistral AI" };

        /// <summary>
        /// Enhanced Mistral Small 3 with multimodal capabilities and a 128k context length.
        /// </summary>
        public static readonly FoundryModel MistralSmall2503 = new() { Name = "mistral-small-2503", Version = "1", Format = "Mistral AI" };
    }

    /// <summary>
    /// Models published by OpenAI.
    /// </summary>
    public static partial class OpenAI
    {
        /// <summary>
        /// codex-mini is a fine-tuned variant of the o4-mini model, designed to deliver rapid, instruction-following performance for developers working in CLI workflows. Whether you're automating shell commands, editing scripts, or refactoring repositories, Codex-Min
        /// </summary>
        public static readonly FoundryModel CodexMini = new() { Name = "codex-mini", Version = "2025-05-16", Format = "OpenAI" };

        /// <summary>
        /// computer-use-preview is the model for Computer Use Agent for use in Responses API. You can use computer-use-preview model to get instructions to control a browser on your computer screen and take action on a user's behalf.
        /// </summary>
        public static readonly FoundryModel ComputerUsePreview = new() { Name = "computer-use-preview", Version = "2025-03-11", Format = "OpenAI" };

        /// <summary>
        ///   <para>
        ///     <b>Direct from Azure models</b>
        ///   </para>
        ///   <para>Direct from Azure models are a select portfolio curated for their market-differentiated capabilities:</para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure's enterprise-grade infrastructure.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>Learn more about <see href="https://aka.ms/DirectfromAzure">Direct from Azure models</see>.</para>
        ///   <para>
        ///     <b>Key capabilities</b>
        ///   </para>
        ///   <para>
        ///     <b>About this model</b>
        ///   </para>
        ///   <para>DALL-E 3 generates images from text prompts that are provided by the user.</para>
        ///   <para>
        ///     <b>Key model capabilities</b>
        ///   </para>
        ///   <para>The image generation API creates an image from a text prompt. It does not edit existing images or create variations.</para>
        ///   <para>
        ///     <b>Use cases</b>
        ///   </para>
        ///   <para>See Responsible AI for additional considerations for responsible use.</para>
        ///   <para>
        ///     <b>Key use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Out of scope use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>Pricing is based on a number of factors, including deployment type and tokens used. <see href="https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067">See pricing details here.</see></para>
        ///   <para>
        ///     <b>Technical specs</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training cut-off date</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training time</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Input formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Output formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Supported languages</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Sample JSON response</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Model architecture</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Long context</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Optimizing model performance</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Additional assets</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training disclosure</b>
        ///   </para>
        ///   <para>
        ///     <b>Training, testing and validation</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Distribution</b>
        ///   </para>
        ///   <para>
        ///     <b>Distribution channels</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>More information</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        /// </summary>
        public static readonly FoundryModel DallE3 = new() { Name = "dall-e-3", Version = "3.0", Format = "OpenAI" };

        /// <summary>
        ///   <para>
        ///     <b>Azure Direct Models</b>
        ///   </para>
        ///   <para>Direct from Azure models are a select portfolio curated for their market-differentiated capabilities:</para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure's enterprise-grade infrastructure.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all as part of one Azure AI Foundry platform.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Azure AI Foundry; reducing integration effort.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>Learn more about <see href="https://aka.ms/DirectfromAzure">Direct from Azure models</see>.</para>
        ///   <para>
        ///     <b>Key capabilities</b>
        ///   </para>
        ///   <para>
        ///     <b>About this model</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Key model capabilities</b>
        ///   </para>
        ///   <para>Davinci-002 supports fine-tuning, allowing developers and businesses to customize the model for specific applications.</para>
        ///   <para>
        ///     <b>Use cases</b>
        ///   </para>
        ///   <para>See Responsible AI for additional considerations for responsible use.</para>
        ///   <para>
        ///     <b>Key use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Out of scope use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>Pricing is based on a number of factors, including deployment type and tokens used. <see href="https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067">See pricing details here.</see></para>
        ///   <para>
        ///     <b>Technical specs</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training cut-off date</b>
        ///   </para>
        ///   <para>This model supports 16384 max input tokens and training data is up to Sep 2021.</para>
        ///   <para>
        ///     <b>Training time</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Input formats</b>
        ///   </para>
        ///   <para>Your training data and validation data sets consist of input and output examples for how you would like the model to perform. The training and validation data you use must be formatted as a JSON Lines (JSONL) document in which each line represents a single prompt-completion pair.</para>
        ///   <para>
        ///     <b>Output formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Supported languages</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Sample JSON response</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Model architecture</b>
        ///   </para>
        ///   <para>Davinci-002 is the latest version of Davinci, a gpt-3 based model.</para>
        ///   <para>
        ///     <b>Long context</b>
        ///   </para>
        ///   <para>This model supports 16384 max input tokens.</para>
        ///   <para>
        ///     <b>Optimizing model performance</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Additional assets</b>
        ///   </para>
        ///   <para>Learn more at https://learn.microsoft.com/azure/cognitive-services/openai/concepts/models</para>
        ///   <para>
        ///     <b>Training disclosure</b>
        ///   </para>
        ///   <para>
        ///     <b>Training, testing and validation</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Distribution</b>
        ///   </para>
        ///   <para>
        ///     <b>Distribution channels</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>More information</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        /// </summary>
        public static readonly FoundryModel Davinci002 = new() { Name = "davinci-002", Version = "3", Format = "OpenAI" };

        /// <summary>
        ///   <para>
        ///     <b>Direct from Azure models</b>
        ///   </para>
        ///   <para>Direct from Azure models are a select portfolio curated for their market-differentiated capabilities:</para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure's enterprise-grade infrastructure.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>Learn more about <see href="https://aka.ms/DirectfromAzure">Direct from Azure models</see>.</para>
        ///   <para>
        ///     <b>Key capabilities</b>
        ///   </para>
        ///   <para>
        ///     <b>About this model</b>
        ///   </para>
        ///   <para>The gpt-35-turbo is a language model designed for conversational interfaces that has been optimized for chat using the Chat Completions API.</para>
        ///   <para>
        ///     <b>Key model capabilities</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Use cases</b>
        ///   </para>
        ///   <para>See Responsible AI for additional considerations for responsible use.</para>
        ///   <para>
        ///     <b>Key use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Out of scope use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>Pricing is based on a number of factors, including deployment type and tokens used. <see href="https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067">See pricing details here.</see></para>
        ///   <para>
        ///     <b>Technical specs</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training cut-off date</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training time</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Input formats</b>
        ///   </para>
        ///   <para>The model expects a prompt string formatted in a specific chat-like transcript format.</para>
        ///   <para>
        ///     <b>Output formats</b>
        ///   </para>
        ///   <para>The model returns a completion that represents a model-written message in the chat.</para>
        ///   <para>
        ///     <b>Supported languages</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Sample JSON response</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Model architecture</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Long context</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Optimizing model performance</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Additional assets</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training disclosure</b>
        ///   </para>
        ///   <para>
        ///     <b>Training, testing and validation</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Distribution</b>
        ///   </para>
        ///   <para>
        ///     <b>Distribution channels</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>More information</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        /// </summary>
        public static readonly FoundryModel Gpt35Turbo = new() { Name = "gpt-35-turbo", Version = "0125", Format = "OpenAI" };

        /// <summary>
        ///   <para>
        ///     <b>Direct from Azure models</b>
        ///   </para>
        ///   <para>Direct from Azure models are a select portfolio curated for their market-differentiated capabilities:</para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure's enterprise-grade infrastructure.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>Learn more about <see href="https://aka.ms/DirectfromAzure">Direct from Azure models</see>.</para>
        ///   <para>
        ///     <b>Key capabilities</b>
        ///   </para>
        ///   <para>
        ///     <b>About this model</b>
        ///   </para>
        ///   <para>gpt-3.5 models can understand and generate natural language or code.</para>
        ///   <para>
        ///     <b>Key model capabilities</b>
        ///   </para>
        ///   <para>gpt-3.5-turbo is available for use with the Chat Completions API. gpt-3.5-turbo Instruct has similar capabilities to text-davinci-003 using the Completions API instead of the Chat Completions API.</para>
        ///   <para>To learn more about how to interact with gpt-3.5-turbo and the Chat Completions API check out our <see href="https://learn.microsoft.com/azure/ai-services/openai/how-to/chatgpt?tabs=python&amp;pivots=programming-language-chat-completions">in-depth how-to.</see>in-depth how-to.</para>
        ///   <para>
        ///     <b>Use cases</b>
        ///   </para>
        ///   <para>See Responsible AI for additional considerations for responsible use.</para>
        ///   <para>
        ///     <b>Key use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Out of scope use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>Pricing is based on a number of factors, including deployment type and tokens used. <see href="https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067">See pricing details here.</see></para>
        ///   <para>
        ///     <b>Technical specs</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training cut-off date</b>
        ///   </para>
        ///   <para>Sep 2021</para>
        ///   <para>
        ///     <b>Training time</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Input formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Output formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Supported languages</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Sample JSON response</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Model architecture</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Long context</b>
        ///   </para>
        ///   <para>You can see the token context length supported by each model in the model summary table.</para>
        ///   <para>Model ID</para>
        ///   <para>Model Availability</para>
        ///   <para>Max Request (tokens)</para>
        ///   <para>Training Data (up to)</para>
        ///   <para>gpt-35-turbo<i>1</i>1 (0301)</para>
        ///   <para>East US, France Central, South Central US, UK South, West Europe</para>
        ///   <para>4,096</para>
        ///   <para>Sep 2021</para>
        ///   <para>gpt-35-turbo (0613)</para>
        ///   <para>Australia East, Canada East, East US, East US 2, France Central, Japan East, North Central US, Sweden Central, Switzerland North, UK South</para>
        ///   <para>4,096</para>
        ///   <para>Sep 2021</para>
        ///   <para>gpt-35-turbo-16k (0613)</para>
        ///   <para>Australia East, Canada East, East US, East US 2, France Central, Japan East, North Central US, Sweden Central, Switzerland North, UK South</para>
        ///   <para>16,384</para>
        ///   <para>Sep 2021</para>
        ///   <para>gpt-35-turbo-instruct (0914)</para>
        ///   <para>East US, Sweden Central</para>
        ///   <para>4,097</para>
        ///   <para>Sep 2021</para>
        ///   <para>gpt-35-turbo (1106)</para>
        ///   <para>Australia East, Canada East, France Central, South India, Sweden Central, UK South, West US</para>
        ///   <para>Input: 16,385 Output: 4,096</para>
        ///   <para>Sep 2021</para>
        ///   <para>
        ///     <i>1</i>1 This model will accept requests &gt; 4,096 tokens. It is not recommended to exceed the 4,096 input token limit as the newer version of the model are capped at 4,096 tokens. If you encounter issues when exceeding 4,096 input tokens with this model this configuration is not officially supported.</para>
        ///   <para>
        ///     <b>Optimizing model performance</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Additional assets</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training disclosure</b>
        ///   </para>
        ///   <para>
        ///     <b>Training, testing and validation</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Distribution</b>
        ///   </para>
        ///   <para>
        ///     <b>Distribution channels</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>More information</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        /// </summary>
        public static readonly FoundryModel Gpt35Turbo16k = new() { Name = "gpt-35-turbo-16k", Version = "0613", Format = "OpenAI" };

        /// <summary>
        ///   <para>
        ///     <b>Direct from Azure models</b>
        ///   </para>
        ///   <para>Direct from Azure models are a select portfolio curated for their market-differentiated capabilities:</para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure's enterprise-grade infrastructure.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>Learn more about <see href="https://aka.ms/DirectfromAzure">Direct from Azure models</see>.</para>
        ///   <para>
        ///     <b>Key capabilities</b>
        ///   </para>
        ///   <para>
        ///     <b>About this model</b>
        ///   </para>
        ///   <para>gpt-3.5 models can understand and generate natural language or code.</para>
        ///   <para>
        ///     <b>Key model capabilities</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Understand and generate natural language</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Generate code</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Chat optimized interactions</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Traditional completions tasks</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Use cases</b>
        ///   </para>
        ///   <para>See Responsible AI for additional considerations for responsible use.</para>
        ///   <para>
        ///     <b>Key use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Out of scope use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>Pricing is based on a number of factors, including deployment type and tokens used. <see href="https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067">See pricing details here.</see></para>
        ///   <para>
        ///     <b>Technical specs</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training cut-off date</b>
        ///   </para>
        ///   <para>Sep 2021</para>
        ///   <para>
        ///     <b>Training time</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Input formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Output formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Supported languages</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Sample JSON response</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Model architecture</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Long context</b>
        ///   </para>
        ///   <para>You can see the token context length supported by each model in the model summary table.</para>
        ///   <para>Model ID</para>
        ///   <para>Model Availability</para>
        ///   <para>Max Request (tokens)</para>
        ///   <para>Training Data (up to)</para>
        ///   <para>gpt-35-turbo<i>1</i>1 (0301)</para>
        ///   <para>East US, France Central, South Central US, UK South, West Europe</para>
        ///   <para>4,096</para>
        ///   <para>Sep 2021</para>
        ///   <para>gpt-35-turbo (0613)</para>
        ///   <para>Australia East, Canada East, East US, East US 2, France Central, Japan East, North Central US, Sweden Central, Switzerland North, UK South</para>
        ///   <para>4,096</para>
        ///   <para>Sep 2021</para>
        ///   <para>gpt-35-turbo-16k (0613)</para>
        ///   <para>Australia East, Canada East, East US, East US 2, France Central, Japan East, North Central US, Sweden Central, Switzerland North, UK South</para>
        ///   <para>16,384</para>
        ///   <para>Sep 2021</para>
        ///   <para>gpt-35-turbo-instruct (0914)</para>
        ///   <para>East US, Sweden Central</para>
        ///   <para>4,097</para>
        ///   <para>Sep 2021</para>
        ///   <para>gpt-35-turbo (1106)</para>
        ///   <para>Australia East, Canada East, France Central, South India, Sweden Central, UK South, West US</para>
        ///   <para>Input: 16,385 Output: 4,096</para>
        ///   <para>Sep 2021</para>
        ///   <para>
        ///     <i>1</i>1 This model will accept requests &gt; 4,096 tokens. It is not recommended to exceed the 4,096 input token limit as the newer version of the model are capped at 4,096 tokens. If you encounter issues when exceeding 4,096 input tokens with this model this configuration is not officially supported.</para>
        ///   <para>
        ///     <b>Optimizing model performance</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Additional assets</b>
        ///   </para>
        ///   <para>To learn more about how to interact with GPT-3.5 Turbo and the Chat Completions API check out our <see href="https://learn.microsoft.com/azure/ai-services/openai/how-to/chatgpt?tabs=python&amp;pivots=programming-language-chat-completions">in-depth how-to.</see>in-depth how-to.</para>
        ///   <para>
        ///     <b>Training disclosure</b>
        ///   </para>
        ///   <para>
        ///     <b>Training, testing and validation</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Distribution</b>
        ///   </para>
        ///   <para>
        ///     <b>Distribution channels</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>More information</b>
        ///   </para>
        /// </summary>
        public static readonly FoundryModel Gpt35TurboInstruct = new() { Name = "gpt-35-turbo-instruct", Version = "0914", Format = "OpenAI" };

        /// <summary>
        ///   <para>
        ///     <b>Direct from Azure models</b>
        ///   </para>
        ///   <para>Direct from Azure models are a select portfolio curated for their market-differentiated capabilities:</para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure's enterprise-grade infrastructure.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>Learn more about <see href="https://aka.ms/DirectfromAzure">Direct from Azure models</see>.</para>
        ///   <para>
        ///     <b>Key capabilities</b>
        ///   </para>
        ///   <para>
        ///     <b>About this model</b>
        ///   </para>
        ///   <para>gpt-4 is a large multimodal model that can solve complex problems with greater accuracy than any of our previous models, thanks to its extensive general knowledge and advanced reasoning capabilities.</para>
        ///   <para>
        ///     <b>Key model capabilities</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>gpt-4-turbo-2024-04-09:</b> This is the GPT-4 Turbo with Vision GA model. It can return up to 4,096 output tokens.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>gpt-4-1106-preview (GPT-4 Turbo):</b> The latest gpt-4 model with improved instruction following, JSON mode, reproducible outputs, parallel function calling, and more. It returns a maximum of 4,096 output tokens.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>gpt-4-vision Preview (GPT-4 Turbo with vision):</b> This multimodal AI model enables users to direct the model to analyze image inputs they provide, along with all the other capabilities of GPT-4 Turbo. It can return up to 4,096 output tokens.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Use cases</b>
        ///   </para>
        ///   <para>See Responsible AI for additional considerations for responsible use.</para>
        ///   <para>
        ///     <b>Key use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Out of scope use cases</b>
        ///   </para>
        ///   <para>Please note that AzureML Studio only supports the deployment of the gpt-4-0314 model version and AI Studio supports the deployment of all the model versions listed below. This preview model is not yet suited for production traffic. As a preview model version, it is not yet suitable for production traffic. This model version will be retired no earlier than July 5, 2024.</para>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>Pricing is based on a number of factors, including deployment type and tokens used. <see href="https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067">See pricing details here.</see></para>
        ///   <para>
        ///     <b>Technical specs</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training cut-off date</b>
        ///   </para>
        ///   <para>gpt-4 provides a wide range of model versions to fit your business needs:</para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>gpt-4-turbo-2024-04-09:</b> The training data is current up to December 2023.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>gpt-4-1106-preview (GPT-4 Turbo):</b> Training Data: Up to April 2023.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>gpt-4-vision Preview (GPT-4 Turbo with vision):</b> Training data is current up to April 2023.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>gpt-4-0613:</b> Training data up to September 2021.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>gpt-4-0314:</b> Training data up to September 2021.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Training time</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Input formats</b>
        ///   </para>
        ///   <para>gpt-4 is a large multimodal model that accepts text or image inputs.</para>
        ///   <para>
        ///     <b>Output formats</b>
        ///   </para>
        ///   <para>gpt-4 outputs text.</para>
        ///   <para>
        ///     <b>Supported languages</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Sample JSON response</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Model architecture</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Long context</b>
        ///   </para>
        ///   <para>gpt-4 provides different context window sizes across model versions:</para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>gpt-4-turbo-2024-04-09:</b> The context window is 128,000 tokens.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>gpt-4-1106-preview (GPT-4 Turbo):</b> Context window: 128,000 tokens.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>gpt-4-vision Preview (GPT-4 Turbo with vision):</b> The context window is 128,000 tokens.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>gpt-4-0613:</b> gpt-4 model with a context window of 8,192 tokens.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>gpt-4-0314:</b> gpt-4 legacy model with a context window of 8,192 tokens.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Optimizing model performance</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Additional assets</b>
        ///   </para>
        ///   <para>Learn more at https://learn.microsoft.com/azure/cognitive-services/openai/concepts/models</para>
        ///   <para>
        ///     <b>Training disclosure</b>
        ///   </para>
        ///   <para>
        ///     <b>Training, testing and validation</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Distribution</b>
        ///   </para>
        ///   <para>
        ///     <b>Distribution channels</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>More information</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        /// </summary>
        public static readonly FoundryModel Gpt4 = new() { Name = "gpt-4", Version = "turbo-2024-04-09", Format = "OpenAI" };

        /// <summary>
        ///   <para>
        ///     <b>Direct from Azure models</b>
        ///   </para>
        ///   <para>Direct from Azure models are a select portfolio curated for their market-differentiated capabilities:</para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure's enterprise-grade infrastructure.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>Learn more about <see href="https://aka.ms/DirectfromAzure">Direct from Azure models</see>.</para>
        ///   <para>
        ///     <b>Key capabilities</b>
        ///   </para>
        ///   <para>
        ///     <b>About this model</b>
        ///   </para>
        ///   <para>gpt-4 can solve difficult problems with greater accuracy than any of the previous OpenAI models. Like gpt-35-turbo, gpt-4 is optimized for chat but works well for traditional completions tasks.</para>
        ///   <para>
        ///     <b>Key model capabilities</b>
        ///   </para>
        ///   <para>gpt-4 can solve difficult problems with greater accuracy than any of the previous OpenAI models. Like gpt-35-turbo, gpt-4 is optimized for chat but works well for traditional completions tasks.</para>
        ///   <para>
        ///     <b>Use cases</b>
        ///   </para>
        ///   <para>See Responsible AI for additional considerations for responsible use.</para>
        ///   <para>
        ///     <b>Key use cases</b>
        ///   </para>
        ///   <para>gpt-4 is optimized for chat but works well for traditional completions tasks.</para>
        ///   <para>
        ///     <b>Out of scope use cases</b>
        ///   </para>
        ///   <para>this model can be deployed for inference, but cannot be finetuned.</para>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>Pricing is based on a number of factors, including deployment type and tokens used. <see href="https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067">See pricing details here.</see></para>
        ///   <para>
        ///     <b>Technical specs</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training cut-off date</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training time</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Input formats</b>
        ///   </para>
        ///   <para>The gpt-4 supports 8192 max input tokens and the gpt-4-32k supports up to 32,768 tokens.</para>
        ///   <para>
        ///     <b>Output formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Supported languages</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Sample JSON response</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Model architecture</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Long context</b>
        ///   </para>
        ///   <para>The gpt-4 supports 8192 max input tokens and the gpt-4-32k supports up to 32,768 tokens.</para>
        ///   <para>
        ///     <b>Optimizing model performance</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Additional assets</b>
        ///   </para>
        ///   <para>Learn more at https://learn.microsoft.com/azure/cognitive-services/openai/concepts/models</para>
        ///   <para>
        ///     <b>Training disclosure</b>
        ///   </para>
        ///   <para>
        ///     <b>Training, testing and validation</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Distribution</b>
        ///   </para>
        ///   <para>
        ///     <b>Distribution channels</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>More information</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        /// </summary>
        public static readonly FoundryModel Gpt432k = new() { Name = "gpt-4-32k", Version = "0613", Format = "OpenAI" };

        /// <summary>
        /// gpt-4.1 outperforms gpt-4o across the board, with major gains in coding, instruction following, and long-context understanding
        /// </summary>
        public static readonly FoundryModel Gpt41 = new() { Name = "gpt-4.1", Version = "2025-04-14", Format = "OpenAI" };

        /// <summary>
        /// gpt-4.1-mini outperform gpt-4o-mini across the board, with major gains in coding, instruction following, and long-context handling
        /// </summary>
        public static readonly FoundryModel Gpt41Mini = new() { Name = "gpt-4.1-mini", Version = "2025-04-14", Format = "OpenAI" };

        /// <summary>
        /// gpt-4.1-nano provides gains in coding, instruction following, and long-context handling along with lower latency and cost
        /// </summary>
        public static readonly FoundryModel Gpt41Nano = new() { Name = "gpt-4.1-nano", Version = "2025-04-14", Format = "OpenAI" };

        /// <summary>
        /// the largest and strongest general purpose model in the gpt model family up to date, best suited for diverse text and image tasks.
        /// </summary>
        public static readonly FoundryModel Gpt45Preview = new() { Name = "gpt-4.5-preview", Version = "2025-02-27", Format = "OpenAI" };

        /// <summary>
        /// OpenAI's most advanced multimodal model in the gpt-4o family. Can handle both text and image inputs.
        /// </summary>
        public static readonly FoundryModel Gpt4o = new() { Name = "gpt-4o", Version = "2024-11-20", Format = "OpenAI" };

        /// <summary>
        /// Best suited for rich, asynchronous audio input/output interactions, such as creating spoken summaries from text.
        /// </summary>
        public static readonly FoundryModel Gpt4oAudioPreview = new() { Name = "gpt-4o-audio-preview", Version = "2024-12-17", Format = "OpenAI" };

        /// <summary>
        /// An affordable, efficient AI solution for diverse text and image tasks.
        /// </summary>
        public static readonly FoundryModel Gpt4oMini = new() { Name = "gpt-4o-mini", Version = "2024-07-18", Format = "OpenAI" };

        /// <summary>
        /// Best suited for rich, asynchronous audio input/output interactions, such as creating spoken summaries from text.
        /// </summary>
        public static readonly FoundryModel Gpt4oMiniAudioPreview = new() { Name = "gpt-4o-mini-audio-preview", Version = "2024-12-17", Format = "OpenAI" };

        /// <summary>
        /// Best suited for rich, asynchronous audio input/output interactions, such as creating spoken summaries from text.
        /// </summary>
        public static readonly FoundryModel Gpt4oMiniRealtimePreview = new() { Name = "gpt-4o-mini-realtime-preview", Version = "2024-12-17", Format = "OpenAI" };

        /// <summary>
        /// A highly efficient and cost effective speech-to-text solution that deliverables reliable and accurate transcripts.
        /// </summary>
        public static readonly FoundryModel Gpt4oMiniTranscribe = new() { Name = "gpt-4o-mini-transcribe", Version = "2025-12-15", Format = "OpenAI" };

        /// <summary>
        /// An advanced text-to-speech solution designed to convert written text into natural-sounding speech.
        /// </summary>
        public static readonly FoundryModel Gpt4oMiniTts = new() { Name = "gpt-4o-mini-tts", Version = "2025-12-15", Format = "OpenAI" };

        /// <summary>
        ///   <para>
        ///     <b>Direct from Azure models</b>
        ///   </para>
        ///   <para>Direct from Azure models are a select portfolio curated for their market-differentiated capabilities:</para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure's enterprise-grade infrastructure.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>Learn more about <see href="https://aka.ms/DirectfromAzure">Direct from Azure models</see>.</para>
        ///   <para>
        ///     <b>Key capabilities</b>
        ///   </para>
        ///   <para>
        ///     <b>About this model</b>
        ///   </para>
        ///   <para>Introducing our new multimodal AI model, which now supports both text and audio modalities.</para>
        ///   <para>
        ///     <b>Key model capabilities</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Enhanced customer service:</b> By integrating audio inputs, gpt-4o-realtime-preview enables more dynamic and comprehensive customer support interactions.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Content innovation:</b> Use gpt-4o-realtime-preview's generative capabilities to create engaging and diverse audio content, catering to a broad range of consumer preferences.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Real-time translation:</b> Leverage gpt-4o-realtime-preview's capability to provide accurate and immediate translations, facilitating seamless communication across different languages</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Use cases</b>
        ///   </para>
        ///   <para>See Responsible AI for additional considerations for responsible use.</para>
        ///   <para>
        ///     <b>Key use cases</b>
        ///   </para>
        ///   <para>The introduction of gpt-4o-realtime-preview opens numerous possibilities for businesses in various sectors: Enhanced customer service, content innovation, and real-time translation capabilities facilitate seamless communication across different languages.</para>
        ///   <para>
        ///     <b>Out of scope use cases</b>
        ///   </para>
        ///   <para>Currently, the gpt-4o-realtime-preview model focuses on text and audio and does not support existing gpt-4o features such as image modality and structured outputs. For many tasks, the generally available gpt-4o models may still be more suitable.</para>
        ///   <para>IMPORTANT: At this time, gpt-4o-realtime-preview usage limits are suitable for test and development. To prevent abuse and preserve service integrity, rate limits will be adjusted as needed.</para>
        ///   <para>IMPORTANT: The system stores your prompts and completions as described in the "Data Use and Access for Abuse Monitoring" section of the service-specific Product Terms for Azure OpenAI Service, except that the Limited Exception does not apply. Abuse monitoring will be turned on for use of the GPT-4o-realtime-preview API even for customers who otherwise are approved for modified abuse monitoring.</para>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>Pricing is based on a number of factors, including deployment type and tokens used. <see href="https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067">See pricing details here.</see></para>
        ///   <para>
        ///     <b>Technical specs</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training cut-off date</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training time</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Input formats</b>
        ///   </para>
        ///   <para>Currently, the gpt-4o-realtime-preview model focuses on text and audio and does not support existing gpt-4o features such as image modality and structured outputs.</para>
        ///   <para>
        ///     <b>Output formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Supported languages</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Sample JSON response</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Model architecture</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Long context</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Optimizing model performance</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Additional assets</b>
        ///   </para>
        ///   <para>The following documents are applicable:</para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <see href="https://learn.microsoft.com/legal/cognitive-services/openai/overview">Overview of Responsible AI practices for Azure OpenAI models</see>
        ///         </para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <see href="https://learn.microsoft.com/legal/cognitive-services/openai/transparency-note">Transparency Note for Azure OpenAI Service</see>
        ///         </para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Training disclosure</b>
        ///   </para>
        ///   <para>
        ///     <b>Training, testing and validation</b>
        ///   </para>
        ///   <para>GPT-4o-realtime-preview has safety built-in by design across modalities, through techniques such as filtering training data and refining the model's behavior through post-training.</para>
        ///   <para>
        ///     <b>Distribution</b>
        ///   </para>
        ///   <para>
        ///     <b>Distribution channels</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>More information</b>
        ///   </para>
        ///   <para>We've evaluated GPT-4o-realtime-preview according to our <see href="https://openai.com/safety/">Preparedness Framework</see> and in line with our <see href="https://openai.com/index/moving-ai-governance-forward/">voluntary commitments</see>. Our evaluations of cybersecurity, CBRN, persuasion, and model autonomy show that GPT-4o-realtime-preview does not score above Medium risk in any of these categories. This assessment involved running a suite of automated and human evaluations throughout the model training process. We tested both pre-safety-mitigation and post-safety-mitigation versions of the model, using custom fine-tuning and prompts, to better elicit model capabilities.</para>
        ///   <para>GPT-4o-realtime-preview has also undergone extensive external red teaming with 70+ <see href="https://openai.com/index/red-teaming-network/">external experts</see> in domains such as social psychology, bias and fairness, and misinformation to identify risks that are introduced or amplified by the newly added modalities. We used these learnings to build out our safety interventions in order to improve the safety of interacting with GPT-4o-realtime-preview. We will continue to mitigate new risks as they're discovered.</para>
        ///   <para>Model Versions:</para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>2024-12-17:</b> Updating the gpt-4o-realtime-preview model with improvements in voice quality and input reliability. As this is a preview version, it is designed for testing and feedback purposes and is not yet optimized for production traffic.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>2024-10-01:</b> Introducing our new multimodal AI model, which now supports both text and audio modalities. As this is a preview version, it is designed for testing and feedback purposes and is not yet optimized for production traffic.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        /// </summary>
        public static readonly FoundryModel Gpt4oRealtimePreview = new() { Name = "gpt-4o-realtime-preview", Version = "2024-12-17", Format = "OpenAI" };

        /// <summary>
        /// A cutting-edge speech-to-text solution that deliverables reliable and accurate transcripts.
        /// </summary>
        public static readonly FoundryModel Gpt4oTranscribe = new() { Name = "gpt-4o-transcribe", Version = "2025-03-20", Format = "OpenAI" };

        /// <summary>
        /// A cutting-edge speech-to-text solution that deliverables reliable and accurate transcripts; now equipped with diarization support aka identifying different speakers through the transcription.
        /// </summary>
        public static readonly FoundryModel Gpt4oTranscribeDiarize = new() { Name = "gpt-4o-transcribe-diarize", Version = "2025-10-15", Format = "OpenAI" };

        /// <summary>
        /// gpt-5 is designed for logic-heavy and multi-step tasks.
        /// </summary>
        public static readonly FoundryModel Gpt5 = new() { Name = "gpt-5", Version = "2025-08-07", Format = "OpenAI" };

        /// <summary>
        /// gpt-5-chat (preview) is an advanced, natural, multimodal, and context-aware conversations for enterprise applications.
        /// </summary>
        public static readonly FoundryModel Gpt5Chat = new() { Name = "gpt-5-chat", Version = "2025-10-03", Format = "OpenAI" };

        /// <summary>
        /// gpt-5-codex is designed for steerability, front end development, and interactivity.
        /// </summary>
        public static readonly FoundryModel Gpt5Codex = new() { Name = "gpt-5-codex", Version = "2025-09-15", Format = "OpenAI" };

        /// <summary>
        /// gpt-5-mini is a lightweight version for cost-sensitive applications.
        /// </summary>
        public static readonly FoundryModel Gpt5Mini = new() { Name = "gpt-5-mini", Version = "2025-08-07", Format = "OpenAI" };

        /// <summary>
        /// gpt-5-nano is optimized for speed, ideal for applications requiring low latency.
        /// </summary>
        public static readonly FoundryModel Gpt5Nano = new() { Name = "gpt-5-nano", Version = "2025-08-07", Format = "OpenAI" };

        /// <summary>
        /// gpt-5-pro uses more compute to think harder and provide consistently better answers.
        /// </summary>
        public static readonly FoundryModel Gpt5Pro = new() { Name = "gpt-5-pro", Version = "2025-10-06", Format = "OpenAI" };

        /// <summary>
        /// gpt-5.1 is designed for logic-heavy and multi-step tasks.
        /// </summary>
        public static readonly FoundryModel Gpt51 = new() { Name = "gpt-5.1", Version = "2025-11-13", Format = "OpenAI" };

        /// <summary>
        /// gpt-5.1-chat (preview) is an advanced, natural, multimodal, and context-aware conversations for enterprise applications.
        /// </summary>
        public static readonly FoundryModel Gpt51Chat = new() { Name = "gpt-5.1-chat", Version = "2025-11-13", Format = "OpenAI" };

        /// <summary>
        /// gpt-5.1-codex is designed for steerability, front end development, and interactivity.
        /// </summary>
        public static readonly FoundryModel Gpt51Codex = new() { Name = "gpt-5.1-codex", Version = "2025-11-13", Format = "OpenAI" };

        /// <summary>
        /// gpt-5.1-codex-max is agentic coding model designed to streamline complex development workflows with advanced efficiency
        /// </summary>
        public static readonly FoundryModel Gpt51CodexMax = new() { Name = "gpt-5.1-codex-max", Version = "2025-12-04", Format = "OpenAI" };

        /// <summary>
        /// gpt-5.1-codex-mini is designed for steerability, front end development, and interactivity.
        /// </summary>
        public static readonly FoundryModel Gpt51CodexMini = new() { Name = "gpt-5.1-codex-mini", Version = "2025-11-13", Format = "OpenAI" };

        /// <summary>
        /// GPT-5.2 is engineered for enterprise agent scenarios—delivering structured, auditable outputs, reliable tool use, and governed integrations.
        /// </summary>
        public static readonly FoundryModel Gpt52 = new() { Name = "gpt-5.2", Version = "2025-12-11", Format = "OpenAI" };

        /// <summary>
        /// gpt-5.2-chat (preview) is an advanced, natural, multimodal, and context-aware conversations for enterprise applications.
        /// </summary>
        public static readonly FoundryModel Gpt52Chat = new() { Name = "gpt-5.2-chat", Version = "2026-02-10", Format = "OpenAI" };

        /// <summary>
        /// gpt-5.2-codex is designed for steerability, front end development, and interactivity.
        /// </summary>
        public static readonly FoundryModel Gpt52Codex = new() { Name = "gpt-5.2-codex", Version = "2026-01-14", Format = "OpenAI" };

        /// <summary>
        /// gpt-5.3-chat (preview) is an advanced, natural, multimodal, and context-aware conversations for enterprise applications.
        /// </summary>
        public static readonly FoundryModel Gpt53Chat = new() { Name = "gpt-5.3-chat", Version = "2026-03-03", Format = "OpenAI" };

        /// <summary>
        /// gpt-5.3-codex is designed for steerability, front end development, and interactivity.
        /// </summary>
        public static readonly FoundryModel Gpt53Codex = new() { Name = "gpt-5.3-codex", Version = "2026-02-24", Format = "OpenAI" };

        /// <summary>
        /// GPT‑5.4 is OpenAI’s most capable frontier model, built to deliver faster, more reliable results for complex professional work.
        /// </summary>
        public static readonly FoundryModel Gpt54 = new() { Name = "gpt-5.4", Version = "2026-03-05", Format = "OpenAI" };

        /// <summary>
        /// GPT‑5.4-Pro is OpenAI’s most capable frontier model, built to deliver faster, more reliable results for complex professional work.
        /// </summary>
        public static readonly FoundryModel Gpt54Pro = new() { Name = "gpt-5.4-pro", Version = "2026-03-05", Format = "OpenAI" };

        /// <summary>
        /// Best suited for rich, asynchronous audio input/output interactions, such as creating spoken summaries from text.
        /// </summary>
        public static readonly FoundryModel GptAudio = new() { Name = "gpt-audio", Version = "2025-08-28", Format = "OpenAI" };

        /// <summary>
        /// A new S2S (speech to speech) model with improved instruction following.
        /// </summary>
        public static readonly FoundryModel GptAudio15 = new() { Name = "gpt-audio-1.5", Version = "2026-02-23", Format = "OpenAI" };

        /// <summary>
        /// Best suited for rich, asynchronous audio input/output interactions, such as creating spoken summaries from text.
        /// </summary>
        public static readonly FoundryModel GptAudioMini = new() { Name = "gpt-audio-mini", Version = "2025-10-06", Format = "OpenAI" };

        /// <summary>
        /// An efficient AI solution for diverse text and image tasks, including text to image, image to image, inpainting, and prompt transformation.
        /// </summary>
        public static readonly FoundryModel GptImage1 = new() { Name = "gpt-image-1", Version = "2025-04-15", Format = "OpenAI" };

        /// <summary>
        /// An efficient AI solution for diverse text and image tasks, including high quality, cheap text to image generation
        /// </summary>
        public static readonly FoundryModel GptImage1Mini = new() { Name = "gpt-image-1-mini", Version = "2025-10-06", Format = "OpenAI" };

        /// <summary>
        /// An efficient AI solution for diverse text and image tasks, including high quality, and editing scenarios
        /// </summary>
        public static readonly FoundryModel GptImage15 = new() { Name = "gpt-image-1.5", Version = "2025-12-16", Format = "OpenAI" };

        /// <summary>
        /// Push the open model frontier with GPT-OSS models, released under the permissive Apache 2.0 license, allowing anyone to use, modify, and deploy them freely.
        /// </summary>
        public static readonly FoundryModel GptOss120b = new() { Name = "gpt-oss-120b", Version = "4", Format = "OpenAI" };

        /// <summary>
        /// Push the open model frontier with GPT-OSS models, released under the permissive Apache 2.0 license, allowing anyone to use, modify, and deploy them freely.
        /// </summary>
        public static readonly FoundryModel GptOss20b = new() { Name = "gpt-oss-20b", Version = "11", Format = "OpenAI" };

        /// <summary>
        /// A new S2S (speech to speech) model with improved instruction following.
        /// </summary>
        public static readonly FoundryModel GptRealtime = new() { Name = "gpt-realtime", Version = "2025-08-28", Format = "OpenAI" };

        /// <summary>
        /// A new S2S (speech to speech) model with improved instruction following.
        /// </summary>
        public static readonly FoundryModel GptRealtime15 = new() { Name = "gpt-realtime-1.5", Version = "2026-02-23", Format = "OpenAI" };

        /// <summary>
        /// gpt-realtime-mini is a smaller version of gpt-realtime S2S (speech to speech) model built on chive architecture. This model excels at instruction following and is optimized for cost efficiency.
        /// </summary>
        public static readonly FoundryModel GptRealtimeMini = new() { Name = "gpt-realtime-mini", Version = "2025-12-15", Format = "OpenAI" };

        /// <summary>
        /// Focused on advanced reasoning and solving complex problems, including math and science tasks. Ideal for applications that require deep contextual understanding and agentic workflows.
        /// </summary>
        public static readonly FoundryModel O1 = new() { Name = "o1", Version = "2024-12-17", Format = "OpenAI" };

        /// <summary>
        /// Smaller, faster, and 80% cheaper than o1-preview, performs well at code generation and small context operations.
        /// </summary>
        public static readonly FoundryModel O1Mini = new() { Name = "o1-mini", Version = "2024-09-12", Format = "OpenAI" };

        /// <summary>
        /// Focused on advanced reasoning and solving complex problems, including math and science tasks. Ideal for applications that require deep contextual understanding and agentic workflows.
        /// </summary>
        public static readonly FoundryModel O1Preview = new() { Name = "o1-preview", Version = "1", Format = "OpenAI" };

        /// <summary>
        /// o3 includes significant improvements on quality and safety while supporting the existing features of o1 and delivering comparable or better performance.
        /// </summary>
        public static readonly FoundryModel O3 = new() { Name = "o3", Version = "2025-04-16", Format = "OpenAI" };

        /// <summary>
        /// The o3 series of models are trained with reinforcement learning to think before they answer and perform complex reasoning. The o1-pro model uses more compute to think harder and provide consistently better answers.
        /// </summary>
        public static readonly FoundryModel O3DeepResearch = new() { Name = "o3-deep-research", Version = "2025-06-26", Format = "OpenAI" };

        /// <summary>
        /// o3-mini includes the o1 features with significant cost-efficiencies for scenarios requiring high performance.
        /// </summary>
        public static readonly FoundryModel O3Mini = new() { Name = "o3-mini", Version = "2025-01-31", Format = "OpenAI" };

        /// <summary>
        /// The o3 series of models are trained with reinforcement learning to think before they answer and perform complex reasoning. The o1-pro model uses more compute to think harder and provide consistently better answers.
        /// </summary>
        public static readonly FoundryModel O3Pro = new() { Name = "o3-pro", Version = "2025-06-10", Format = "OpenAI" };

        /// <summary>
        /// o4-mini includes significant improvements on quality and safety while supporting the existing features of o3-mini and delivering comparable or better performance.
        /// </summary>
        public static readonly FoundryModel O4Mini = new() { Name = "o4-mini", Version = "2025-04-16", Format = "OpenAI" };

        /// <summary>
        /// An efficient AI solution to generate videos
        /// </summary>
        public static readonly FoundryModel Sora = new() { Name = "sora", Version = "2025-05-02", Format = "OpenAI" };

        /// <summary>
        /// Text-embedding-3 series models are the latest and most capable embedding model from OpenAI.
        /// </summary>
        public static readonly FoundryModel TextEmbedding3Large = new() { Name = "text-embedding-3-large", Version = "1", Format = "OpenAI" };

        /// <summary>
        /// Text-embedding-3 series models are the latest and most capable embedding model from OpenAI.
        /// </summary>
        public static readonly FoundryModel TextEmbedding3Small = new() { Name = "text-embedding-3-small", Version = "1", Format = "OpenAI" };

        /// <summary>
        ///   <para>
        ///     <b>Direct from Azure models</b>
        ///   </para>
        ///   <para>Direct from Azure models are a select portfolio curated for their market-differentiated capabilities:</para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure's enterprise-grade infrastructure.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>Learn more about <see href="https://aka.ms/DirectfromAzure">Direct from Azure models</see>.</para>
        ///   <para>
        ///     <b>Key capabilities</b>
        ///   </para>
        ///   <para>
        ///     <b>About this model</b>
        ///   </para>
        ///   <para>text-embedding-ada-002 outperforms all the earlier embedding models on text search, code search, and sentence similarity tasks and gets comparable performance on text classification.</para>
        ///   <para>
        ///     <b>Key model capabilities</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Text search</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Code search</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Sentence similarity tasks</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Text classification</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>Note: this model can be deployed for inference, specifically for embeddings, but cannot be finetuned.</para>
        ///   <para>
        ///     <b>Use cases</b>
        ///   </para>
        ///   <para>See Responsible AI for additional considerations for responsible use.</para>
        ///   <para>
        ///     <b>Key use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Out of scope use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>Pricing is based on a number of factors, including deployment type and tokens used. <see href="https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067">See pricing details here.</see></para>
        ///   <para>
        ///     <b>Technical specs</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training cut-off date</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training time</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Input formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Output formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Supported languages</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Sample JSON response</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Model architecture</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Long context</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Optimizing model performance</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Additional assets</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training disclosure</b>
        ///   </para>
        ///   <para>
        ///     <b>Training, testing and validation</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Distribution</b>
        ///   </para>
        ///   <para>
        ///     <b>Distribution channels</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>More information</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        /// </summary>
        public static readonly FoundryModel TextEmbeddingAda002 = new() { Name = "text-embedding-ada-002", Version = "2", Format = "OpenAI" };

        /// <summary>
        ///   <para>
        ///     <b>Direct from Azure models</b>
        ///   </para>
        ///   <para>Direct from Azure models are a select portfolio curated for their market-differentiated capabilities:</para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure's enterprise-grade infrastructure.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>Learn more about <see href="https://aka.ms/DirectfromAzure">Direct from Azure models</see>.</para>
        ///   <para>
        ///     <b>Key capabilities</b>
        ///   </para>
        ///   <para>
        ///     <b>About this model</b>
        ///   </para>
        ///   <para>TTS is a model that converts text to natural sounding speech. TTS is optimized for realtime or interactive scenarios. For offline scenarios, TTS-HD provides higher quality. The API supports six different voices.</para>
        ///   <para>
        ///     <b>Key model capabilities</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>TTS: optimized for speed.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>TTS-HD: optimized for quality.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Use cases</b>
        ///   </para>
        ///   <para>See Responsible AI for additional considerations for responsible use.</para>
        ///   <para>
        ///     <b>Key use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Out of scope use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>Pricing is based on a number of factors, including deployment type and tokens used. <see href="https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067">See pricing details here.</see></para>
        ///   <para>
        ///     <b>Technical specs</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training cut-off date</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training time</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Input formats</b>
        ///   </para>
        ///   <para>Max request data size: 4,096 chars can be converted from text to speech per API request.</para>
        ///   <para>
        ///     <b>Output formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Supported languages</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Sample JSON response</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Model architecture</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Long context</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Optimizing model performance</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Additional assets</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training disclosure</b>
        ///   </para>
        ///   <para>
        ///     <b>Training, testing and validation</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Distribution</b>
        ///   </para>
        ///   <para>
        ///     <b>Distribution channels</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>More information</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        /// </summary>
        public static readonly FoundryModel Tts = new() { Name = "tts", Version = "001", Format = "OpenAI" };

        /// <summary>
        ///   <para>
        ///     <b>Direct from Azure models</b>
        ///   </para>
        ///   <para>Direct from Azure models are a select portfolio curated for their market-differentiated capabilities:</para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure's enterprise-grade infrastructure.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>Learn more about <see href="https://aka.ms/DirectfromAzure">Direct from Azure models</see>.</para>
        ///   <para>
        ///     <b>Key capabilities</b>
        ///   </para>
        ///   <para>
        ///     <b>About this model</b>
        ///   </para>
        ///   <para>TTS-HD is a model that converts text to natural sounding speech.</para>
        ///   <para>
        ///     <b>Key model capabilities</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>TTS: optimized for speed.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>TTS-HD: optimized for quality.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Use cases</b>
        ///   </para>
        ///   <para>See Responsible AI for additional considerations for responsible use.</para>
        ///   <para>
        ///     <b>Key use cases</b>
        ///   </para>
        ///   <para>TTS is optimized for realtime or interactive scenarios. For offline scenarios, TTS-HD provides higher quality.</para>
        ///   <para>
        ///     <b>Out of scope use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>Pricing is based on a number of factors, including deployment type and tokens used. <see href="https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067">See pricing details here.</see></para>
        ///   <para>
        ///     <b>Technical specs</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training cut-off date</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training time</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Input formats</b>
        ///   </para>
        ///   <para>Max request data size: 4,096 chars can be converted from text to speech per API request.</para>
        ///   <para>
        ///     <b>Output formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Supported languages</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Sample JSON response</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Model architecture</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Long context</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Optimizing model performance</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Additional assets</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training disclosure</b>
        ///   </para>
        ///   <para>
        ///     <b>Training, testing and validation</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Distribution</b>
        ///   </para>
        ///   <para>
        ///     <b>Distribution channels</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>More information</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        /// </summary>
        public static readonly FoundryModel TtsHd = new() { Name = "tts-hd", Version = "001", Format = "OpenAI" };

        /// <summary>
        ///   <para>
        ///     <b>Direct from Azure models</b>
        ///   </para>
        ///   <para>Direct from Azure models are a select portfolio curated for their market-differentiated capabilities:</para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure's enterprise-grade infrastructure.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>Learn more about <see href="https://aka.ms/DirectfromAzure">Direct from Azure models</see>.</para>
        ///   <para>
        ///     <b>Key capabilities</b>
        ///   </para>
        ///   <para>
        ///     <b>About this model</b>
        ///   </para>
        ///   <para>The Whisper models are trained for speech recognition and translation tasks, capable of transcribing speech audio into the text in the language it is spoken (automatic speech recognition) as well as translated into English (speech translation).</para>
        ///   <para>
        ///     <b>Key model capabilities</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Speech recognition (automatic speech recognition)</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Speech translation into English</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Processing of audio up to 25mb per API request</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Use cases</b>
        ///   </para>
        ///   <para>See Responsible AI for additional considerations for responsible use.</para>
        ///   <para>
        ///     <b>Key use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Out of scope use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>Pricing is based on a number of factors, including deployment type and tokens used. <see href="https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067">See pricing details here.</see></para>
        ///   <para>
        ///     <b>Technical specs</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training cut-off date</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training time</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Input formats</b>
        ///   </para>
        ///   <para>Max request data size: 25mb of audio can be converted from speech to text per API request.</para>
        ///   <para>
        ///     <b>Output formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Supported languages</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Sample JSON response</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Model architecture</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Long context</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Optimizing model performance</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Additional assets</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training disclosure</b>
        ///   </para>
        ///   <para>
        ///     <b>Training, testing and validation</b>
        ///   </para>
        ///   <para>Researchers at OpenAI developed the models to study the robustness of speech processing systems trained under large-scale weak supervision.</para>
        ///   <para>
        ///     <b>Distribution</b>
        ///   </para>
        ///   <para>
        ///     <b>Distribution channels</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>More information</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        /// </summary>
        public static readonly FoundryModel Whisper = new() { Name = "whisper", Version = "001", Format = "OpenAI" };
    }

    /// <summary>
    /// Models published by Stability AI.
    /// </summary>
    public static partial class StabilityAI
    {
        /// <summary>
        ///   <para>
        ///     <b>Models from Microsoft, Partners, and Community</b>
        ///   </para>
        ///   <para>Models from Microsoft, Partners, and Community models are a select portfolio of curated models both general-purpose and niche models across diverse scenarios by developed by Microsoft teams, partners, and community contributors</para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Managed by Microsoft:</b> Purchase and manage models directly through Azure with a single license, world class support and enterprise grade Azure infrastructure</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Validated by providers:</b> Each model is validated and maintained by its respective provider, with Azure offering integration and deployment guidance.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Innovation and agility:</b> Combines Microsoft research models with rapid, community-driven advancements.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Seamless Azure integration:</b> Standard Azure AI Foundry experience, with support managed by the model provider.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Flexible deployment:</b> Deployable as <b>Managed Compute</b> or <b>Serverless API</b>, based on provider preference.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <see href="https://aka.ms/Azure1P3PModels">Learn more about models from Microsoft, Partners, and Community</see>
        ///   </para>
        ///   <para>
        ///     <b>Key capabilities</b>
        ///   </para>
        ///   <para>
        ///     <b>About this model</b>
        ///   </para>
        ///   <para>Stable Diffusion 3.5 Large produces diverse outputs, creating images that are representative of the world, with different skin tones and features, all without requiring extensive prompting.</para>
        ///   <para>It also offers unmatched versatility, generating visuals in virtually any style, from 3D and photography to painting and line art. Our analysis shows that Stable Diffusion 3.5 Large leads the market in prompt adherence and rivals significantly larger models in image quality.</para>
        ///   <para>
        ///     <b>Key model capabilities</b>
        ///   </para>
        ///   <para>Stable Diffusion 3.5 Large produces diverse outputs, creating images that are representative of the world, with different skin tones and features, all without requiring extensive prompting.</para>
        ///   <para>It also offers unmatched versatility, generating visuals in virtually any style, from 3D and photography to painting and line art. Our analysis shows that Stable Diffusion 3.5 Large leads the market in prompt adherence and rivals significantly larger models in image quality.</para>
        ///   <para>
        ///     <b>Use cases</b>
        ///   </para>
        ///   <para>See Responsible AI for additional considerations for responsible use.</para>
        ///   <para>
        ///     <b>Key use cases</b>
        ///   </para>
        ///   <para>Advertising and marketing Media and entertainment, Gaming and metaverse Education and training Retail Publishing</para>
        ///   <para>
        ///     <b>Out of scope use cases</b>
        ///   </para>
        ///   <para>The model was not trained to be factual or true representations of people or events. As such, using the model to generate such content is out-of-scope of the abilities of this model.</para>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>Pricing is based on a number of factors, including deployment type and tokens used. <see href="https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067">See pricing details here.</see></para>
        ///   <para>
        ///     <b>Technical specs</b>
        ///   </para>
        ///   <para>At 8.1 billion parameters, with superior quality and prompt adherence, this base model is the most powerful in the Stable Diffusion family. This model is ideal for professional use cases at 1 megapixel resolution.</para>
        ///   <para>
        ///     <b>Training cut-off date</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training time</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Input formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Output formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Supported languages</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Sample JSON response</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Model architecture</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Long context</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Optimizing model performance</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Additional assets</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training disclosure</b>
        ///   </para>
        ///   <para>
        ///     <b>Training, testing and validation</b>
        ///   </para>
        ///   <para>This model was trained on a wide variety of data, including synthetic data and filtered publicly available data.</para>
        ///   <para>
        ///     <b>Distribution</b>
        ///   </para>
        ///   <para>
        ///     <b>Distribution channels</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>More information</b>
        ///   </para>
        ///   <para>We believe in safe, responsible AI practices and take deliberate measures to ensure Integrity starts at the early stages of development. This means we have taken and continue to take reasonable steps to prevent the misuse of Stable Diffusion 3.5 by bad actors. For more information about our approach to Safety please visit our Stable Safety page.</para>
        /// </summary>
        public static readonly FoundryModel StableDiffusion35Large = new() { Name = "Stable-Diffusion-3.5-Large", Version = "1", Format = "Stability AI" };

        /// <summary>
        ///   <para>
        ///     <b>Models from Microsoft, Partners, and Community</b>
        ///   </para>
        ///   <para>Models from Microsoft, Partners, and Community models are a select portfolio of curated models both general-purpose and niche models across diverse scenarios by developed by Microsoft teams, partners, and community contributors</para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Managed by Microsoft:</b> Purchase and manage models directly through Azure with a single license, world class support and enterprise grade Azure infrastructure</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Validated by providers:</b> Each model is validated and maintained by its respective provider, with Azure offering integration and deployment guidance.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Innovation and agility:</b> Combines Microsoft research models with rapid, community-driven advancements.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Seamless Azure integration:</b> Standard Azure AI Foundry experience, with support managed by the model provider.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Flexible deployment:</b> Deployable as <b>Managed Compute</b> or <b>Serverless API</b>, based on provider preference.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <see href="https://aka.ms/Azure1P3PModels">Learn more about models from Microsoft, Partners, and Community</see>
        ///   </para>
        ///   <para>
        ///     <b>Key capabilities</b>
        ///   </para>
        ///   <para>
        ///     <b>About this model</b>
        ///   </para>
        ///   <para>Leveraging an enhanced version of SDXL, Stable Image Core, delivers exceptional speed and efficiency while maintaining the high-quality output synonymous with Stable Diffusion models.</para>
        ///   <para>
        ///     <b>Key model capabilities</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Use cases</b>
        ///   </para>
        ///   <para>See Responsible AI for additional considerations for responsible use.</para>
        ///   <para>
        ///     <b>Key use cases</b>
        ///   </para>
        ///   <list type="number">
        ///     <item>
        ///       <description>
        ///         <para>Advertising and marketing</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Media and entertainment</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Gaming and metaverse</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Education and training</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Retail</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Publishing</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Out of scope use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>Pricing is based on a number of factors, including deployment type and tokens used. <see href="https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067">See pricing details here.</see></para>
        ///   <para>
        ///     <b>Technical specs</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training cut-off date</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training time</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Input formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Output formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Supported languages</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Sample JSON response</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Model architecture</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Long context</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Optimizing model performance</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Additional assets</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training disclosure</b>
        ///   </para>
        ///   <para>
        ///     <b>Training, testing and validation</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Distribution</b>
        ///   </para>
        ///   <para>
        ///     <b>Distribution channels</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>More information</b>
        ///   </para>
        ///   <para>We believe in safe, responsible AI practices and take deliberate measures to ensure Integrity starts at the early stages of development. For more information about our approach to Safety please visit our <see href="https://stability.ai/safety">Stable Safety</see> page.</para>
        /// </summary>
        public static readonly FoundryModel StableImageCore = new() { Name = "Stable-Image-Core", Version = "1", Format = "Stability AI" };

        /// <summary>
        ///   <para>
        ///     <b>Models from Microsoft, Partners, and Community</b>
        ///   </para>
        ///   <para>Models from Microsoft, Partners, and Community models are a select portfolio of curated models both general-purpose and niche models across diverse scenarios by developed by Microsoft teams, partners, and community contributors</para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Managed by Microsoft:</b> Purchase and manage models directly through Azure with a single license, world class support and enterprise grade Azure infrastructure</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Validated by providers:</b> Each model is validated and maintained by its respective provider, with Azure offering integration and deployment guidance.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Innovation and agility:</b> Combines Microsoft research models with rapid, community-driven advancements.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Seamless Azure integration:</b> Standard Azure AI Foundry experience, with support managed by the model provider.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Flexible deployment:</b> Deployable as <b>Managed Compute</b> or <b>Serverless API</b>, based on provider preference.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <see href="https://aka.ms/Azure1P3PModels">Learn more about models from Microsoft, Partners, and Community</see>
        ///   </para>
        ///   <para>
        ///     <b>Key capabilities</b>
        ///   </para>
        ///   <para>
        ///     <b>About this model</b>
        ///   </para>
        ///   <para>Powered by the advanced capabilities of Stable Diffusion 3.5 Large, Stable Image Ultra sets a new standard in photorealism. It also excels in typography, dynamic lighting, and vibrant color rendering.</para>
        ///   <para>
        ///     <b>Key model capabilities</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Typography</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Dynamic lighting</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Vibrant color rendering</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Product imagery for marketing and advertising</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Use cases</b>
        ///   </para>
        ///   <para>See Responsible AI for additional considerations for responsible use.</para>
        ///   <para>
        ///     <b>Key use cases</b>
        ///   </para>
        ///   <list type="number">
        ///     <item>
        ///       <description>
        ///         <para>Advertising and marketing</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Media and entertainment</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Gaming and metaverse</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Education and training</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Retail</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Publishing</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Out of scope use cases</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Pricing</b>
        ///   </para>
        ///   <para>Pricing is based on a number of factors, including deployment type and tokens used. <see href="https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067">See pricing details here.</see></para>
        ///   <para>
        ///     <b>Technical specs</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training cut-off date</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training time</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Input formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Output formats</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Supported languages</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Sample JSON response</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Model architecture</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Long context</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Optimizing model performance</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Additional assets</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Training disclosure</b>
        ///   </para>
        ///   <para>
        ///     <b>Training, testing and validation</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>Distribution</b>
        ///   </para>
        ///   <para>
        ///     <b>Distribution channels</b>
        ///   </para>
        ///   <para>The provider has not supplied this information.</para>
        ///   <para>
        ///     <b>More information</b>
        ///   </para>
        ///   <para>We believe in safe, responsible AI practices and take deliberate measures to ensure Integrity starts at the early stages of development. For more information about our approach to Safety please visit our <see href="https://stability.ai/safety">Stable Safety</see> page.</para>
        /// </summary>
        public static readonly FoundryModel StableImageUltra = new() { Name = "Stable-Image-Ultra", Version = "1", Format = "Stability AI" };
    }

    /// <summary>
    /// Models published by xAI.
    /// </summary>
    public static partial class XAI
    {
        /// <summary>
        /// Grok 3 is xAI's debut model, pretrained by Colossus at supermassive scale to excel in specialized domains like finance, healthcare, and the law.
        /// </summary>
        public static readonly FoundryModel Grok3 = new() { Name = "grok-3", Version = "1", Format = "xAI" };

        /// <summary>
        /// Grok 3 Mini is a lightweight model that thinks before responding. Trained on mathematic and scientific problems, it is great for logic-based tasks.
        /// </summary>
        public static readonly FoundryModel Grok3Mini = new() { Name = "grok-3-mini", Version = "1", Format = "xAI" };

        /// <summary>
        /// Grok 4 is the latest reasoning model from xAI with advanced reasoning and tool-use capabilities, enabling it to achieve new state-of-the-art performance across challenging academic and industry benchmarks.
        /// </summary>
        public static readonly FoundryModel Grok4 = new() { Name = "grok-4", Version = "1", Format = "xAI" };

        /// <summary>
        /// Grok 4.1 Fast Non‑Reasoning is designed for low‑latency, near‑instant responses, emphasizing speed, high‑quality outputs, and smooth tool‑calling in agentic workflows, making it well‑suited for high‑throughput, real‑time scenarios where immediate responses
        /// </summary>
        public static readonly FoundryModel Grok41FastNonReasoning = new() { Name = "grok-4-1-fast-non-reasoning", Version = "1", Format = "xAI" };

        /// <summary>
        /// Grok 4.1 Fast Reasoning is a frontier multimodal model built for high‑performance, agentic execution—combining strong reasoning, advanced tool calling, and agentic search to handle complex tasks with speed and precision. It delivers natural, fluid dialogue
        /// </summary>
        public static readonly FoundryModel Grok41FastReasoning = new() { Name = "grok-4-1-fast-reasoning", Version = "1", Format = "xAI" };

        /// <summary>
        /// Grok 4 Fast is an efficiency-focused large language model developed by xAI, pre-trained on general-purpose data and post-trained on task demonstrations and tool use, with built-in safety features including refusal behaviors, a fixed system prompt enforcing
        /// </summary>
        public static readonly FoundryModel Grok4FastNonReasoning = new() { Name = "grok-4-fast-non-reasoning", Version = "1", Format = "xAI" };

        /// <summary>
        /// Grok 4 Fast is an efficiency-focused large language model developed by xAI, pre-trained on general-purpose data and post-trained on task demonstrations and tool use, with built-in safety features including refusal behaviors, a fixed system prompt enforcing
        /// </summary>
        public static readonly FoundryModel Grok4FastReasoning = new() { Name = "grok-4-fast-reasoning", Version = "1", Format = "xAI" };

        /// <summary>
        /// Grok Code Fast 1 is a fast, economical AI model for agentic coding, built from scratch with a new architecture, trained on programming-rich data, and fine-tuned for real-world coding tasks like bug fixes and project setup.
        /// </summary>
        public static readonly FoundryModel GrokCodeFast1 = new() { Name = "grok-code-fast-1", Version = "1", Format = "xAI" };
    }
}
