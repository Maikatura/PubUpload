using System.Text.Json;
using FileUploader.Data;
using FileUploader.Models;
using FileUploader.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileUploader.Controllers;

[ApiController]
[Route("api")]
public class FileController : ControllerBase
{
    // Uploaded content is served from the same origin as the app, so it must never be able to run
    // script there (an SVG or HTML-ish text file could otherwise read the upload history and delete
    // tokens out of localStorage). Sandbox forces an opaque origin; media still renders.
    private const string FileCsp = "default-src 'none'; img-src 'self' data:; media-src 'self'; style-src 'unsafe-inline'; sandbox";

    // Chrome's built-in PDF viewer refuses to load under a sandbox CSP. PDF scripts run inside the
    // viewer, not the page origin, so a no-script policy without sandbox is enough here.
    private const string PdfCsp = "default-src 'none'; object-src 'self'";

    private readonly ApplicationDbContext myDbContext;
    private readonly FileStorageService myStorageService;
    private readonly FileValidationService myValidationService;
    private readonly ShortCodeService myShortCodeService;
    private readonly MetadataStripperService myMetadataStripper;
    private readonly ILogger<FileController> myLogger;

    public FileController(
        ApplicationDbContext dbContext,
        FileStorageService storageService,
        FileValidationService validationService,
        ShortCodeService shortCodeService,
        MetadataStripperService metadataStripper,
        ILogger<FileController> logger)
    {
        myDbContext = dbContext;
        myStorageService = storageService;
        myValidationService = validationService;
        myShortCodeService = shortCodeService;
        myMetadataStripper = metadataStripper;
        myLogger = logger;
    }

    [HttpPost("upload")]
    [RequestSizeLimit(105 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken cancellationToken)
    {
        try
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(new { error = "No file provided" });
            }

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

            // The form file is already buffered (and seekable), so validate and save straight from it
            // instead of copying the whole upload into memory first.
            await using var uploadStream = file.OpenReadStream();

            var validation = await myValidationService.ValidateFile(uploadStream, file.FileName, file.Length);
            if (!validation.IsValid)
            {
                return BadRequest(new { error = validation.ErrorMessage });
            }

            var stripResult = myMetadataStripper.StripMetadata(uploadStream, extension);
            if (!stripResult.Success)
            {
                // Storing the original would silently publish EXIF/GPS data, so refuse instead.
                return BadRequest(new { error = "Could not remove metadata from this image, so it was not uploaded. Try re-saving or exporting it and upload again." });
            }

            var shortCode = await myShortCodeService.GenerateUniqueShortCodeAsync(async code =>
                await myDbContext.UploadedFiles.AnyAsync(f => f.ShortCode == code, cancellationToken)
            );

            // The original file name is never persisted: names often contain personal details
            // ("passport_jane_doe.jpg"). The browser keeps the real name in its local history.
            var uploadedFile = new UploadedFile
            {
                Id = Guid.NewGuid(),
                ShortCode = shortCode,
                FileName = shortCode + extension,
                FileExtension = extension,
                ContentType = FileValidationService.GetContentType(extension),
                DeleteToken = Guid.NewGuid(),
                UploadedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };

            var dataStream = stripResult.Stream;
            try
            {
                dataStream.Position = 0;
                uploadedFile.StoragePath = await myStorageService.SaveFileAsync(dataStream, uploadedFile.Id, extension, cancellationToken);
                uploadedFile.FileSizeBytes = dataStream.Length;
            }
            finally
            {
                if (!ReferenceEquals(dataStream, uploadStream))
                {
                    await dataStream.DisposeAsync();
                }
            }

            try
            {
                myDbContext.UploadedFiles.Add(uploadedFile);
                await myDbContext.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                myStorageService.DeleteFile(uploadedFile.StoragePath);
                throw;
            }

            var baseUrl = $"{Request.Scheme}://{Request.Host}";

            myLogger.LogInformation("File uploaded: {Extension} ({Size} bytes)", uploadedFile.FileExtension, uploadedFile.FileSizeBytes);

            return new JsonResult(new
            {
                success = true,
                url = $"{baseUrl}/{uploadedFile.ShortCode}",
                deletionURL = $"{baseUrl}/delete/{uploadedFile.DeleteToken}",
                fileName = file.FileName, // echoed back only, not stored
                shortCode = uploadedFile.ShortCode,
                deleteToken = uploadedFile.DeleteToken,
                fileExtension = uploadedFile.FileExtension,
                fileSize = uploadedFile.FileSizeBytes,
                uploadedAt = uploadedFile.UploadedAt,
                expiresAt = uploadedFile.ExpiresAt
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            myLogger.LogError(ex, "Error during file upload");
            return StatusCode(500, new { error = "An error occurred during upload" });
        }
    }

    [HttpGet("/{shortCode}")]
    [HttpGet("/{shortCode}.{extension}")]
    public async Task<IActionResult> ViewFile(string shortCode, string? extension = null,
        [FromQuery] bool forceDownload = false)
    {
        try
        {
            if (shortCode.Length > 10)
            {
                return NotFound("File not found");
            }

            var file = await myDbContext.UploadedFiles
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.ShortCode == shortCode);

            if (file == null || string.IsNullOrWhiteSpace(file.StoragePath))
            {
                return NotFound("File not found");
            }

            if (file.ExpiresAt <= DateTime.UtcNow)
            {
                // Remove from DB immediately so new requests get a 404. The file is renamed to
                // .trash rather than deleted so in-flight streams keep working; cleanup removes it.
                myDbContext.UploadedFiles.Remove(file);
                await myDbContext.SaveChangesAsync();
                myStorageService.MoveToTrash(file.StoragePath);
                return NotFound("File not found");
            }

            var fileStream = myStorageService.GetFileStream(file.StoragePath);
            if (fileStream == null)
            {
                return NotFound("File not found");
            }

            // Content type comes from our own extension map, never from what the uploader claimed.
            var contentType = FileValidationService.GetContentType(file.FileExtension);
            Response.Headers["Content-Security-Policy"] = contentType == "application/pdf" ? PdfCsp : FileCsp;
            Response.Headers["Cache-Control"] = "public, max-age=86400";

            return new FileStreamResult(fileStream, contentType)
            {
                FileDownloadName = forceDownload ? file.FileName : null,
                EnableRangeProcessing = true,
                LastModified = new DateTimeOffset(DateTime.SpecifyKind(file.UploadedAt, DateTimeKind.Utc))
            };
        }
        catch (Exception ex)
        {
            // Deliberately not logging the short code: logs must not contain file URLs.
            myLogger.LogError(ex, "Error serving file");
            return StatusCode(500, "An error occurred while downloading the file");
        }
    }

