using System.Globalization;
using ZKAccess;
using ZKAccess.Models;

var host = args.Length > 0 ? args[0] : "192.168.1.254";
var commKey = args.Length > 1 && int.TryParse(args[1], out var parsedKey) ? parsedKey : 1;

var options = new ZkDeviceOptions
{
    Host = host,
    Port = 4370,
    CommKey = commKey
};

await using var device = new ZkDevice(options);

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
    Console.WriteLine("2. User list");
    Console.WriteLine("3. Attendance logs");
    Console.WriteLine("4. Connection status");
    Console.WriteLine("5. Probe ADMS / Push options");
    Console.WriteLine("6. Watch live attendance events [experimental]");
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
                await ShowUsersAsync(device);
                break;

            case "3":
                await ShowAttendanceLogsAsync(device);
                break;

            case "4":
                ShowConnectionStatus(device, host);
                break;

            case "5":
                await ProbePushOptionsAsync(device);
                break;

            case "6":
                await WatchLiveAttendanceAsync(options);
                break;

            case "0":
            case "q":
            case "quit":
            case "exit":
                Console.WriteLine("Disconnecting...");
                return;

            default:
                Console.WriteLine("Unknown selection. Choose 0-6.");
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

static async Task ShowUsersAsync(ZkDevice device)
{
    Console.WriteLine("Reading users...");
    var users = await device.GetUsersAsync();

    Console.WriteLine();
    Console.WriteLine($"Users ({users.Count})");
    Console.WriteLine(new string('-', 92));
    Console.WriteLine($"{"UID",-6} {"User ID",-16} {"Name",-28} {"Privilege",-10} {"Group",-8} {"Card",-12}");
    Console.WriteLine(new string('-', 92));

    if (users.Count == 0)
    {
        Console.WriteLine("No users found.");
        return;
    }

    foreach (var user in users)
        PrintUser(user);
}

static async Task ShowAttendanceLogsAsync(ZkDevice device)
{
    Console.WriteLine("Reading attendance logs...");
    var logs = await device.GetAttendanceLogsAsync();

    Console.WriteLine();
    Console.WriteLine($"Attendance logs ({logs.Count})");
    Console.WriteLine(new string('-', 96));
    Console.WriteLine($"{"UID",-6} {"User ID",-16} {"Timestamp",-20} {"Status",-8} {"Punch",-7} {"WorkCode",-10}");
    Console.WriteLine(new string('-', 96));

    if (logs.Count == 0)
    {
        Console.WriteLine("No attendance logs found.");
        return;
    }

    foreach (var log in logs.OrderBy(x => x.Timestamp))
    {
        var timestamp = log.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Console.WriteLine(
            $"{log.Uid,-6} {TrimForTable(log.UserId, 16),-16} {timestamp,-20} " +
            $"{log.Status,-8} {log.Punch,-7} {(log.WorkCode?.ToString() ?? string.Empty),-10}");
    }
}

static async Task WatchLiveAttendanceAsync(ZkDeviceOptions options)
{
    Console.WriteLine("Live attendance events (experimental)");
    Console.WriteLine("-------------------------------------");
    Console.WriteLine("Opening a dedicated live-event session...");

    await using var live = new ZkLiveEventClient(options);
    await live.ConnectAsync();

    Console.WriteLine($"Live session connected. Session ID: {live.SessionId}");
    Console.WriteLine("Touch the terminal / verify a user now.");
    Console.WriteLine("Press Enter to stop watching.");
    Console.WriteLine();

    using var cts = new CancellationTokenSource();

    var watchTask = Task.Run(async () =>
    {
        await foreach (var evt in live.WatchAttendanceAsync(cts.Token))
        {
            if (!evt.Parsed)
            {
                Console.WriteLine($"[RAW LIVE EVENT] {Convert.ToHexString(evt.RawData)}");
                continue;
            }

            var timestamp = evt.Timestamp?.ToString(
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture) ?? "<unknown>";

            Console.WriteLine(
                $"[LIVE] User={evt.UserId ?? "<unknown>"}  Time={timestamp}  " +
                $"Status={evt.Status?.ToString() ?? "?"}  Punch={evt.Punch?.ToString() ?? "?"}");
        }
    });

    Console.ReadLine();
    cts.Cancel();

    try
    {
        await watchTask;
    }
    catch (OperationCanceledException)
    {
        // Normal end of the live watcher.
    }

    Console.WriteLine("Live watcher stopped.");
}

static async Task ProbePushOptionsAsync(ZkDevice device)
{
    var optionNames = new[]
    {
        "~DeviceName",
        "~SerialNumber",
        "~Platform",
        "~ProductTime",
        "~ZKFPVersion",
        "~FaceFunOn",
        "~PushVersion",
        "PushVersion",
        "ADMS",
        "EnableADMS",
        "CloudServer",
        "CloudServerIP",
        "CloudServerPort",
        "ServerIP",
        "ServerPort",
        "WebServer",
        "WebServerIP",
        "WebServerPort",
        "PushServer",
        "PushServerIP",
        "PushServerPort",
        "ServerAddr",
        "ServerAddress",
        "ServerURL",
        "ServerUrl",
        "EnableProxyServer",
        "ProxyServer",
        "ProxyServerIP",
        "ProxyServerPort"
    };

    Console.WriteLine("Probing read-only device options related to ADMS / Push...");
    var options = await device.GetOptionsAsync(optionNames);

    Console.WriteLine();
    Console.WriteLine("ADMS / Push capability probe");
    Console.WriteLine("----------------------------");

    var found = 0;
    foreach (var (name, value) in options)
    {
        if (value is null)
        {
            Console.WriteLine($"{name,-22} : <not available>");
            continue;
        }

        found++;
        Console.WriteLine($"{name,-22} : {(string.IsNullOrWhiteSpace(value) ? "<empty>" : value)}");
    }

    Console.WriteLine();
    Console.WriteLine($"Readable options: {found}/{options.Count}");
    Console.WriteLine("This probe only reads configuration; it does not change anything on the device.");
}

static void PrintUser(ZkUser user)
{
    Console.WriteLine(
        $"{user.Uid,-6} {TrimForTable(user.UserId, 16),-16} {TrimForTable(user.Name, 28),-28} " +
        $"{user.Privilege,-10} {TrimForTable(user.GroupId, 8),-8} {user.CardNumber,-12}");
}

static void ShowConnectionStatus(ZkDevice device, string host)
{
    Console.WriteLine("Connection status");
    Console.WriteLine("-----------------");
    Console.WriteLine($"Host      : {host}:4370");
    Console.WriteLine($"Connected : {device.IsConnected}");
    Console.WriteLine($"Session ID: {device.SessionId}");
}

static string TrimForTable(string? value, int width)
{
    value ??= string.Empty;
    return value.Length <= width ? value : value[..Math.Max(0, width - 1)] + "…";
}

static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "<unknown>" : value;
