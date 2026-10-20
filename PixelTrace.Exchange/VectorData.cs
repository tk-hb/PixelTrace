using System.Buffers.Binary;

namespace PixelTrace.Exchange;

public static class VectorData
{
    public const int Dimensions = 768;
    public const int ByteCount = Dimensions * sizeof(float);
    public static byte[] Encode(ReadOnlySpan<float> vector)
    {
        if (vector.Length != Dimensions)
        {
            throw new InvalidDataException("임베딩은 768차원이어야 합니다.");
        }

        byte[] bytes = new byte[ByteCount];
        for (int i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)), vector[i]);
        }

        return bytes;
    }

    public static float[] Decode(ReadOnlySpan<byte> bytes)
    {
        float[] vector = DecodeRaw(bytes);
        double norm = 0;
        foreach (float value in vector)
        {
            if (!float.IsFinite(value))
            {
                throw new InvalidDataException("임베딩에 유효하지 않은 수가 있습니다.");
            }

            norm += (double)value * value;
        }

        if (norm == 0)
        {
            throw new InvalidDataException("빈 임베딩입니다.");
        }

        norm = Math.Sqrt(norm);
        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }

        return vector;
    }

    public static float[] DecodeRaw(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteCount)
        {
            throw new InvalidDataException("저장된 임베딩 크기가 올바르지 않습니다.");
        }

        float[] vector = new float[Dimensions];
        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * sizeof(float), sizeof(float)));
        }

        return vector;
    }

    public static void Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteCount)
        {
            throw new InvalidDataException("저장된 임베딩 크기가 올바르지 않습니다.");
        }

        double norm = 0;
        for (int i = 0; i < Dimensions; i++)
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * sizeof(float), sizeof(float)));
            if (!float.IsFinite(value))
            {
                throw new InvalidDataException("임베딩에 유효하지 않은 수가 있습니다.");
            }

            norm += (double)value * value;
        }

        if (norm == 0)
        {
            throw new InvalidDataException("빈 임베딩입니다.");
        }
    }
}
