using System.Buffers.Binary;
using System.Net.Sockets;

namespace ZKAccess.Transport;

internal sealed class TcpZkTransport : IZkTransport
{
    private TcpClient? _client;
    private NetworkStream? _stream;

    public bool IsConnected => _client?.Connected == true && _stream is not null;

    public async Task ConnectAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await DisposeAsync();

        _client = new TcpClient();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await _client.ConnectAsync(host, port, timeoutCts.Token);
            _stream = _client.GetStream();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task<byte[]> ExchangeAsync(
        byte[] request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("Transport is not connected.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;

        await stream.WriteAsync(request, token);
        await stream.FlushAsync(token);

        var header = new byte[8];
        await ReadExactlyAsync(stream, header, token);

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        if (payloadLength < 8 || payloadLength > 16 * 1024 * 1024)
            throw new ZkProtocolException($"Invalid response payload length: {payloadLength}.");

        var response = new byte[8 + (int)payloadLength];
        header.CopyTo(response, 0);
        await ReadExactlyAsync(stream, response.AsMemory(8), token);

        return response;
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        return ValueTask.CompletedTask;
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
                throw new IOException("The ZKTeco device closed the connection unexpectedly.");

            offset += read;
        }
    }
}
