using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MatDock.Core.Data;

/// <summary>
/// Design-time factory used by the EF Core tools (<c>dotnet ef migrations …</c>).
/// It builds the context against a throwaway SQLite file and a system user accessor,
/// so migrations can be created without booting the full web host.
/// </summary>
public sealed class MatDockDbContextFactory : IDesignTimeDbContextFactory<MatDockDbContext>
{
    public MatDockDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MatDockDbContext>()
            .UseSqlite("Data Source=matdock-design.db")
            .Options;

        return new MatDockDbContext(options, new SystemCurrentUserAccessor());
    }
}
