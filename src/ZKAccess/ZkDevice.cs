using System.Buffers.Binary;
using System.Text;
using ZKAccess.Models;
using ZKAccess.Protocol;
using ZKAccess.Transport;

namespace ZKAccess;

public sealed class ZkDevice : IAsyncDisposable
{
    private const int MaxTcpBufferChunk = 0xFFC0;

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

    public async Task<string?> GetOptionAsync(
        string optionName,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        if (string.IsNullOrWhiteSpace(optionName))
            throw new ArgumentException("Option name cannot be empty.", nameof(optionName));

        return await ReadOptionAsync(optionName.Trim(), cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetOptionsAsync(
        IEnumerable<string> optionNames,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentNullException.ThrowIfNull(optionNames);

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawName in optionNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var optionName = rawName?.Trim();
            if (string.IsNullOrEmpty(optionName) || result.ContainsKey(optionName))
                continue;

            try
            {
                result[optionName] = await ReadOptionAsync(optionName, cancellationToken);
            }
            catch (ZkProtocolException)
            {
                result[optionName] = null;
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<ZkUser>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var userCount = await ReadUserCountAsync(cancellationToken);
        if (userCount == 0)
            return Array.Empty<ZkUser>();

        var data = await ReadWithBufferAsync(
            ZkCommands.UserTemplateRead,
            ZkCommands.FunctionUser,
            0,
            cancellationToken);

        if (data.Length < 4)
            throw new ZkProtocolException("User dataset is shorter than its 4-byte size header.");

        var totalSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)));
        if (totalSize == 0)
            return Array.Empty<ZkUser>();

        if (totalSize > data.Length - 4)
            throw new ZkProtocolException(
                $"User dataset declares {totalSize} bytes but only {data.Length - 4} bytes were received.");

        if (totalSize % userCount != 0)
            throw new ZkProtocolException(
                $"Cannot determine user record size: {totalSize} bytes for {userCount} users.");

        var recordSize = totalSize / userCount;
        if (recordSize is not (28 or 72))
            throw new ZkProtocolException($"Unsupported ZKTeco user record size: {recordSize} bytes.");

        var users = new List<ZkUser>(userCount);
        var records = data.AsSpan(4, totalSize);

        for (var offset = 0; offset + recordSize <= records.Length; offset += recordSize)
        {
            var record = records.Slice(offset, recordSize);
            users.Add(recordSize == 72 ? ParseUser72(record) : ParseUser28(record));
        }

        return users;
    }

    public async Task<IReadOnlyList<ZkAttendanceLog>> GetAttendanceLogsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var recordCount = await ReadAttendanceCountAsync(cancellationToken);
        if (recordCount == 0)
            return Array.Empty<ZkAttendanceLog>();

        var users = await GetUsersAsync(cancellationToken);
        var usersByUid = users.ToDictionary(x => x.Uid);
        var usersByUserId = users
            .Where(x => !string.IsNullOrWhiteSpace(x.UserId))
            .GroupBy(x => x.UserId)
            .ToDictionary(x => x.Key, x => x.First());

        var data = await ReadWithBufferAsync(
            ZkCommands.AttendanceRead,
            0,
            0,
            cancellationToken);

        if (data.Length < 4)
            throw new ZkProtocolException("Attendance dataset is shorter than its 4-byte size header.");

