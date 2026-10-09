// Use the official SDK installed in a temporary directory; no repo dependency.
import { createRequire } from "node:module";
import { readFile, writeFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import { performance } from "node:perf_hooks";

const [sdkDirectory, outputPath, kvCacheType = "q8_0", nativeDevice = "", capacityText = "132096", workflowTemplatePath] = process.argv.slice(2);
const capacity = Number(capacityText);
if (!Number.isInteger(capacity) || capacity < 2048 || capacity > 132096) {
  throw new Error("Benchmark context capacity must be an integer from 2048 through 132096.");
}
if (!["q8_0", "q4_0"].includes(kvCacheType)) {
  throw new Error("Benchmark KV cache must be q8_0 or q4_0.");
}
if (nativeDevice && !/^(CUDA|ROCm|Vulkan)\d+$/.test(nativeDevice)) {
  throw new Error("Expected an explicit CUDA, ROCm or Vulkan engine device.");
}
const require = createRequire(import.meta.url);
const { LMStudioClient } = require(`${sdkDirectory}/node_modules/@lmstudio/sdk/dist/index.cjs`);
const client = new LMStudioClient({ baseUrl: "ws://127.0.0.1:12349" });
const config = {
  gpu: { ratio: "max", mainGpu: 0 },
  gpuStrictVramCap: false,
  offloadKVCacheToGpu: true,
  contextLength: capacity,
  maxParallelPredictions: 1,
  evalBatchSize: 512,
  physicalBatchSize: 512,
  flashAttention: true,
  contextCheckpoints: 32,
  seed: 20260930,
  tryMmap: false,
  keepModelInMemory: false,
  llamaKCacheQuantizationType: kvCacheType,
  llamaVCacheQuantizationType: kvCacheType,
  speculativeDraftMtp: false,
  speculativeDraftSimple: false,
  llamaCppArgumentsOverride: {
    enabled: true,
    excludeAllConfig: false,
    disabledParameters: ["--threads", "--threads-batch", "--split-mode", "--temp", "--seed", "--top-k", "--top-p", "--min-p", "--repeat-penalty", "--presence-penalty"],
    overrideParameters: [
      { key: "--threads", value: "8" },
      { key: "--threads-batch", value: "8" },
      { key: "--split-mode", value: "none" },
      { key: "--temp", value: "0" },
      { key: "--seed", value: "20260930" },
      { key: "--top-k", value: "0" },
      { key: "--top-p", value: "1" },
      { key: "--min-p", value: "0" },
      { key: "--repeat-penalty", value: "1" },
      { key: "--presence-penalty", value: "0" },
    ],
  },
};
let workflowTemplateHash = null;
if (workflowTemplatePath) {
  const template = await readFile(workflowTemplatePath, "utf8");
  config.promptTemplate = { type: "jinja", jinjaPromptTemplate: { template } };
  workflowTemplateHash = createHash("sha256").update(template).digest("hex");
}
if (nativeDevice) {
  config.llamaCppArgumentsOverride.disabledParameters.push("--device", "--main-gpu");
  config.llamaCppArgumentsOverride.overrideParameters.push(
    { key: "--device", value: nativeDevice },
    { key: "--main-gpu", value: "0" },
  );
}
const start = performance.now();
const model = await client.llm.load(
  "agentic-router-benchmark/qwen3.8-27b/qwen3.8-27b-Q4_K_M.gguf",
  { identifier: "ar-runtime-context-benchmark", ttl: 3600, config, verbose: false },
);
const effective = await model.getLoadConfig();
const effectiveTemplate = effective.promptTemplate?.jinjaPromptTemplate?.template;
const effectiveTemplateHash = typeof effectiveTemplate === "string"
  ? createHash("sha256").update(effectiveTemplate).digest("hex") : null;
if (workflowTemplateHash && effectiveTemplateHash && workflowTemplateHash !== effectiveTemplateHash) {
  throw new Error("Effective benchmark workflow template differs from the requested template.");
}
delete effective.promptTemplate;
const requested = { ...config };
delete requested.promptTemplate;
const info = await model.getModelInfo();
const record = {
  loading_stage_wall_s: (performance.now() - start) / 1000,
  config_requested: requested,
  config_effective: effective,
  workflow_prompt_template_sha256: workflowTemplateHash,
  effective_prompt_template_sha256: effectiveTemplateHash,
  model: { identifier: info.identifier, modelKey: info.modelKey,
    architecture: info.architecture, quantization: info.quantization, contextLength: info.contextLength },
};
await writeFile(outputPath, JSON.stringify(record, null, 2));
console.log(JSON.stringify(record, null, 2));
process.exit(0);
