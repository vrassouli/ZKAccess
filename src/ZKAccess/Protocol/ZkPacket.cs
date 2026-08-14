using System.Buffers.Binary;

namespace ZKAccess.Protocol;

internal static class ZkPacket
{
    private const ushort Magic1 = 0x5050;
    private const ushort Magic2 = 0x7D82;

    public static byte[] BuildTcpRequest(
        ushort command,
        ReadOnlySpan<byte> data,
        ushort sessionId,
        ushort replyId)
    {
        var commandPacket = BuildCommand(command, data, sessionId, replyId);
        var tcpPacket = new byte[8 + commandPacket.Length];

        BinaryPrimitives.WriteUInt16LittleEndian(tcpPacket.AsSpan(0, 2), Magic1);
        BinaryPrimitives.WriteUInt16LittleEndian(tcpPacket.AsSpan(2, 2), Magic2);
        BinaryPrimitives.WriteUInt32LittleEndian(tcpPacket.AsSpan(4, 4), (uint)commandPacket.Length);
        commandPacket.CopyTo(tcpPacket.AsSpan(8));

        return tcpPacket;
    }

    public static ZkResponse ParseTcpResponse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 16)
            throw new ZkProtocolException("ZKTeco TCP response is shorter than 16 bytes.");

        var magic1 = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(0, 2));
        var magic2 = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(2, 2));

        if (magic1 != Magic1 || magic2 != Magic2)
            throw new ZkProtocolException("Invalid ZKTeco TCP framing magic.");

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(4, 4));
        if (payloadLength < 8 || payloadLength > int.MaxValue || packet.Length != 8 + (int)payloadLength)
            throw new ZkProtocolException("Invalid ZKTeco TCP payload length.");

        var command = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(8, 2));
        var checksum = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(10, 2));
        var sessionId = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(12, 2));
        var replyId = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(14, 2));
        var data = packet.Slice(16).ToArray();

        return new ZkResponse(command, checksum, sessionId, replyId, data);
    }

    private static byte[] BuildCommand(
        ushort command,
        ReadOnlySpan<byte> data,
        ushort sessionId,
        ushort replyId)
    {
        var checksumPacket = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(checksumPacket.AsSpan(0, 2), command);
        BinaryPrimitives.WriteUInt16LittleEndian(checksumPacket.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(checksumPacket.AsSpan(4, 2), sessionId);
        BinaryPrimitives.WriteUInt16LittleEndian(checksumPacket.AsSpan(6, 2), replyId);
        data.CopyTo(checksumPacket.AsSpan(8));

        var checksum = ZkChecksum.Calculate(checksumPacket);

        replyId++;
        if (replyId == ushort.MaxValue)
            replyId = 0;

        var packet = new byte[checksumPacket.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), command);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), checksum);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4, 2), sessionId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6, 2), replyId);
        data.CopyTo(packet.AsSpan(8));

        return packet;
    }
}

internal sealed record ZkResponse(
    ushort Command,
    ushort Checksum,
    ushort SessionId,
    ushort ReplyId,
    byte[] Data);
