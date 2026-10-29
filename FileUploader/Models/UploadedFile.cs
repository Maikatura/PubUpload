using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components.Forms;

namespace FileUploader.Models;

public class UploadedFile
{
    [Key]
    public Guid Id { get; set; }
        
    
    [MaxLength(10)]
    [AllowNull]
    public string? ShortCode { get; set; } = string.Empty;  // ✅ NEW
        
    [Required]
    [MaxLength(255)]
    public string FileName { get; set; } = string.Empty;
        
    [Required]
    [MaxLength(100)]
    public string FileExtension { get; set; } = string.Empty;
        
    [Required]
    public long FileSizeBytes { get; set; }
        
    [Required]
    [MaxLength(100)]
    public string ContentType { get; set; } = string.Empty;
    
    [MaxLength(200)]
    [AllowNull]
    public string? StoragePath { get; set; } = null;
        
    // SHA-256 hex of the delete token; the token itself is only ever given to the uploader.
    [Required]
    [MaxLength(64)]
    public string DeleteTokenHash { get; set; } = string.Empty;
        
    [Required]
    public DateTime UploadedAt { get; set; }
        
    [Required]
    public DateTime ExpiresAt { get; set; }
        
    public int DownloadCount { get; set; } = 0;
}

// Custom implementation of IBrowserFile
public class CustomBrowserFile : IBrowserFile
{
    public string Name { get; }
    
    public DateTimeOffset LastModified { get; }
    public long Size { get; }
    public string ContentType { get; }
    private readonly byte[] _content;

    public CustomBrowserFile(string name, long size, string contentType, byte[] content)
    {
        Name = name;
        Size = size;
        ContentType = contentType;
        _content = content;
    }

    public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
    {
        return new MemoryStream(_content);
    }
}