    // POST/DELETE only. A GET that deletes gets triggered by link previewers, prefetchers and
    // URL scanners. The /delete/{token} page asks for confirmation and then calls this.
    [HttpPost("delete/{deleteToken:guid}")]
    [HttpDelete("delete/{deleteToken:guid}")]
    public async Task<IActionResult> Delete(Guid deleteToken)
    {
        try
        {
            var file = await myDbContext.UploadedFiles
                .FirstOrDefaultAsync(f => f.DeleteToken == deleteToken);

            if (file == null)
            {
                return NotFound(new { error = "File not found or already deleted" });
            }

            myDbContext.UploadedFiles.Remove(file);
            await myDbContext.SaveChangesAsync();

            if (!string.IsNullOrWhiteSpace(file.StoragePath))
            {
                myStorageService.DeleteFile(file.StoragePath);
            }

            myLogger.LogInformation("File deleted via token");

            return Ok(new { success = true, message = "File deleted successfully" });
        }
        catch (Exception ex)
        {
            myLogger.LogError(ex, "Error deleting file");
            return StatusCode(500, new { error = "An error occurred while deleting the file" });
        }
    }

    // The config is built on the server. This used to echo back an arbitrary ?data= query string as
    // a download, which let anyone craft links serving attacker-chosen files from this domain.
    [HttpGet("/download-config")]
    public IActionResult DownloadShareXConfig()
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var json = JsonSerializer.Serialize(ShareXConfigBuilder.Build(baseUrl), new JsonSerializerOptions { WriteIndented = true });
        var safeHost = new string(Request.Host.Host.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-').ToArray());
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", $"{safeHost}-sharex.sxcu");
    }
}
