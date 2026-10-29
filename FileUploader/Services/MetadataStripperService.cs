using System.Text;

namespace FileUploader.Services;

public class MetadataStripperService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif"
    };

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static readonly HashSet<string> PngMetadataChunks = new(StringComparer.Ordinal)
    {
        "tEXt", "zTXt", "iTXt", "eXIf", "tIME", "mkBF", "mkBS", "mkBT", "mTIm", "caBX"
    };

    public readonly record struct StripResult(bool Success, Stream Stream);

    /// <summary>
    /// Removes EXIF/XMP/comments (GPS, camera serials, timestamps...) from supported images.
    /// Returns Success=false when a supported image can't be parsed. The caller must then reject
    /// the upload: falling back to the original would silently publish the metadata.
    /// </summary>
    public StripResult StripMetadata(Stream input, string extension)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!input.CanSeek)
        {
            return new StripResult(!SupportedExtensions.Contains(extension), input);
        }

        input.Position = 0;

        if (!SupportedExtensions.Contains(extension))
        {
            return new StripResult(true, input);
        }

        try
        {
            var header = new byte[12];
            var headerLength = input.Read(header, 0, header.Length);
            input.Position = 0;
            var span = header.AsSpan(0, headerLength);

            // Detect the real format from the bytes, not the extension: a PNG renamed to .jpg must
            // still be stripped (browsers sniff and render it anyway).
            Stream? cleaned =
                IsJpeg(span) ? StripJpeg(input) :
                IsPng(span) ? StripPng(input) :
                IsWebp(span) ? StripWebp(input) :
                IsGif(span) ? StripGif(input) :
                null;

            input.Position = 0;
            if (cleaned == null)
            {
                return new StripResult(false, input);
            }

            cleaned.Position = 0;
            return new StripResult(true, cleaned);
        }
        catch
        {
            input.Position = 0;
            return new StripResult(false, input);
        }
    }

    private static bool IsJpeg(ReadOnlySpan<byte> header) => header.Length >= 2 && header[0] == 0xFF && header[1] == 0xD8;

    private static bool IsPng(ReadOnlySpan<byte> header) =>
        header.Length >= 8 && header[..8].SequenceEqual(PngSignature);

    private static bool IsWebp(ReadOnlySpan<byte> header) =>
        header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8);

    private static bool IsGif(ReadOnlySpan<byte> header) =>
        header.Length >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8));

    private static Stream StripJpeg(Stream input)
    {
        var output = new MemoryStream();
        output.Write(ReadExactly(input, 2)); // SOI

        while (true)
        {
            var ff = input.ReadByte();
            if (ff != 0xFF) throw new InvalidDataException("JPEG marker missing");

            var marker = input.ReadByte();
            while (marker == 0xFF) marker = input.ReadByte(); // fill bytes before a marker are legal
            if (marker < 0) throw new EndOfStreamException();

            if (marker == 0x01 || (marker is >= 0xD0 and <= 0xD7))
            {
                output.WriteByte(0xFF);
                output.WriteByte((byte)marker);
                continue;
            }

            if (marker == 0xD9) // EOI
            {
                // Anything after EOI is dropped: phones append motion-photo videos and vendor
                // trailers there, which can carry their own location data.
                output.WriteByte(0xFF);
                output.WriteByte(0xD9);
                break;
            }

            var hi = input.ReadByte();
            var lo = input.ReadByte();
            if (hi < 0 || lo < 0) throw new EndOfStreamException();
            var length = (hi << 8) | lo;
            if (length < 2) throw new InvalidDataException("JPEG segment too short");

            var payload = ReadExactly(input, length - 2);

            if (ShouldKeepJpegSegment((byte)marker, payload))
            {
                output.WriteByte((byte)ff);
                output.WriteByte((byte)marker);
                output.WriteByte((byte)(length >> 8));
                output.WriteByte((byte)length);
                output.Write(payload);
            }

            if (marker == 0xDA)
            {
                // Start of scan: copy entropy-coded data up to the next real marker, then keep
                // parsing (progressive JPEGs have several scans with segments in between).
                CopyJpegScan(input, output);
            }
        }

        return output;
    }

    private static void CopyJpegScan(Stream input, Stream output)
    {
        while (true)
        {
            var b = input.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            if (b != 0xFF)
            {
                output.WriteByte((byte)b);
                continue;
            }

            var next = input.ReadByte();
            while (next == 0xFF) next = input.ReadByte();
            if (next < 0) throw new EndOfStreamException();

            if (next == 0x00 || next is >= 0xD0 and <= 0xD7)
            {
                // Stuffed 0xFF byte or restart marker: part of the scan data.
                output.WriteByte(0xFF);
                output.WriteByte((byte)next);
                continue;
            }

            // A real marker: rewind so the segment loop handles it.
            input.Position -= 2;
            return;
        }
    }

    private static bool ShouldKeepJpegSegment(byte marker, byte[] payload)
    {
        if (marker is >= 0xE0 and <= 0xEF)
        {
            return marker switch
            {
                0xE0 => true, // APP0 JFIF
                0xE1 => payload.Length >= 6 && !payload.AsSpan(0, 6).SequenceEqual("Exif\0\0"u8)
                        && !StartsWithAscii(payload, "http://ns.adobe.com/xap/1.0/\0")
                        && !StartsWithAscii(payload, "http://ns.adobe.com/xmp/extension/\0"),
                0xE2 => payload.Length >= 12 && payload.AsSpan(0, 12).SequenceEqual("ICC_PROFILE\0"u8),
                0xEE => true, // APP14 Adobe color transform
                _ => false
            };
        }

        if (marker == 0xFE) return false; // COM comment

        return true;
    }

    private static bool StartsWithAscii(byte[] payload, string text)
    {
        if (payload.Length < text.Length) return false;
        for (int i = 0; i < text.Length; i++)
        {
            if (payload[i] != (byte)text[i]) return false;
        }
        return true;
    }

    private static Stream StripPng(Stream input)
    {
        var output = new MemoryStream();
        output.Write(ReadExactly(input, 8)); // signature

        while (true)
        {
            var lengthBytes = ReadExactly(input, 4);
            var typeBytes = ReadExactly(input, 4);
            var length = (lengthBytes[0] << 24) | (lengthBytes[1] << 16) | (lengthBytes[2] << 8) | lengthBytes[3];
            var data = ReadExactly(input, length);
            var crc = ReadExactly(input, 4);

            var type = Encoding.ASCII.GetString(typeBytes);
            if (PngMetadataChunks.Contains(type))
            {
                continue;
            }

            output.Write(lengthBytes);
            output.Write(typeBytes);
            output.Write(data);
            output.Write(crc);

            if (type == "IEND")
            {
                break;
            }
        }

        return output;
    }

    private static Stream StripWebp(Stream input)
    {
        var riff = ReadExactly(input, 4);
        var originalSize = ReadExactly(input, 4);
        var webp = ReadExactly(input, 4);
        if (!riff.AsSpan().SequenceEqual("RIFF"u8) || !webp.AsSpan().SequenceEqual("WEBP"u8))
        {
            throw new InvalidDataException("Bad WEBP header");
        }

        var output = new MemoryStream();
        output.Write(riff);
        output.Write(originalSize);
        output.Write(webp);

        uint newFileSize = 4; // "WEBP"

        // Only walk chunks inside the declared RIFF size; bytes after it are not part of the image.
        var riffSize = (long)(uint)(originalSize[0] | (originalSize[1] << 8) | (originalSize[2] << 16) | (originalSize[3] << 24));
        var riffEnd = Math.Min(input.Length, 8 + riffSize);

        while (input.Position + 8 <= riffEnd)
        {
            var fourCc = ReadExactly(input, 4);
            var chunkSizeBytes = ReadExactly(input, 4);
            var chunkSize = (uint)(chunkSizeBytes[0] | (chunkSizeBytes[1] << 8) | (chunkSizeBytes[2] << 16) | (chunkSizeBytes[3] << 24));
            var chunkData = ReadExactly(input, (int)chunkSize);
            var hasPad = (chunkSize & 1) == 1;
            if (hasPad)
            {
                ReadExactly(input, 1);
            }

            var fourCcStr = Encoding.ASCII.GetString(fourCc);
            var isMetadata = fourCcStr == "EXIF" || fourCcStr == "XMP ";

            if (fourCcStr == "VP8X" && chunkData.Length > 0)
            {
                // Clear the "has EXIF" (0x08) and "has XMP" (0x04) flags since those chunks are removed.
                chunkData[0] &= unchecked((byte)~0x0C);
            }

            if (!isMetadata)
            {
                output.Write(fourCc);
                output.Write(chunkSizeBytes);
                output.Write(chunkData);
                if (hasPad) output.WriteByte(0);
                newFileSize += 8u + chunkSize + (hasPad ? 1u : 0u);
            }
        }

        output.Position = 4;
        output.WriteByte((byte)newFileSize);
        output.WriteByte((byte)(newFileSize >> 8));
        output.WriteByte((byte)(newFileSize >> 16));
        output.WriteByte((byte)(newFileSize >> 24));
        output.Position = output.Length;

        return output;
    }

    private static Stream StripGif(Stream input)
    {
        var output = new MemoryStream();
        output.Write(ReadExactly(input, 6)); // header

        var lsd = ReadExactly(input, 7); // logical screen descriptor
        output.Write(lsd);

        if ((lsd[4] & 0x80) != 0)
        {
            var colorCount = 2 << (lsd[4] & 0x07);
            output.Write(ReadExactly(input, colorCount * 3));
        }

        while (input.Position < input.Length)
        {
            var block = input.ReadByte();
            if (block < 0) break;

            if (block == 0x3B) // trailer
            {
                output.WriteByte(0x3B);
                break;
            }

            if (block == 0x21) // extension
            {
                var label = input.ReadByte();
                if (label < 0) throw new InvalidDataException("GIF extension truncated");

                if (label == 0xFE) // comment extension, drop
                {
                    SkipSubBlocks(input);
                    continue;
                }

                if (label == 0xFF) // application extension
                {
                    var appHeader = ReadExactly(input, 11);
                    var isXmp = appHeader.AsSpan().SequenceEqual("XMP DataXMP"u8);
                    var subBlocks = MoveSubBlocks(input);

                    if (!isXmp)
                    {
                        output.WriteByte(0x21);
                        output.WriteByte(0xFF);
                        output.Write(appHeader);
                        output.Write(subBlocks);
                    }
                    continue;
                }

                output.WriteByte(0x21);
                output.WriteByte((byte)label);
                if (label == 0x01) // plain text extension header
                {
                    output.Write(ReadExactly(input, 12));
                }
                output.Write(MoveSubBlocks(input));
            }
            else if (block == 0x2C) // image descriptor
            {
                output.WriteByte(0x2C);
                var descriptor = ReadExactly(input, 9);
                output.Write(descriptor);

                if ((descriptor[8] & 0x80) != 0)
                {
                    var colorCount = 2 << (descriptor[8] & 0x07);
                    output.Write(ReadExactly(input, colorCount * 3));
                }

                // LZW minimum code size + image data sub-blocks
                var minCodeSize = input.ReadByte();
                if (minCodeSize < 0) throw new InvalidDataException("GIF image data truncated");
                output.WriteByte((byte)minCodeSize);
                output.Write(MoveSubBlocks(input));
            }
            else
            {
                throw new InvalidDataException($"Unknown GIF block 0x{block:X2}");
            }
        }

        return output;
    }

    private static byte[] MoveSubBlocks(Stream input)
    {
        using var buffer = new MemoryStream();
        while (true)
        {
            var size = input.ReadByte();
            if (size == 0 || size < 0)
            {
                buffer.WriteByte(0x00);
                break;
            }
            buffer.WriteByte((byte)size);
            buffer.Write(ReadExactly(input, size));
        }
        return buffer.ToArray();
    }

    private static void SkipSubBlocks(Stream input)
    {
        while (true)
        {
            var size = input.ReadByte();
            if (size == 0 || size < 0) break;
            ReadExactly(input, size);
        }
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        // Lengths come from the file itself; never allocate more than what is actually left.
        if (count < 0 || count > stream.Length - stream.Position) throw new EndOfStreamException();
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
        return buffer;
    }
}