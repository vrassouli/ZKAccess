using ZKAccess;

var host = args.Length > 0 ? args[0] : "192.168.1.254";
var commKey = args.Length > 1 && int.TryParse(args[1], out var parsedKey) ? parsedKey : 1;

await using var device = new ZkDevice(new ZkDeviceOptions
{
    Host = host,
    Port = 4370,
    CommKey = commKey
});

Console.WriteLine($"Connecting to {host}:4370...");
await device.ConnectAsync();
Console.WriteLine($"Connected and authenticated. Session ID: {device.SessionId}");
