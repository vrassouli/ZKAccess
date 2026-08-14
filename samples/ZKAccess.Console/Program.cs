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
    Console.WriteLine("7. Storage / capacity");
    Console.WriteLine("8. Device time");
    Console.WriteLine("9. Set device time to this computer's local time");
    Console.WriteLine("10. Find user by User ID");
    Console.WriteLine("11. Add / update user [write]");
    Console.WriteLine("12. Delete user [write]");
    Console.WriteLine("13. Fingerprint templates [read-only]");
    Console.WriteLine("0. Exit");
    Console.WriteLine();
    Console.Write("Select: ");

    var choice = Console.ReadLine()?.Trim();
    Console.WriteLine();

    try
    {
        switch (choice)
        {
            case "1": await ShowDeviceInfoAsync(device); break;
            case "2": await ShowUsersAsync(device); break;
            case "3": await ShowAttendanceLogsAsync(device); break;
            case "4": ShowConnectionStatus(device, host); break;
            case "5": await ProbePushOptionsAsync(device); break;
            case "6": await WatchLiveAttendanceAsync(device, options); break;
            case "7": await ShowStorageInfoAsync(device); break;
            case "8": await ShowDeviceTimeAsync(device); break;
            case "9": await SetDeviceTimeAsync(device); break;
            case "10": await FindUserAsync(device); break;
            case "11": await AddOrUpdateUserAsync(options); break;
            case "12": await DeleteUserAsync(device, options); break;
            case "13": await ShowFingerprintTemplatesAsync(device, options); break;

            case "0":
            case "q":
            case "quit":
            case "exit":
                Console.WriteLine("Disconnecting...");
                return;

            default:
                Console.WriteLine("Unknown selection. Choose 0-13.");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Operation failed: {ex.Message}");
    }
}

static async Task ShowFingerprintTemplatesAsync(ZkDevice device, ZkDeviceOptions options)
{
    Console.WriteLine("Reading fingerprint templates...");
    var users = await device.GetUsersAsync();
    var usersByUid = users.ToDictionary(x => x.Uid);

    await using var fingerprints = new ZkFingerprintClient(options);
    var templates = await fingerprints.GetTemplatesAsync();

    Console.WriteLine();
    Console.WriteLine($"Fingerprint templates ({templates.Count})");
    Console.WriteLine(new string('-', 84));
    Console.WriteLine($"{"UID",-6} {"User ID",-14} {"Name",-24} {"Finger",-8} {"Valid",-7} {"Bytes",-8}");
    Console.WriteLine(new string('-', 84));

    if (templates.Count == 0)
    {
        Console.WriteLine("No fingerprint templates found.");
        return;
    }

    foreach (var template in templates.OrderBy(x => x.Uid).ThenBy(x => x.FingerIndex))
    {
        usersByUid.TryGetValue(template.Uid, out var user);
        Console.WriteLine(
            $"{template.Uid,-6} {TrimForTable(user?.UserId, 14),-14} {TrimForTable(user?.Name, 24),-24} " +
            $"{template.FingerIndex,-8} {template.Valid,-7} {template.Size,-8}");
    }
}

static async Task FindUserAsync(ZkDevice device)
{
    Console.Write("User ID: ");
    var userId = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(userId))
        return;

    var users = await device.GetUsersAsync();
    var user = users.FirstOrDefault(x => string.Equals(x.UserId, userId, StringComparison.Ordinal));

    if (user is null)
    {
        Console.WriteLine("User not found.");
        return;
    }

    Console.WriteLine();
    Console.WriteLine("User");
    Console.WriteLine("----");
    Console.WriteLine($"UID       : {user.Uid}");
    Console.WriteLine($"User ID   : {user.UserId}");
    Console.WriteLine($"Name      : {user.Name}");
    Console.WriteLine($"Privilege : {user.Privilege}");
    Console.WriteLine($"Group     : {user.GroupId}");
    Console.WriteLine($"Card      : {user.CardNumber}");
}

