using ZKAccess;

var host = args.Length > 0 ? args[0] : "192.168.1.254";
var commKey = args.Length > 1 && int.TryParse(args[1], out var parsedKey) ? parsedKey : 1;

await using var device = new ZkDevice(new ZkDeviceOptions
{
    Host = host,
    Port = 4370,
    CommKey = commKey
});

Console.WriteLine("ZKAccess Sample Console");
Console.WriteLine("=======================");
Console.WriteLine($"Connecting to {host}:4370...");

try
{
    await device.ConnectAsync();
    Console.WriteLine($"Connected and authenticated. Session ID: {device.SessionId}");
}
catch (Exception ex)
{
    Console.WriteLine($"Connection failed: {ex.Message}");
    return;
}

while (true)
{
    Console.WriteLine();
    Console.WriteLine("Menu");
    Console.WriteLine("----");
    Console.WriteLine("1. Device information");
    Console.WriteLine("2. User list            [bulk transfer - coming next]");
    Console.WriteLine("3. Attendance logs      [bulk transfer - coming next]");
    Console.WriteLine("4. Connection status");
    Console.WriteLine("0. Exit");
    Console.WriteLine();
    Console.Write("Select: ");

    var choice = Console.ReadLine()?.Trim();
    Console.WriteLine();

    try
    {
        switch (choice)
        {
            case "1":
                await ShowDeviceInfoAsync(device);
                break;

            case "2":
                ShowPendingFeature("User list", "GetUsersAsync()");
                break;

            case "3":
                ShowPendingFeature("Attendance logs", "GetAttendanceLogsAsync()");
                break;

            case "4":
                ShowConnectionStatus(device, host);
                break;

            case "0":
            case "q":
            case "quit":
            case "exit":
                Console.WriteLine("Disconnecting...");
                return;

            default:
                Console.WriteLine("Unknown selection. Choose 0-4.");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Operation failed: {ex.Message}");
    }
}

static async Task ShowDeviceInfoAsync(ZkDevice device)
{
    Console.WriteLine("Reading device information...");
    var info = await device.GetDeviceInfoAsync();

    Console.WriteLine();
    Console.WriteLine("Device information");
    Console.WriteLine("------------------");
    Console.WriteLine($"Name     : {Display(info.DeviceName)}");
    Console.WriteLine($"Serial   : {Display(info.SerialNumber)}");
    Console.WriteLine($"Platform : {Display(info.Platform)}");
    Console.WriteLine($"Firmware : {Display(info.FirmwareVersion)}");
}

static void ShowConnectionStatus(ZkDevice device, string host)
{
    Console.WriteLine("Connection status");
    Console.WriteLine("-----------------");
    Console.WriteLine($"Host      : {host}:4370");
    Console.WriteLine($"Connected : {device.IsConnected}");
    Console.WriteLine($"Session ID: {device.SessionId}");
}

static void ShowPendingFeature(string title, string api)
{
    Console.WriteLine(title);
    Console.WriteLine(new string('-', title.Length));
    Console.WriteLine($"{api} is the next protocol feature to implement.");
    Console.WriteLine("It requires ZKTeco bulk/multi-packet transfer support, which is intentionally not stubbed with fake data.");
}

static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "<unknown>" : value;
