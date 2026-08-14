namespace ZKAccess.Models;

public sealed record ZkDeviceInfo(
    string? DeviceName,
    string? SerialNumber,
    string? Platform,
    string? FirmwareVersion);
