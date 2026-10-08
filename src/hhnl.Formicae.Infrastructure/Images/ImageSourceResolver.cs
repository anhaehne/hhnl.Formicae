using hhnl.Formicae.Application.Images;
using hhnl.Formicae.Application.Integrations;

namespace hhnl.Formicae.Infrastructure.Images;

public sealed class ImageSourceResolver(IDevOpsPlatformFactory platforms) : IImageSourceResolver
{
    public async Task<string?> ResolveCommitAsync(ImageSource source, CancellationToken token)
    {
        if (source.RepositoryUrl is null) return null;
        var context = await platforms.CreateForRepositoryAsync(source.RepositoryUrl, token);
        return await context.Platform.GetBranchHeadShaAsync(context.Repository, source.Ref, token);
    }
}
public sealed class FakeImageSourceResolver : IImageSourceResolver
{
    public Task<string?> ResolveCommitAsync(ImageSource source, CancellationToken token)
    {
        if (source.RepositoryUrl is not null) throw new ArgumentException("Repository builds require a connected source-control adapter.");
        return Task.FromResult<string?>(null);
    }
}
