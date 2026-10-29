using FileUploader.Models;

namespace FileUploader.Services;

public class FileStorageService
{
    private readonly string myStoragePath;
    private readonly long myMaxFileSize = FileSizeModel.MaxFileSizeBytes;
    private readonly ILogger<FileStorageService> myLogger;

    public FileStorageService(IConfiguration configuration, ILogger<FileStorageService> logger)
    {
        myLogger = logger;
        myStoragePath = configuration["FileStorage:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads");

        if (!Directory.Exists(myStoragePath))
        {
            Directory.CreateDirectory(myStoragePath);
        }
    }

    public async Task<string> SaveFileAsync(Stream fileStream, Guid fileId, string extension, CancellationToken cancellationToken = default)
    {
        if (fileStream == null)
            throw new ArgumentNullException(nameof(fileStream));

        if (!fileStream.CanRead)
            throw new ArgumentException("Stream must be readable", nameof(fileStream));

        if (fileStream.Length > myMaxFileSize)
            throw new ArgumentException("File exceeds maximum size limit", nameof(fileStream));

        if (string.IsNullOrWhiteSpace(extension))
            throw new ArgumentException("Extension is required", nameof(extension));

        // Sanitize extension
        if (!extension.StartsWith("."))
            extension = $".{extension}";

        if (extension.Contains("..") || extension.Contains("/") || extension.Contains("\\"))
            throw new ArgumentException("Invalid extension characters");

        var fileName = $"{fileId}{extension}";
        var filePath = Path.Combine(myStoragePath, fileName);

        Directory.CreateDirectory(myStoragePath);

        try
        {
            await using var fileStreamOutput = new FileStream(
                filePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            await fileStream.CopyToAsync(fileStreamOutput, 81920, cancellationToken);
            return filePath;
        }
        catch
        {
            // Don't leave a half-written file behind (e.g. the client aborted the upload).
            TryDelete(filePath);
            throw;
        }
    }

    /// <summary>
    /// Deletes the file. If it is locked (being streamed on Windows), it's renamed to .trash and
    /// the cleanup service removes it later, so deleted content never stays reachable.
    /// </summary>
    public void DeleteFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        try
        {
            File.Delete(filePath);
        }
        catch (IOException)
        {
            MoveToTrash(filePath);
        }
    }

    public Stream? GetFileStream(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        // FileShare.Delete lets the file be deleted/renamed while someone is still streaming it.
        return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            bufferSize: 64 * 1024, useAsync: true);
    }

    public bool FileExists(string filePath)
    {
        return File.Exists(filePath);
    }

    public void MoveToTrash(string storagePath)
    {
        try
        {
            if (File.Exists(storagePath))
            {
                // Operating systems allow renaming a file even while it's being read/streamed.
                var trashPath = storagePath + ".trash";
                File.Move(storagePath, trashPath, overwrite: true);
            }
        }
        catch (IOException)
        {
            // Hard-locked; the cleanup service will pick it up as an orphan later.
            myLogger.LogWarning("Could not move file to trash; it will be retried by cleanup");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
