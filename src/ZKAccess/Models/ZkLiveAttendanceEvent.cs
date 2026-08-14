namespace ZKAccess.Models;

public sealed record ZkLiveAttendanceEvent(
    string? UserId,
    DateTime? Timestamp,
    byte? Status,
    byte? Punch,
    byte[] RawData,
    bool Parsed);
