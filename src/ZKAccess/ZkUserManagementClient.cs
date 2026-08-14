using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using ZKAccess.Models;
using ZKAccess.Protocol;

namespace ZKAccess;

/// <summary>
/// Performs user-management writes using a dedicated authenticated ZKTeco TCP session.
/// The write layout is the 72-byte ZK8 user record verified by the main TCP reader.
/// </summary>
public sealed class ZkUserManagementClient : IAsyncDisposable
{
    private readonly ZkDeviceOptions _options;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private ushort _sessionId;
    private ushort _replyId = 0xFFFE;

    public ZkUserManagementClient(ZkDeviceOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public bool IsConnected => _client?.Connected == true && _stream is not null;
    public ushort SessionId => _sessionId;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
            return;

        _client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.ConnectTimeout);
        await _client.ConnectAsync(_options.Host, _options.Port, cts.Token);
        _stream = _client.GetStream();

        _sessionId = 0;
        _replyId = 0xFFFE;

        var response = await ExchangeAsync(ZkCommands.Connect, ReadOnlyMemory<byte>.Empty, cancellationToken);
        if (response.Command == ZkCommands.AckUnauthenticated)
        {
            var auth = ZkAuthentication.MakeCommKey(_options.CommKey, response.SessionId);
            response = await ExchangeAsync(ZkCommands.Auth, auth, cancellationToken);
        }

        if (response.Command != ZkCommands.AckOk)
            throw new ZkProtocolException(
                $"User-management connection was rejected. Response: {response.Command} (0x{response.Command:X4}).");
    }

    public async Task SetUserAsync(ZkUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        await ConnectAsync(cancellationToken);

        if (user.Uid == 0)
            throw new ArgumentOutOfRangeException(nameof(user), "UID 0 is reserved; choose a positive device UID.");
        if (string.IsNullOrWhiteSpace(user.UserId))
            throw new ArgumentException("UserId is required.", nameof(user));

        var data = BuildUser72(user);
        var response = await ExchangeAsync(ZkCommands.UserWrite, data, cancellationToken);
        EnsureAck(response, "write user");
        await RefreshAsync(cancellationToken);
    }

    public async Task DeleteUserAsync(ushort uid, CancellationToken cancellationToken = default)
    {
        await ConnectAsync(cancellationToken);
        if (uid == 0)
            throw new ArgumentOutOfRangeException(nameof(uid), "UID 0 is reserved.");

        var data = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(data, uid);

        var response = await ExchangeAsync(ZkCommands.DeleteUser, data, cancellationToken);
        EnsureAck(response, "delete user");
        await RefreshAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        await ValueTask.CompletedTask;
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var response = await ExchangeAsync(ZkCommands.RefreshData, ReadOnlyMemory<byte>.Empty, cancellationToken);
        EnsureAck(response, "refresh device data");
    }

    private static byte[] BuildUser72(ZkUser user)
    {
        var data = new byte[72];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0, 2), user.Uid);
        data[2] = user.Privilege;

        WriteFixed(data.AsSpan(3, 8), user.Password, "Password");
        WriteFixed(data.AsSpan(11, 24), user.Name, "Name");
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(35, 4), user.CardNumber);
        // byte 39 is reserved/padding.
        WriteFixed(data.AsSpan(40, 7), user.GroupId, "GroupId");
        // byte 47 is reserved/padding.
        WriteFixed(data.AsSpan(48, 24), user.UserId, "UserId");

        return data;
    }

    private static void WriteFixed(Span<byte> destination, string? value, string fieldName)
    {
        value ??= string.Empty;
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > destination.Length)
            throw new ArgumentException(
                $"{fieldName} is too long for this device format: {bytes.Length} bytes, maximum {destination.Length} bytes.");

        destination.Clear();
        bytes.CopyTo(destination);
    }

    private async Task<ZkResponse> ExchangeAsync(
        ushort command,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("User-management client is not connected.");
        var request = ZkPacket.BuildTcpRequest(command, data.Span, _sessionId, _replyId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.RequestTimeout);

        await stream.WriteAsync(request, cts.Token);
        await stream.FlushAsync(cts.Token);

        var header = new byte[8];
        await ReadExactlyAsync(stream, header, cts.Token);
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        if (payloadLength < 8 || payloadLength > 1024 * 1024)
            throw new ZkProtocolException($"Invalid user-management response length: {payloadLength}.");

        var frame = new byte[8 + checked((int)payloadLength)];
        header.CopyTo(frame, 0);
        await ReadExactlyAsync(stream, frame.AsMemory(8), cts.Token);

        var response = ZkPacket.ParseTcpResponse(frame);
        _sessionId = response.SessionId;
        _replyId = response.ReplyId;
        return response;
    }

    private static async Task ReadExactlyAsync(
        NetworkStream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
                throw new IOException("The ZKTeco device closed the user-management connection.");
            offset += read;
        }
    }

    private static void EnsureAck(ZkResponse response, string operation)
    {
        if (response.Command == ZkCommands.AckOk)
            return;

        throw new ZkProtocolException(
            $"Device failed to {operation}. Response: {response.Command} (0x{response.Command:X4}).");
    }
}
