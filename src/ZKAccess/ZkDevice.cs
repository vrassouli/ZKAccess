using System.Text;
using ZKAccess.Models;
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

    public async Task<ZkDeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var firmware = await ReadFirmwareVersionAsync(cancellationToken);
        var serial = await ReadOptionAsync("~SerialNumber", cancellationToken);
        var platform = await ReadOptionAsync("~Platform", cancellationToken);
        var deviceName = await ReadOptionAsync("~DeviceName", cancellationToken);

        return new ZkDeviceInfo(
            DeviceName: deviceName,
            SerialNumber: serial,
            Platform: platform,
            FirmwareVersion: firmware);
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

    private async Task<string?> ReadFirmwareVersionAsync(CancellationToken cancellationToken)
    {
        var response = await SendCommandAsync(
            ZkCommands.GetVersion,
            ReadOnlyMemory<byte>.Empty,
            cancellationToken);

        EnsureDataResponse(response, "firmware version");
        return DecodeNullTerminated(response.Data);
    }

    private async Task<string?> ReadOptionAsync(string optionName, CancellationToken cancellationToken)
    {
        var data = Encoding.ASCII.GetBytes(optionName + "\0");
        var response = await SendCommandAsync(ZkCommands.OptionsRead, data, cancellationToken);
        EnsureDataResponse(response, $"option '{optionName}'");

        var text = DecodeNullTerminated(response.Data);
        if (string.IsNullOrEmpty(text))
            return text;

        var separator = text.IndexOf('=');
        return separator >= 0 ? text[(separator + 1)..].TrimStart('=') : text;
    }

    private static string? DecodeNullTerminated(byte[] data)
    {
        if (data.Length == 0)
            return null;

        var zero = Array.IndexOf(data, (byte)0);
        var length = zero >= 0 ? zero : data.Length;
        return Encoding.UTF8.GetString(data, 0, length).Trim();
    }

    private static void EnsureDataResponse(ZkResponse response, string operation)
    {
        if (response.Command is ZkCommands.AckOk or ZkCommands.AckData)
            return;

        throw new ZkProtocolException(
            $"Device failed to read {operation}. Response command: {response.Command} (0x{response.Command:X4}).");
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException("The device is not connected. Call ConnectAsync() first.");
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
