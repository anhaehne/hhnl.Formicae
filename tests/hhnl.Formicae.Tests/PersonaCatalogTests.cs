using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace hhnl.Formicae.Tests;

public sealed class PersonaCatalogTests
{
    [Fact]
    public async Task Default_persona_is_virtual_immutable_and_empty()
    {
        var store = new InMemoryPersonaStore(); var service = new PersonaService(store);
        var persona = Assert.Single(await service.ListAsync(default));
        Assert.True(persona.BuiltIn); Assert.Equal("default", persona.Id); Assert.Equal("", persona.Instructions);
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync("default", new(1, "changed"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteAsync("default", 1, default));
        Assert.Empty(await store.ListAsync(default));
    }
    [Fact]
    public async Task Catalog_revisions_reject_stale_edits_deletes_and_hide_deleted_personas()
    {
        var service = new PersonaService(new InMemoryPersonaStore());
        var first = await service.CreateAsync(new(" Reviewer ", " Check code ", " Concise ", " Avoid edits "), default);
        Assert.Equal("Reviewer", first.Name); Assert.Equal(1, first.Revision); Assert.False(first.BuiltIn);
        var second = (await service.UpdateAsync(first.Id, new(1, "Reviewer 2", "new"), default))!;
        Assert.Equal(2, second.Revision); Assert.Equal(first.CreatedAt, second.CreatedAt);
        await Assert.ThrowsAsync<PersonaConflictException>(() => service.UpdateAsync(first.Id, new(1, "stale"), default));
        await Assert.ThrowsAsync<PersonaConflictException>(() => service.UpdateAsync(first.Id, new(3, "future"), default));
        await Assert.ThrowsAsync<PersonaConflictException>(() => service.DeleteAsync(first.Id, 1, default));
        Assert.True(await service.DeleteAsync(first.Id, 2, default));
        Assert.Null(await service.GetAsync(first.Id, default));
        Assert.Single(await service.ListAsync(default));
        Assert.False(await service.DeleteAsync(first.Id, 3, default));
    }
    [Theory]
    [InlineData("name")]
    [InlineData("instructions")]
    [InlineData("tone")]
    [InlineData("constraints")]
    [InlineData("empty")]
    public async Task Oversized_or_empty_required_fields_are_rejected(string field)
    {
        var request = new CreatePersonaRequest(field == "name" ? new string('x', 121) : field == "empty" ? " " : "name",
            field == "instructions" ? new string('x', 16001) : "", field == "tone" ? new string('x', 1001) : "",
            field == "constraints" ? new string('x', 8001) : "");
        var store = new InMemoryPersonaStore();
        await Assert.ThrowsAsync<ArgumentException>(() => new PersonaService(store).CreateAsync(request, default));
        Assert.Empty(await store.ListAsync(default));
    }
}

public sealed class PersonaPersistenceTests(MigrationPostgresFixture fixture) : IClassFixture<MigrationPostgresFixture>
{
    [Fact]
    public async Task Persona_catalog_persists_and_enforces_revision_compare_and_swap()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await db.Database.MigrateAsync();
        var service = new PersonaService(new EfPersonaStore(db)); var first = await service.CreateAsync(new("Audit", "Inspect"), default);
        async Task<PersonaResponse?> Update(string name)
        {
            await using var other = new FormicaeDbContext(new DbContextOptionsBuilder<FormicaeDbContext>().UseNpgsql(db.Database.GetConnectionString()).Options);
            try { return await new PersonaService(new EfPersonaStore(other)).UpdateAsync(first.Id, new(1, name), default); }
            catch (PersonaConflictException) { return null; }
        }
        var contenders = await Task.WhenAll(Update("A"), Update("B"));
        Assert.Single(contenders, item => item is not null);
        var winner = (await service.GetAsync(first.Id, default))!;
        Assert.Equal(2, winner.Revision);
        Assert.True(await service.DeleteAsync(first.Id, winner.Revision, default));
        Assert.Null(await service.GetAsync(first.Id, default));
        db.ChangeTracker.Clear();
        var deleted = await db.Personas.SingleAsync(); Assert.True(deleted.IsDeleted); Assert.Equal(3, deleted.Revision);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
