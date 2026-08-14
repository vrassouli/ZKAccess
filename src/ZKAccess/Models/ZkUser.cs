namespace ZKAccess.Models;

public sealed record ZkUser(
    ushort Uid,
    string UserId,
    string Name,
    byte Privilege,
    string Password,
    string GroupId,
    uint CardNumber);
