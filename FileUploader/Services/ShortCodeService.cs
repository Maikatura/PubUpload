using System.Security.Cryptography;

namespace FileUploader.Services;

public class ShortCodeService
{
    private const string Characters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const int MinCodeLength = 7;
    private const int MaxCodeLength = 10;
    private const int MaxAttemptsPerLength = 10;
    
    
    
    public string GenerateShortCode(int length)
    {
        var code = new char[length];
        
        for (int i = 0; i < length; i++)
        {
            code[i] = Characters[RandomNumberGenerator.GetInt32(Characters.Length)];
        }
        
        return new string(code);
    }
    
    public async Task<string> GenerateUniqueShortCodeAsync(Func<string, Task<bool>> existsCheck)
    {
        for (int currentLength = MinCodeLength; currentLength <= MaxCodeLength; currentLength++)
        {
            int attempts = 0;

            while (attempts < MaxAttemptsPerLength)
            {
                string code = GenerateShortCode(currentLength);
                attempts++;

                if (!await existsCheck(code))
                {
                    return code;
                }
            }
        }

        // If we've tried all lengths up to MaxCodeLength and still failed
        throw new InvalidOperationException(
            $"Failed to generate unique short code after trying lengths {MinCodeLength} to {MaxCodeLength}"
        );
    }
}