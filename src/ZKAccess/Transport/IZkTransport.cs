namespace ZKAccess.Transport;

internal interface IZkTransport : IAsyncDisposable
{
    Task ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken);
    Task<byte[]> ExchangeAsync(byte[] request, TimeSpan timeout, CancellationToken cancellationToken);
    bool IsConnected { get; }
}
