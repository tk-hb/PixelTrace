#include "PixelTraceLlama.h"
#include "llama.h"
#include "ggml-backend.h"
#include "mtmd.h"
#include "mtmd-helper.h"
#include <android/log.h>
#include <algorithm>
#include <climits>
#include <cstring>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace {
thread_local std::string last_error;
std::once_flag backend_once;
constexpr int max_tokens = 8192;

void log_message(ggml_log_level level, const char * text, void *) {
    int priority = level == GGML_LOG_LEVEL_ERROR ? ANDROID_LOG_ERROR : ANDROID_LOG_INFO;
    __android_log_write(priority, "PixelTrace.Llama", text);
}

struct engine {
    llama_model * model = nullptr;
    llama_context * context = nullptr;
    mtmd_context * vision = nullptr;
    ggml_backend_dev_t devices[2] = {nullptr, nullptr};
    int capacity = 0;
    bool rejected_cpu_compute = false;
    bool use_gpu = true;
    int threads = std::max(1u, std::thread::hardware_concurrency() / 2);

    ~engine() {
        if (context) llama_free(context);
        if (vision) mtmd_free(vision);
        if (model) llama_model_free(model);
    }

    static bool require_gpu_compute(ggml_tensor * tensor, bool ask, void * user_data) {
        auto & self = *static_cast<engine *>(user_data);
        if (!ask) return false;
        // Shape/index work may use CPU, but never silently perform the model's
        // matrix multiplications or image convolutions on CPU in a GPU engine.
        bool compute = tensor->op == GGML_OP_MUL_MAT || tensor->op == GGML_OP_MUL_MAT_ID ||
                       tensor->op == GGML_OP_CONV_2D;
        if (!compute || !tensor->buffer) return false;
        auto dev = ggml_backend_buft_get_device(ggml_backend_buffer_get_type(tensor->buffer));
        if (dev && (ggml_backend_dev_type(dev) == GGML_BACKEND_DEVICE_TYPE_GPU ||
                    ggml_backend_dev_type(dev) == GGML_BACKEND_DEVICE_TYPE_IGPU)) return false;
        self.rejected_cpu_compute = true;
        __android_log_print(ANDROID_LOG_ERROR, "PixelTrace.Llama", "Rejected CPU compute in GPU engine: %s (%s)", tensor->name, ggml_op_name(tensor->op));
        return true; // observe this node, then stop this graph; result is discarded
    }

    void check_gpu_compute() const {
        if (rejected_cpu_compute) throw std::runtime_error("Vulkan cannot execute a model matrix/convolution operation on this device. CPU fallback was rejected.");
    }

    void prepare_context(size_t count) {
        if (!count || count > max_tokens) {
            throw std::runtime_error("Input exceeds the 8192-token context; it was not truncated.");
        }
        int required = 256;
        while (required < static_cast<int>(count)) required *= 2;
        if (context && capacity == required) return;
        if (context) llama_free(context);
        context = nullptr;
        capacity = 0;
        auto params = llama_context_default_params();
        params.n_ctx = required;
        params.n_batch = required;
        params.n_ubatch = required;
        params.n_seq_max = 1;
        params.n_threads = threads;
        params.n_threads_batch = threads;
        params.embeddings = true;
        params.pooling_type = LLAMA_POOLING_TYPE_MEAN;
        params.attention_type = LLAMA_ATTENTION_TYPE_NON_CAUSAL;
        params.offload_kqv = use_gpu;
        params.op_offload = use_gpu;
        if (use_gpu) {
            params.cb_eval = require_gpu_compute;
            params.cb_eval_user_data = this;
        }
        context = llama_init_from_model(model, params);
        if (!context) throw std::runtime_error("BF16 embedding context initialization failed; the selected backend was not changed.");
        capacity = required;
    }

    void result(llama_batch_ext * batch, float * output) {
        // Non-causal attention and mean pooling require the entire input in one
        // process call, including both text wrappers and all image feature rows.
        auto memory = llama_get_memory(context);
        if (memory) llama_memory_clear(memory, true);
        int status = llama_process(context, LLAMA_PROCESS_TYPE_DECODE, batch);
        check_gpu_compute();
        if (status != 0) {
            throw std::runtime_error("BF16 embedding inference failed; engine and model were not changed.");
        }
        const float * values = llama_get_embeddings_seq(context, 0);
        if (!values) throw std::runtime_error("The model returned no pooled embedding.");
        std::copy_n(values, 768, output);
    }

