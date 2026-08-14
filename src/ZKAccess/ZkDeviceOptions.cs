namespace ZKAccess;

public sealed class ZkDeviceOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 4370;
    public int CommKey { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new ArgumentException("Host is required.", nameof(Host));

        if (Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), "Port must be between 1 and 65535.");

        if (CommKey < 0)
            throw new ArgumentOutOfRangeException(nameof(CommKey), "CommKey cannot be negative.");

        if (ConnectTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));

        if (RequestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
    }
}
