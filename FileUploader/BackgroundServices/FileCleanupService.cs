using FileUploader.Data;
using FileUploader.Services;
using Microsoft.EntityFrameworkCore;

namespace FileUploader.BackgroundServices;

public class FileCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<FileCleanupService> _logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);

    // Files younger than this are skipped by the orphan sweep: an upload writes its file to disk
    // just before its database row is saved, and must not be deleted in between.
    private static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromMinutes(10);

    public FileCleanupService(IServiceProvider serviceProvider, ILogger<FileCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("File Cleanup Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupExpiredFilesAsync(stoppingToken);
                await CleanupOrphanedFilesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during file cleanup");
            }

            try
            {
                await Task.Delay(_cleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CleanupExpiredFilesAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var fileStorage = scope.ServiceProvider.GetRequiredService<FileStorageService>();

        var expiredFiles = await dbContext.UploadedFiles
            .Where(f => f.ExpiresAt <= DateTime.UtcNow)
            .ToListAsync(cancellationToken);

        if (expiredFiles.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Found {Count} expired files to delete", expiredFiles.Count);

        foreach (var file in expiredFiles)
        {
            try
            {
                fileStorage.DeleteFile(file.StoragePath);
                dbContext.UploadedFiles.Remove(file);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting expired file");
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task CleanupOrphanedFilesAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var uploadsPath = configuration["FileStorage:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads");

        if (!Directory.Exists(uploadsPath))
        {
            return;
        }

        var dbFileIdSet = new HashSet<Guid>(await dbContext.UploadedFiles
            .Select(f => f.Id)
            .ToListAsync(cancellationToken));

        var cutoff = DateTime.UtcNow - OrphanGracePeriod;
        var deletedCount = 0;

        foreach (var filePath in Directory.EnumerateFiles(uploadsPath))
        {
            try
            {
                var fileName = Path.GetFileName(filePath);

                // Trashed files (expired, or deleted while being streamed) are always removed.
                // Previously "<guid>.png.trash" failed the GUID parse below and was kept forever.
                var isTrash = fileName.EndsWith(".trash", StringComparison.OrdinalIgnoreCase);

                if (!isTrash)
                {
                    var dot = fileName.IndexOf('.');
                    var idPart = dot >= 0 ? fileName[..dot] : fileName;
                    if (!Guid.TryParse(idPart, out var fileId))
                    {
                        continue; // not one of ours
                    }

                    if (dbFileIdSet.Contains(fileId) || File.GetLastWriteTimeUtc(filePath) > cutoff)
                    {
                        continue;
                    }
                }

                File.Delete(filePath);
                deletedCount++;
            }
            catch (IOException)
            {
                // Still open by a reader (Windows); retry next run.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing orphaned file");
            }
        }

        if (deletedCount > 0)
        {
            _logger.LogInformation("Cleaned up {Count} orphaned/trashed files", deletedCount);
        }
    }
}
