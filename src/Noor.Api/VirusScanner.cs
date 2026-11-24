using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Noor.Api;

public enum ScanVerdict { Clean, Infected, Unavailable }

public interface IVirusScanner
{
    Task<ScanVerdict> ScanAsync(Stream content, CancellationToken ct);
}

/// <summary>Streams files to clamd with the INSTREAM command.</summary>
public sealed class ClamAvScanner(IConfiguration config, ILogger<ClamAvScanner> logger) : IVirusScanner
{
    public async Task<ScanVerdict> ScanAsync(Stream content, CancellationToken ct)
    {
        var host = config["Scan:ClamAvHost"];
        if (string.IsNullOrEmpty(host)) return ScanVerdict.Unavailable;
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            await client.ConnectAsync(host, config.GetValue("Scan:ClamAvPort", 3310), timeout.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token);
            var buffer = new byte[64 * 1024];
            var size = new byte[4];
            int read;
            while ((read = await content.ReadAsync(buffer, timeout.Token)) > 0)
            {
                BinaryPrimitives.WriteUInt32BigEndian(size, (uint)read);
                await stream.WriteAsync(size, timeout.Token);
                await stream.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }
            BinaryPrimitives.WriteUInt32BigEndian(size, 0);
            await stream.WriteAsync(size, timeout.Token);

            using var reply = new MemoryStream();
            await stream.CopyToAsync(reply, timeout.Token);
            var text = Encoding.ASCII.GetString(reply.ToArray()).TrimEnd('\0', '\n');
            if (text.EndsWith("OK", StringComparison.Ordinal)) return ScanVerdict.Clean;
            if (text.EndsWith("FOUND", StringComparison.Ordinal))
            {
                logger.LogWarning("Upload rejected by antivirus: {Result}", text);
                return ScanVerdict.Infected;
            }
            logger.LogError("Unexpected antivirus reply: {Result}", text);
            return ScanVerdict.Unavailable;
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogError(e, "Antivirus unavailable");
            return ScanVerdict.Unavailable;
        }
    }
}
