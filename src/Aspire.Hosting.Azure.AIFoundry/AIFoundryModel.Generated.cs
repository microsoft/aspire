// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
namespace Aspire.Hosting.Azure;

/// <summary>
/// Generated strongly typed model descriptors for Azure AI Foundry.
/// </summary>
public partial class AIFoundryModel
{
    /// <summary>
    /// Models published by AI21 Labs.
    /// </summary>
    public static partial class AI21Labs
    {
        /// <summary>
        /// A 398B parameters (94B active) multilingual model, offering a 256K long context window, function calling, structured output, and grounded generation.
        /// </summary>
        public static readonly AIFoundryModel AI21Jamba15Large = new() { Name = "AI21-Jamba-1.5-Large", Version = "1", Format = "AI21 Labs" };

        /// <summary>
        /// A 52B parameters (12B active) multilingual model, offering a 256K long context window, function calling, structured output, and grounded generation.
        /// </summary>
        public static readonly AIFoundryModel AI21Jamba15Mini = new() { Name = "AI21-Jamba-1.5-Mini", Version = "1", Format = "AI21 Labs" };
    }

    /// <summary>
    /// Models published by Anthropic.
    /// </summary>
    public static partial class Anthropic
    {
        /// <summary>
        /// Claude Haiku 4.5 delivers near-frontier performance for a wide range of use cases, and stands out as one of the best coding and agent models – with the right speed and cost to power free products and scaled sub-agents.
        /// </summary>
        public static readonly AIFoundryModel ClaudeHaiku45 = new() { Name = "claude-haiku-4-5", Version = "20251001", Format = "Anthropic" };

        /// <summary>
        /// Claude Opus 4.1 is an industry leader for coding. It delivers sustained performance on long-running tasks that require focused effort and thousands of steps, significantly expanding what AI agents can solve.
        /// </summary>
        public static readonly AIFoundryModel ClaudeOpus41 = new() { Name = "claude-opus-4-1", Version = "20250805", Format = "Anthropic" };

        /// <summary>
        /// Claude Opus 4.5 is Anthropic’s most intelligent model, and an industry leader across coding, agents, computer use, and enterprise workflows. With a 200K token context window and 64K max output, Opus 4.5 is ideal for production code, sophisticated agents, o
        /// </summary>
        public static readonly AIFoundryModel ClaudeOpus45 = new() { Name = "claude-opus-4-5", Version = "20251101", Format = "Anthropic" };

        /// <summary>
        /// Claude Sonnet 4.5 is Anthropic&apos;s most capable model for complex agents and an industry leader for coding and computer use.
        /// </summary>
        public static readonly AIFoundryModel ClaudeSonnet45 = new() { Name = "claude-sonnet-4-5", Version = "20250929", Format = "Anthropic" };
    }

    /// <summary>
    /// Models published by Black Forest Labs.
    /// </summary>
    public static partial class BlackForestLabs
    {
        /// <summary>
        /// Generate images with amazing image quality, prompt adherence, and diversity at blazing fast speeds. FLUX1.1 [pro] delivers six times faster image generation and achieved the highest Elo score on Artificial Analysis benchmarks when launched, surpassing all
        /// </summary>
        public static readonly AIFoundryModel Flux11Pro = new() { Name = "FLUX-1.1-pro", Version = "1", Format = "Black Forest Labs" };

        /// <summary>
        /// Generate and edit images through both text and image prompts. FLUX.1 Kontext is a multimodal flow matching model that enables both text-to-image generation and in-context image editing. Modify images while maintaining character consistency and performing l
        /// </summary>
        public static readonly AIFoundryModel Flux1KontextPro = new() { Name = "FLUX.1-Kontext-pro", Version = "1", Format = "Black Forest Labs" };
    }

    /// <summary>
    /// Models published by Cohere.
    /// </summary>
    public static partial class Cohere
    {
        /// <summary>
        /// Command A is a highly efficient generative model that excels at agentic and multilingual use cases.
        /// </summary>
        public static readonly AIFoundryModel CohereCommandA = new() { Name = "cohere-command-a", Version = "4", Format = "Cohere" };

        /// <summary>
        /// Command R is a scalable generative model targeting RAG and Tool Use to enable production-scale AI for enterprise.
        /// </summary>
        public static readonly AIFoundryModel CohereCommandR = new() { Name = "Cohere-command-r", Version = "1", Format = "Cohere" };

        /// <summary>
        /// Command R is a scalable generative model targeting RAG and Tool Use to enable production-scale AI for enterprise.
        /// </summary>
        public static readonly AIFoundryModel CohereCommandR082024 = new() { Name = "Cohere-command-r-08-2024", Version = "1", Format = "Cohere" };

        /// <summary>
        /// Command R+ is a state-of-the-art RAG-optimized model designed to tackle enterprise-grade workloads.
        /// </summary>
        public static readonly AIFoundryModel CohereCommandRPlus = new() { Name = "Cohere-command-r-plus", Version = "1", Format = "Cohere" };

        /// <summary>
        /// Command R+ is a state-of-the-art RAG-optimized model designed to tackle enterprise-grade workloads.
        /// </summary>
        public static readonly AIFoundryModel CohereCommandRPlus082024 = new() { Name = "Cohere-command-r-plus-08-2024", Version = "1", Format = "Cohere" };

        /// <summary>
        /// Cohere Embed English is the market&apos;s leading text representation model used for semantic search, retrieval-augmented generation (RAG), classification, and clustering.
        /// </summary>
        public static readonly AIFoundryModel CohereEmbedV3English = new() { Name = "Cohere-embed-v3-english", Version = "1", Format = "Cohere" };

        /// <summary>
        /// Cohere Embed Multilingual is the market&apos;s leading text representation model used for semantic search, retrieval-augmented generation (RAG), classification, and clustering.
        /// </summary>
        public static readonly AIFoundryModel CohereEmbedV3Multilingual = new() { Name = "Cohere-embed-v3-multilingual", Version = "1", Format = "Cohere" };

        /// <summary>
        /// Rerank improves search systems by sorting documents based on their semantic similarity to a query
        /// </summary>
        public static readonly AIFoundryModel CohereRerankV40Fast = new() { Name = "Cohere-rerank-v4.0-fast", Version = "2", Format = "Cohere" };

        /// <summary>
        /// Rerank improves search systems by sorting documents based on their semantic similarity to a query
        /// </summary>
        public static readonly AIFoundryModel CohereRerankV40Pro = new() { Name = "Cohere-rerank-v4.0-pro", Version = "1", Format = "Cohere" };

        /// <summary>
        /// Embed 4 transforms texts and images into numerical vectors
        /// </summary>
        public static readonly AIFoundryModel EmbedV40 = new() { Name = "embed-v-4-0", Version = "6", Format = "Cohere" };
    }

    /// <summary>
    /// Models published by Core42.
    /// </summary>
    public static partial class Core42
    {
        /// <summary>
        /// JAIS 30b Chat is an auto-regressive bilingual LLM for Arabic &amp; English with state-of-the-art capabilities in Arabic.
        /// </summary>
        public static readonly AIFoundryModel Jais30bChat = new() { Name = "jais-30b-chat", Version = "3", Format = "Core42" };
    }

    /// <summary>
    /// Models published by DeepSeek.
    /// </summary>
    public static partial class DeepSeek
    {
        /// <summary>
        /// DeepSeek-R1 excels at reasoning tasks using a step-by-step training process, such as language, scientific reasoning, and coding tasks.
        /// </summary>
        public static readonly AIFoundryModel DeepSeekR1 = new() { Name = "DeepSeek-R1", Version = "1", Format = "DeepSeek" };

        /// <summary>
        /// The DeepSeek R1 0528 model has improved reasoning capabilities, this version also offers a reduced hallucination rate, enhanced support for function calling, and better experience for vibe coding.
        /// </summary>
        public static readonly AIFoundryModel DeepSeekR10528 = new() { Name = "DeepSeek-R1-0528", Version = "1", Format = "DeepSeek" };

        /// <summary>
        /// A strong Mixture-of-Experts (MoE) language model with 671B total parameters with 37B activated for each token.
        /// </summary>
        public static readonly AIFoundryModel DeepSeekV3 = new() { Name = "DeepSeek-V3", Version = "1", Format = "DeepSeek" };

        /// <summary>
        /// DeepSeek-V3-0324 demonstrates notable improvements over its predecessor, DeepSeek-V3, in several key aspects, including enhanced reasoning, improved function calling, and superior code generation capabilities.
        /// </summary>
        public static readonly AIFoundryModel DeepSeekV30324 = new() { Name = "DeepSeek-V3-0324", Version = "1", Format = "DeepSeek" };

        /// <summary>
        /// DeepSeek-V3.1 is a hybrid model that enhances tool usage, thinking efficiency, and supports both thinking and non-thinking modes via chat template switching
        /// </summary>
        public static readonly AIFoundryModel DeepSeekV31 = new() { Name = "DeepSeek-V3.1", Version = "1", Format = "DeepSeek" };
    }

    /// <summary>
    /// Models published by Meta.
    /// </summary>
    public static partial class Meta
    {
        /// <summary>
        /// Excels in image reasoning capabilities on high-res images for visual understanding apps.
        /// </summary>
        public static readonly AIFoundryModel Llama3211BVisionInstruct = new() { Name = "Llama-3.2-11B-Vision-Instruct", Version = "6", Format = "Meta" };

        /// <summary>
        /// Advanced image reasoning capabilities for visual understanding agentic apps.
        /// </summary>
        public static readonly AIFoundryModel Llama3290BVisionInstruct = new() { Name = "Llama-3.2-90B-Vision-Instruct", Version = "5", Format = "Meta" };

        /// <summary>
        /// Llama 3.3 70B Instruct offers enhanced reasoning, math, and instruction following with performance comparable to Llama 3.1 405B.
        /// </summary>
        public static readonly AIFoundryModel Llama3370BInstruct = new() { Name = "Llama-3.3-70B-Instruct", Version = "9", Format = "Meta" };

        /// <summary>
        /// Llama 4 Maverick 17B 128E Instruct FP8 is great at precise image understanding and creative writing, offering high quality at a lower price compared to Llama 3.3 70B
        /// </summary>
        public static readonly AIFoundryModel Llama4Maverick17B128EInstructFP8 = new() { Name = "Llama-4-Maverick-17B-128E-Instruct-FP8", Version = "1", Format = "Meta" };

        /// <summary>
        /// Llama 4 Scout 17B 16E Instruct is great at multi-document summarization, parsing extensive user activity for personalized tasks, and reasoning over vast codebases.
        /// </summary>
        public static readonly AIFoundryModel Llama4Scout17B16EInstruct = new() { Name = "Llama-4-Scout-17B-16E-Instruct", Version = "2", Format = "Meta" };

        /// <summary>
        /// A powerful 70-billion parameter model excelling in reasoning, coding, and broad language applications.
        /// </summary>
        public static readonly AIFoundryModel MetaLlama370BInstruct = new() { Name = "Meta-Llama-3-70B-Instruct", Version = "9", Format = "Meta" };

        /// <summary>
        /// A versatile 8-billion parameter model optimized for dialogue and text generation tasks.
        /// </summary>
        public static readonly AIFoundryModel MetaLlama38BInstruct = new() { Name = "Meta-Llama-3-8B-Instruct", Version = "9", Format = "Meta" };

        /// <summary>
        /// The Llama 3.1 instruction tuned text only models are optimized for multilingual dialogue use cases and outperform many of the available open source and closed chat models on common industry benchmarks.
        /// </summary>
        public static readonly AIFoundryModel MetaLlama31405BInstruct = new() { Name = "Meta-Llama-3.1-405B-Instruct", Version = "1", Format = "Meta" };

