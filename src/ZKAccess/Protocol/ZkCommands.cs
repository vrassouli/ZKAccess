namespace ZKAccess.Protocol;

internal static class ZkCommands
{
    public const ushort Connect = 1000;
    public const ushort Exit = 1001;
    public const ushort Auth = 1102;

    public const ushort AckOk = 2000;
    public const ushort AckError = 2001;
    public const ushort AckUnauthenticated = 2005;
}
