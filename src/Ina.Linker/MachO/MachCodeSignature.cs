using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Ina.Linker.MachO;

/// <summary>
/// Generates the ad-hoc code signature that Apple's linker also adds to every image. macOS refuses to run
/// unsigned code on Apple silicon; an ad-hoc signature carries no identity, only the hashes of the pages of the
/// file, and therefore needs no certificate and no signing tool.
/// </summary>
internal static class MachCodeSignature
{
    private const int PageSizeBits = 12;
    private const int PageSize = 1 << PageSizeBits;
    private const int HashSize = 32;

    private const uint MagicEmbeddedSignature = 0xfade0cc0;
    private const uint MagicCodeDirectory = 0xfade0c02;
    private const uint SlotCodeDirectory = 0;
    private const uint CodeDirectoryVersion = 0x20400;
    private const uint FlagAdHoc = 0x2;
    private const uint FlagLinkerSigned = 0x20000;
    private const byte HashTypeSha256 = 2;
    private const ulong ExecutableSegmentMainBinary = 0x1;

    private const int SuperBlobSize = 12;
    private const int BlobIndexSize = 8;
    private const int CodeDirectorySize = 88;

    /// <summary>The size of the signature of an image whose signed part is <paramref name="codeSize"/> bytes long.</summary>
    public static int Size(long codeSize, string identifier)
    {
        var hashes = (codeSize + PageSize - 1) / PageSize;
        return checked((int)(SuperBlobSize + BlobIndexSize + CodeDirectorySize + Encoding.UTF8.GetByteCount(identifier) + 1 + hashes * HashSize));
    }

    /// <summary>
    /// Writes the signature of <paramref name="code"/>, the part of the file in front of the signature, to
    /// <paramref name="signature"/>.
    /// </summary>
    /// <param name="identifier">A name for the signed code, conventionally the file name.</param>
    /// <param name="textOffset">File offset of the executable segment.</param>
    /// <param name="textSize">File size of the executable segment.</param>
    public static void Sign(ReadOnlySpan<byte> code, Span<byte> signature, string identifier, ulong textOffset, ulong textSize)
    {
        var name = Encoding.UTF8.GetBytes(identifier);
        var hashCount = (code.Length + PageSize - 1) / PageSize;
        var identifierOffset = CodeDirectorySize;
        var hashOffset = identifierOffset + name.Length + 1;
        var directorySize = hashOffset + hashCount * HashSize;
        var total = SuperBlobSize + BlobIndexSize + directorySize;
        signature = signature[..total];
        signature.Clear();

        // All fields of the signature are big-endian, unlike the rest of the file.
        BinaryPrimitives.WriteUInt32BigEndian(signature, MagicEmbeddedSignature);
        BinaryPrimitives.WriteUInt32BigEndian(signature[4..], (uint)total);
        BinaryPrimitives.WriteUInt32BigEndian(signature[8..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(signature[12..], SlotCodeDirectory);
        BinaryPrimitives.WriteUInt32BigEndian(signature[16..], SuperBlobSize + BlobIndexSize);

        var directory = signature[(SuperBlobSize + BlobIndexSize)..];
        BinaryPrimitives.WriteUInt32BigEndian(directory, MagicCodeDirectory);
        BinaryPrimitives.WriteUInt32BigEndian(directory[4..], (uint)directorySize);
        BinaryPrimitives.WriteUInt32BigEndian(directory[8..], CodeDirectoryVersion);
        BinaryPrimitives.WriteUInt32BigEndian(directory[12..], FlagAdHoc | FlagLinkerSigned);
        BinaryPrimitives.WriteUInt32BigEndian(directory[16..], (uint)hashOffset);
        BinaryPrimitives.WriteUInt32BigEndian(directory[20..], (uint)identifierOffset);
        BinaryPrimitives.WriteUInt32BigEndian(directory[24..], 0);                  // special slots
        BinaryPrimitives.WriteUInt32BigEndian(directory[28..], (uint)hashCount);
        BinaryPrimitives.WriteUInt32BigEndian(directory[32..], (uint)code.Length);  // code limit
        directory[36] = HashSize;
        directory[37] = HashTypeSha256;
        directory[39] = PageSizeBits;
        BinaryPrimitives.WriteUInt64BigEndian(directory[64..], textOffset);
        BinaryPrimitives.WriteUInt64BigEndian(directory[72..], textSize);
        BinaryPrimitives.WriteUInt64BigEndian(directory[80..], ExecutableSegmentMainBinary);

        name.CopyTo(directory[identifierOffset..]);
        for (var i = 0; i < hashCount; i++)
        {
            var page = code.Slice(i * PageSize, Math.Min(PageSize, code.Length - i * PageSize));
            SHA256.HashData(page, directory.Slice(hashOffset + i * HashSize, HashSize));
        }
    }
}