        var totalSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)));
        if (totalSize == 0)
            return Array.Empty<ZkAttendanceLog>();

        if (totalSize > data.Length - 4)
            throw new ZkProtocolException(
                $"Attendance dataset declares {totalSize} bytes but only {data.Length - 4} bytes were received.");

        if (totalSize % recordCount != 0)
            throw new ZkProtocolException(
                $"Cannot determine attendance record size: {totalSize} bytes for {recordCount} records.");

        var recordSize = totalSize / recordCount;
        if (recordSize is not (8 or 16 or 40))
            throw new ZkProtocolException($"Unsupported ZKTeco attendance record size: {recordSize} bytes.");

        var logs = new List<ZkAttendanceLog>(recordCount);
        var records = data.AsSpan(4, totalSize);

        for (var offset = 0; offset + recordSize <= records.Length; offset += recordSize)
        {
            var record = records.Slice(offset, recordSize);
            logs.Add(recordSize switch
            {
                8 => ParseAttendance8(record, usersByUid),
                16 => ParseAttendance16(record, usersByUserId),
                40 => ParseAttendance40(record),
                _ => throw new UnreachableException()
            });
        }

        return logs;
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

    private async Task<int> ReadUserCountAsync(CancellationToken cancellationToken)
    {
        var sizes = await ReadStorageSizesAsync(cancellationToken);
        return sizes.Users;
    }

    private async Task<int> ReadAttendanceCountAsync(CancellationToken cancellationToken)
    {
        var sizes = await ReadStorageSizesAsync(cancellationToken);
        return sizes.AttendanceRecords;
    }

    private async Task<(int Users, int AttendanceRecords)> ReadStorageSizesAsync(
        CancellationToken cancellationToken)
    {
        var response = await SendCommandAsync(
            ZkCommands.GetFreeSizes,
            ReadOnlyMemory<byte>.Empty,
            cancellationToken);

        EnsureDataResponse(response, "device storage sizes");
        if (response.Data.Length < 36)
            throw new ZkProtocolException("Device storage-size response is too short.");

        var users = BinaryPrimitives.ReadInt32LittleEndian(response.Data.AsSpan(16, 4));
        var records = BinaryPrimitives.ReadInt32LittleEndian(response.Data.AsSpan(32, 4));

        if (users < 0 || records < 0)
            throw new ZkProtocolException(
                $"Device returned invalid storage counts: users={users}, attendance={records}.");

        return (users, records);
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

        var response = await SendCommandAsync(ZkCommands.PrepareBuffer, prepare, cancellationToken);

        if (response.Command == ZkCommands.Data)
            return response.Data;

        if (response.Command != ZkCommands.AckOk)
            throw new ZkProtocolException(
                $"Device does not support buffered reads for command {command}. Response: {response.Command} (0x{response.Command:X4}).");

        if (response.Data.Length < 5)
            throw new ZkProtocolException("Buffered-read preparation response is missing the dataset size.");

        var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(response.Data.AsSpan(1, 4)));

        using var output = new MemoryStream(size);
        var start = 0;

        while (start < size)
        {
            var chunkSize = Math.Min(MaxTcpBufferChunk, size - start);
            var request = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(0, 4), start);
            BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(4, 4), chunkSize);

            var chunk = await SendCommandAsync(ZkCommands.ReadBuffer, request, cancellationToken);
            if (chunk.Command != ZkCommands.Data)
                throw new ZkProtocolException(
                    $"Buffered read failed at offset {start}. Response: {chunk.Command} (0x{chunk.Command:X4}).");

            if (chunk.Data.Length < chunkSize)
                throw new ZkProtocolException(
                    $"Buffered read at offset {start} returned {chunk.Data.Length} bytes; expected {chunkSize}.");

            output.Write(chunk.Data, 0, chunkSize);
            start += chunkSize;
        }

        try
        {
            await SendCommandAsync(
                ZkCommands.FreeData,
                ReadOnlyMemory<byte>.Empty,
                cancellationToken);
        }
        catch
        {
        }

        return output.ToArray();
    }

    private static ZkUser ParseUser72(ReadOnlySpan<byte> record)
    {
        var uid = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0, 2));
        var privilege = record[2];
        var password = DecodeFixed(record.Slice(3, 8));
        var name = DecodeFixed(record.Slice(11, 24));
        var card = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(35, 4));
        var groupId = DecodeFixed(record.Slice(40, 7));
        var userId = DecodeFixed(record.Slice(48, 24));

        if (string.IsNullOrWhiteSpace(name))
            name = $"NN-{userId}";

        return new ZkUser(uid, userId, name, privilege, password, groupId, card);
    }

    private static ZkUser ParseUser28(ReadOnlySpan<byte> record)
    {
        var uid = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0, 2));
        var privilege = record[2];
        var password = DecodeFixed(record.Slice(3, 5));
        var name = DecodeFixed(record.Slice(8, 8));
        var card = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(16, 4));
        var groupId = record[21].ToString();
        var userId = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(24, 4)).ToString();

        if (string.IsNullOrWhiteSpace(name))
            name = $"NN-{userId}";

        return new ZkUser(uid, userId, name, privilege, password, groupId, card);
    }

    private static ZkAttendanceLog ParseAttendance8(
        ReadOnlySpan<byte> record,
        IReadOnlyDictionary<ushort, ZkUser> usersByUid)
    {
        var uid = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0, 2));
        var status = record[2];
        var timestamp = DecodeDeviceTime(record.Slice(3, 4));
        var punch = record[7];
        var userId = usersByUid.TryGetValue(uid, out var user) ? user.UserId : uid.ToString();

        return new ZkAttendanceLog(uid, userId, timestamp, status, punch);
    }

    private static ZkAttendanceLog ParseAttendance16(
        ReadOnlySpan<byte> record,
        IReadOnlyDictionary<string, ZkUser> usersByUserId)
    {
        var numericUserId = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(0, 4));
        var userId = numericUserId.ToString();
        var timestamp = DecodeDeviceTime(record.Slice(4, 4));
        var status = record[8];
        var punch = record[9];
        var workCode = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(12, 4));
        var uid = usersByUserId.TryGetValue(userId, out var user)
            ? user.Uid
            : checked((ushort)Math.Min(numericUserId, ushort.MaxValue));

        return new ZkAttendanceLog(uid, userId, timestamp, status, punch, workCode);
    }

    private static ZkAttendanceLog ParseAttendance40(ReadOnlySpan<byte> record)
    {
        var uid = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0, 2));
        var userId = DecodeFixed(record.Slice(2, 24));
        var status = record[26];
        var timestamp = DecodeDeviceTime(record.Slice(27, 4));
        var punch = record[31];

        return new ZkAttendanceLog(uid, userId, timestamp, status, punch);
    }

    private static DateTime DecodeDeviceTime(ReadOnlySpan<byte> data)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data);

        var second = (int)(value % 60);
        value /= 60;
        var minute = (int)(value % 60);
        value /= 60;
        var hour = (int)(value % 24);
        value /= 24;
        var day = (int)(value % 31) + 1;
        value /= 31;
        var month = (int)(value % 12) + 1;
        value /= 12;
        var year = (int)value + 2000;

        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
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

    private static string DecodeFixed(ReadOnlySpan<byte> data)
    {
        var zero = data.IndexOf((byte)0);
        if (zero >= 0)
            data = data[..zero];
        return Encoding.UTF8.GetString(data).Trim();
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