    static void position(llama_batch_ext * batch, int index, int pos) {
        if (index < 0 || !llama_batch_ext_set_pos(batch, index, &pos) ||
            !llama_batch_ext_set_output_embd(batch, index, true)) {
            throw std::runtime_error("Failed to assemble the full embedding sequence.");
        }
    }

    void text(const uint8_t * input, size_t length, float * output) {
        if (length > INT32_MAX) throw std::runtime_error("Query is too long.");
        const auto * vocab = llama_model_get_vocab(model);
        // Special tokens come from the GGUF tokenizer; user text is never parsed
        // as multimodal control tokens. No separately downloaded tokenizer.
        int count = llama_tokenize(vocab, reinterpret_cast<const char *>(input), static_cast<int>(length), nullptr, 0, true, false);
        if (count >= 0) throw std::runtime_error("Query tokenization returned an invalid size.");
        if (-static_cast<int64_t>(count) > max_tokens) throw std::runtime_error("Query exceeds 8192 tokens; it was not truncated.");
        std::vector<llama_token> tokens(-count);
        count = llama_tokenize(vocab, reinterpret_cast<const char *>(input), static_cast<int>(length), tokens.data(), static_cast<int>(tokens.size()), true, false);
        if (count <= 0) throw std::runtime_error("Query tokenization failed.");
        prepare_context(count);
        std::unique_ptr<llama_batch_ext, decltype(&llama_batch_ext_free)> batch(llama_batch_ext_init(context), llama_batch_ext_free);
        if (!batch) throw std::runtime_error("Embedding batch allocation failed.");
        for (int i = 0; i < count; ++i) position(batch.get(), llama_batch_ext_add_token(batch.get(), 0, tokens[i]), i);
        result(batch.get(), output);
    }

    void image(const uint8_t * input, size_t length, float * output) {
        auto decoded = mtmd_helper_bitmap_init_from_buf(vision, input, length, false, mtmd_helper_init_opt_default());
        mtmd::bitmap_ptr bitmap(decoded.bitmap);
        if (decoded.video_ctx) mtmd_helper_video_free(decoded.video_ctx);
        if (!bitmap) throw std::runtime_error("Image decoding failed.");
        mtmd::input_chunks_ptr chunks(mtmd_input_chunks_init());
        mtmd_input_part part = {nullptr, bitmap.get()};
        const mtmd_input_part * parts[] = {&part};
        if (mtmd_tokenize_from_parts(vision, chunks.get(), parts, 1, true) != 0) {
            throw std::runtime_error("Multimodal image tokenization failed.");
        }
        prepare_context(mtmd_helper_get_n_tokens(chunks.get()));
        std::unique_ptr<llama_batch_ext, decltype(&llama_batch_ext_free)> batch(llama_batch_ext_init(context), llama_batch_ext_free);
        if (!batch) throw std::runtime_error("Embedding batch allocation failed.");
        const size_t width = llama_model_n_embd_inp(model);
        std::vector<std::vector<float>> features;
        int pos = 0;
        for (size_t c = 0; c < mtmd_input_chunks_size(chunks.get()); ++c) {
            const auto * chunk = mtmd_input_chunks_get(chunks.get(), c);
            if (mtmd_input_chunk_get_type(chunk) == MTMD_INPUT_CHUNK_TYPE_TEXT) {
                size_t count = 0;
                const auto * tokens = mtmd_input_chunk_get_tokens_text(chunk, &count);
                for (size_t i = 0; i < count; ++i) position(batch.get(), llama_batch_ext_add_token(batch.get(), 0, tokens[i]), pos++);
            } else if (mtmd_input_chunk_get_type(chunk) == MTMD_INPUT_CHUNK_TYPE_IMAGE) {
                if (mtmd_encode_chunk(vision, chunk) != 0) throw std::runtime_error("BF16 vision encoding failed on the selected backend.");
                check_gpu_compute();
                size_t count = mtmd_input_chunk_get_n_tokens(chunk);
                const float * values = mtmd_get_output_embd(vision);
                if (!values) throw std::runtime_error("Vision encoder returned no image features.");
                features.emplace_back(values, values + count * width);
                for (size_t i = 0; i < count; ++i) {
                    llama_embd row = {features.back().data() + i * width, 1, width};
                    position(batch.get(), llama_batch_ext_add_embd(batch.get(), 0, row), pos++);
                }
            } else {
                throw std::runtime_error("Unexpected non-image media chunk.");
            }
        }
        result(batch.get(), output);
    }
};
}

