using PixelTrace.Exchange;

namespace PixelTrace.Mobile;

internal static class EmbeddingVector
{
    public const int Dimensions = VectorData.Dimensions;
    public const int ByteCount = VectorData.ByteCount;

    public static double Norm(ReadOnlySpan<float> vector)
    {
        if (vector.Length != Dimensions)
        {
            throw new InvalidDataException("임베딩 벡터는 768차원이어야 합니다.");
        }

        double squaredNorm = 0;
        foreach (float value in vector)
        {
            if (!float.IsFinite(value))
            {
                throw new InvalidDataException("임베딩 벡터에 NaN 또는 Infinity가 포함되어 있습니다.");
            }

            squaredNorm += (double)value * value;
        }

        if (squaredNorm == 0)
        {
            throw new InvalidDataException("빈 임베딩 벡터입니다.");
        }

        return Math.Sqrt(squaredNorm);
    }

    public static float[] Normalize(float[] vector)
    {
        double norm = Norm(vector);
        for (int i = 0; i < vector.Length; i++)
        {
            // Keep the divisor in double precision: a finite float vector can
            // have a norm larger than float.MaxValue.
            vector[i] = (float)(vector[i] / norm);
        }

        return vector;
    }

    public static float[] Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteCount)
        {
            throw new InvalidDataException("저장된 벡터의 크기가 올바르지 않습니다.");
        }

        return VectorData.DecodeRaw(bytes);
    }

    public static byte[] Encode(ReadOnlySpan<float> vector)
    {
        if (vector.Length != Dimensions)
        {
            throw new InvalidDataException("임베딩 벡터는 768차원이어야 합니다.");
        }

        return VectorData.Encode(vector);
    }
}
