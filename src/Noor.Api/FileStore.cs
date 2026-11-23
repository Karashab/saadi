using System.Security.Cryptography;

namespace Noor.Api;

public sealed record StoredFile(string Key, long Size, string Sha256);

/// <summary>Storage boundary, so an S3-compatible backend can replace local disk without touching the API.</summary>
public interface IFileStore
{
    Task<StoredFile> SaveAsync(Stream content, CancellationToken ct);
    Stream OpenRead(string key);
    void Delete(string key);
}

public sealed class LocalFileStore : IFileStore
{
    private readonly string _root;

    public LocalFileStore(IConfiguration config)
    {
        var root = config["Storage:Root"];
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("Storage:Root is required");
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredFile> SaveAsync(Stream content, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var key = $"{now:yyyy}/{now:MM}/{Guid.NewGuid():N}";
        var target = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".uploading";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    size += read;
                }
                await output.FlushAsync(ct);
            }
            File.Move(temp, target);
            return new StoredFile(key, size, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    public Stream OpenRead(string key) =>
        new FileStream(Resolve(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);

    public void Delete(string key) => File.Delete(Resolve(key));

    private string Resolve(string key)
    {
        var path = Path.GetFullPath(Path.Combine(_root, key));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("invalid storage key");
        return path;
    }
}
