using System.Security.Cryptography;
using System.Text;

namespace FileUploader.Services;

/// <summary>
/// Only the SHA-256 of a delete token is stored, so a leaked database can't be used to delete files.
/// Tokens are random GUIDs, so no salt is needed. Must stay in sync with the SQL in the
/// HashDeleteTokens migration: lowercase hyphenated GUID, UTF-8, lowercase hex.
/// </summary>
public static class DeleteTokenHasher
{
    public static string Hash(Guid token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token.ToString("D"))));
}
