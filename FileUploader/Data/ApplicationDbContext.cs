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
            entity.HasIndex(e => e.DeleteTokenHash).IsUnique();
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasIndex(e => e.UploadedAt);
        });
    }
}