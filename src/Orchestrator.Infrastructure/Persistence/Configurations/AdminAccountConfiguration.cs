using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Identity;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class AdminAccountConfiguration : IEntityTypeConfiguration<AdminAccount>
{
    public void Configure(EntityTypeBuilder<AdminAccount> b)
    {
        b.ToTable("admin_accounts");
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedNever();
        b.Property(a => a.LoginName).HasMaxLength(200).IsRequired();
        b.Property(a => a.PasswordHash).HasMaxLength(500).IsRequired();

        // Login names are lowercased and trimmed on write and looked up
        // case-insensitively (Task 12), so the unique index on the stored form alone
        // is sufficient - it never needs a separate case-insensitive expression index.
        b.HasIndex(a => a.LoginName).IsUnique();
    }
}
