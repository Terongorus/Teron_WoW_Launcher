using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace TeronWoWLauncher.Services.Patching.MpqReader;

/// <summary>
/// A minimal, read-only MPQ archive reader: locate a file by name and open it as a Stream.
/// Vendored (not a NuGet dependency) and trimmed to only what MpqPatchMetadataExtractor needs -
/// archive creation/writing, locale-specific lookups, and every compression method except "stored"
/// and ZLib are deliberately not supported (an archive/file needing any of those simply fails to
/// open, which the caller treats as "nothing found", same as any other unreadable archive - see
/// MpqPatchMetadataExtractor's own doc comment for why that's an acceptable degrade here).
///
/// Adapted from War3Net.IO.Mpq's MpqArchive.cs (https://github.com/Drake53/War3Net, MIT License,
/// Copyright (c) Drake53), itself an implementation of Blizzard's MoPaQ archive format.
/// </summary>
internal sealed class MpqArchiveReader : IDisposable
{
    // The header can start at any 512-byte-aligned offset (some archives have extra data - e.g. a
    // "protection" shunt - prepended before it).
    private const int HeaderAlignBytes = 0x200;

    private readonly Stream _stream;
    private readonly MpqHeader _header;
    private readonly MpqHashEntry[] _hashTable;
    private readonly MpqBlockEntry[] _blockTable;

    private MpqArchiveReader(Stream stream, MpqHeader header, MpqHashEntry[] hashTable, MpqBlockEntry[] blockTable)
    {
        _stream = stream;
        _header = header;
        _hashTable = hashTable;
        _blockTable = blockTable;
    }

    internal static MpqArchiveReader Open(string path)
    {
        FileStream stream = File.OpenRead(path);
        try
        {
            return Open(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static MpqArchiveReader Open(Stream stream)
    {
        if (!TryLocateHeader(stream, out MpqHeader? header))
        {
            throw new MpqParserException("Unable to locate an MPQ header in this file.");
        }

        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        stream.Seek(header.HashTablePosition, SeekOrigin.Begin);
        byte[] hashTableBytes = reader.ReadBytes(checked((int)(header.HashTableSize * 16)));
        MpqCrypto.DecryptBlock(hashTableBytes, MpqCrypto.HashString("(hash table)", 0x300));
        var hashTable = new MpqHashEntry[header.HashTableSize];
        using (var hashReader = new BinaryReader(new MemoryStream(hashTableBytes)))
        {
            for (int i = 0; i < hashTable.Length; i++)
            {
                hashTable[i] = MpqHashEntry.Read(hashReader);
            }
        }

        stream.Seek(header.BlockTablePosition, SeekOrigin.Begin);
        byte[] blockTableBytes = reader.ReadBytes(checked((int)(header.BlockTableSize * 16)));
        MpqCrypto.DecryptBlock(blockTableBytes, MpqCrypto.HashString("(block table)", 0x300));
        var blockTable = new MpqBlockEntry[header.BlockTableSize];
        using (var blockReader = new BinaryReader(new MemoryStream(blockTableBytes)))
        {
            for (int i = 0; i < blockTable.Length; i++)
            {
                blockTable[i] = MpqBlockEntry.Read(blockReader);
            }
        }

        return new MpqArchiveReader(stream, header, hashTable, blockTable);
    }

    internal bool FileExists(string fileName)
    {
        foreach (MpqHashEntry _ in GetHashEntries(fileName))
        {
            return true;
        }

        return false;
    }

    /// <summary>Opens the first matching entry for <paramref name="fileName"/>, decrypted and
    /// decompressed, or null if the file doesn't exist or uses an unsupported
    /// encryption/compression scheme.</summary>
    internal Stream? OpenFile(string fileName)
    {
        foreach (MpqHashEntry hash in GetHashEntries(fileName))
        {
            if (hash.BlockIndex >= _blockTable.Length)
            {
                continue;
            }

            MpqBlockEntry entry = _blockTable[hash.BlockIndex];
            if (!entry.Flags.HasFlag(MpqFileFlags.Exists))
            {
                continue;
            }

            byte[] data = MpqFileReader.ReadAllBytes(_stream, _header, entry, fileName);
            return new MemoryStream(data, writable: false);
        }

        return null;
    }

    /// <summary>
    /// Probes the hash table starting at the file name's table-index hash, scanning forward
    /// (wrapping around) for entries whose combined name hash matches, stopping once a run of
    /// matches is followed by a never-used slot. Faithfully ported from
    /// War3Net.IO.Mpq.MpqArchive.GetHashEntries.
    /// </summary>
    private IEnumerable<MpqHashEntry> GetHashEntries(string fileName)
    {
        if (_hashTable.Length == 0)
        {
            yield break;
        }

        uint index = MpqHashEntry.GetTableIndex(fileName) & (_header.HashTableSize - 1);
        ulong name = MpqHashEntry.GetHashedFileName(fileName);
        bool foundAny = false;

        for (uint i = index; i < _hashTable.Length; i++)
        {
            MpqHashEntry hash = _hashTable[i];
            if (hash.Name == name && !hash.IsEmpty && !hash.IsDeleted)
            {
                foundAny = true;
                yield return hash;
            }
            else if (hash.IsEmpty && foundAny)
            {
                yield break;
            }
        }

        for (uint i = 0; i < index; i++)
        {
            MpqHashEntry hash = _hashTable[i];
            if (hash.Name == name && !hash.IsEmpty && !hash.IsDeleted)
            {
                foundAny = true;
                yield return hash;
            }
            else if (hash.IsEmpty && foundAny)
            {
                yield break;
            }
        }
    }

    private static bool TryLocateHeader(Stream stream, [NotNullWhen(true)] out MpqHeader? header)
    {
        stream.Seek(0, SeekOrigin.Begin);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        for (long offset = 0; offset <= stream.Length - MpqHeader.Size; offset += HeaderAlignBytes)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            uint signature = reader.ReadUInt32();
            if (signature == MpqHeader.MpqSignature)
            {
                stream.Seek(offset, SeekOrigin.Begin);
                header = MpqHeader.Parse(reader, (uint)offset);
                return true;
            }
        }

        header = null;
        return false;
    }

    public void Dispose() => _stream.Dispose();
}
