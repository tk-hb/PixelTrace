package com.tkhb.pixeltrace;

import com.google.ai.edge.litertlm.Backend;
import com.google.ai.edge.litertlm.EmbeddingEngine;
import com.google.ai.edge.litertlm.EmbeddingEngineConfig;
import com.google.ai.edge.litertlm.EmbeddingOptions;
import com.google.ai.edge.litertlm.InputData;
import java.util.Collections;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.Callable;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Small Java surface for the C# binding. Native engine work stays on one thread. */
public final class LocalEmbedder implements AutoCloseable {
    private final ExecutorService executor = Executors.newSingleThreadExecutor(r -> {
        Thread thread = new Thread(r, "PixelTrace-Embedding");
        thread.setDaemon(true);
        return thread;
    });
    private EmbeddingEngine engine;

    public LocalEmbedder(String modelPath, String cacheDir, String device, int visionTokens) {
        try {
            execute(() -> {
                Backend backend;
                if (device.equals("gpu")) backend = new Backend.GPU();
                else if (device.equals("cpu")) backend = new Backend.CPU();
                else throw new IllegalArgumentException("Unsupported embedding backend: " + device);
                EmbeddingEngineConfig config = new EmbeddingEngineConfig(
                    modelPath, backend, backend, null, cacheDir, null, 1024, visionTokens);
                engine = new EmbeddingEngine(config);
                engine.initialize();
                return null;
            });
        } catch (RuntimeException error) {
            try {
                execute(() -> { if (engine != null) engine.close(); return null; });
            } catch (RuntimeException cleanup) {
                error.addSuppressed(cleanup);
            } finally {
                executor.shutdown();
            }
            throw error;
        }
    }

    public float[] embedText(String text) {
        return execute(() -> engine.computeEmbedding(
            Collections.singletonList(new InputData.Text(text)),
            new EmbeddingOptions(true, true, 768)).getEmbedding());
    }

    public float[] embedImage(byte[] encodedImage) {
        return execute(() -> engine.computeEmbedding(
            Collections.singletonList(new InputData.Image(encodedImage)),
            new EmbeddingOptions(true, true, 768)).getEmbedding());
    }

    /** One batch request. LiteRT-LM 0.18 processes its entries sequentially. */
    public float[] embedImages(byte[][] encodedImages) {
        return execute(() -> {
            List<List<InputData>> requests = new ArrayList<>(encodedImages.length);
            for (byte[] image : encodedImages) {
                requests.add(Collections.singletonList(new InputData.Image(image)));
            }
            List<com.google.ai.edge.litertlm.EmbeddingResponse> responses = engine.computeEmbeddingBatch(
                requests, new EmbeddingOptions(true, true, 768));
            if (responses.size() != encodedImages.length) throw new IllegalStateException("Embedding batch result count mismatch");
            float[] result = new float[Math.multiplyExact(encodedImages.length, 768)];
            for (int i = 0; i < responses.size(); i++) {
                float[] vector = responses.get(i).getEmbedding();
                if (vector.length != 768) throw new IllegalStateException("Embedding dimension mismatch");
                System.arraycopy(vector, 0, result, i * 768, 768);
            }
            return result;
        });
    }

    private <T> T execute(Callable<T> work) {
        try {
            return executor.submit(work).get();
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
            throw new IllegalStateException("Embedding thread interrupted", error);
        } catch (ExecutionException error) {
            Throwable cause = error.getCause();
            if (cause instanceof RuntimeException) throw (RuntimeException) cause;
            if (cause instanceof Error) throw (Error) cause;
            throw new IllegalStateException("Embedding failed", cause);
        }
    }

    @Override public void close() {
        if (executor.isShutdown()) return;
        try {
            execute(() -> {
                if (engine != null) { engine.close(); engine = null; }
                return null;
            });
        } finally {
            executor.shutdown();
        }
    }
}
