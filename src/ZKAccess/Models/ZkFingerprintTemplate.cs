namespace ZKAccess.Models;

public sealed record ZkFingerprintTemplate(
    ushort Uid,
    byte FingerIndex,
    byte Valid,
    byte[] Template)
{
    public int Size => Template.Length;
}
