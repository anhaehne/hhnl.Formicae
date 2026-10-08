using hhnl.Formicae.Application.Images;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace hhnl.Formicae.Infrastructure.Images;

public sealed class EfImageStore(FormicaeDbContext db) : IImageStore
{
    public Task<ImageDefinition?> GetAsync(string id, CancellationToken t) => db.ManagedImages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, t);
    public async Task<IReadOnlyList<ImageDefinition>> ListAsync(CancellationToken t) => await db.ManagedImages.AsNoTracking().OrderBy(x => x.Name).ToListAsync(t);
    public async Task<IReadOnlyList<ImageRevision>> RevisionsAsync(string id, CancellationToken t) => await db.ManagedImageRevisions.AsNoTracking().Where(x => x.ImageId == id).OrderByDescending(x => x.Revision).ToListAsync(t);
    public async Task CreateAsync(ImageDefinition image, ImageRevision revision, CancellationToken t)
    { db.ManagedImages.Add(image); db.ManagedImageRevisions.Add(revision); await db.SaveChangesAsync(t); }
    public async Task<bool> UpdateAsync(ImageDefinition image, ImageRevision? revision, int expectedRevision, CancellationToken t)
    {
        var entry = db.Attach(image); entry.State = EntityState.Modified; entry.Property(x => x.Revision).OriginalValue = expectedRevision;
        if (revision is not null) db.ManagedImageRevisions.Add(revision);
        try { await db.SaveChangesAsync(t); return true; }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return false; }
        finally { db.ChangeTracker.Clear(); }
    }
    public Task<ImageBuild?> BuildAsync(string id, CancellationToken t) => db.ManagedImageBuilds.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, t);
    public async Task<IReadOnlyList<ImageBuild>> BuildsAsync(string? imageId, CancellationToken t)
        => await db.ManagedImageBuilds.AsNoTracking().Where(x => imageId == null || x.ImageId == imageId).OrderBy(x => x.CreatedAt).ToListAsync(t);
    public async Task AddBuildAsync(ImageBuild build, CancellationToken t) { db.ManagedImageBuilds.Add(build); await db.SaveChangesAsync(t); db.ChangeTracker.Clear(); }
    public async Task<bool> UpdateBuildAsync(ImageBuild build, int expectedRevision, CancellationToken t)
    {
        var entry = db.Attach(build); entry.State = EntityState.Modified; entry.Property(x => x.Revision).OriginalValue = expectedRevision;
        try { await db.SaveChangesAsync(t); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        finally { db.ChangeTracker.Clear(); }
    }
}

public sealed class InMemoryImageStore : IImageStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, ImageDefinition> images = new();
    private readonly List<ImageRevision> revisions = [];
    private readonly Dictionary<string, ImageBuild> builds = new();
    public Task<ImageDefinition?> GetAsync(string id, CancellationToken t) { lock(gate) return Task.FromResult(images.GetValueOrDefault(id)); }
    public Task<IReadOnlyList<ImageDefinition>> ListAsync(CancellationToken t) { lock(gate) return Task.FromResult<IReadOnlyList<ImageDefinition>>(images.Values.OrderBy(x=>x.Name).ToArray()); }
    public Task<IReadOnlyList<ImageRevision>> RevisionsAsync(string id, CancellationToken t) { lock(gate) return Task.FromResult<IReadOnlyList<ImageRevision>>(revisions.Where(x=>x.ImageId==id).OrderByDescending(x=>x.Revision).ToArray()); }
    public Task CreateAsync(ImageDefinition image, ImageRevision revision, CancellationToken t) { lock(gate) { images.Add(image.Id,image); revisions.Add(revision); } return Task.CompletedTask; }
    public Task<bool> UpdateAsync(ImageDefinition image, ImageRevision? revision, int expectedRevision, CancellationToken t)
    { lock(gate) { if (!images.TryGetValue(image.Id,out var old) || old.Revision != expectedRevision) return Task.FromResult(false); images[image.Id]=image; if(revision is not null) revisions.Add(revision); return Task.FromResult(true); } }
    public Task<ImageBuild?> BuildAsync(string id, CancellationToken t) { lock(gate) return Task.FromResult(builds.GetValueOrDefault(id)); }
    public Task<IReadOnlyList<ImageBuild>> BuildsAsync(string? imageId, CancellationToken t) { lock(gate) return Task.FromResult<IReadOnlyList<ImageBuild>>(builds.Values.Where(x=>imageId==null||x.ImageId==imageId).OrderBy(x=>x.CreatedAt).ToArray()); }
    public Task AddBuildAsync(ImageBuild build, CancellationToken t) { lock(gate) builds.Add(build.Id, build); return Task.CompletedTask; }
    public Task<bool> UpdateBuildAsync(ImageBuild build,int expectedRevision,CancellationToken t)
    { lock(gate) { if(!builds.TryGetValue(build.Id,out var old)||old.Revision!=expectedRevision) return Task.FromResult(false); builds[build.Id]=build; return Task.FromResult(true); } }
}
