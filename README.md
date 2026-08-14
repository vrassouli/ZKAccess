# ZKAccess

Cross-platform .NET client for the ZKTeco standalone device protocol.

The initial implementation focuses on the protocol path verified against a real **ZKTeco uFace202** device over TCP port `4370`:

- TCP framing
- ZKTeco command checksum
- session negotiation via `CMD_CONNECT`
- Comm Key authentication via `CMD_AUTH`
- graceful disconnect via `CMD_EXIT`

No Windows COM SDK or `zkemkeeper.dll` is required.

## Requirements

- .NET 8+
- A reachable ZKTeco standalone device
- Device IP/hostname, port and Comm Key

## Usage

```csharp
using ZKAccess;

await using var device = new ZkDevice(new ZkDeviceOptions
{
    Host = "192.168.1.254",
    Port = 4370,
    CommKey = 1
});

await device.ConnectAsync();

Console.WriteLine($"Connected. Session ID: {device.SessionId}");
```

## Run the sample

```bash
dotnet run --project samples/ZKAccess.Console -- 192.168.1.254 1
```

## Tests

```bash
dotnet test
```

The test suite contains regression vectors captured from a real uFace202 session, including the initial connect packet and Comm Key authentication value.

## Status

Early development. Connection and authentication are implemented first because they have been verified against hardware. Device information, users and attendance log APIs will be added incrementally as their commands are verified.
