using Android.Graphics;

namespace PixelTrace.Mobile;

internal sealed record OnnxImagePatches(float[] Pixels, long[] Positions, int Tokens)
{
    public const int PatchDimension = 16 * 16 * 3;
    public static OnnxImagePatches Create(byte[] content, int maxTokens, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using Bitmap source = BitmapFactory.DecodeByteArray(content, 0, content.Length)
            ?? throw new InvalidDataException("ONNX 입력 이미지를 읽지 못했습니다.");
        // Gemma pools 3x3 patches of 16x16 pixels into each image token.
        const int cell = 48;
        double scale = Math.Sqrt(maxTokens * cell * cell / ((double)source.Width * source.Height));
        int width = Math.Max(cell, (int)(source.Width * scale / cell) * cell);
        int height = Math.Max(cell, (int)(source.Height * scale / cell) * cell);
        // Very wide/tall photos still need at least one pooling cell per axis.
        width = Math.Min(width, maxTokens * cell);
        height = Math.Min(height, maxTokens * cell);
        Bitmap resized = Bitmap.CreateScaledBitmap(source, width, height, true)!;
        try
        {
            int columns = width / 16;
            int rows = height / 16;
            float[] pixels = new float[checked(rows * columns * PatchDimension)];
            long[] positions = new long[rows * columns * 2];
            int[] scanline = new int[width * 16];
            for (int row = 0; row < rows; row++)
            {
                ct.ThrowIfCancellationRequested();
                resized.GetPixels(scanline, 0, width, 0, row * 16, width, 16);
                for (int column = 0; column < columns; column++)
                {
                    int patch = row * columns + column;
                    positions[patch * 2] = column;
                    positions[patch * 2 + 1] = row;
                    int target = patch * PatchDimension;
                    for (int y = 0; y < 16; y++)
                    {
                        for (int x = 0; x < 16; x++)
                        {
                            int color = scanline[y * width + column * 16 + x];
                            pixels[target++] = ((color >> 16) & 255) / 255f;
                            pixels[target++] = ((color >> 8) & 255) / 255f;
                            pixels[target++] = (color & 255) / 255f;
                        }
                    }
                }
            }

            return new(pixels, positions, rows * columns / 9);
        }
        finally
        {
            if (!ReferenceEquals(source, resized))
            {
                resized.Dispose();
            }
        }
    }
}
