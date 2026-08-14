namespace ZKAccess.Models;

public sealed record ZkAttendanceLog(
    ushort Uid,
    string UserId,
    DateTime Timestamp,
    byte Status,
    byte Punch,
    uint? WorkCode = null);