void * pt_llama_create_v2(const char * model, const char * projector, int32_t image_tokens, int32_t backend) {
    last_error.clear();
    try {
        std::call_once(backend_once, [] {
            llama_log_set(log_message, nullptr);
            mtmd_helper_log_set(log_message, nullptr);
            ggml_backend_load_all();
            llama_backend_init();
        });
        auto value = std::make_unique<engine>();
        if (backend != 0 && backend != 1) throw std::runtime_error("Invalid backend; choose CPU or Vulkan GPU.");
        value->use_gpu = backend == 1;
        if (value->use_gpu) {
            for (size_t i = 0; i < ggml_backend_dev_count(); ++i) {
                auto dev = ggml_backend_dev_get(i);
                const char * name = ggml_backend_reg_name(ggml_backend_dev_backend_reg(dev));
                auto type = ggml_backend_dev_type(dev);
                if ((type == GGML_BACKEND_DEVICE_TYPE_GPU || type == GGML_BACKEND_DEVICE_TYPE_IGPU) && std::strcmp(name, "Vulkan") == 0) {
                    value->devices[0] = dev;
                    break;
                }
            }
            if (!value->devices[0]) throw std::runtime_error("No usable Vulkan GPU. CPU fallback is disabled.");
        }
        auto params = llama_model_default_params();
        params.devices = value->devices;
        params.n_gpu_layers = value->use_gpu ? -1 : 0;
        params.split_mode = LLAMA_SPLIT_MODE_NONE;
        value->model = llama_model_load_from_file(model, params);
        if (!value->model) throw std::runtime_error("BF16 text model could not be loaded on the selected backend.");
        char architecture[64] = {};
        llama_model_meta_val_str(value->model, "general.architecture", architecture, sizeof(architecture));
        if (std::strcmp(architecture, "gemma-embedding2") != 0 ||
            llama_model_ftype(value->model) != LLAMA_FTYPE_MOSTLY_BF16 || llama_model_n_embd_out(value->model) != 768) {
            throw std::runtime_error("Expected an EmbeddingGemma 2 BF16 model with 768 output dimensions.");
        }
        auto vision_params = mtmd_context_params_default();
        vision_params.use_gpu = value->use_gpu;
        vision_params.device = value->devices[0];
        vision_params.n_threads = value->threads;
        vision_params.image_max_tokens = image_tokens;
        vision_params.image_min_tokens = image_tokens;
        vision_params.warmup = false;
        if (value->use_gpu) {
            vision_params.cb_eval = engine::require_gpu_compute;
            vision_params.cb_eval_user_data = value.get();
        }
        value->vision = mtmd_init_from_file(projector, value->model, vision_params);
        if (!value->vision || !mtmd_support_vision(value->vision)) throw std::runtime_error("BF16 vision model could not be loaded on the selected backend.");
        const char * device_name = value->use_gpu ? ggml_backend_dev_name(value->devices[0]) : "CPU";
        __android_log_print(ANDROID_LOG_INFO, "PixelTrace.Llama", "BF16 GGUF loaded: text + vision, device %s. BF16 weights do not imply all-BF16 arithmetic.", device_name);
        return value.release();
    } catch (const std::exception & error) {
        last_error = error.what();
        return nullptr;
    } catch (...) {
        last_error = "Unknown native model loading failure.";
        return nullptr;
    }
}

void pt_llama_free(void * value) { delete static_cast<engine *>(value); }

int32_t pt_llama_encode(void * value, int32_t kind, const uint8_t * input, size_t length, float * output, int32_t dimensions) {
    last_error.clear();
    try {
        if (!value || !input || !length || !output || dimensions != 768) throw std::runtime_error("Invalid embedding input or output buffer.");
        auto & instance = *static_cast<engine *>(value);
        instance.rejected_cpu_compute = false;
        if (kind == 0) instance.text(input, length, output);
        else if (kind == 1) instance.image(input, length, output);
        else throw std::runtime_error("Unsupported input kind.");
        return 0;
    } catch (const std::exception & error) {
        last_error = error.what();
        return -1;
    } catch (...) {
        last_error = "Unknown native embedding failure.";
        return -1;
    }
}

const char * pt_llama_error(void) { return last_error.c_str(); }
const char * pt_llama_revision(void) { return PT_LLAMA_REVISION; }
