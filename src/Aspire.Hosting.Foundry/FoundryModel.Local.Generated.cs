// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
namespace Aspire.Hosting.Foundry;

/// <summary>
/// Generated strongly typed model descriptors for Microsoft Foundry.
/// </summary>
public partial class FoundryModel
{
    /// <summary>
    /// Models available on Foundry Local.
    /// </summary>
    public static partial class Local
    {
        /// <summary>
        ///   <para>This model is an optimized version of DeepSeek-R1-Distill-Qwen-1.5B to enable local inference on Intel GPUs.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> MIT</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the DeepSeek-R1-Distill-Qwen-1.5B for local inference on Intel GPUs.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/deepseek-ai/DeepSeek-R1-Distill-Qwen-1.5B">DeepSeek-R1-Distill-Qwen-1.5B</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel DeepseekR115b = new() { Name = "deepseek-r1-1.5b", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of DeepSeek-R1-Distill-Qwen-14B to enable local inference on Intel GPUs.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> MIT</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the DeepSeek-R1-Distill-Qwen-14B for local inference on Intel GPUs.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/deepseek-ai/DeepSeek-R1-Distill-Qwen-14B">DeepSeek-R1-Distill-Qwen-14B</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel DeepseekR114b = new() { Name = "deepseek-r1-14b", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of DeepSeek-R1-Distill-Qwen-7B to enable local inference on Intel GPUs.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> MIT</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the DeepSeek-R1-Distill-Qwen-7B for local inference on Intel GPUs.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/deepseek-ai/DeepSeek-R1-Distill-Qwen-7B">DeepSeek-R1-Distill-Qwen-7B</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel DeepseekR17b = new() { Name = "deepseek-r1-7b", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of gpt-oss-20b to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> Apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License Description:</b> Use of this model is subject to the terms of the Apache License, Version 2.0, available at <see href="http://www.apache.org/licenses/LICENSE-2.0">http://www.apache.org/licenses/LICENSE-2.0</see>.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the gpt-oss-20b model for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Azure AI Foundry model <see href="https://ai.azure.com/catalog/models/gpt-oss-20b">gpt-oss-20b</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel GptOss20b = new() { Name = "gpt-oss-20b", Version = "1", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Mistral-7B-Instruct-v0.2 to enable local inference on Intel GPUs.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Mistral-7B-Instruct-v0.2 for local inference on Intel GPUs.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/mistralai/Mistral-7B-Instruct-v0.2">Mistral-7B-Instruct-v0.2</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Mistral7bV02 = new() { Name = "mistral-7b-v0.2", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of nemotron-speech-streaming-en-0.6b to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> MIT</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is an optimized version of the nemotron-speech-streaming-en-0.6b model for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/nvidia/nemotron-speech-streaming-en-0.6b">nemotron-speech-streaming-en-0.6b</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel NemotronSpeechStreamingEn06b = new() { Name = "nemotron-speech-streaming-en-0.6b", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Phi-3-Mini-128K-Instruct to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> MIT</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Phi-3-Mini-128K-Instruct for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/microsoft/Phi-3-Mini-128K-Instruct">Phi-3-Mini-128K-Instruct</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Phi3Mini128k = new() { Name = "phi-3-mini-128k", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Phi-3-Mini-4K-Instruct to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> MIT</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Phi-3-Mini-4K-Instruct for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/microsoft/Phi-3-mini-4k-instruct">Phi-3-Mini-4K-Instruct</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Phi3Mini4k = new() { Name = "phi-3-mini-4k", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Phi-3.5-mini-instruct to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> MIT</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Phi-3.5-mini-instruct for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/microsoft/Phi-3.5-mini-instruct">Phi-3.5-mini-instruct</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Phi35Mini = new() { Name = "phi-3.5-mini", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Phi-4 to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> MIT</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Phi-4 for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/microsoft/Phi-4">Phi-4</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Phi4 = new() { Name = "phi-4", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Phi-4-mini-instruct to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> MIT</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Phi-4-mini-instruct for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/microsoft/Phi-4-mini-instruct">Phi-4-mini-instruct</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Phi4Mini = new() { Name = "phi-4-mini", Version = "5", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Phi-4-mini-reasoning to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> MIT</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Phi-4-mini-reasoning for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/microsoft/Phi-4-mini-reasoning">Phi-4-mini-reasoning</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Phi4MiniReasoning = new() { Name = "phi-4-mini-reasoning", Version = "3", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen2.5-0.5B-Instruct to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen2.5-0.5B-Instruct for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct">Qwen2.5-0.5B-Instruct</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen2505b = new() { Name = "qwen2.5-0.5b", Version = "4", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen2.5-1.5B-Instruct to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen2.5-1.5B-Instruct for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct">Qwen2.5-1.5B-Instruct</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen2515b = new() { Name = "qwen2.5-1.5b", Version = "4", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen2.5-14B-Instruct to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen2.5-14B-Instruct for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen2.5-14B-Instruct">Qwen2.5-14B-Instruct</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen2514b = new() { Name = "qwen2.5-14b", Version = "4", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen2.5-7B-Instruct to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen2.5-7B-Instruct for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen2.5-7B-Instruct">Qwen2.5-7B-Instruct</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen257b = new() { Name = "qwen2.5-7b", Version = "4", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen2.5-Coder-0.5B-Instruct to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen2.5-Coder-0.5B-Instruct for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen2.5-Coder-0.5B-Instruct">Qwen2.5-Coder-0.5B-Instruct</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen25Coder05b = new() { Name = "qwen2.5-coder-0.5b", Version = "4", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen2.5-Coder-1.5B-Instruct to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen2.5-Coder-1.5B-Instruct for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen2.5-Coder-1.5B-Instruct">Qwen2.5-Coder-1.5B-Instruct</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen25Coder15b = new() { Name = "qwen2.5-coder-1.5b", Version = "4", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen2.5-Coder-14B-Instruct to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen2.5-Coder-14B-Instruct for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen2.5-Coder-14B-Instruct">Qwen2.5-Coder-14B-Instruct</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen25Coder14b = new() { Name = "qwen2.5-coder-14b", Version = "4", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen2.5-Coder-7B-Instruct to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen2.5-Coder-7B-Instruct for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen2.5-Coder-7B-Instruct">Qwen2.5-Coder-7B-Instruct</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen25Coder7b = new() { Name = "qwen2.5-coder-7b", Version = "4", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen3-0.6B to enable local inference. This model uses KLD Gradient quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen3-0.6B for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen3-0.6B">Qwen3-0.6B</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen306b = new() { Name = "qwen3-0.6b", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen3-1.7B to enable local inference. This model uses KLD Gradient quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen3-1.7B for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen3-1.7B">Qwen3-1.7B</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen317b = new() { Name = "qwen3-1.7b", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen3-14B to enable local inference. This model uses GPTQ quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen3-14B for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen3-14B">Qwen3-14B</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen314b = new() { Name = "qwen3-14b", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen3-4B to enable local inference. This model uses KLD Gradient quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen3-4B for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen3-4B">Qwen3-4B</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen34b = new() { Name = "qwen3-4b", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen3-8B to enable local inference. This model uses KLD Gradient quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen3-8B for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen3-8B">Qwen3-8B</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen38b = new() { Name = "qwen3-8b", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>
        ///     <b>Qwen3 Embedding 0.6B Cuda Gpu</b>
        ///   </para>
        ///   <para>This is the GPU (NVIDIA CUDA)-optimized variant of qwen3-embedding-0.6b, a text embedding model from the Qwen3 family developed by Alibaba Cloud and optimized by Microsoft.</para>
        ///   <para>
        ///     <b>Model Details</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Type</b>: Text Embedding (ONNX)</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Parameters</b>: 0.6 billion</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Context Length</b>: 32K tokens</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Embedding Dimension</b>: Up to 1024</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Quantization</b>: KLD Gradient quantization</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Target Device</b>: GPU (NVIDIA CUDA)</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Execution Provider</b>: CUDAExecutionProvider</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Supported Languages</b>: 100+</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Intended Use</b>
        ///   </para>
        ///   <para>This model is optimized for local execution on devices with GPU (NVIDIA CUDA) hardware acceleration using Foundry Local.</para>
        ///   <para>
        ///     <b>Capabilities</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Text retrieval and semantic search</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Code retrieval</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Text classification and clustering</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Bitext mining</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Multilingual and cross-lingual retrieval</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>License</b>
        ///   </para>
        ///   <para>This model is licensed under Apache 2.0. See <see href="https://huggingface.co/Qwen/Qwen3-Embedding-0.6B/blob/main/LICENSE">license details</see>.</para>
        ///   <para>
        ///     <b>Source</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>HuggingFace: <see href="https://huggingface.co/Qwen/Qwen3-Embedding-0.6B">Qwen3-Embedding-0.6B</see></para>
        ///       </description>
        ///     </item>
        ///   </list>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen3Embedding06b = new() { Name = "qwen3-embedding-0.6b", Version = "1", Format = "Microsoft" };

