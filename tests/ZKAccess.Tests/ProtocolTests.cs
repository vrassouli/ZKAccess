using ZKAccess.Protocol;

namespace ZKAccess.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void ConnectPacket_MatchesVerifiedUFace202Vector()
    {
        var packet = ZkPacket.BuildTcpRequest(
            ZkCommands.Connect,
            ReadOnlySpan<byte>.Empty,
            sessionId: 0,
            replyId: 0xFFFE);

        Assert.Equal(
            "5050827D08000000E80317FC00000000",
            Convert.ToHexString(packet));
    }

    [Fact]
    public void CommKey_MatchesVerifiedUFace202Vector()
    {
        var key = ZkAuthentication.MakeCommKey(
            commKey: 1,
            sessionId: 3398);

        Assert.Equal("61FD3274", Convert.ToHexString(key));
    }
}
