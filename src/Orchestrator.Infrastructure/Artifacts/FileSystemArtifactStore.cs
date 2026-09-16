using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Evidence;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Infrastructure.Artifacts;

/// <summary>
/// Stores artifact bytes as files under <c>{root}/{taskId:N}/{artifactId}.bin</c>
/// and metadata as <see cref="Artifact"/> rows. Writes go to a <c>.tmp</c> file
/// first and are moved into place atomically, so a crash cannot leave a
/// half-written file under a committed id. Reads re-hash the bytes and throw
/// <see cref="ArtifactIntegrityException"/> on mismatch.
/// </summary>
public sealed class FileSystemArtifactStore(string rootDirectory, OrchestratorDbContext db, TimeProvider clock)
    : IArtifactStore
{
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(90);

    private readonly string _rootDirectory = Path.GetFullPath(rootDirectory);

    public async Task<string> PutAsync(ArtifactDescriptor descriptor, Stream content, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var directory = ResolveDirectory(descriptor.TaskId);
        Directory.CreateDirectory(directory);

        // Stage under a random name first: the durable id is assigned by the
        // Artifact constructor below, so no file may carry a committed id
        // until the row exists to own it.
        var tmpPath = Path.Combine(directory, "tmp-" + Guid.NewGuid().ToString("N") + ".tmp");
        var (contentHash, sizeBytes) = await WriteAndHashAsync(tmpPath, content, ct);

        var artifact = new Artifact(
            descriptor.TaskId, descriptor.Type, descriptor.ContentType, descriptor.Sensitivity,
            descriptor.Producer, contentHash, sizeBytes, now, now + DefaultRetention);
        db.Artifacts.Add(artifact);

        File.Move(tmpPath, Path.Combine(directory, artifact.Id + ".bin"), overwrite: false);
        await db.SaveChangesAsync(ct);

        return artifact.Id;
    }

    public async Task<Stream> OpenAsync(string artifactId, CancellationToken ct)
    {
        var artifact = await db.Artifacts
            .SingleOrDefaultAsync(a => a.Id == artifactId, ct);
        if (artifact is null)
            throw new ArtifactNotFoundException(artifactId);

        var path = Path.Combine(ResolveDirectory(artifact.TaskId), artifactId + ".bin");
        if (!File.Exists(path))
            throw new ArtifactNotFoundException(artifactId);

        var bytes = await File.ReadAllBytesAsync(path, ct);
        var actualHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(actualHash), Encoding.UTF8.GetBytes(artifact.ContentHash)))
            throw new ArtifactIntegrityException(artifactId);

        return new MemoryStream(bytes, writable: false);
    }

    private static async Task<(string ContentHash, long SizeBytes)> WriteAndHashAsync(
        string tmpPath, Stream content, CancellationToken ct)
    {
        await using var dest = new FileStream(
            tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[81920];
        long size = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            await dest.WriteAsync(buffer.AsMemory(0, read), ct);
            size += read;
        }

        await dest.FlushAsync(ct);
        return ("sha256:" + Convert.ToHexStringLower(sha.GetCurrentHash()), size);
    }

    /// <summary>
    /// Resolves the per-task directory, verifying it stays inside the store root
    /// so a crafted task id can never escape it.
    /// </summary>
    private string ResolveDirectory(Guid taskId)
    {
        var candidate = Path.GetFullPath(Path.Combine(_rootDirectory, taskId.ToString("N")));
        var root = _rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (candidate != root && !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Task id resolves outside the artifact store.", nameof(taskId));

        return candidate;
    }
}
