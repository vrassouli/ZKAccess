using ZKAccess.Protocol;
using ZKAccess.Transport;

namespace ZKAccess;

public sealed class ZkDevice : IAsyncDisposable
{
    private readonly ZkDeviceOptions _options;
    private readonly IZkTransport _transport;
    private ushort _sessionId;
    private ushort _replyId = 0xFFFE;

    public ZkDevice(ZkDeviceOptions options)
        : this(options, new TcpZkTransport())
    {
    }

    internal ZkDevice(ZkDeviceOptions options, IZkTransport transport)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options.Validate();
    }

    public bool IsConnected { get; private set; }
    public ushort SessionId => _sessionId;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
            return;

        _sessionId = 0;
        _replyId = 0xFFFE;

        await _transport.ConnectAsync(
            _options.Host,
            _options.Port,
            _options.ConnectTimeout,
            cancellationToken);

        try
        {
            var response = await SendCommandAsync(
                ZkCommands.Connect,
                ReadOnlyMemory<byte>.Empty,
                cancellationToken);

            _sessionId = response.SessionId;
            _replyId = response.ReplyId;

            if (response.Command == ZkCommands.AckUnauthenticated)
            {
                var authKey = ZkAuthentication.MakeCommKey(_options.CommKey, _sessionId);
                response = await SendCommandAsync(ZkCommands.Auth, authKey, cancellationToken);
                _sessionId = response.SessionId;
                _replyId = response.ReplyId;
            }

            if (response.Command != ZkCommands.AckOk)
                throw new ZkProtocolException(
                    $"Device rejected connection. Response command: {response.Command} (0x{response.Command:X4}).");

            IsConnected = true;
        }
        catch
        {
            await _transport.DisposeAsync();
            IsConnected = false;
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!_transport.IsConnected)
        {
            IsConnected = false;
            return;
        }

        if (IsConnected)
        {
            try
            {
                await SendCommandAsync(
                    ZkCommands.Exit,
                    ReadOnlyMemory<byte>.Empty,
                    cancellationToken);
            }
            catch
            {
                // The TCP connection is disposed below even if the device does not ACK CMD_EXIT.
            }
        }

        await _transport.DisposeAsync();
        IsConnected = false;
        _sessionId = 0;
        _replyId = 0xFFFE;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }

    private async Task<ZkResponse> SendCommandAsync(
        ushort command,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        var request = ZkPacket.BuildTcpRequest(command, data.Span, _sessionId, _replyId);
        var rawResponse = await _transport.ExchangeAsync(
            request,
            _options.RequestTimeout,
            cancellationToken);

        var response = ZkPacket.ParseTcpResponse(rawResponse);
        _sessionId = response.SessionId;
        _replyId = response.ReplyId;
        return response;
    }
}
