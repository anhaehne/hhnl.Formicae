using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace hhnl.Formicae.Infrastructure.Persistence;

public sealed class FormicaeDbContextFactory : IDesignTimeDbContextFactory<FormicaeDbContext>
{
    public FormicaeDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FormicaeDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=formicae;Username=formicae;Password=formicae")
            .Options;

        return new FormicaeDbContext(options);
    }
}
