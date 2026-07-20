using System;
using System.IO;
using System.IO.Compression;

namespace TeronWoWLauncher.Services.Patching.MpqReader;

/// <summary>
/// Reads one MPQ file entry's full contents: sector-by-sector decryption and decompression,
/// concatenated into a single byte array. Callers only ever need the whole (small, text) file at
/// once, so unlike the reference implementation this doesn't support partial/seekable reads -
/// just "give me all the bytes."
///
/// Adapted from War3Net.IO.Mpq's MpqStream.cs/MpqStreamFactory.cs/MpqStreamUtils.cs
/// (https://github.com/Drake53/War3Net, MIT License, Copyright (c) Drake53). Only the "stored"
/// (uncompressed) and ZLib compression methods are supported - MPQ's other methods (BZip2, PKWARE
/// implode, Huffman, ADPCM, sparse) throw NotSupportedException, which
/// MpqPatchMetadataExtractor's caller treats the same as any other unreadable file.
/// </summary>
internal static class MpqFileReader
{
    internal static byte[] ReadAllBytes(Stream archiveStream, MpqHeader header, MpqBlockEntry entry, string fileName)
    {
        uint filePosition = entry.FilePosition(header.HeaderOffset);
        uint? fileSeed = entry.IsEncrypted ? ComputeFileSeed(entry, fileName) : null;

        return entry.IsSingleUnit
            ? ReadSingleUnit(archiveStream, filePosition, entry, fileSeed)
            : ReadMultiSector(archiveStream, filePosition, header.SectorSize, entry, fileSeed);
    }

    private static uint ComputeFileSeed(MpqBlockEntry entry, string fileName)
    {
        uint seed = MpqCrypto.CalculateFileSeed(fileName);
        return entry.IsFixKey ? MpqCrypto.AdjustFileSeed(seed, entry.FileOffset, entry.FileSize) : seed;
    }

    private static byte[] ReadSingleUnit(Stream stream, uint filePosition, MpqBlockEntry entry, uint? fileSeed)
    {
        byte[] data = ReadRange(stream, filePosition, checked((int)entry.CompressedSize));

        if (fileSeed is uint seed && data.Length > 3)
        {
            MpqCrypto.DecryptBlock(data, seed);
        }

        return entry.Flags.HasFlag(MpqFileFlags.CompressedMulti) && data.Length != entry.FileSize
            ? DecompressSector(data, checked((int)entry.FileSize))
            : data;
    }

    private static byte[] ReadMultiSector(Stream stream, uint filePosition, int sectorSize, MpqBlockEntry entry, uint? fileSeed)
    {
        int sectorCount = checked((int)((entry.FileSize + sectorSize - 1) / sectorSize));
        bool isCompressed = entry.IsCompressed;

        // Compressed (non-single-unit) files are prefixed with a table of byte offsets - one per
        // sector, plus a trailing one marking the end - so sector boundaries can vary (each sector
        // compresses to a different size). Uncompressed files don't have this table; sectors are
        // simply sectorSize apart (except the last, which may be shorter).
        uint[] sectorPositions;
        if (isCompressed)
        {
            int tableEntries = sectorCount + 1;
            byte[] tableBytes = ReadRange(stream, filePosition, tableEntries * 4);
            if (fileSeed is uint tableSeed && tableEntries > 1)
            {
                var asUInt32 = new uint[tableEntries];
                Buffer.BlockCopy(tableBytes, 0, asUInt32, 0, tableBytes.Length);
                MpqCrypto.DecryptBlock(asUInt32, tableSeed - 1);
                sectorPositions = asUInt32;
            }
            else
            {
                sectorPositions = new uint[tableEntries];
                Buffer.BlockCopy(tableBytes, 0, sectorPositions, 0, tableBytes.Length);
            }
        }
        else
        {
            sectorPositions = Array.Empty<uint>();
        }

        using var result = new MemoryStream(checked((int)entry.FileSize));
        for (int sectorIndex = 0; sectorIndex < sectorCount; sectorIndex++)
        {
            int expectedLength = Math.Min(checked((int)(entry.FileSize - (sectorIndex * (long)sectorSize))), sectorSize);

            uint sectorOffset;
            int sectorLength;
            if (isCompressed)
            {
                sectorOffset = sectorPositions[sectorIndex];
                sectorLength = checked((int)(sectorPositions[sectorIndex + 1] - sectorOffset));
            }
            else
            {
                sectorOffset = (uint)(sectorIndex * sectorSize);
                sectorLength = expectedLength;
            }

            byte[] sector = ReadRange(stream, filePosition + sectorOffset, sectorLength);

            if (fileSeed is uint seed && sectorLength > 3)
            {
                MpqCrypto.DecryptBlock(sector, (uint)(sectorIndex + seed));
            }

            if (isCompressed && sectorLength != expectedLength)
            {
                sector = DecompressSector(sector, expectedLength);
            }

            result.Write(sector, 0, sector.Length);
        }

        return result.ToArray();
    }

    private static byte[] ReadRange(Stream stream, uint offset, int count)
    {
        var buffer = new byte[count];
        lock (stream)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = stream.Read(buffer, totalRead, count - totalRead);
                if (read == 0)
                {
                    throw new MpqParserException("Unexpected end of stream while reading MPQ file data.");
                }

                totalRead += read;
            }
        }

        return buffer;
    }

    /// <summary>The first byte of a compressed sector is a bitmask of which algorithm(s) were
    /// applied. Only a bare ZLib mask (0x02) is supported here.</summary>
    private static byte[] DecompressSector(byte[] sector, int expectedLength)
    {
        const byte ZLibMask = 0x02;

        if (sector.Length == 0)
        {
            return Array.Empty<byte>();
        }

        byte mask = sector[0];
        if (mask != ZLibMask)
        {
            throw new NotSupportedException($"MPQ compression mask 0x{mask:X2} is not supported (only plain ZLib is).");
        }

        using var compressedStream = new MemoryStream(sector, 1, sector.Length - 1);
        using var inflater = new ZLibStream(compressedStream, CompressionMode.Decompress);
        var output = new byte[expectedLength];
        int totalRead = 0;
        while (totalRead < expectedLength)
        {
            int read = inflater.Read(output, totalRead, expectedLength - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead == expectedLength ? output : output[..totalRead];
    }
}
