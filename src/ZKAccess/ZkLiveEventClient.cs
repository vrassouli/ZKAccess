using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using ZKAccess.Models;
using ZKAccess.Protocol;

namespace ZKAccess;

public sealed class ZkLiveEventClient : IAsyncDisposable
{
    private const ushort CmdCancelCapture = 62;
    private const ushort CmdStartVerify = 60;
    private const ushort CmdRegisterEvent = 500;
    private const ushort EventAttendance = 1;
    private const ushort CmdConnect = 1000;
    private const ushort CmdAuth = 1102;
    private const ushort CmdAckOk = 2000;
    private const ushort CmdAckUnauth = 2005;

    private readonly ZkDeviceOptions _options;
    private readonly IReadOnlyDictionary<string, ZkUser> _usersByUserId;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private ushort _sessionId;
    private ushort _replyId = 0xFFFE;

    public ZkLiveEventClient(ZkDeviceOptions options, IEnumerable<ZkUser>? users = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _usersByUserId = (users ?? Array.Empty<ZkUser>())
            .Where(x => !string.IsNullOrWhiteSpace(x.UserId))
            .GroupBy(x => x.UserId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
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

        var response = await ExchangeAsync(CmdConnect, ReadOnlyMemory<byte>.Empty, cancellationToken);
        _sessionId = response.SessionId;
        _replyId = response.ReplyId;

        if (response.Command == CmdAckUnauth)
        {
            var auth = ZkAuthentication.MakeCommKey(_options.CommKey, _sessionId);
            response = await ExchangeAsync(CmdAuth, auth, cancellationToken);
            _sessionId = response.SessionId;
            _replyId = response.ReplyId;
        }

        if (response.Command != CmdAckOk)
            throw new ZkProtocolException($"Live-event connection was rejected. Response: {response.Command} (0x{response.Command:X4}).");
    }

    public async IAsyncEnumerable<ZkLiveAttendanceEvent> WatchAttendanceAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await ConnectAsync(cancellationToken);

        try
        {
            await TryCommandAsync(CmdCancelCapture, cancellationToken);
            await TryCommandAsync(CmdStartVerify, cancellationToken);

            var flags = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(flags, EventAttendance);
            var register = await ExchangeAsync(CmdRegisterEvent, flags, cancellationToken);
            if (register.Command != CmdAckOk)
                throw new ZkProtocolException($"Device rejected live attendance registration. Response: {register.Command} (0x{register.Command:X4}).");

            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] frame;
                try
                {
                    frame = await ReadFrameAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                var response = ZkPacket.ParseTcpResponse(frame);
                _sessionId = response.SessionId;
                _replyId = response.ReplyId;

                await SendAckAsync(cancellationToken);

                if (response.Command != CmdRegisterEvent)
                    continue;

                foreach (var evt in ParseEvents(response.Data))
                    yield return ResolveUser(evt);
            }
        }
        finally
        {
            if (IsConnected)
            {
                try
                {
                    var flags = new byte[4];
                    await ExchangeAsync(CmdRegisterEvent, flags, CancellationToken.None);
                }
                catch
                {
                    // Best effort only while leaving live mode.
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        return ValueTask.CompletedTask;
    }

    private ZkLiveAttendanceEvent ResolveUser(ZkLiveAttendanceEvent evt)
    {
        if (evt.UserId is null || !_usersByUserId.TryGetValue(evt.UserId, out var user))
            return evt;

        return evt with { UserName = user.Name };
    }

    private async Task TryCommandAsync(ushort command, CancellationToken cancellationToken)
    {
        try
        {
            await ExchangeAsync(command, ReadOnlyMemory<byte>.Empty, cancellationToken);
        }
        catch
        {
            // Some firmware revisions do not require these preparation commands.
        }
    }

    private async Task<ZkResponse> ExchangeAsync(
        ushort command,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("Live event client is not connected.");
        var request = ZkPacket.BuildTcpRequest(command, data.Span, _sessionId, _replyId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.RequestTimeout);
        await stream.WriteAsync(request, cts.Token);
        await stream.FlushAsync(cts.Token);

        var frame = await ReadFrameAsync(cts.Token);
        var response = ZkPacket.ParseTcpResponse(frame);
        _sessionId = response.SessionId;
        _replyId = response.ReplyId;
        return response;
    }

    private async Task SendAckAsync(CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("Live event client is not connected.");
        var ack = ZkPacket.BuildTcpRequest(CmdAckOk, ReadOnlySpan<byte>.Empty, _sessionId, 0xFFFE);
        await stream.WriteAsync(ack, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private async Task<byte[]> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("Live event client is not connected.");
        var header = new byte[8];
        await ReadExactlyAsync(stream, header, cancellationToken);

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        if (payloadLength < 8 || payloadLength > 1024 * 1024)
            throw new ZkProtocolException($"Invalid live-event payload length: {payloadLength}.");

        var frame = new byte[8 + checked((int)payloadLength)];
        header.CopyTo(frame, 0);
        await ReadExactlyAsync(stream, frame.AsMemory(8), cancellationToken);
        return frame;
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
                throw new IOException("The ZKTeco device closed the live-event connection.");
            offset += read;
        }
    }

    private static IEnumerable<ZkLiveAttendanceEvent> ParseEvents(byte[] data)
    {
        var offset = 0;

        while (data.Length - offset >= 10)
        {
            var remaining = data.Length - offset;
            var length = remaining switch
            {
                10 => 10,
                12 => 12,
                14 => 14,
                32 => 32,
                36 => 36,
                37 => 37,
                >= 52 => 52,
                _ => remaining
            };

            var raw = data.AsSpan(offset, length).ToArray();
            if (TryParseEvent(raw, out var parsed))
                yield return parsed;
            else
                yield return UnknownEvent(raw);

            offset += length;
        }

        if (offset < data.Length)
            yield return UnknownEvent(data.AsSpan(offset).ToArray());
    }

    private static ZkLiveAttendanceEvent UnknownEvent(byte[] raw) =>
        new(null, null, null, null, null, ZkVerificationMethod.Unknown, raw, false);

    private static bool TryParseEvent(byte[] raw, out ZkLiveAttendanceEvent result)
    {
        try
        {
            string userId;
            byte status;
            byte punch;
            ReadOnlySpan<byte> time;

            switch (raw.Length)
            {
                case 10:
                    userId = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0, 2)).ToString();
                    status = raw[2];
                    punch = raw[3];
                    time = raw.AsSpan(4, 6);
                    break;

                case 12:
                    userId = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0, 4)).ToString();
                    status = raw[4];
                    punch = raw[5];
                    time = raw.AsSpan(6, 6);
                    break;

                case 14:
                    userId = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0, 2)).ToString();
                    status = raw[2];
                    punch = raw[3];
                    time = raw.AsSpan(4, 6);
                    break;

                case 32:
                case 36:
                case 37:
                case 52:
                    userId = DecodeFixed(raw.AsSpan(0, 24));
                    status = raw[24];
                    punch = raw[25];
                    time = raw.AsSpan(26, 6);
                    break;

                default:
                    result = default!;
                    return false;
            }

            var timestamp = DecodeTimeHex(time);
            result = new ZkLiveAttendanceEvent(
                userId,
                null,
                timestamp,
                status,
                punch,
                MapVerificationMethod(status),
                raw,
                true);
            return true;
        }
        catch
        {
            result = default!;
            return false;
        }
    }

    private static ZkVerificationMethod MapVerificationMethod(byte status)
    {
        // Verification/status codes vary by model and firmware. Keep unknown until a code has
        // been hardware-verified for a specific method instead of guessing from third-party tables.
        return ZkVerificationMethod.Unknown;
    }

    private static DateTime DecodeTimeHex(ReadOnlySpan<byte> time)
    {
        if (time.Length != 6)
            throw new ArgumentException("Live timestamp must be exactly six bytes.", nameof(time));

        return new DateTime(
            2000 + time[0],
            time[1],
            time[2],
            time[3],
            time[4],
            time[5],
            DateTimeKind.Unspecified);
    }

    private static string DecodeFixed(ReadOnlySpan<byte> data)
    {
        var zero = data.IndexOf((byte)0);
        if (zero >= 0)
            data = data[..zero];
        return Encoding.UTF8.GetString(data).Trim();
    }
}
