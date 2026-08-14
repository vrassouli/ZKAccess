using System.Buffers.Binary;
using System.Net.Sockets;
using ZKAccess.Models;
using ZKAccess.Protocol;

namespace ZKAccess;

public sealed class ZkFingerprintClient : IAsyncDisposable
{
    private const int MaxTcpBufferChunk = 0xFFC0;

    private readonly ZkDeviceOptions _options;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private ushort _sessionId;
    private ushort _replyId = 0xFFFE;

    public ZkFingerprintClient(ZkDeviceOptions options)
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
                $"Fingerprint connection was rejected. Response: {response.Command} (0x{response.Command:X4}).");
    }

    public async Task<IReadOnlyList<ZkFingerprintTemplate>> GetTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        await ConnectAsync(cancellationToken);

        var data = await ReadWithBufferAsync(
            ZkCommands.DbRead,
            ZkCommands.FunctionFingerprintTemplate,
            0,
            cancellationToken);

        if (data.Length < 4)
            return Array.Empty<ZkFingerprintTemplate>();

        var totalSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4));
        if (totalSize <= 0)
            return Array.Empty<ZkFingerprintTemplate>();

        if (totalSize > data.Length - 4)
            throw new ZkProtocolException(
                $"Fingerprint dataset declares {totalSize} bytes but only {data.Length - 4} bytes were received.");

        var templates = new List<ZkFingerprintTemplate>();
        var offset = 4;
        var end = 4 + totalSize;

        while (offset < end)
        {
            if (end - offset < 6)
                throw new ZkProtocolException("Fingerprint dataset ended inside a template header.");

            var recordSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
            var uid = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2, 2));
            var fingerIndex = data[offset + 4];
            var valid = data[offset + 5];

            if (recordSize < 6)
                throw new ZkProtocolException($"Invalid fingerprint record size: {recordSize}.");
            if (offset + recordSize > end)
                throw new ZkProtocolException("Fingerprint record extends past the declared dataset size.");

            var template = data.AsSpan(offset + 6, recordSize - 6).ToArray();
            templates.Add(new ZkFingerprintTemplate(uid, fingerIndex, valid, template));
            offset += recordSize;
        }

        return templates;
    }

    public async Task<IReadOnlyList<ZkFingerprintTemplate>> GetTemplatesAsync(
        ushort uid,
        CancellationToken cancellationToken = default)
    {
        var all = await GetTemplatesAsync(cancellationToken);
        return all.Where(x => x.Uid == uid).ToArray();
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        return ValueTask.CompletedTask;
    }

    private async Task<byte[]> ReadWithBufferAsync(
        ushort command,
        int function,
        int extension,
        CancellationToken cancellationToken)
    {
        var prepare = new byte[11];
        prepare[0] = 1;
        BinaryPrimitives.WriteInt16LittleEndian(prepare.AsSpan(1, 2), unchecked((short)command));
        BinaryPrimitives.WriteInt32LittleEndian(prepare.AsSpan(3, 4), function);
        BinaryPrimitives.WriteInt32LittleEndian(prepare.AsSpan(7, 4), extension);

        var response = await ExchangeAsync(ZkCommands.PrepareBuffer, prepare, cancellationToken);

        if (response.Command == ZkCommands.Data)
            return response.Data;

        if (response.Command != ZkCommands.AckOk)
            throw new ZkProtocolException(
                $"Device does not support buffered fingerprint reads. Response: {response.Command} (0x{response.Command:X4}).");

        if (response.Data.Length < 5)
            throw new ZkProtocolException("Fingerprint buffered-read response is missing dataset size.");

        var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(response.Data.AsSpan(1, 4)));
        using var output = new MemoryStream(size);

        var start = 0;
        while (start < size)
        {
            var chunkSize = Math.Min(MaxTcpBufferChunk, size - start);
            var request = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(0, 4), start);
            BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(4, 4), chunkSize);

            var chunk = await ExchangeAsync(ZkCommands.ReadBuffer, request, cancellationToken);
            if (chunk.Command != ZkCommands.Data)
                throw new ZkProtocolException(
                    $"Fingerprint buffered read failed at offset {start}. Response: {chunk.Command} (0x{chunk.Command:X4}).");
            if (chunk.Data.Length < chunkSize)
                throw new ZkProtocolException(
                    $"Fingerprint buffered read returned {chunk.Data.Length} bytes; expected {chunkSize}.");

            output.Write(chunk.Data, 0, chunkSize);
            start += chunkSize;
        }

        try
        {
            await ExchangeAsync(ZkCommands.FreeData, ReadOnlyMemory<byte>.Empty, cancellationToken);
        }
        catch
        {
        }

        return output.ToArray();
    }

    private async Task<ZkResponse> ExchangeAsync(
        ushort command,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("Fingerprint client is not connected.");
        var request = ZkPacket.BuildTcpRequest(command, data.Span, _sessionId, _replyId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.RequestTimeout);
        await stream.WriteAsync(request, cts.Token);
        await stream.FlushAsync(cts.Token);

        var header = new byte[8];
        await ReadExactlyAsync(stream, header, cts.Token);
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        if (payloadLength < 8 || payloadLength > 16 * 1024 * 1024)
            throw new ZkProtocolException($"Invalid fingerprint response length: {payloadLength}.");

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
                throw new IOException("The ZKTeco device closed the fingerprint connection.");
            offset += read;
        }
    }
}
