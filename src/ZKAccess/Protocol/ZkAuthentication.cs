namespace ZKAccess.Protocol;

internal static class ZkAuthentication
{
    public static byte[] MakeCommKey(int commKey, ushort sessionId, byte ticks = 50)
    {
        uint reversed = 0;
        var key = unchecked((uint)commKey);

        for (var i = 0; i < 32; i++)
        {
            reversed <<= 1;
            if ((key & (1u << i)) != 0)
                reversed |= 1;
        }

        reversed = unchecked(reversed + sessionId);

        var bytes = BitConverter.GetBytes(reversed);
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(bytes);

        bytes[0] ^= (byte)'Z';
        bytes[1] ^= (byte)'K';
        bytes[2] ^= (byte)'S';
        bytes[3] ^= (byte)'O';

        (bytes[0], bytes[2]) = (bytes[2], bytes[0]);
        (bytes[1], bytes[3]) = (bytes[3], bytes[1]);

        bytes[0] ^= ticks;
        bytes[1] ^= ticks;
        bytes[2] = ticks;
        bytes[3] ^= ticks;

        return bytes;
    }
}
