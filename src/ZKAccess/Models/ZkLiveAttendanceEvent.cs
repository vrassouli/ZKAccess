namespace ZKAccess.Models;

public sealed record ZkLiveAttendanceEvent(
    string? UserId,
    string? UserName,
    DateTime? Timestamp,
    byte? Status,
    byte? Punch,
    ZkVerificationMethod VerificationMethod,
    byte[] RawData,
    bool Parsed);
