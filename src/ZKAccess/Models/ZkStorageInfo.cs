namespace ZKAccess.Models;

public sealed record ZkStorageInfo(
    int Users,
    int UserCapacity,
    int AttendanceRecords,
    int AttendanceCapacity,
    int Fingerprints,
    int FingerprintCapacity,
    int Cards,
    int Faces,
    int FaceCapacity,
    int AvailableUsers,
    int AvailableAttendanceRecords,
    int AvailableFingerprints);