static async Task AddOrUpdateUserAsync(ZkDeviceOptions options)
{
    Console.WriteLine("Add / update user");
    Console.WriteLine("-----------------");
    Console.WriteLine("This writes a 72-byte ZK8 user record to the device.");

    Console.Write("UID (internal numeric device UID): ");
    if (!ushort.TryParse(Console.ReadLine(), out var uid) || uid == 0)
    {
        Console.WriteLine("Invalid UID.");
        return;
    }

    Console.Write("User ID: ");
    var userId = Console.ReadLine()?.Trim() ?? string.Empty;
    Console.Write("Name: ");
    var name = Console.ReadLine()?.Trim() ?? string.Empty;
    Console.Write("Privilege [0]: ");
    var privilegeText = Console.ReadLine()?.Trim();
    var privilege = byte.TryParse(privilegeText, out var p) ? p : (byte)0;
    Console.Write("Password [empty]: ");
    var password = Console.ReadLine() ?? string.Empty;
    Console.Write("Group ID [empty]: ");
    var group = Console.ReadLine()?.Trim() ?? string.Empty;
    Console.Write("Card number [0]: ");
    var cardText = Console.ReadLine()?.Trim();
    var card = uint.TryParse(cardText, out var c) ? c : 0u;

    var user = new ZkUser(uid, userId, name, privilege, password, group, card);

    Console.Write($"Write UID {uid}, User ID '{userId}', Name '{name}'? [y/N]: ");
    var confirm = Console.ReadLine()?.Trim();
    if (!string.Equals(confirm, "y", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(confirm, "yes", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Cancelled.");
        return;
    }

    await using var admin = new ZkUserManagementClient(options);
    await admin.SetUserAsync(user);
    Console.WriteLine("User write acknowledged by device.");
}

static async Task DeleteUserAsync(ZkDevice device, ZkDeviceOptions options)
{
    Console.Write("User ID to delete: ");
    var userId = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(userId))
        return;

    var users = await device.GetUsersAsync();
    var user = users.FirstOrDefault(x => string.Equals(x.UserId, userId, StringComparison.Ordinal));
    if (user is null)
    {
        Console.WriteLine("User not found; nothing deleted.");
        return;
    }

    Console.WriteLine($"Found UID={user.Uid}, Name='{user.Name}'.");
    Console.Write("Type DELETE to confirm: ");
    if (!string.Equals(Console.ReadLine()?.Trim(), "DELETE", StringComparison.Ordinal))
    {
        Console.WriteLine("Cancelled.");
        return;
    }

    await using var admin = new ZkUserManagementClient(options);
    await admin.DeleteUserAsync(user.Uid);
    Console.WriteLine("Delete acknowledged by device.");
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

static async Task ShowStorageInfoAsync(ZkDevice device)
{
    Console.WriteLine("Reading storage / capacity...");
    var storage = await device.GetStorageInfoAsync();
    Console.WriteLine();
    Console.WriteLine("Storage / capacity");
    Console.WriteLine("------------------");
    Console.WriteLine($"Users                 : {storage.Users} / {storage.UserCapacity}  (available {storage.AvailableUsers})");
    Console.WriteLine($"Attendance records    : {storage.AttendanceRecords} / {storage.AttendanceCapacity}  (available {storage.AvailableAttendanceRecords})");
    Console.WriteLine($"Fingerprints          : {storage.Fingerprints} / {storage.FingerprintCapacity}  (available {storage.AvailableFingerprints})");
    Console.WriteLine($"Cards                 : {storage.Cards}");
    Console.WriteLine($"Faces                 : {storage.Faces} / {storage.FaceCapacity}");
}

static async Task ShowDeviceTimeAsync(ZkDevice device)
{
    var deviceTime = await device.GetTimeAsync();
    var localTime = DateTime.Now;
    var difference = deviceTime - localTime;
    Console.WriteLine("Device time");
    Console.WriteLine("-----------");
    Console.WriteLine($"Device : {deviceTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"Local  : {localTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"Delta  : {difference.TotalSeconds:+0;-0;0} seconds");
}

static async Task SetDeviceTimeAsync(ZkDevice device)
{
    var timestamp = DateTime.Now;
    Console.WriteLine($"Setting device time to {timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}...");
    await device.SetTimeAsync(timestamp);
    var actual = await device.GetTimeAsync();
    Console.WriteLine($"Device now reports    {actual.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
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
        Console.WriteLine($"{log.Uid,-6} {TrimForTable(log.UserId, 16),-16} {timestamp,-20} {log.Status,-8} {log.Punch,-7} {(log.WorkCode?.ToString() ?? string.Empty),-10}");
    }
}

static async Task WatchLiveAttendanceAsync(ZkDevice device, ZkDeviceOptions options)
{
    Console.WriteLine("Live attendance events (experimental)");
    Console.WriteLine("-------------------------------------");
    Console.WriteLine("Loading users for event identity resolution...");
    var users = await device.GetUsersAsync();
    Console.WriteLine($"Loaded {users.Count} user(s).");
    Console.WriteLine("Opening a dedicated live-event session...");

    await using var live = new ZkLiveEventClient(options, users);
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

            var timestamp = evt.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "<unknown>";
            var identity = string.IsNullOrWhiteSpace(evt.UserName)
                ? evt.UserId ?? "<unknown>"
                : $"{evt.UserName} ({evt.UserId})";

            Console.WriteLine(
                $"[LIVE] User={identity}  Time={timestamp}  Method={evt.VerificationMethod}  " +
                $"Status={evt.Status?.ToString() ?? "?"}  Punch={evt.Punch?.ToString() ?? "?"}");
        }
    });

    Console.ReadLine();
    cts.Cancel();
    try { await watchTask; } catch (OperationCanceledException) { }
    Console.WriteLine("Live watcher stopped.");
}

static async Task ProbePushOptionsAsync(ZkDevice device)
{
    var optionNames = new[]
    {
        "~DeviceName", "~SerialNumber", "~Platform", "~ProductTime", "~ZKFPVersion", "~FaceFunOn",
        "~PushVersion", "PushVersion", "ADMS", "EnableADMS", "CloudServer", "CloudServerIP",
        "CloudServerPort", "ServerIP", "ServerPort", "WebServer", "WebServerIP", "WebServerPort",
        "PushServer", "PushServerIP", "PushServerPort", "ServerAddr", "ServerAddress", "ServerURL",
        "ServerUrl", "EnableProxyServer", "ProxyServer", "ProxyServerIP", "ProxyServerPort"
    };

    Console.WriteLine("Probing read-only device options related to ADMS / Push...");
    var foundOptions = await device.GetOptionsAsync(optionNames);
    Console.WriteLine();
    Console.WriteLine("ADMS / Push capability probe");
    Console.WriteLine("----------------------------");

    var found = 0;
    foreach (var (name, value) in foundOptions)
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
    Console.WriteLine($"Readable options: {found}/{foundOptions.Count}");
    Console.WriteLine("This probe only reads configuration; it does not change anything on the device.");
}

static void PrintUser(ZkUser user)
{
    Console.WriteLine($"{user.Uid,-6} {TrimForTable(user.UserId, 16),-16} {TrimForTable(user.Name, 28),-28} {user.Privilege,-10} {TrimForTable(user.GroupId, 8),-8} {user.CardNumber,-12}");
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
