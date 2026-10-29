using Microsoft.EntityFrameworkCore;
using FileUploader.Models;

namespace FileUploader.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<UploadedFile> UploadedFiles { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<UploadedFile>(entity =>
        {
            entity.HasIndex(e => e.ShortCode)
                .IsUnique()
                .HasFilter("\"ShortCode\" IS NOT NULL");
            // ✅ UNIQUE INDEX
            entity.HasIndex(e => e.DeleteToken);
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasIndex(e => e.UploadedAt);
        });
    }
}