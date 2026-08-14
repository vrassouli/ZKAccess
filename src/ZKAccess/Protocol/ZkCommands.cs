namespace ZKAccess.Protocol;

internal static class ZkCommands
{
    public const ushort DbRead = 7;
    public const ushort UserWrite = 8;
    public const ushort UserTemplateRead = 9;
    public const ushort OptionsRead = 11;
    public const ushort AttendanceRead = 13;
    public const ushort DeleteUser = 18;
    public const ushort GetFreeSizes = 50;

    public const ushort GetTime = 201;
    public const ushort SetTime = 202;

    public const ushort Connect = 1000;
    public const ushort Exit = 1001;
    public const ushort RefreshData = 1013;
    public const ushort GetVersion = 1100;
    public const ushort Auth = 1102;

    public const ushort PrepareData = 1500;
    public const ushort Data = 1501;
    public const ushort FreeData = 1502;
    public const ushort PrepareBuffer = 1503;
    public const ushort ReadBuffer = 1504;

    public const ushort AckOk = 2000;
    public const ushort AckError = 2001;
    public const ushort AckData = 2002;
    public const ushort AckUnauthenticated = 2005;

    public const int FunctionFingerprintTemplate = 2;
    public const int FunctionUser = 5;
}