        /// <summary>
        ///   <para>
        ///     <b>Qwen3 Embedding 8B Cuda Gpu</b>
        ///   </para>
        ///   <para>This is the GPU (NVIDIA CUDA)-optimized variant of qwen3-embedding-8b, a text embedding model from the Qwen3 family developed by Alibaba Cloud and optimized by Microsoft.</para>
        ///   <para>
        ///     <b>Model Details</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Type</b>: Text Embedding (ONNX)</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Parameters</b>: 8 billion</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Context Length</b>: 32K tokens</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Embedding Dimension</b>: Up to 4096</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Quantization</b>: KLD Gradient quantization</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Target Device</b>: GPU (NVIDIA CUDA)</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Execution Provider</b>: CUDAExecutionProvider</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Supported Languages</b>: 100+</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Intended Use</b>
        ///   </para>
        ///   <para>This model is optimized for local execution on devices with GPU (NVIDIA CUDA) hardware acceleration using Foundry Local.</para>
        ///   <para>
        ///     <b>Capabilities</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>Text retrieval and semantic search</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Code retrieval</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Text classification and clustering</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Bitext mining</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>Multilingual and cross-lingual retrieval</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>License</b>
        ///   </para>
        ///   <para>This model is licensed under Apache 2.0. See <see href="https://huggingface.co/Qwen/Qwen3-Embedding-8B/blob/main/LICENSE">license details</see>.</para>
        ///   <para>
        ///     <b>Source</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>HuggingFace: <see href="https://huggingface.co/Qwen/Qwen3-Embedding-8B">Qwen3-Embedding-8B</see></para>
        ///       </description>
        ///     </item>
        ///   </list>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen3Embedding8b = new() { Name = "qwen3-embedding-8b", Version = "1", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen3-VL-2B-Instruct to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen3-VL-2B-Instruct for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen3-VL-2B-Instruct">Qwen3-VL-2B-Instruct</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen3Vl2bInstruct = new() { Name = "qwen3-vl-2b-instruct", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen3-VL-4B-Instruct to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen3-VL-4B-Instruct for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen3-VL-4B-Instruct">Qwen3-VL-4B-Instruct</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen3Vl4bInstruct = new() { Name = "qwen3-vl-4b-instruct", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen3-VL-8B-Instruct to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen3-VL-8B-Instruct for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen3-VL-8B-Instruct">Qwen3-VL-8B-Instruct</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen3Vl8bInstruct = new() { Name = "qwen3-vl-8b-instruct", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen3.5-0.8B to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen3.5-0.8B for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen3.5-0.8B">Qwen3.5-0.8B</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen3508b = new() { Name = "qwen3.5-0.8b", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen3.5-2B to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen3.5-2B for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen3.5-2B">Qwen3.5-2B</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen352b = new() { Name = "qwen3.5-2b", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen3.5-4B to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen3.5-4B for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen3.5-4B">Qwen3.5-4B</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen354b = new() { Name = "qwen3.5-4b", Version = "2", Format = "Microsoft" };

        /// <summary>
        ///   <para>This model is an optimized version of Qwen3.5-9B to enable local inference. This model uses RTN quantization.</para>
        ///   <para>
        ///     <b>Model Description</b>
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Developed by:</b> Microsoft</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model type:</b> ONNX</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>License:</b> apache-2.0</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Model Description:</b> This is a conversion of the Qwen3.5-9B for local inference.</para>
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <description>
        ///         <para>
        ///           <b>Disclaimer:</b> Model is only an optimization of the base model, any risk associated with the model is the responsibility of the user of the model. Please verify and test for your scenarios. There may be a slight difference in output from the base model with the optimizations applied. Note that optimizations applied are distinct from fine tuning and thus do not alter the intended uses or capabilities of the model.</para>
        ///       </description>
        ///     </item>
        ///   </list>
        ///   <para>
        ///     <b>Base Model Information</b>
        ///   </para>
        ///   <para>See Hugging Face model <see href="https://huggingface.co/Qwen/Qwen3.5-9B">Qwen3.5-9B</see> for details.</para>
        /// </summary>
        [AspireValue("FoundryModels")]
        public static readonly FoundryModel Qwen359b = new() { Name = "qwen3.5-9b", Version = "2", Format = "Microsoft" };
    }
}
