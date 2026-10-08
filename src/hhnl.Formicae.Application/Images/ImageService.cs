using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Application.Images;

public sealed class ImageService(IImageStore store, IImageSourceResolver sources, IClock clock)
{
    public async Task<IReadOnlyList<ImageResponse>> ListAsync(CancellationToken token)
        => (await store.ListAsync(token)).Where(x => !x.IsArchived).Select(Response).ToArray();
    public async Task<ImageResponse?> GetAsync(string id, CancellationToken token)
        => await store.GetAsync(id, token) is { } image ? Response(image) : null;
    public Task<IReadOnlyList<ImageRevision>> RevisionsAsync(string id, CancellationToken token) => store.RevisionsAsync(id, token);
    public async Task<ImageResponse> CreateAsync(ImageRequest request, CancellationToken token)
    {
        Validate(request);
        var now = clock.UtcNow;
        var image = new ImageDefinition { Name = request.Name.Trim(), Description = request.Description?.Trim() ?? "",
            SourceJson = JsonSerializer.Serialize(request.Source, ImageJson.Options), CreatedAt = now, UpdatedAt = now };
        await store.CreateAsync(image, Revision(image), token);
        return Response(image);
    }
    public async Task<ImageResponse?> UpdateAsync(string id, ImageRequest request, CancellationToken token)
    {
        Validate(request);
        var image = await store.GetAsync(id, token);
        if (image is null || image.IsArchived) return null;
        if (request.ExpectedRevision != image.Revision) throw Conflict();
        var next = image with { Name = request.Name.Trim(), Description = request.Description?.Trim() ?? "",
            SourceJson = JsonSerializer.Serialize(request.Source, ImageJson.Options), Revision = checked(image.Revision + 1), UpdatedAt = clock.UtcNow };
        if (!await store.UpdateAsync(next, Revision(next), image.Revision, token)) throw Conflict();
        return Response(next);
    }
    public async Task<bool> ArchiveAsync(string id, int expectedRevision, CancellationToken token)
    {
        var image = await store.GetAsync(id, token);
        if (image is null || image.IsArchived) return false;
        if (expectedRevision != image.Revision) throw Conflict();
        if (!await store.UpdateAsync(image with { IsArchived = true, Revision = checked(image.Revision + 1), UpdatedAt = clock.UtcNow }, null, image.Revision, token)) throw Conflict();
        return true; // Artifacts and saved workflow references are deliberately retained.
    }
    public async Task<ImageBuildResponse?> QueueAsync(string id, int expectedRevision, CancellationToken token)
    {
        var image = await store.GetAsync(id, token);
        if (image is null || image.IsArchived) return null;
        if (image.Revision != expectedRevision) throw Conflict();
        var source = ImageJson.Source(image.SourceJson);
        var commit = await sources.ResolveCommitAsync(source, token);
        if (commit is not null && !Regex.IsMatch(commit, "^[a-fA-F0-9]{40,64}$")) throw new ArgumentException("Repository ref did not resolve to an immutable commit.");
        var now = clock.UtcNow;
        var build = new ImageBuild { ImageId = id, ImageName = image.Name, SourceRevision = image.Revision, SourceJson = image.SourceJson,
            CommitSha = commit, CreatedAt = now, UpdatedAt = now };
        await store.AddBuildAsync(build, token);
        return Response(build);
    }
    public async Task<IReadOnlyList<ImageBuildResponse>> BuildsAsync(string id, CancellationToken token)
        => (await store.BuildsAsync(id, token)).OrderByDescending(x => x.CreatedAt).Select(Response).ToArray();
    public async Task<ImageBuildResponse?> GetBuildAsync(string imageId, string buildId, CancellationToken token)
        => await store.BuildAsync(buildId, token) is { } build && build.ImageId == imageId ? Response(build) : null;
    public async Task<ImageBuildResponse?> CancelAsync(string imageId, string buildId, CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var build = await store.BuildAsync(buildId, token);
            if (build is null || build.ImageId != imageId) return null;
            if (ImageJson.Terminal(build.State)) return Response(build);
            var next = build with { State = ImageBuildState.Cancelled, CleanupPending = true, Revision = build.Revision + 1, UpdatedAt = clock.UtcNow };
            if (await store.UpdateBuildAsync(next, build.Revision, token)) return Response(next);
        }
        throw Conflict();
    }
    public async Task<PreparedImageSnapshot?> SnapshotAsync(string imageId, string buildId, IReadOnlyList<string> pullSecrets, CancellationToken token)
    {
        var image = await store.GetAsync(imageId, token);
        var build = await store.BuildAsync(buildId, token);
        if (image is null || image.IsArchived || build is null || build.ImageId != imageId || build.State != ImageBuildState.Ready ||
            build.Reference is null || !IsDigest(build.Reference)) return null;
        return new(imageId, buildId, build.SourceRevision, build.ImageName, build.Reference, PullSecretNames: pullSecrets);
    }
    public static bool IsDigest(string reference) => EnvironmentImageReferences.IsValid(reference) && Regex.IsMatch(reference, "@sha256:[a-f0-9]{64}$");
    public static void Validate(ImageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120 || request.Description?.Length > 2000)
            throw new ArgumentException("Image name is required (up to 120 characters); description must be at most 2000 characters.");
        var s = request.Source ?? throw new ArgumentException("Image source is required.");
        if (s.Platform != "linux/amd64") throw new ArgumentException("Only linux/amd64 worker-compatible images are supported.");
        if (s.RepositoryUrl is null)
        {
            if (string.IsNullOrWhiteSpace(s.Dockerfile) || Encoding.UTF8.GetByteCount(s.Dockerfile) > 65536)
                throw new ArgumentException("Inline Dockerfile is required and must be at most 65536 UTF-8 bytes.");
        }
        else
        {
            if (!Uri.TryCreate(s.RepositoryUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new ArgumentException("Use an HTTPS URL from a connected repository without credentials.");
            if (!string.IsNullOrEmpty(s.Dockerfile)) throw new ArgumentException("Repository sources use DockerfilePath instead of inline Dockerfile content.");
            if (string.IsNullOrWhiteSpace(s.Ref) || s.Ref.Length > 256 || s.Ref.StartsWith('-') || s.Ref.Any(char.IsControl)) throw new ArgumentException("Repository ref is invalid.");
        }
        foreach (var path in new[] { s.DockerfilePath, s.ContextPath })
            if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.StartsWith('/') || path.Contains('\\') || path.Split('/').Contains("..") || path.Any(char.IsControl))
                throw new ArgumentException("Dockerfile and context paths must remain inside the repository.");
        if (s.Target is not null && !Regex.IsMatch(s.Target, "^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$")) throw new ArgumentException("Target stage is invalid.");
        if (s.BuildArguments?.Count > 32 || s.BuildArguments?.Any(x => !Regex.IsMatch(x.Key, "^[A-Za-z_][A-Za-z0-9_]{0,63}$") || x.Value is null || x.Value.Length > 4096 || x.Value.Contains('\0')) == true)
            throw new ArgumentException("Use at most 32 bounded non-secret build arguments.");
    }
    private static ImageRevision Revision(ImageDefinition image) => new() { ImageId = image.Id, Revision = image.Revision, Name = image.Name, SourceJson = image.SourceJson, CreatedAt = image.UpdatedAt };
    private static ImageResponse Response(ImageDefinition x) => new(x.Id, x.Name, x.Description, x.Revision, ImageJson.Source(x.SourceJson), x.IsArchived, x.CreatedAt, x.UpdatedAt);
    public static ImageBuildResponse Response(ImageBuild x) => new(x.Id, x.ImageId, x.ImageName, x.SourceRevision, x.State.ToString(), x.CommitSha, x.Reference, x.Failure, x.Logs, x.CreatedAt, x.UpdatedAt);
    private static ImageConflictException Conflict() => new("This image changed. Reload its current revision before retrying.");
}
