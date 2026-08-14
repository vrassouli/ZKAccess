namespace ZKAccess;

public sealed class ZkProtocolException : Exception
{
    public ZkProtocolException(string message) : base(message)
    {
    }

    public ZkProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
