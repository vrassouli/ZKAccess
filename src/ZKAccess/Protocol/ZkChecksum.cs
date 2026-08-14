using System.Buffers.Binary;

namespace ZKAccess.Protocol;

internal static class ZkChecksum
{
    public static ushort Calculate(ReadOnlySpan<byte> packet)
    {
        var checksum = 0;
        var i = 0;

        while (i + 1 < packet.Length)
        {
            checksum += BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(i, 2));

            if (checksum > ushort.MaxValue)
                checksum -= ushort.MaxValue;

            i += 2;
        }

        if (i < packet.Length)
            checksum += packet[i];

        while (checksum > ushort.MaxValue)
            checksum -= ushort.MaxValue;

        checksum = ~checksum;

        while (checksum < 0)
            checksum += ushort.MaxValue;

        return (ushort)checksum;
    }
}