        /// <summary>
        /// The Llama 3.1 instruction tuned text only models are optimized for multilingual dialogue use cases and outperform many of the available open source and closed chat models on common industry benchmarks.
        /// </summary>
        public static readonly AIFoundryModel MetaLlama3170BInstruct = new() { Name = "Meta-Llama-3.1-70B-Instruct", Version = "4", Format = "Meta" };

        /// <summary>
        /// The Llama 3.1 instruction tuned text only models are optimized for multilingual dialogue use cases and outperform many of the available open source and closed chat models on common industry benchmarks.
        /// </summary>
        public static readonly AIFoundryModel MetaLlama318BInstruct = new() { Name = "Meta-Llama-3.1-8B-Instruct", Version = "5", Format = "Meta" };
    }

    /// <summary>
    /// Models published by Microsoft.
    /// </summary>
    public static partial class Microsoft
    {
        /// <summary>
        /// ## Azure AI Content Safety ## Introduction Azure AI Content Safety is a safety system for monitoring content generated by both foundation models and humans. Detect and block potential risks, threats, and quality problems. You can build an advanced safety system for foundation models to detect and mitigate harmful content and risks in user prompts and AI-generated outputs. Use Prompt Shields to detect and block prompt injection attacks, groundedness detection to pinpoint ungrounded or hallucinated materials, and protected material detection to identify copyrighted or owned content. ## Core Features - **Block harmful input and output**  - **Description**: Detect and block violence, hate, sexual, and self-harm content for both text, images and multimodal. Configure severity thresholds for your specific use case and adhere to your responsible AI policies.  - **Key Features**: Violence, hate, sexual, and self-harm content detection. Custom blocklist. - **Policy customization with custom categories**  - **Description**: Create unique content filters tailored to your requirements using custom categories. Quickly train a new custom category by providing examples of content you need to block.  - **Key Features**: Custom categories - **Identify the security risks**  - **Description**: Safeguard your AI applications against prompt injection attacks and jailbreak attempts. Identify and mitigate both direct and indirect threats with prompt shields.  - **Key Features**: Direct jailbreak attack, indirect prompt injection from docs. - **Detect and correct Gen AI hallucinations**  - **Description**: Identify and correct generative AI hallucinations and ensure outputs are reliable, accurate, and grounded in data with groundedness detection.  - **Key Features**: Groundedness detection, reasoning, and correction. - **Identify protected material**  - **Description**: Pinpoint copyrighted content and provide sources for preexisting text and code with protected material detection.  - **Key Features**: Protected material for code, protected material for text ## Use Cases - Generative AI services screen user-submitted prompts and generated outputs to ensure safe and appropriate content. - Online marketplaces monitor and filter product listings and other user-generated content to prevent harmful or inappropriate material. - Gaming platforms manage and moderate user-created game content and in-game communication to maintain a safe environment. - Social media platforms review and regulate user-uploaded images and posts to enforce community standards and prevent harmful content. - Enterprise media companies implement centralized content moderation systems to ensure the safety and appropriateness of their published materials. - K-12 educational technology providers filter out potentially harmful or inappropriate content to create a safe learning environment for students and educators. ## Benefits - **No ML experience required**: Incorporate content safety features into your projects with no machine learning experience required. - **Effortlessly customize your RAI policies**: Customizing your content safety classifiers can be done with one line of description, a few samples using Custom Categories. - **State of the art models**: ready for use APIs, SOTA models, and flexible deployment options reduce the need for ongoing manual training or extensive customization. Microsoft has a science team and policy experts working on the frontier of Gen AI to constantly improve the safety and security models to ensure our customers can develop and deploy generative AI safely and responsibly. - **Global Reach**: Support more than 100 languages, enabling businesses to communicate effectively with customers, partners, and employees worldwide. - **Scalable and Reliable**: Built on Azure’s cloud infrastructure, the Azure AI Content Safety service scales automatically to meet demand, from small business applications to global enterprise workloads. - **Security and Compliance**: Azure AI Content Safety runs on Azure’s secure cloud infrastructure, ensuring data privacy and compliance with global standards. User data is not stored after the translation process. - **Flexible deployment**: Azure AI Content Safety can be deployed on cloud, on premises and on devices. ## Technical Details - **Deployment**  - **Container for on-premise deployment**: [Content safety containers overview - Azure AI Content Safety - Azure AI services | Microsoft Learn](https://learn.microsoft.com/azure/ai-services/content-safety/how-to/containers/container-overview)  - **Embedded Content Safety**: [Embedded Content Safety - Azure AI Content Safety - Azure AI services | Microsoft Learn](https://learn.microsoft.com/azure/ai-services/content-safety/how-to/embedded-content-safety?tabs=windows-target%2Ctext)  - **Cloud**: [Azure AI Content Safety documentation - Quickstarts, Tutorials, API Reference - Azure AI services | Microsoft Learn](https://learn.microsoft.com/azure/ai-services/content-safety/) - **Requirements**: Requirements vary feature by feature, for more details, refer to the Azure AI Content Safety documentation: [Azure AI Content Safety documentation - Quickstarts, Tutorials, API Reference - Azure AI services | Microsoft Learn](https://learn.microsoft.com/azure/ai-services/content-safety/). - **Support**: Azure AI Content Safety is part of Azure AI Services. Support options for AI Services can be found here: [Azure AI services support and help options - Azure AI services | Microsoft Learn](https://learn.microsoft.com/azure/ai-services/cognitive-services-support-options?context=%2Fazure%2Fai-services%2Fcontent-safety%2Fcontext%2Fcontext). ## Pricing Explore pricing options here: [Azure AI Content Safety - Pricing | Microsoft Azure](https://azure.microsoft.com/pricing/details/cognitive-services/content-safety/).
        /// </summary>
        public static readonly AIFoundryModel AzureAIContentSafety = new() { Name = "Azure-AI-Content-Safety", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// # Azure AI Content Understanding  ## Introduction Azure AI Content Understanding empowers you to transform unstructured multimodal data—such as text, images, audio, and video—into structured, actionable insights. By streamlining content processing with advanced AI techniques like schema extraction and grounding, it delivers accurate structured data for downstream applications. Offering prebuilt templates for common use cases and customizable models, it helps you unify diverse data types into a single, efficient pipeline, optimizing workflows and accelerating time to value. ## Core Features  - **Multimodal data ingestion**  Ingest a range of modalities such as documents, images, audio, or video. Use a variety of AI models to convert the input data into a structured format that can be easily processed and analyzed by downstream services or applications. - **Customizable output schemas**  Customize the schemas of extracted results to meet your specific needs. Tailor the format and structure of summaries, insights, or features to include only the most relevant details—such as key points or timestamps—from video or audio files.  - **Confidence scores** Leverage confidence scores to minimize human intervention and continuously improve accuracy through user feedback. - **Output ready for downstream applications**  Automate business processes by building enterprise AI apps or agentic workflows. Use outputs that downstream applications can consume for reasoning with retrieval-augmented generation (RAG). - **Grounding** Ensure the information extracted, inferred, or abstracted is represented in the underlying content. - **Automatic labeling** Save time and effort on manual annotation and create models quicker by using large language models (LLMs) to extract fields from various document types. ## Use Cases  - **Post-call analytics for call centers**: Generate insights from call recordings, track key performance indicators (KPIs), and answer customer questions more accurately and efficiently.  - **Tax process automation**: Streamline the tax return process by extracting data from tax forms to create a consolidated view of information across various documents.  - **Media asset management**: Extract features from images and videos to provide richer tools for targeted content and enhance media asset management solutions.  - **Chart understanding**: Enhance chart understanding by automating the analysis and interpretation of various types of charts and diagrams using Content Understanding. ## Benefits  - **Streamline workflows**: Azure AI Content Understanding standardizes the extraction of content, structure, and insights from various content types into a unified process. - **Simplify field extraction**: Field extraction in Content Understanding makes it easier to generate structured output from unstructured content. Define a schema to extract, classify, or generate field values with no complex prompt engineering. - **Enhance accuracy**: Content Understanding employs multiple AI models to analyze and cross-validate information simultaneously, resulting in more accurate and reliable results. - **Confidence scores &amp; grounding**: Content Understanding ensures the accuracy of extracted values while minimizing the cost of human review. ## Technical Details  - **Deployment**: Deployment options may vary by service, reference the following docs for more information: [Create an Azure AI Services multi-service resource](https://learn.microsoft.com/en-us/azure/ai-services/content-understanding/how-to/create-multi-service-resource). - **Requirements**: Requirements may vary depending on the input data you are analyzing, reference the following docs for more information: [Service quotas and limits](https://learn.microsoft.com/en-us/azure/ai-services/content-understanding/service-limits). - **Support**: Support options for AI Services can be found here: [Azure AI services support and help options](https://learn.microsoft.com/en-us/azure/ai-services/cognitive-services-support-options?view=doc-intel-4.0.0).  ## Pricing  View up-to-date pay-as-you-go pricing details here: [Azure AI Content Understanding pricing](https://azure.microsoft.com/en-us/pricing/details/content-understanding/).
        /// </summary>
        public static readonly AIFoundryModel AzureAIContentUnderstanding = new() { Name = "Azure-AI-Content-Understanding", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// ## Azure AI Document Intelligence Document Intelligence is a cloud-based service that enables you to build intelligent document processing solutions. Massive amounts of data, spanning a wide variety of data types, are stored in forms and documents. Document Intelligence enables you to effectively manage the velocity at which data is collected and processed and is key to improved operations, informed data-driven decisions, and enlightened innovation.  ## Core Features - **General extraction models**  - **Description**: General extraction models enable text extraction from forms and documents and return structured business-ready content ready for your organization&apos;s action, use, or development.  - **Key Features**   - Read model allows you to extract written or printed text liens, words, locations, and detected languages.   - Layout model, on top of text extraction, extracts structural information like tables, selection marks, paragraphs, titles, headings, and subheadings. Layout model can also output the extraction results in a Markdown format, enabling you to define your semantic chunking strategy based on provided building blocks, allowing for easier RAG (Retrieval Augmented Generation). - **Prebuilt models**  - **Description**: Prebuilt models enable you to add intelligent document processing to your apps and flows without having to train and build your own models. Prebuilt models extract a pre-defined set of fields depending on the document type.  - **Key Features**   - **Financial Services and Legal Documents**: Credit Cards, Bank Statement, Pay Slip, Check, Invoices, Receipts, Contracts.   - **US Tax Documents**: Unified Tax, W-2, 1099 Combo, 1040 (multiple variations), 1098 (multiple variations), 1099 (multiple variations).   - **US Mortgage Documents**: 1003, 1004, 1005, 1008, Closing Disclosure.   - **Personal Identification Documents**: Identity Documents, Health Insurance Cards, Marriage Certificates. - **Custom models**  - **Description**: Custom models are trained using your labeled datasets to extract distinct data from forms and documents, specific to your use cases. Standalone custom models can be combined to create composed models.  - **Key Features**   - **Document field extraction models**    - **Custom generative**: Build a custom extraction model using generative AI for documents with unstructured format and varying templates.    - **Custom neural**: Extract data from mixed-type documents.    - **Custom template**: Extract data from static layouts.    - **Custom composed**: Extract data using a collection of models. Explicitly choose the classifier and enable confidence-based routing based on the threshold you set.   - **Custom classification models**    - **Custom classifier**: Identify designated document types (classes) before invoking an extraction model. - **Add-on capabilities**  - **Description**: Use the add-on features to extend the results to include more features extracted from your documents. Some add-on features incur an extra cost. These optional features can be enabled and disabled depending on the scenario of the document extraction.  - **Key Features**   - High resolution extraction   - Formula extraction   - Font extraction   - Barcode extraction   - Language detection   - Searchable PDF output ## Use Cases - **Accounts payable**: A company can increase the efficiency of its accounts payable clerks by using the prebuilt invoice model and custom forms to speed up invoice data entry with a human in the loop. The prebuilt invoice model can extract key fields, such as Invoice Total and Shipping Address. - **Insurance form processing**: A customer can train a model by using custom forms to extract a key-value pair in insurance forms and then feeds the data to their business flow to improve the accuracy and efficiency of their process. For their unique forms, customers can build their own model that extracts key values by using custom forms. These extracted values then become actionable data for various workflows within their business. - **Bank form processing**: A bank can use the prebuilt ID model and custom forms to speed up the data entry for &quot;know your customer&quot; documentation, or to speed up data entry for a mortgage packet. If a bank requires their customers to submit personal identification as part of a process, the prebuilt ID model can extract key values, such as Name and Document Number, speeding up the overall time for data entry. - **Robotic process automation (RPA)**: Using the custom extraction model, customers can extract specific data needed from distinct types of documents. The key-value pair extracted can then be entered into various systems such as databases, or CRM systems, through RPA, replacing manual data entry. Customers can also use custom classification model to categorize documents based on their content and file them in proper location. As such, an organized set of data extracted from the custom model can be an essential first step to document RPA scenarios for businesses that manage large volumes of documents regularly. ## Benefits - **No experience required**: Incorporate Document Intelligence features into your projects with no machine learning experience required. - **Effortlessly customize your models**: Training your own custom extraction and classification model can be done with as little as one document labeled, making it easy to train your own models. - **State of the art models**: ready for use APIs, constantly enhanced models, and flexible deployment options reduce the need for ongoing manual training or extensive customization. ## Technical Details: - **Deployment**: Deployment options may vary by service, reference the following docs for more information: [Use Document Intelligence models](https://learn.microsoft.com/azure/ai-services/document-intelligence/how-to-guides/use-sdk-rest-api?view=doc-intel-3.1.0&amp;tabs=linux&amp;pivots=programming-language-rest-api) and [Install and run containers](https://learn.microsoft.com/azure/ai-services/document-intelligence/containers/install-run?view=doc-intel-4.0.0&amp;tabs=read). - **Requirements**: Requirements may vary slightly depending on the model you are using to analyze the documents. Reference the following docs for more information: [Service quotas and limits](https://learn.microsoft.com/azure/ai-services/document-intelligence/service-limits?view=doc-intel-4.0.0). - **Support**: Support options for AI Services can be found here: [Azure AI services support and help options - Azure AI services | Microsoft Learn](https://learn.microsoft.com/azure/ai-services/cognitive-services-support-options?context=%2Fazure%2Fai-services%2Fdocument-intelligence%2Fcontext%2Fcontext&amp;view=doc-intel-4.0.0). ## Pricing View up-to-date pricing information for the pay-as-you-go pricing model here: [Azure AI Document Intelligence pricing](https://azure.microsoft.com/pricing/details/ai-document-intelligence/).
        /// </summary>
        public static readonly AIFoundryModel AzureAIDocumentIntelligence = new() { Name = "Azure-AI-Document-Intelligence", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// ## Azure AI Vision ## Introduction The Azure AI Vision service gives you access to advanced algorithms that process images and videos and return insights based on the visual features and content you are interested in. Azure AI Vision can power a diverse set of scenarios, including digital asset management, video content search &amp; summary, identity verification, generating accessible alt-text for images, and many more. The key product categories for Azure AI Vision include Video Analysis, Image Analysis, Face, and Optical Character Recognition.  ## Core Features - **Video analysis**  - **Description**: Video Analysis includes video-related features like Spatial Analysis and Video Retrieval. Spatial Analysis analyzes the presence and movement of people on a video feed and produces events that other systems can respond to. Video Retrieval lets you create an index of videos that you can search in your natural language.  - **Key Features**: Video retrieval, spatial analysis, person counting, person in a zone, person crossing a line, person distance - **Face**  - **Description**: The Face service provides AI algorithms that detect, recognize, and analyze human faces in images. Facial recognition software is important in many different scenarios, such as identification, touchless access control, and face blurring for privacy.  - **Key Features**: Face detection and analysis, face liveness, face identification, face verification - **Image analysis**  - **Description**: The Image Analysis service extracts many visual features from images, such as objects, faces, adult content, and auto-generated text descriptions.  - **Key Features**: Image tagging, image classification, object detection, image captioning, dense captioning, face detection, optical character recognition, image embeddings, and image search - **Optical character recognition**  - **Description**: The Optical Character Recognition (OCR) service extracts text from images. You can use the Read API to extract printed and handwritten text from photos and documents. It uses deep-learning-based models and works with text on various surfaces and backgrounds. These include business documents, invoices, receipts, posters, business cards, letters, and whiteboards. The OCR APIs support extracting printed text in several languages.  - **Key Features**: OCR ## Use Cases - Boost content discovery with image analysis - Verify identities with the Face service - Search content in videos ## Benefits - **No experience required**: Incorporate vision features into your projects with no machine learning experience required. - **Effortlessly customize your models**: Customizing your image classification and object detection models can be done with as little as one image per tag, making it easy to train your own models. - **State of the art models**: Ready to use APIs, constantly enhanced models, and flexible deployment options reduce the need for ongoing manual training or extensive customization. ## Technical Details - **Deployment**: Deployment options may vary by service, reference the following docs for more information: [Image Analysis Overview](https://learn.microsoft.com/azure/ai-services/computer-vision/overview-image-analysis?tabs=4-0), [Optical Character Recognition Overview](https://learn.microsoft.com/azure/ai-services/computer-vision/overview-ocr), [Video Analysis Overview](https://learn.microsoft.com/azure/ai-services/computer-vision/intro-to-spatial-analysis-public-preview?tabs=sa), and [Face Overview](https://learn.microsoft.com/azure/ai-services/computer-vision/overview-identity). - **Requirements**: Requirements may very slightly depending on the data you are analyzing, reference the following docs for more information: [Image Analysis Overview](https://learn.microsoft.com/azure/ai-services/computer-vision/overview-image-analysis?tabs=4-0), [Optical Character Recognition Overview](https://learn.microsoft.com/azure/ai-services/computer-vision/overview-ocr), [Video Analysis Overview](https://learn.microsoft.com/azure/ai-services/computer-vision/intro-to-spatial-analysis-public-preview?tabs=sa), and [Face Overview](https://learn.microsoft.com/azure/ai-services/computer-vision/overview-identity). - **Support**: Support options for AI Services can be found here: [Azure AI services support and help options - Azure AI services | Microsoft Learn](https://learn.microsoft.com/azure/ai-services/cognitive-services-support-options?context=%2Fazure%2Fai-services%2Fcomputer-vision%2Fcontext%2Fcontext). ## Pricing View up-to-date pricing information for the pay-as-you-go pricing model here: [Azure AI Vision pricing](https://azure.microsoft.com/pricing/details/cognitive-services/computer-vision).
        /// </summary>
        public static readonly AIFoundryModel AzureAIVision = new() { Name = "Azure-AI-Vision", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Language detection quickly and accurately identifies the language of any text, supporting over 100 languages and dialects, including the ISO 15924 standard for a select number of languages.
        /// </summary>
        public static readonly AIFoundryModel AzureLanguageLanguageDetection = new() { Name = "Azure-Language-Language-detection", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// PII Redaction for Text automatically detects and masks sensitive information such as names, addresses, phone numbers, credit card details, and other personally identifiable information (PII) in unstructured text.
        /// </summary>
        public static readonly AIFoundryModel AzureLanguageTextPiiRedaction = new() { Name = "Azure-Language-Text-PII-redaction", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Transcribes streaming or recorded audio into readable text across 140+ languages and dialects. Accuracy can be further optimized with custom models for your specialized use cases.
        /// </summary>
        public static readonly AIFoundryModel AzureSpeechSpeechToText = new() { Name = "Azure-Speech-Speech-to-text", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Text-to-speech enables your applications, tools, or devices to convert text into natural synthesized speech. It leverages advanced out-of-the-box [prebuilt neural voices](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/language-support?t
        /// </summary>
        public static readonly AIFoundryModel AzureSpeechTextToSpeech = new() { Name = "Azure-Speech-Text-to-speech", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Text to speech avatar converts text into a digital video of a human (either a standard avatar or a custom text to speech avatar) speaking with a natural-sounding voice. The text to speech avatar video can be synthesized asynchronously or in real time. Deve
        /// </summary>
        public static readonly AIFoundryModel AzureSpeechTextToSpeechAvatar = new() { Name = "Azure-Speech-Text-to-speech-Avatar", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Voice Live API is a single unified API that enables low-latency, high-quality speech to speech interactions for voice agents.
        /// </summary>
        public static readonly AIFoundryModel AzureSpeechVoiceLive = new() { Name = "Azure-Speech-Voice-Live", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Document translation is a cloud-based, multilingual service that uses AI to translate documents from one language to another while preserving the document layout.
        /// </summary>
        public static readonly AIFoundryModel AzureTranslatorDocumentTranslation = new() { Name = "Azure-Translator-Document-translation", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Text translation is a cloud-based, multilingual service that uses neural machine translation models (NMT) and/or large language models (LLM) to translate text from one language to another, supporting 135 languages.
        /// </summary>
        public static readonly AIFoundryModel AzureTranslatorTextTranslation = new() { Name = "Azure-Translator-Text-translation", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// MAI-DS-R1 is a DeepSeek-R1 reasoning model that has been post-trained by the Microsoft AI team to fill in information gaps in the previous version of the model and improve its harm protections while maintaining R1 reasoning capabilities.
        /// </summary>
        public static readonly AIFoundryModel MaiDSR1 = new() { Name = "MAI-DS-R1", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Model router is a deployable AI model that is trained to select the most suitable large language model (LLM) for a given prompt.
        /// </summary>
        public static readonly AIFoundryModel ModelRouter = new() { Name = "model-router", Version = "2025-11-18", Format = "Microsoft" };

        /// <summary>
        /// Same Phi-3-medium model, but with a larger context size for RAG or few shot prompting.
        /// </summary>
        public static readonly AIFoundryModel Phi3Medium128kInstruct = new() { Name = "Phi-3-medium-128k-instruct", Version = "7", Format = "Microsoft" };

        /// <summary>
        /// A 14B parameters model, proves better quality than Phi-3-mini, with a focus on high-quality, reasoning-dense data.
        /// </summary>
        public static readonly AIFoundryModel Phi3Medium4kInstruct = new() { Name = "Phi-3-medium-4k-instruct", Version = "6", Format = "Microsoft" };

        /// <summary>
        /// Same Phi-3-mini model, but with a larger context size for RAG or few shot prompting.
        /// </summary>
        public static readonly AIFoundryModel Phi3Mini128kInstruct = new() { Name = "Phi-3-mini-128k-instruct", Version = "13", Format = "Microsoft" };

        /// <summary>
        /// Tiniest member of the Phi-3 family. Optimized for both quality and low latency.
        /// </summary>
        public static readonly AIFoundryModel Phi3Mini4kInstruct = new() { Name = "Phi-3-mini-4k-instruct", Version = "15", Format = "Microsoft" };

        /// <summary>
        /// Same Phi-3-small model, but with a larger context size for RAG or few shot prompting.
        /// </summary>
        public static readonly AIFoundryModel Phi3Small128kInstruct = new() { Name = "Phi-3-small-128k-instruct", Version = "5", Format = "Microsoft" };

        /// <summary>
        /// A 7B parameters model, proves better quality than Phi-3-mini, with a focus on high-quality, reasoning-dense data.
        /// </summary>
        public static readonly AIFoundryModel Phi3Small8kInstruct = new() { Name = "Phi-3-small-8k-instruct", Version = "6", Format = "Microsoft" };

        /// <summary>
        /// Refresh of Phi-3-mini model.
        /// </summary>
        public static readonly AIFoundryModel Phi35MiniInstruct = new() { Name = "Phi-3.5-mini-instruct", Version = "6", Format = "Microsoft" };

        /// <summary>
        /// A new mixture of experts model
        /// </summary>
        public static readonly AIFoundryModel Phi35MoEInstruct = new() { Name = "Phi-3.5-MoE-instruct", Version = "5", Format = "Microsoft" };

        /// <summary>
        /// Refresh of Phi-3-vision model.
        /// </summary>
        public static readonly AIFoundryModel Phi35VisionInstruct = new() { Name = "Phi-3.5-vision-instruct", Version = "2", Format = "Microsoft" };

        /// <summary>
        /// Phi-4 14B, a highly capable model for low latency scenarios.
        /// </summary>
        public static readonly AIFoundryModel Phi4 = new() { Name = "Phi-4", Version = "7", Format = "Microsoft" };

        /// <summary>
        /// 3.8B parameters Small Language Model outperforming larger models in reasoning, math, coding, and function-calling
        /// </summary>
        public static readonly AIFoundryModel Phi4MiniInstruct = new() { Name = "Phi-4-mini-instruct", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// Lightweight math reasoning model optimized for multi-step problem solving
        /// </summary>
        public static readonly AIFoundryModel Phi4MiniReasoning = new() { Name = "Phi-4-mini-reasoning", Version = "1", Format = "Microsoft" };

        /// <summary>
        /// First small multimodal model to have 3 modality inputs (text, audio, image), excelling in quality and efficiency
        /// </summary>
        public static readonly AIFoundryModel Phi4MultimodalInstruct = new() { Name = "Phi-4-multimodal-instruct", Version = "2", Format = "Microsoft" };

        /// <summary>
        /// State-of-the-art open-weight reasoning model.
        /// </summary>
        public static readonly AIFoundryModel Phi4Reasoning = new() { Name = "Phi-4-reasoning", Version = "1", Format = "Microsoft" };
    }

    /// <summary>
    /// Models published by Mistral AI.
    /// </summary>
    public static partial class MistralAI
    {
        /// <summary>
        /// Codestral 25.01 by Mistral AI is designed for code generation, supporting 80+ programming languages, and optimized for tasks like code completion and fill-in-the-middle
        /// </summary>
        public static readonly AIFoundryModel Codestral2501 = new() { Name = "Codestral-2501", Version = "2", Format = "Mistral AI" };

        /// <summary>
        /// Ministral 3B is a state-of-the-art Small Language Model (SLM) optimized for edge computing and on-device applications. As it is designed for low-latency and compute-efficient inference, it it also the perfect model for standard GenAI applications that have
        /// </summary>
        public static readonly AIFoundryModel Ministral3B = new() { Name = "Ministral-3B", Version = "1", Format = "Mistral AI" };

        /// <summary>
        /// Document conversion to markdown with interleaved images and text
        /// </summary>
        public static readonly AIFoundryModel MistralDocumentAi2505 = new() { Name = "mistral-document-ai-2505", Version = "1", Format = "Mistral AI" };

        /// <summary>
        /// Mistral Large (2407) is an advanced Large Language Model (LLM) with state-of-the-art reasoning, knowledge and coding capabilities.
        /// </summary>
        public static readonly AIFoundryModel MistralLarge2407 = new() { Name = "Mistral-large-2407", Version = "1", Format = "Mistral AI" };

        /// <summary>
        /// Mistral Large 24.11 offers enhanced system prompts, advanced reasoning and function calling capabilities.
        /// </summary>
        public static readonly AIFoundryModel MistralLarge2411 = new() { Name = "Mistral-Large-2411", Version = "2", Format = "Mistral AI" };

        /// <summary>
        /// Mistral Large 3 is a state-of-the-art General-purpose Multimodal granular Mixture-of-Experts model with 39B active parameters, 673B total parameters featuring 128 experts per layer and Multi-Latent attention.
        /// </summary>
        public static readonly AIFoundryModel MistralLarge3 = new() { Name = "Mistral-Large-3", Version = "1", Format = "Mistral AI" };

        /// <summary>
        /// Mistral Medium 3 is an advanced Large Language Model (LLM) with state-of-the-art reasoning, knowledge, coding and vision capabilities.
        /// </summary>
        public static readonly AIFoundryModel MistralMedium2505 = new() { Name = "mistral-medium-2505", Version = "1", Format = "Mistral AI" };

        /// <summary>
        /// Mistral Nemo is a cutting-edge Language Model (LLM) boasting state-of-the-art reasoning, world knowledge, and coding capabilities within its size category.
        /// </summary>
        public static readonly AIFoundryModel MistralNemo = new() { Name = "Mistral-Nemo", Version = "1", Format = "Mistral AI" };

        /// <summary>
        /// Mistral Small can be used on any language-based task that requires high efficiency and low latency.
        /// </summary>
        public static readonly AIFoundryModel MistralSmall = new() { Name = "Mistral-small", Version = "1", Format = "Mistral AI" };

        /// <summary>
        /// Enhanced Mistral Small 3 with multimodal capabilities and a 128k context length.
        /// </summary>
        public static readonly AIFoundryModel MistralSmall2503 = new() { Name = "mistral-small-2503", Version = "1", Format = "Mistral AI" };
    }

    /// <summary>
    /// Models published by OpenAI.
    /// </summary>
    public static partial class OpenAI
    {
        /// <summary>
        /// codex-mini is a fine-tuned variant of the o4-mini model, designed to deliver rapid, instruction-following performance for developers working in CLI workflows. Whether you&apos;re automating shell commands, editing scripts, or refactoring repositories, Codex-Min
        /// </summary>
        public static readonly AIFoundryModel CodexMini = new() { Name = "codex-mini", Version = "2025-05-16", Format = "OpenAI" };

        /// <summary>
        /// computer-use-preview is the model for Computer Use Agent for use in Responses API. You can use computer-use-preview model to get instructions to control a browser on your computer screen and take action on a user&apos;s behalf.
        /// </summary>
        public static readonly AIFoundryModel ComputerUsePreview = new() { Name = "computer-use-preview", Version = "2025-03-11", Format = "OpenAI" };

        /// <summary>
        /// # Direct from Azure models Direct from Azure models are a select portfolio curated for their market-differentiated capabilities: - Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure&apos;s enterprise-grade infrastructure. - Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry. - Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort. - Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings. Learn more about [Direct from Azure models](https://aka.ms/DirectfromAzure). # Key capabilities ## About this model DALL-E 3 generates images from text prompts that are provided by the user. ## Key model capabilities The image generation API creates an image from a text prompt. It does not edit existing images or create variations. # Use cases See Responsible AI for additional considerations for responsible use. ## Key use cases The provider has not supplied this information. ## Out of scope use cases The provider has not supplied this information. # Pricing Pricing is based on a number of factors, including deployment type and tokens used. [See pricing details here.](https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067) # Technical specs The provider has not supplied this information. ## Training cut-off date The provider has not supplied this information. ## Training time The provider has not supplied this information. ## Input formats The provider has not supplied this information. ## Output formats  The provider has not supplied this information. ## Supported languages The provider has not supplied this information. ## Sample JSON response The provider has not supplied this information. ## Model architecture The provider has not supplied this information. ## Long context The provider has not supplied this information. ## Optimizing model performance The provider has not supplied this information. ## Additional assets The provider has not supplied this information. # Training disclosure ## Training, testing and validation  The provider has not supplied this information. # Distribution ## Distribution channels The provider has not supplied this information. # More information The provider has not supplied this information.
        /// </summary>
        public static readonly AIFoundryModel DallE3 = new() { Name = "dall-e-3", Version = "3.0", Format = "OpenAI" };

        /// <summary>
        /// ﻿# Azure Direct Models Direct from Azure models are a select portfolio curated for their market-differentiated capabilities: - Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure&apos;s enterprise-grade infrastructure. - Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all as part of one Azure AI Foundry platform. - Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Azure AI Foundry; reducing integration effort. - Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings. Learn more about [Direct from Azure models](https://aka.ms/DirectfromAzure). # Key capabilities ## About this model The provider has not supplied this information. ## Key model capabilities Davinci-002 supports fine-tuning, allowing developers and businesses to customize the model for specific applications. # Use cases See Responsible AI for additional considerations for responsible use. ## Key use cases The provider has not supplied this information. ## Out of scope use cases The provider has not supplied this information. # Pricing Pricing is based on a number of factors, including deployment type and tokens used. [See pricing details here.](https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067) # Technical specs The provider has not supplied this information. ## Training cut-off date This model supports 16384 max input tokens and training data is up to Sep 2021. ## Training time The provider has not supplied this information. ## Input formats Your training data and validation data sets consist of input and output examples for how you would like the model to perform. The training and validation data you use must be formatted as a JSON Lines (JSONL) document in which each line represents a single prompt-completion pair. ## Output formats  The provider has not supplied this information. ## Supported languages The provider has not supplied this information. ## Sample JSON response The provider has not supplied this information. ## Model architecture Davinci-002 is the latest version of Davinci, a gpt-3 based model. ## Long context This model supports 16384 max input tokens. ## Optimizing model performance The provider has not supplied this information. ## Additional assets Learn more at &lt;https://learn.microsoft.com/azure/cognitive-services/openai/concepts/models&gt; # Training disclosure ## Training, testing and validation The provider has not supplied this information. # Distribution ## Distribution channels The provider has not supplied this information. # More information The provider has not supplied this information.
        /// </summary>
        public static readonly AIFoundryModel Davinci002 = new() { Name = "davinci-002", Version = "3", Format = "OpenAI" };

        /// <summary>
        /// # Direct from Azure models Direct from Azure models are a select portfolio curated for their market-differentiated capabilities: - Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure&apos;s enterprise-grade infrastructure. - Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry. - Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort. - Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings. Learn more about [Direct from Azure models](https://aka.ms/DirectfromAzure). # Key capabilities ## About this model The gpt-35-turbo is a language model designed for conversational interfaces that has been optimized for chat using the Chat Completions API. ## Key model capabilities The provider has not supplied this information. # Use cases See Responsible AI for additional considerations for responsible use. ## Key use cases The provider has not supplied this information. ## Out of scope use cases The provider has not supplied this information. # Pricing Pricing is based on a number of factors, including deployment type and tokens used. [See pricing details here.](https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067) # Technical specs The provider has not supplied this information. ## Training cut-off date The provider has not supplied this information. ## Training time The provider has not supplied this information. ## Input formats The model expects a prompt string formatted in a specific chat-like transcript format. ## Output formats  The model returns a completion that represents a model-written message in the chat. ## Supported languages The provider has not supplied this information. ## Sample JSON response The provider has not supplied this information. ## Model architecture The provider has not supplied this information. ## Long context The provider has not supplied this information. ## Optimizing model performance The provider has not supplied this information. ## Additional assets The provider has not supplied this information. # Training disclosure ## Training, testing and validation  The provider has not supplied this information. # Distribution ## Distribution channels The provider has not supplied this information. # More information The provider has not supplied this information.
        /// </summary>
        public static readonly AIFoundryModel Gpt35Turbo = new() { Name = "gpt-35-turbo", Version = "0125", Format = "OpenAI" };

        /// <summary>
        /// # Direct from Azure models Direct from Azure models are a select portfolio curated for their market-differentiated capabilities: - Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure&apos;s enterprise-grade infrastructure. - Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry. - Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort. - Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings. Learn more about [Direct from Azure models](https://aka.ms/DirectfromAzure). # Key capabilities ## About this model gpt-3.5 models can understand and generate natural language or code. ## Key model capabilities gpt-3.5-turbo is available for use with the Chat Completions API. gpt-3.5-turbo Instruct has similar capabilities to text-davinci-003 using the Completions API instead of the Chat Completions API. To learn more about how to interact with gpt-3.5-turbo and the Chat Completions API check out our &lt;a href=&quot;https://learn.microsoft.com/azure/ai-services/openai/how-to/chatgpt?tabs=python&amp;pivots=programming-language-chat-completions&quot; target=&quot;_blank&quot;&gt;in-depth how-to.&lt;/a&gt; # Use cases See Responsible AI for additional considerations for responsible use. ## Key use cases The provider has not supplied this information. ## Out of scope use cases The provider has not supplied this information. # Pricing Pricing is based on a number of factors, including deployment type and tokens used. [See pricing details here.](https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067) # Technical specs The provider has not supplied this information. ## Training cut-off date Sep 2021 ## Training time The provider has not supplied this information. ## Input formats The provider has not supplied this information. ## Output formats  The provider has not supplied this information. ## Supported languages The provider has not supplied this information. ## Sample JSON response The provider has not supplied this information. ## Model architecture The provider has not supplied this information. ## Long context You can see the token context length supported by each model in the model summary table. | Model ID            | Model Availability                                                             | Max Request (tokens)    | Training Data (up to) | | ------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------ | --------------------------- | --------------------- | | gpt-35-turbo&lt;sup&gt;1&lt;/sup&gt; (0301) | East US, France Central, South Central US, UK South, West Europe                                      | 4,096            | Sep 2021       | | gpt-35-turbo (0613)       | Australia East, Canada East, East US, East US 2, France Central, Japan East, North Central US, Sweden Central, Switzerland North, UK South | 4,096            | Sep 2021       | | gpt-35-turbo-16k (0613)     | Australia East, Canada East, East US, East US 2, France Central, Japan East, North Central US, Sweden Central, Switzerland North, UK South | 16,384           | Sep 2021       | | gpt-35-turbo-instruct (0914)  | East US, Sweden Central                                                          | 4,097            | Sep 2021       | | gpt-35-turbo (1106)       | Australia East, Canada East, France Central, South India, Sweden Central, UK South, West US                        | Input: 16,385 Output: 4,096 | Sep 2021       | &lt;sup&gt;1&lt;/sup&gt; This model will accept requests &gt; 4,096 tokens. It is not recommended to exceed the 4,096 input token limit as the newer version of the model are capped at 4,096 tokens. If you encounter issues when exceeding 4,096 input tokens with this model this configuration is not officially supported. ## Optimizing model performance The provider has not supplied this information. ## Additional assets The provider has not supplied this information. # Training disclosure ## Training, testing and validation  The provider has not supplied this information. # Distribution ## Distribution channels The provider has not supplied this information. # More information The provider has not supplied this information.
        /// </summary>
        public static readonly AIFoundryModel Gpt35Turbo16k = new() { Name = "gpt-35-turbo-16k", Version = "0613", Format = "OpenAI" };

        /// <summary>
        /// # Direct from Azure models Direct from Azure models are a select portfolio curated for their market-differentiated capabilities: - Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure&apos;s enterprise-grade infrastructure. - Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry. - Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort. - Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings. Learn more about [Direct from Azure models](https://aka.ms/DirectfromAzure). # Key capabilities ## About this model gpt-3.5 models can understand and generate natural language or code. ## Key model capabilities - Understand and generate natural language - Generate code - Chat optimized interactions - Traditional completions tasks # Use cases See Responsible AI for additional considerations for responsible use. ## Key use cases The provider has not supplied this information. ## Out of scope use cases The provider has not supplied this information. # Pricing Pricing is based on a number of factors, including deployment type and tokens used. [See pricing details here.](https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067) # Technical specs The provider has not supplied this information. ## Training cut-off date Sep 2021 ## Training time The provider has not supplied this information. ## Input formats The provider has not supplied this information. ## Output formats  The provider has not supplied this information. ## Supported languages The provider has not supplied this information. ## Sample JSON response The provider has not supplied this information. ## Model architecture The provider has not supplied this information. ## Long context You can see the token context length supported by each model in the model summary table. | Model ID            | Model Availability                                                             | Max Request (tokens)    | Training Data (up to) | | ------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------ | --------------------------- | --------------------- | | gpt-35-turbo&lt;sup&gt;1&lt;/sup&gt; (0301) | East US, France Central, South Central US, UK South, West Europe                                      | 4,096            | Sep 2021       | | gpt-35-turbo (0613)       | Australia East, Canada East, East US, East US 2, France Central, Japan East, North Central US, Sweden Central, Switzerland North, UK South | 4,096            | Sep 2021       | | gpt-35-turbo-16k (0613)     | Australia East, Canada East, East US, East US 2, France Central, Japan East, North Central US, Sweden Central, Switzerland North, UK South | 16,384           | Sep 2021       | | gpt-35-turbo-instruct (0914)  | East US, Sweden Central                                                          | 4,097            | Sep 2021       | | gpt-35-turbo (1106)       | Australia East, Canada East, France Central, South India, Sweden Central, UK South, West US                        | Input: 16,385 Output: 4,096 | Sep 2021       | &lt;sup&gt;1&lt;/sup&gt; This model will accept requests &gt; 4,096 tokens. It is not recommended to exceed the 4,096 input token limit as the newer version of the model are capped at 4,096 tokens. If you encounter issues when exceeding 4,096 input tokens with this model this configuration is not officially supported. ## Optimizing model performance The provider has not supplied this information. ## Additional assets To learn more about how to interact with GPT-3.5 Turbo and the Chat Completions API check out our &lt;a href=&quot;https://learn.microsoft.com/azure/ai-services/openai/how-to/chatgpt?tabs=python&amp;pivots=programming-language-chat-completions&quot; target=&quot;_blank&quot;&gt;in-depth how-to.&lt;/a&gt; # Training disclosure ## Training, testing and validation The provider has not supplied this information. # Distribution ## Distribution channels The provider has not supplied this information. # More information
        /// </summary>
        public static readonly AIFoundryModel Gpt35TurboInstruct = new() { Name = "gpt-35-turbo-instruct", Version = "0914", Format = "OpenAI" };

        /// <summary>
        /// # Direct from Azure models Direct from Azure models are a select portfolio curated for their market-differentiated capabilities: - Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure&apos;s enterprise-grade infrastructure. - Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry. - Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort. - Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings. Learn more about [Direct from Azure models](https://aka.ms/DirectfromAzure). # Key capabilities ## About this model gpt-4 is a large multimodal model that can solve complex problems with greater accuracy than any of our previous models, thanks to its extensive general knowledge and advanced reasoning capabilities. ## Key model capabilities - **gpt-4-turbo-2024-04-09:** This is the GPT-4 Turbo with Vision GA model. It can return up to 4,096 output tokens. - **gpt-4-1106-preview (GPT-4 Turbo):** The latest gpt-4 model with improved instruction following, JSON mode, reproducible outputs, parallel function calling, and more. It returns a maximum of 4,096 output tokens. - **gpt-4-vision Preview (GPT-4 Turbo with vision):** This multimodal AI model enables users to direct the model to analyze image inputs they provide, along with all the other capabilities of GPT-4 Turbo. It can return up to 4,096 output tokens. # Use cases See Responsible AI for additional considerations for responsible use. ## Key use cases The provider has not supplied this information. ## Out of scope use cases Please note that AzureML Studio only supports the deployment of the gpt-4-0314 model version and AI Studio supports the deployment of all the model versions listed below. This preview model is not yet suited for production traffic. As a preview model version, it is not yet suitable for production traffic. This model version will be retired no earlier than July 5, 2024. # Pricing Pricing is based on a number of factors, including deployment type and tokens used. [See pricing details here.](https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067) # Technical specs The provider has not supplied this information. ## Training cut-off date gpt-4 provides a wide range of model versions to fit your business needs: - **gpt-4-turbo-2024-04-09:** The training data is current up to December 2023. - **gpt-4-1106-preview (GPT-4 Turbo):** Training Data: Up to April 2023. - **gpt-4-vision Preview (GPT-4 Turbo with vision):** Training data is current up to April 2023. - **gpt-4-0613:** Training data up to September 2021. - **gpt-4-0314:** Training data up to September 2021. ## Training time The provider has not supplied this information. ## Input formats gpt-4 is a large multimodal model that accepts text or image inputs. ## Output formats gpt-4 outputs text. ## Supported languages The provider has not supplied this information. ## Sample JSON response The provider has not supplied this information. ## Model architecture The provider has not supplied this information. ## Long context gpt-4 provides different context window sizes across model versions: - **gpt-4-turbo-2024-04-09:** The context window is 128,000 tokens. - **gpt-4-1106-preview (GPT-4 Turbo):** Context window: 128,000 tokens. - **gpt-4-vision Preview (GPT-4 Turbo with vision):** The context window is 128,000 tokens. - **gpt-4-0613:** gpt-4 model with a context window of 8,192 tokens. - **gpt-4-0314:** gpt-4 legacy model with a context window of 8,192 tokens. ## Optimizing model performance The provider has not supplied this information. ## Additional assets Learn more at &lt;https://learn.microsoft.com/azure/cognitive-services/openai/concepts/models&gt; # Training disclosure ## Training, testing and validation The provider has not supplied this information. # Distribution ## Distribution channels The provider has not supplied this information. # More information The provider has not supplied this information.
        /// </summary>
        public static readonly AIFoundryModel Gpt4 = new() { Name = "gpt-4", Version = "turbo-2024-04-09", Format = "OpenAI" };

        /// <summary>
        /// # Direct from Azure models Direct from Azure models are a select portfolio curated for their market-differentiated capabilities: - Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure&apos;s enterprise-grade infrastructure. - Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry. - Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort. - Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings. Learn more about [Direct from Azure models](https://aka.ms/DirectfromAzure). # Key capabilities ## About this model gpt-4 can solve difficult problems with greater accuracy than any of the previous OpenAI models. Like gpt-35-turbo, gpt-4 is optimized for chat but works well for traditional completions tasks. ## Key model capabilities gpt-4 can solve difficult problems with greater accuracy than any of the previous OpenAI models. Like gpt-35-turbo, gpt-4 is optimized for chat but works well for traditional completions tasks. # Use cases See Responsible AI for additional considerations for responsible use. ## Key use cases gpt-4 is optimized for chat but works well for traditional completions tasks. ## Out of scope use cases this model can be deployed for inference, but cannot be finetuned. # Pricing Pricing is based on a number of factors, including deployment type and tokens used. [See pricing details here.](https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067) # Technical specs The provider has not supplied this information. ## Training cut-off date The provider has not supplied this information. ## Training time The provider has not supplied this information. ## Input formats The gpt-4 supports 8192 max input tokens and the gpt-4-32k supports up to 32,768 tokens. ## Output formats  The provider has not supplied this information. ## Supported languages The provider has not supplied this information. ## Sample JSON response The provider has not supplied this information. ## Model architecture The provider has not supplied this information. ## Long context The gpt-4 supports 8192 max input tokens and the gpt-4-32k supports up to 32,768 tokens. ## Optimizing model performance The provider has not supplied this information. ## Additional assets Learn more at &lt;https://learn.microsoft.com/azure/cognitive-services/openai/concepts/models&gt; # Training disclosure ## Training, testing and validation  The provider has not supplied this information. # Distribution ## Distribution channels The provider has not supplied this information. # More information The provider has not supplied this information.
        /// </summary>
        public static readonly AIFoundryModel Gpt432k = new() { Name = "gpt-4-32k", Version = "0613", Format = "OpenAI" };

        /// <summary>
        /// gpt-4.1 outperforms gpt-4o across the board, with major gains in coding, instruction following, and long-context understanding
        /// </summary>
        public static readonly AIFoundryModel Gpt41 = new() { Name = "gpt-4.1", Version = "2025-04-14", Format = "OpenAI" };

        /// <summary>
        /// gpt-4.1-mini outperform gpt-4o-mini across the board, with major gains in coding, instruction following, and long-context handling
        /// </summary>
        public static readonly AIFoundryModel Gpt41Mini = new() { Name = "gpt-4.1-mini", Version = "2025-04-14", Format = "OpenAI" };

        /// <summary>
        /// gpt-4.1-nano provides gains in coding, instruction following, and long-context handling along with lower latency and cost
        /// </summary>
        public static readonly AIFoundryModel Gpt41Nano = new() { Name = "gpt-4.1-nano", Version = "2025-04-14", Format = "OpenAI" };

        /// <summary>
        /// the largest and strongest general purpose model in the gpt model family up to date, best suited for diverse text and image tasks.
        /// </summary>
        public static readonly AIFoundryModel Gpt45Preview = new() { Name = "gpt-4.5-preview", Version = "2025-02-27", Format = "OpenAI" };

        /// <summary>
        /// OpenAI&apos;s most advanced multimodal model in the gpt-4o family. Can handle both text and image inputs.
        /// </summary>
        public static readonly AIFoundryModel Gpt4o = new() { Name = "gpt-4o", Version = "2024-11-20", Format = "OpenAI" };

        /// <summary>
        /// Best suited for rich, asynchronous audio input/output interactions, such as creating spoken summaries from text.
        /// </summary>
        public static readonly AIFoundryModel Gpt4oAudioPreview = new() { Name = "gpt-4o-audio-preview", Version = "2024-12-17", Format = "OpenAI" };

        /// <summary>
        /// An affordable, efficient AI solution for diverse text and image tasks.
        /// </summary>
        public static readonly AIFoundryModel Gpt4oMini = new() { Name = "gpt-4o-mini", Version = "2024-07-18", Format = "OpenAI" };

        /// <summary>
        /// Best suited for rich, asynchronous audio input/output interactions, such as creating spoken summaries from text.
        /// </summary>
        public static readonly AIFoundryModel Gpt4oMiniAudioPreview = new() { Name = "gpt-4o-mini-audio-preview", Version = "2024-12-17", Format = "OpenAI" };

        /// <summary>
        /// Best suited for rich, asynchronous audio input/output interactions, such as creating spoken summaries from text.
        /// </summary>
        public static readonly AIFoundryModel Gpt4oMiniRealtimePreview = new() { Name = "gpt-4o-mini-realtime-preview", Version = "2024-12-17", Format = "OpenAI" };

        /// <summary>
        /// A highly efficient and cost effective speech-to-text solution that deliverables reliable and accurate transcripts.
        /// </summary>
        public static readonly AIFoundryModel Gpt4oMiniTranscribe = new() { Name = "gpt-4o-mini-transcribe", Version = "2025-03-20", Format = "OpenAI" };

        /// <summary>
        /// An advanced text-to-speech solution designed to convert written text into natural-sounding speech.
        /// </summary>
        public static readonly AIFoundryModel Gpt4oMiniTts = new() { Name = "gpt-4o-mini-tts", Version = "2025-03-20", Format = "OpenAI" };

        /// <summary>
        /// # Direct from Azure models Direct from Azure models are a select portfolio curated for their market-differentiated capabilities: - Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure&apos;s enterprise-grade infrastructure. - Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry. - Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort. - Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings. Learn more about [Direct from Azure models](https://aka.ms/DirectfromAzure). # Key capabilities ## About this model Introducing our new multimodal AI model, which now supports both text and audio modalities. ## Key model capabilities - **Enhanced customer service:** By integrating audio inputs, gpt-4o-realtime-preview enables more dynamic and comprehensive customer support interactions. - **Content innovation:** Use gpt-4o-realtime-preview&apos;s generative capabilities to create engaging and diverse audio content, catering to a broad range of consumer preferences. - **Real-time translation:** Leverage gpt-4o-realtime-preview&apos;s capability to provide accurate and immediate translations, facilitating seamless communication across different languages # Use cases See Responsible AI for additional considerations for responsible use. ## Key use cases The introduction of gpt-4o-realtime-preview opens numerous possibilities for businesses in various sectors: Enhanced customer service, content innovation, and real-time translation capabilities facilitate seamless communication across different languages. ## Out of scope use cases Currently, the gpt-4o-realtime-preview model focuses on text and audio and does not support existing gpt-4o features such as image modality and structured outputs. For many tasks, the generally available gpt-4o models may still be more suitable. _IMPORTANT: At this time, gpt-4o-realtime-preview usage limits are suitable for test and development. To prevent abuse and preserve service integrity, rate limits will be adjusted as needed._ _IMPORTANT: The system stores your prompts and completions as described in the &quot;Data Use and Access for Abuse Monitoring&quot; section of the service-specific Product Terms for Azure OpenAI Service, except that the Limited Exception does not apply. Abuse monitoring will be turned on for use of the GPT-4o-realtime-preview API even for customers who otherwise are approved for modified abuse monitoring._ # Pricing Pricing is based on a number of factors, including deployment type and tokens used. [See pricing details here.](https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067) # Technical specs The provider has not supplied this information. ## Training cut-off date The provider has not supplied this information. ## Training time The provider has not supplied this information. ## Input formats Currently, the gpt-4o-realtime-preview model focuses on text and audio and does not support existing gpt-4o features such as image modality and structured outputs. ## Output formats  The provider has not supplied this information. ## Supported languages The provider has not supplied this information. ## Sample JSON response The provider has not supplied this information. ## Model architecture The provider has not supplied this information. ## Long context The provider has not supplied this information. ## Optimizing model performance The provider has not supplied this information. ## Additional assets The following documents are applicable:  - [Overview of Responsible AI practices for Azure OpenAI models](https://learn.microsoft.com/legal/cognitive-services/openai/overview) - [Transparency Note for Azure OpenAI Service](https://learn.microsoft.com/legal/cognitive-services/openai/transparency-note) # Training disclosure ## Training, testing and validation  GPT-4o-realtime-preview has safety built-in by design across modalities, through techniques such as filtering training data and refining the model&apos;s behavior through post-training. # Distribution ## Distribution channels The provider has not supplied this information. # More information We&apos;ve evaluated GPT-4o-realtime-preview according to our [Preparedness Framework](https://openai.com/safety/) and in line with our [voluntary commitments](https://openai.com/index/moving-ai-governance-forward/). Our evaluations of cybersecurity, CBRN, persuasion, and model autonomy show that GPT-4o-realtime-preview does not score above Medium risk in any of these categories. This assessment involved running a suite of automated and human evaluations throughout the model training process. We tested both pre-safety-mitigation and post-safety-mitigation versions of the model, using custom fine-tuning and prompts, to better elicit model capabilities.  GPT-4o-realtime-preview has also undergone extensive external red teaming with 70+ [external experts](https://openai.com/index/red-teaming-network/) in domains such as social psychology, bias and fairness, and misinformation to identify risks that are introduced or amplified by the newly added modalities. We used these learnings to build out our safety interventions in order to improve the safety of interacting with GPT-4o-realtime-preview. We will continue to mitigate new risks as they&apos;re discovered. Model Versions: - **2024-12-17:** Updating the gpt-4o-realtime-preview model with improvements in voice quality and input reliability. As this is a preview version, it is designed for testing and feedback purposes and is not yet optimized for production traffic. - **2024-10-01:** Introducing our new multimodal AI model, which now supports both text and audio modalities. As this is a preview version, it is designed for testing and feedback purposes and is not yet optimized for production traffic.
        /// </summary>
        public static readonly AIFoundryModel Gpt4oRealtimePreview = new() { Name = "gpt-4o-realtime-preview", Version = "2024-12-17", Format = "OpenAI" };

        /// <summary>
        /// A cutting-edge speech-to-text solution that deliverables reliable and accurate transcripts.
        /// </summary>
        public static readonly AIFoundryModel Gpt4oTranscribe = new() { Name = "gpt-4o-transcribe", Version = "2025-03-20", Format = "OpenAI" };

        /// <summary>
        /// A cutting-edge speech-to-text solution that deliverables reliable and accurate transcripts; now equipped with diarization support aka identifying different speakers through the transcription.
        /// </summary>
        public static readonly AIFoundryModel Gpt4oTranscribeDiarize = new() { Name = "gpt-4o-transcribe-diarize", Version = "2025-10-15", Format = "OpenAI" };

        /// <summary>
        /// gpt-5 is designed for logic-heavy and multi-step tasks.
        /// </summary>
        public static readonly AIFoundryModel Gpt5 = new() { Name = "gpt-5", Version = "2025-08-07", Format = "OpenAI" };

        /// <summary>
        /// gpt-5-chat (preview) is an advanced, natural, multimodal, and context-aware conversations for enterprise applications.
        /// </summary>
        public static readonly AIFoundryModel Gpt5Chat = new() { Name = "gpt-5-chat", Version = "2025-10-03", Format = "OpenAI" };

        /// <summary>
        /// gpt-5-codex is designed for steerability, front end development, and interactivity.
        /// </summary>
        public static readonly AIFoundryModel Gpt5Codex = new() { Name = "gpt-5-codex", Version = "2025-09-15", Format = "OpenAI" };

        /// <summary>
        /// gpt-5-mini is a lightweight version for cost-sensitive applications.
        /// </summary>
        public static readonly AIFoundryModel Gpt5Mini = new() { Name = "gpt-5-mini", Version = "2025-08-07", Format = "OpenAI" };

        /// <summary>
        /// gpt-5-nano is optimized for speed, ideal for applications requiring low latency.
        /// </summary>
        public static readonly AIFoundryModel Gpt5Nano = new() { Name = "gpt-5-nano", Version = "2025-08-07", Format = "OpenAI" };

        /// <summary>
        /// gpt-5-pro uses more compute to think harder and provide consistently better answers.
        /// </summary>
        public static readonly AIFoundryModel Gpt5Pro = new() { Name = "gpt-5-pro", Version = "2025-10-06", Format = "OpenAI" };

        /// <summary>
        /// gpt-5.1 is designed for logic-heavy and multi-step tasks.
        /// </summary>
        public static readonly AIFoundryModel Gpt51 = new() { Name = "gpt-5.1", Version = "2025-11-13", Format = "OpenAI" };

        /// <summary>
        /// gpt-5.1-chat (preview) is an advanced, natural, multimodal, and context-aware conversations for enterprise applications.
        /// </summary>
        public static readonly AIFoundryModel Gpt51Chat = new() { Name = "gpt-5.1-chat", Version = "2025-11-13", Format = "OpenAI" };

        /// <summary>
        /// gpt-5.1-codex is designed for steerability, front end development, and interactivity.
        /// </summary>
        public static readonly AIFoundryModel Gpt51Codex = new() { Name = "gpt-5.1-codex", Version = "2025-11-13", Format = "OpenAI" };

        /// <summary>
        /// gpt-5.1-codex-max is agentic coding model designed to streamline complex development workflows with advanced efficiency
        /// </summary>
        public static readonly AIFoundryModel Gpt51CodexMax = new() { Name = "gpt-5.1-codex-max", Version = "2025-12-04", Format = "OpenAI" };

        /// <summary>
        /// gpt-5.1-codex-mini is designed for steerability, front end development, and interactivity.
        /// </summary>
        public static readonly AIFoundryModel Gpt51CodexMini = new() { Name = "gpt-5.1-codex-mini", Version = "2025-11-13", Format = "OpenAI" };

        /// <summary>
        /// GPT-5.2 is engineered for enterprise agent scenarios—delivering structured, auditable outputs, reliable tool use, and governed integrations.
        /// </summary>
        public static readonly AIFoundryModel Gpt52 = new() { Name = "gpt-5.2", Version = "2025-12-11", Format = "OpenAI" };

        /// <summary>
        /// gpt-5.2-chat (preview) is an advanced, natural, multimodal, and context-aware conversations for enterprise applications.
        /// </summary>
        public static readonly AIFoundryModel Gpt52Chat = new() { Name = "gpt-5.2-chat", Version = "2025-12-11", Format = "OpenAI" };

        /// <summary>
        /// Best suited for rich, asynchronous audio input/output interactions, such as creating spoken summaries from text.
        /// </summary>
        public static readonly AIFoundryModel GptAudio = new() { Name = "gpt-audio", Version = "2025-08-28", Format = "OpenAI" };

        /// <summary>
        /// Best suited for rich, asynchronous audio input/output interactions, such as creating spoken summaries from text.
        /// </summary>
        public static readonly AIFoundryModel GptAudioMini = new() { Name = "gpt-audio-mini", Version = "2025-10-06", Format = "OpenAI" };

        /// <summary>
        /// An efficient AI solution for diverse text and image tasks, including text to image, image to image, inpainting, and prompt transformation.
        /// </summary>
        public static readonly AIFoundryModel GptImage1 = new() { Name = "gpt-image-1", Version = "2025-04-15", Format = "OpenAI" };

        /// <summary>
        /// An efficient AI solution for diverse text and image tasks, including high quality, cheap text to image generation
        /// </summary>
        public static readonly AIFoundryModel GptImage1Mini = new() { Name = "gpt-image-1-mini", Version = "2025-10-06", Format = "OpenAI" };

        /// <summary>
        /// Push the open model frontier with GPT-OSS models, released under the permissive Apache 2.0 license, allowing anyone to use, modify, and deploy them freely.
        /// </summary>
        public static readonly AIFoundryModel GptOss120b = new() { Name = "gpt-oss-120b", Version = "4", Format = "OpenAI" };

        /// <summary>
        /// Push the open model frontier with GPT-OSS models, released under the permissive Apache 2.0 license, allowing anyone to use, modify, and deploy them freely.
        /// </summary>
        public static readonly AIFoundryModel GptOss20b = new() { Name = "gpt-oss-20b", Version = "11", Format = "OpenAI" };

        /// <summary>
        /// A new S2S (speech to speech) model with improved instruction following.
        /// </summary>
        public static readonly AIFoundryModel GptRealtime = new() { Name = "gpt-realtime", Version = "2025-08-28", Format = "OpenAI" };

        /// <summary>
        /// gpt-realtime-mini is a smaller version of gpt-realtime S2S (speech to speech) model built on chive architecture. This model excels at instruction following and is optimized for cost efficiency.
        /// </summary>
        public static readonly AIFoundryModel GptRealtimeMini = new() { Name = "gpt-realtime-mini", Version = "2025-10-06", Format = "OpenAI" };

        /// <summary>
        /// Focused on advanced reasoning and solving complex problems, including math and science tasks. Ideal for applications that require deep contextual understanding and agentic workflows.
        /// </summary>
        public static readonly AIFoundryModel O1 = new() { Name = "o1", Version = "2024-12-17", Format = "OpenAI" };

        /// <summary>
        /// Smaller, faster, and 80% cheaper than o1-preview, performs well at code generation and small context operations.
        /// </summary>
        public static readonly AIFoundryModel O1Mini = new() { Name = "o1-mini", Version = "2024-09-12", Format = "OpenAI" };

        /// <summary>
        /// Focused on advanced reasoning and solving complex problems, including math and science tasks. Ideal for applications that require deep contextual understanding and agentic workflows.
        /// </summary>
        public static readonly AIFoundryModel O1Preview = new() { Name = "o1-preview", Version = "1", Format = "OpenAI" };

        /// <summary>
        /// o3 includes significant improvements on quality and safety while supporting the existing features of o1 and delivering comparable or better performance.
        /// </summary>
        public static readonly AIFoundryModel O3 = new() { Name = "o3", Version = "2025-04-16", Format = "OpenAI" };

        /// <summary>
        /// The o3 series of models are trained with reinforcement learning to think before they answer and perform complex reasoning. The o1-pro model uses more compute to think harder and provide consistently better answers.
        /// </summary>
        public static readonly AIFoundryModel O3DeepResearch = new() { Name = "o3-deep-research", Version = "2025-06-26", Format = "OpenAI" };

        /// <summary>
        /// o3-mini includes the o1 features with significant cost-efficiencies for scenarios requiring high performance.
        /// </summary>
        public static readonly AIFoundryModel O3Mini = new() { Name = "o3-mini", Version = "2025-01-31", Format = "OpenAI" };

        /// <summary>
        /// The o3 series of models are trained with reinforcement learning to think before they answer and perform complex reasoning. The o1-pro model uses more compute to think harder and provide consistently better answers.
        /// </summary>
        public static readonly AIFoundryModel O3Pro = new() { Name = "o3-pro", Version = "2025-06-10", Format = "OpenAI" };

        /// <summary>
        /// o4-mini includes significant improvements on quality and safety while supporting the existing features of o3-mini and delivering comparable or better performance.
        /// </summary>
        public static readonly AIFoundryModel O4Mini = new() { Name = "o4-mini", Version = "2025-04-16", Format = "OpenAI" };

        /// <summary>
        /// An efficient AI solution to generate videos
        /// </summary>
        public static readonly AIFoundryModel Sora = new() { Name = "sora", Version = "2025-05-02", Format = "OpenAI" };

        /// <summary>
        /// Text-embedding-3 series models are the latest and most capable embedding model from OpenAI.
        /// </summary>
        public static readonly AIFoundryModel TextEmbedding3Large = new() { Name = "text-embedding-3-large", Version = "1", Format = "OpenAI" };

        /// <summary>
        /// Text-embedding-3 series models are the latest and most capable embedding model from OpenAI.
        /// </summary>
        public static readonly AIFoundryModel TextEmbedding3Small = new() { Name = "text-embedding-3-small", Version = "1", Format = "OpenAI" };

        /// <summary>
        /// # Direct from Azure models Direct from Azure models are a select portfolio curated for their market-differentiated capabilities: - Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure&apos;s enterprise-grade infrastructure. - Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry. - Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort. - Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings. Learn more about [Direct from Azure models](https://aka.ms/DirectfromAzure). # Key capabilities ## About this model text-embedding-ada-002 outperforms all the earlier embedding models on text search, code search, and sentence similarity tasks and gets comparable performance on text classification. ## Key model capabilities - Text search - Code search  - Sentence similarity tasks - Text classification Note: this model can be deployed for inference, specifically for embeddings, but cannot be finetuned. # Use cases See Responsible AI for additional considerations for responsible use. ## Key use cases The provider has not supplied this information. ## Out of scope use cases The provider has not supplied this information. # Pricing Pricing is based on a number of factors, including deployment type and tokens used. [See pricing details here.](https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067) # Technical specs The provider has not supplied this information. ## Training cut-off date The provider has not supplied this information. ## Training time The provider has not supplied this information. ## Input formats The provider has not supplied this information. ## Output formats  The provider has not supplied this information. ## Supported languages The provider has not supplied this information. ## Sample JSON response The provider has not supplied this information. ## Model architecture The provider has not supplied this information. ## Long context The provider has not supplied this information. ## Optimizing model performance The provider has not supplied this information. ## Additional assets The provider has not supplied this information. # Training disclosure ## Training, testing and validation  The provider has not supplied this information. # Distribution ## Distribution channels The provider has not supplied this information. # More information The provider has not supplied this information.
        /// </summary>
        public static readonly AIFoundryModel TextEmbeddingAda002 = new() { Name = "text-embedding-ada-002", Version = "2", Format = "OpenAI" };

        /// <summary>
        /// # Direct from Azure models Direct from Azure models are a select portfolio curated for their market-differentiated capabilities: - Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure&apos;s enterprise-grade infrastructure. - Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry. - Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort. - Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings. Learn more about [Direct from Azure models](https://aka.ms/DirectfromAzure). # Key capabilities ## About this model TTS is a model that converts text to natural sounding speech. TTS is optimized for realtime or interactive scenarios. For offline scenarios, TTS-HD provides higher quality. The API supports six different voices. ## Key model capabilities - TTS: optimized for speed. - TTS-HD: optimized for quality. # Use cases See Responsible AI for additional considerations for responsible use. ## Key use cases The provider has not supplied this information. ## Out of scope use cases The provider has not supplied this information. # Pricing Pricing is based on a number of factors, including deployment type and tokens used. [See pricing details here.](https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067) # Technical specs The provider has not supplied this information. ## Training cut-off date The provider has not supplied this information. ## Training time The provider has not supplied this information. ## Input formats Max request data size: 4,096 chars can be converted from text to speech per API request. ## Output formats  The provider has not supplied this information. ## Supported languages The provider has not supplied this information. ## Sample JSON response The provider has not supplied this information. ## Model architecture The provider has not supplied this information. ## Long context The provider has not supplied this information. ## Optimizing model performance The provider has not supplied this information. ## Additional assets The provider has not supplied this information. # Training disclosure ## Training, testing and validation  The provider has not supplied this information. # Distribution ## Distribution channels The provider has not supplied this information. # More information The provider has not supplied this information.
        /// </summary>
        public static readonly AIFoundryModel Tts = new() { Name = "tts", Version = "001", Format = "OpenAI" };

        /// <summary>
        /// # Direct from Azure models Direct from Azure models are a select portfolio curated for their market-differentiated capabilities: - Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure&apos;s enterprise-grade infrastructure. - Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry. - Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort. - Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings. Learn more about [Direct from Azure models](https://aka.ms/DirectfromAzure). # Key capabilities ## About this model TTS-HD is a model that converts text to natural sounding speech. ## Key model capabilities - TTS: optimized for speed. - TTS-HD: optimized for quality. # Use cases See Responsible AI for additional considerations for responsible use. ## Key use cases TTS is optimized for realtime or interactive scenarios. For offline scenarios, TTS-HD provides higher quality. ## Out of scope use cases The provider has not supplied this information. # Pricing Pricing is based on a number of factors, including deployment type and tokens used. [See pricing details here.](https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067) # Technical specs The provider has not supplied this information. ## Training cut-off date The provider has not supplied this information. ## Training time The provider has not supplied this information. ## Input formats Max request data size: 4,096 chars can be converted from text to speech per API request. ## Output formats The provider has not supplied this information. ## Supported languages The provider has not supplied this information. ## Sample JSON response The provider has not supplied this information. ## Model architecture The provider has not supplied this information. ## Long context The provider has not supplied this information. ## Optimizing model performance The provider has not supplied this information. ## Additional assets The provider has not supplied this information. # Training disclosure ## Training, testing and validation The provider has not supplied this information. # Distribution ## Distribution channels The provider has not supplied this information. # More information The provider has not supplied this information.
        /// </summary>
        public static readonly AIFoundryModel TtsHd = new() { Name = "tts-hd", Version = "001", Format = "OpenAI" };

        /// <summary>
        /// # Direct from Azure models Direct from Azure models are a select portfolio curated for their market-differentiated capabilities: - Secure and managed by Microsoft: Purchase and manage models directly through Azure with a single license, consistent support, and no third-party dependencies, backed by Azure&apos;s enterprise-grade infrastructure. - Streamlined operations: Benefit from unified billing, governance, and seamless PTU portability across models hosted on Azure - all part of Microsoft Foundry. - Future-ready flexibility: Access the latest models as they become available, and easily test, deploy, or switch between them within Microsoft Foundry; reducing integration effort. - Cost control and optimization: Scale on demand with pay-as-you-go flexibility or reserve PTUs for predictable performance and savings. Learn more about [Direct from Azure models](https://aka.ms/DirectfromAzure). # Key capabilities ## About this model The Whisper models are trained for speech recognition and translation tasks, capable of transcribing speech audio into the text in the language it is spoken (automatic speech recognition) as well as translated into English (speech translation). ## Key model capabilities - Speech recognition (automatic speech recognition) - Speech translation into English - Processing of audio up to 25mb per API request # Use cases See Responsible AI for additional considerations for responsible use. ## Key use cases The provider has not supplied this information. ## Out of scope use cases The provider has not supplied this information. # Pricing Pricing is based on a number of factors, including deployment type and tokens used. [See pricing details here.](https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067) # Technical specs The provider has not supplied this information. ## Training cut-off date The provider has not supplied this information. ## Training time The provider has not supplied this information. ## Input formats Max request data size: 25mb of audio can be converted from speech to text per API request. ## Output formats  The provider has not supplied this information. ## Supported languages The provider has not supplied this information. ## Sample JSON response The provider has not supplied this information. ## Model architecture The provider has not supplied this information. ## Long context The provider has not supplied this information. ## Optimizing model performance The provider has not supplied this information. ## Additional assets The provider has not supplied this information. # Training disclosure ## Training, testing and validation  Researchers at OpenAI developed the models to study the robustness of speech processing systems trained under large-scale weak supervision. # Distribution ## Distribution channels The provider has not supplied this information. # More information The provider has not supplied this information.
        /// </summary>
        public static readonly AIFoundryModel Whisper = new() { Name = "whisper", Version = "001", Format = "OpenAI" };
    }

    /// <summary>
    /// Models published by Stability AI.
    /// </summary>
    public static partial class StabilityAI
    {
        /// <summary>
        /// ﻿# Models from Microsoft, Partners, and Community Models from Microsoft, Partners, and Community models are a select portfolio of curated models both general-purpose and niche models across diverse scenarios by developed by Microsoft teams, partners, and community contributors  - **Managed by Microsoft:** Purchase and manage models directly through Azure with a single license, world class support and enterprise grade Azure infrastructure - **Validated by providers:** Each model is validated and maintained by its respective provider, with Azure offering integration and deployment guidance. - **Innovation and agility:** Combines Microsoft research models with rapid, community-driven advancements. - **Seamless Azure integration:** Standard Azure AI Foundry experience, with support managed by the model provider. - **Flexible deployment:** Deployable as **Managed Compute** or **Serverless API**, based on provider preference. [Learn more about models from Microsoft, Partners, and Community](https://aka.ms/Azure1P3PModels) # Key capabilities ## About this model Stable Diffusion 3.5 Large produces diverse outputs, creating images that are representative of the world, with different skin tones and features, all without requiring extensive prompting.  It also offers unmatched versatility, generating visuals in virtually any style, from 3D and photography to painting and line art. Our analysis shows that Stable Diffusion 3.5 Large leads the market in prompt adherence and rivals significantly larger models in image quality. ## Key model capabilities Stable Diffusion 3.5 Large produces diverse outputs, creating images that are representative of the world, with different skin tones and features, all without requiring extensive prompting.  It also offers unmatched versatility, generating visuals in virtually any style, from 3D and photography to painting and line art. Our analysis shows that Stable Diffusion 3.5 Large leads the market in prompt adherence and rivals significantly larger models in image quality. # Use cases See Responsible AI for additional considerations for responsible use. ## Key use cases Advertising and marketing Media and entertainment, Gaming and metaverse Education and training Retail Publishing ## Out of scope use cases The model was not trained to be factual or true representations of people or events. As such, using the model to generate such content is out-of-scope of the abilities of this model. # Pricing Pricing is based on a number of factors, including deployment type and tokens used. [See pricing details here.](https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067) # Technical specs At 8.1 billion parameters, with superior quality and prompt adherence, this base model is the most powerful in the Stable Diffusion family. This model is ideal for professional use cases at 1 megapixel resolution. ## Training cut-off date The provider has not supplied this information. ## Training time The provider has not supplied this information. ## Input formats The provider has not supplied this information. ## Output formats  The provider has not supplied this information. ## Supported languages The provider has not supplied this information. ## Sample JSON response The provider has not supplied this information. ## Model architecture The provider has not supplied this information. ## Long context The provider has not supplied this information. ## Optimizing model performance The provider has not supplied this information. ## Additional assets The provider has not supplied this information. # Training disclosure ## Training, testing and validation  This model was trained on a wide variety of data, including synthetic data and filtered publicly available data. # Distribution ## Distribution channels The provider has not supplied this information. # More information We believe in safe, responsible AI practices and take deliberate measures to ensure Integrity starts at the early stages of development. This means we have taken and continue to take reasonable steps to prevent the misuse of Stable Diffusion 3.5 by bad actors. For more information about our approach to Safety please visit our Stable Safety page.
        /// </summary>
        public static readonly AIFoundryModel StableDiffusion35Large = new() { Name = "Stable-Diffusion-3.5-Large", Version = "1", Format = "Stability AI" };

        /// <summary>
        /// ﻿# Models from Microsoft, Partners, and Community Models from Microsoft, Partners, and Community models are a select portfolio of curated models both general-purpose and niche models across diverse scenarios by developed by Microsoft teams, partners, and community contributors  - **Managed by Microsoft:** Purchase and manage models directly through Azure with a single license, world class support and enterprise grade Azure infrastructure - **Validated by providers:** Each model is validated and maintained by its respective provider, with Azure offering integration and deployment guidance.  - **Innovation and agility:** Combines Microsoft research models with rapid, community-driven advancements.  - **Seamless Azure integration:** Standard Azure AI Foundry experience, with support managed by the model provider.  - **Flexible deployment:** Deployable as **Managed Compute** or **Serverless API**, based on provider preference. [Learn more about models from Microsoft, Partners, and Community](https://aka.ms/Azure1P3PModels) # Key capabilities ## About this model Leveraging an enhanced version of SDXL, Stable Image Core, delivers exceptional speed and efficiency while maintaining the high-quality output synonymous with Stable Diffusion models. ## Key model capabilities The provider has not supplied this information. # Use cases See Responsible AI for additional considerations for responsible use. ## Key use cases 1. Advertising and marketing 2. Media and entertainment 3. Gaming and metaverse 4. Education and training 5. Retail 6. Publishing ## Out of scope use cases The provider has not supplied this information. # Pricing Pricing is based on a number of factors, including deployment type and tokens used. [See pricing details here.](https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067) # Technical specs The provider has not supplied this information. ## Training cut-off date The provider has not supplied this information. ## Training time The provider has not supplied this information. ## Input formats The provider has not supplied this information. ## Output formats The provider has not supplied this information. ## Supported languages The provider has not supplied this information. ## Sample JSON response The provider has not supplied this information. ## Model architecture The provider has not supplied this information. ## Long context The provider has not supplied this information. ## Optimizing model performance The provider has not supplied this information. ## Additional assets The provider has not supplied this information. # Training disclosure ## Training, testing and validation The provider has not supplied this information. # Distribution ## Distribution channels The provider has not supplied this information. # More information We believe in safe, responsible AI practices and take deliberate measures to ensure Integrity starts at the early stages of development. For more information about our approach to Safety please visit our [Stable Safety](https://stability.ai/safety) page.
        /// </summary>
        public static readonly AIFoundryModel StableImageCore = new() { Name = "Stable-Image-Core", Version = "1", Format = "Stability AI" };

        /// <summary>
        /// ﻿# Models from Microsoft, Partners, and Community Models from Microsoft, Partners, and Community models are a select portfolio of curated models both general-purpose and niche models across diverse scenarios by developed by Microsoft teams, partners, and community contributors  - **Managed by Microsoft:** Purchase and manage models directly through Azure with a single license, world class support and enterprise grade Azure infrastructure - **Validated by providers:** Each model is validated and maintained by its respective provider, with Azure offering integration and deployment guidance. - **Innovation and agility:** Combines Microsoft research models with rapid, community-driven advancements. - **Seamless Azure integration:** Standard Azure AI Foundry experience, with support managed by the model provider. - **Flexible deployment:** Deployable as **Managed Compute** or **Serverless API**, based on provider preference. [Learn more about models from Microsoft, Partners, and Community](https://aka.ms/Azure1P3PModels) # Key capabilities ## About this model Powered by the advanced capabilities of Stable Diffusion 3.5 Large, Stable Image Ultra sets a new standard in photorealism. It also excels in typography, dynamic lighting, and vibrant color rendering. ## Key model capabilities - Typography - Dynamic lighting - Vibrant color rendering - Product imagery for marketing and advertising # Use cases See Responsible AI for additional considerations for responsible use. ## Key use cases 1. Advertising and marketing 2. Media and entertainment 3. Gaming and metaverse 4. Education and training 5. Retail 6. Publishing ## Out of scope use cases The provider has not supplied this information. # Pricing Pricing is based on a number of factors, including deployment type and tokens used. [See pricing details here.](https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/microsoft/?msockid=1775f99b2f8e614e1ba1eb792e496067) # Technical specs The provider has not supplied this information. ## Training cut-off date The provider has not supplied this information. ## Training time The provider has not supplied this information. ## Input formats The provider has not supplied this information. ## Output formats  The provider has not supplied this information. ## Supported languages The provider has not supplied this information. ## Sample JSON response The provider has not supplied this information. ## Model architecture The provider has not supplied this information. ## Long context The provider has not supplied this information. ## Optimizing model performance The provider has not supplied this information. ## Additional assets The provider has not supplied this information. # Training disclosure ## Training, testing and validation  The provider has not supplied this information. # Distribution ## Distribution channels The provider has not supplied this information. # More information We believe in safe, responsible AI practices and take deliberate measures to ensure Integrity starts at the early stages of development. For more information about our approach to Safety please visit our [Stable Safety](https://stability.ai/safety) page.
        /// </summary>
        public static readonly AIFoundryModel StableImageUltra = new() { Name = "Stable-Image-Ultra", Version = "1", Format = "Stability AI" };
    }

    /// <summary>
    /// Models published by xAI.
    /// </summary>
    public static partial class XAI
    {
        /// <summary>
        /// Grok 3 is xAI&apos;s debut model, pretrained by Colossus at supermassive scale to excel in specialized domains like finance, healthcare, and the law.
        /// </summary>
        public static readonly AIFoundryModel Grok3 = new() { Name = "grok-3", Version = "1", Format = "xAI" };

        /// <summary>
        /// Grok 3 Mini is a lightweight model that thinks before responding. Trained on mathematic and scientific problems, it is great for logic-based tasks.
        /// </summary>
        public static readonly AIFoundryModel Grok3Mini = new() { Name = "grok-3-mini", Version = "1", Format = "xAI" };

        /// <summary>
        /// Grok 4 is the latest reasoning model from xAI with advanced reasoning and tool-use capabilities, enabling it to achieve new state-of-the-art performance across challenging academic and industry benchmarks.
        /// </summary>
        public static readonly AIFoundryModel Grok4 = new() { Name = "grok-4", Version = "1", Format = "xAI" };

        /// <summary>
        /// Grok 4 Fast is an efficiency-focused large language model developed by xAI, pre-trained on general-purpose data and post-trained on task demonstrations and tool use, with built-in safety features including refusal behaviors, a fixed system prompt enforcing
        /// </summary>
        public static readonly AIFoundryModel Grok4FastNonReasoning = new() { Name = "grok-4-fast-non-reasoning", Version = "1", Format = "xAI" };

        /// <summary>
        /// Grok 4 Fast is an efficiency-focused large language model developed by xAI, pre-trained on general-purpose data and post-trained on task demonstrations and tool use, with built-in safety features including refusal behaviors, a fixed system prompt enforcing
        /// </summary>
        public static readonly AIFoundryModel Grok4FastReasoning = new() { Name = "grok-4-fast-reasoning", Version = "1", Format = "xAI" };

        /// <summary>
        /// Grok Code Fast 1 is a fast, economical AI model for agentic coding, built from scratch with a new architecture, trained on programming-rich data, and fine-tuned for real-world coding tasks like bug fixes and project setup.
        /// </summary>
        public static readonly AIFoundryModel GrokCodeFast1 = new() { Name = "grok-code-fast-1", Version = "1", Format = "xAI" };
    }
}
