using FileSignatures;
using FileUploader.Models;


namespace FileUploader.Services;

public class FileValidationService
{
    private readonly long myMaxFileSizeBytes = FileSizeModel.MaxFileSizeBytes;

    private static readonly HashSet<string> BlacklistedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".bat", ".cmd", ".com", ".msi", ".scr",
        ".sh", ".bash", ".ps1", ".vbs", ".js", ".jar", ".app",
        ".deb", ".rpm", ".apk", ".dmg", ".pkg",

        // Archives (consider risk)
        ".zip", ".rar", ".7z", ".tar", ".gz"
    };

    private static readonly HashSet<string> myAllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".ico", ".tiff",
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg",
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma", ".opus",
        ".pdf", ".txt", ".md", ".log", ".avif", ".unitypackage"
    };

    public static HashSet<string> AllowedExtensions => myAllowedExtensions;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".gif"] = "image/gif",
        [".bmp"] = "image/bmp", [".webp"] = "image/webp", [".svg"] = "image/svg+xml", [".ico"] = "image/x-icon",
        [".tiff"] = "image/tiff", [".avif"] = "image/avif",
        [".mp4"] = "video/mp4", [".avi"] = "video/x-msvideo", [".mkv"] = "video/x-matroska", [".mov"] = "video/quicktime",
        [".wmv"] = "video/x-ms-wmv", [".flv"] = "video/x-flv", [".webm"] = "video/webm", [".m4v"] = "video/x-m4v",
        [".mpg"] = "video/mpeg", [".mpeg"] = "video/mpeg",
        [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".flac"] = "audio/flac", [".aac"] = "audio/aac",
        [".ogg"] = "audio/ogg", [".m4a"] = "audio/mp4", [".wma"] = "audio/x-ms-wma", [".opus"] = "audio/opus",
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain; charset=utf-8", [".md"] = "text/plain; charset=utf-8", [".log"] = "text/plain; charset=utf-8",
    };

    /// <summary>
    /// Content type for serving a stored file, derived from its extension. The client-supplied
    /// Content-Type is never trusted: it could say text/html and turn an upload into stored XSS.
    /// </summary>
    public static string GetContentType(string extension) =>
        ContentTypes.TryGetValue(extension, out var type) ? type : "application/octet-stream";


    public async Task<(bool IsValid, string ErrorMessage)> ValidateFile(Stream fileStream, string fileName, long fileSize)
    {
        if (fileSize > myMaxFileSizeBytes) return (false, "File too large");
        if (fileSize == 0) return (false, "Empty file");

        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        // 2. Extension Filter (Fast fail)
        if (!IsAllowedExtension(extension)) 
            return (false, $"Extension '{extension}' not allowed");

        // 3. Library-based Content Verification

        if (fileStream.CanSeek)
        {
            fileStream.Position = 0;
        }
        else
        {
            // The stream cannot be reset. 
            // You must read the data into a buffer (MemoryStream) first.
            throw new InvalidOperationException("The uploaded stream does not support seeking. Validation logic must be adjusted.");
        }
        // var inspector = new FileFormatInspector();
        // var detectedFormat = inspector.DetermineFileFormat(fileStream);
        //
        // if (detectedFormat == null)
        //     return (false, "Unable to determine file type from content");


        // string detectedExt = "." + detectedFormat.Extension;
        
        // 4. Verify Detected Type matches Extension
        // if (detectedExt != extension)
        //     return (false, $"File content ({detectedExt}) does not match extension ({extension})");

        // 5. Final Allowlist Check (Optional, if library detects more than you want)
        // if (!AllowedExtensions.Contains(detectedExt))
        //     return (false, "Detected file type is not allowed");

        return (true, string.Empty);
    }

    public bool IsAllowedExtension(string extension)
    {
        return myAllowedExtensions.Contains(extension) && !BlacklistedExtensions.Contains(extension);
    }
}