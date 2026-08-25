using System;
using System.IO;

namespace TeronWoWLauncher.Services.Patching.MpqReader;

/// <summary>
/// The subset of an MPQ block-table entry's flags this reader cares about. Adapted from
/// War3Net.IO.Mpq's MpqFileFlags.cs (https://github.com/Drake53/War3Net, MIT License,
/// Copyright (c) Drake53).
/// </summary>
[Flags]
internal enum MpqFileFlags : uint
{
    CompressedPK = 0x00000100, // AKA "imploded" (PKWARE implode) - not supported by this reader.
    CompressedMulti = 0x00000200, // The generic multi-algorithm compression scheme - only its ZLib variant is supported here.
    Compressed = 0x0000FF00,
    Encrypted = 0x00010000,
    FixKey = 0x00020000, // AKA BlockOffsetAdjustedKey.
    SingleUnit = 0x01000000,
    Exists = 0x80000000,
}

/// <summary>
/// A parsed MPQ header (version 0 only - the format vanilla WoW and its custom patches use).
/// Adapted from War3Net.IO.Mpq's MpqHeader.cs (https://github.com/Drake53/War3Net, MIT License,
/// Copyright (c) Drake53).
/// </summary>
internal sealed class MpqHeader
{
    internal const uint MpqSignature = 0x1A51504D; // "MPQ\x1A"
    internal const int Size = 32;

    internal required uint HeaderOffset { get; init; }
    internal required ushort BlockSizeShift { get; init; }
    internal required uint HashTableOffset { get; init; }
    internal required uint BlockTableOffset { get; init; }
    internal required uint HashTableSize { get; init; }
    internal required uint BlockTableSize { get; init; }

    internal int SectorSize => 0x200 << BlockSizeShift;
    internal uint HashTablePosition => HashTableOffset + HeaderOffset;
    internal uint BlockTablePosition => BlockTableOffset + HeaderOffset;

    internal static MpqHeader Parse(BinaryReader reader, uint headerOffset)
    {
        uint id = reader.ReadUInt32();
        if (id != MpqSignature)
        {
            throw new MpqParserException($"Invalid MPQ header signature at offset {headerOffset}.");
        }

        _ = reader.ReadUInt32(); // DataOffset - unused by this read-only reader.
        _ = reader.ReadUInt32(); // ArchiveSize - unused by this read-only reader.
        ushort version = reader.ReadUInt16();
        ushort blockSizeShift = reader.ReadUInt16();
        uint hashTableOffset = reader.ReadUInt32();
        uint blockTableOffset = reader.ReadUInt32();
        uint hashTableSize = reader.ReadUInt32();
        uint blockTableSize = reader.ReadUInt32();

        // Some archives report version 1 as a protection trick while actually being version-0
        // format (the real version-1 extended header fields never follow) - treat any non-zero
        // version as this same quirk rather than rejecting the archive outright, matching the
        // reference implementation's own handling.
        if (version != 0 && version != 1)
        {
            throw new MpqParserException($"MPQ format version {version} is not supported.");
        }

        return new MpqHeader
        {
            HeaderOffset = headerOffset,
            BlockSizeShift = blockSizeShift,
            HashTableOffset = hashTableOffset,
            BlockTableOffset = blockTableOffset,
            HashTableSize = hashTableSize,
            BlockTableSize = blockTableSize,
        };
    }
}

/// <summary>
/// One entry in the (always-encrypted) hash table, mapping a hashed file name to a block-table
/// index. Adapted from War3Net.IO.Mpq's MpqHash.cs (https://github.com/Drake53/War3Net, MIT
/// License, Copyright (c) Drake53).
/// </summary>
internal readonly struct MpqHashEntry
{
    internal required ulong Name { get; init; }
    internal required uint BlockIndex { get; init; }

    internal bool IsEmpty => BlockIndex == 0xFFFFFFFF;
    internal bool IsDeleted => BlockIndex == 0xFFFFFFFE;

    internal static MpqHashEntry Read(BinaryReader reader)
    {
        ulong name = reader.ReadUInt64();
        _ = reader.ReadUInt32(); // Locale - this reader doesn't distinguish locales, matches any.
        uint blockIndex = reader.ReadUInt32();
        return new MpqHashEntry { Name = name, BlockIndex = blockIndex };
    }

    /// <summary>The starting probe index into the hash table for a given file name.</summary>
    internal static uint GetTableIndex(string fileName) => MpqCrypto.HashString(fileName, 0);

    /// <summary>The combined verification hash stored as MpqHashEntry.Name for a given file name.</summary>
    internal static ulong GetHashedFileName(string fileName)
    {
        uint nameA = MpqCrypto.HashString(fileName, 0x100);
        uint nameB = MpqCrypto.HashString(fileName, 0x200);
        return nameA | ((ulong)nameB << 32);
    }
}

/// <summary>
/// One entry in the (always-encrypted) block table, describing where a file's data lives and how
/// it's stored. Adapted from War3Net.IO.Mpq's MpqEntry.cs (https://github.com/Drake53/War3Net, MIT
/// License, Copyright (c) Drake53).
/// </summary>
internal sealed class MpqBlockEntry
{
    internal required uint FileOffset { get; init; }
    internal required uint CompressedSize { get; init; }
    internal required uint FileSize { get; init; }
    internal required MpqFileFlags Flags { get; init; }

    internal uint FilePosition(uint headerOffset) => headerOffset + FileOffset;

    internal bool IsCompressed => (Flags & MpqFileFlags.Compressed) != 0;
    internal bool IsEncrypted => Flags.HasFlag(MpqFileFlags.Encrypted);
    internal bool IsSingleUnit => Flags.HasFlag(MpqFileFlags.SingleUnit);
    internal bool IsFixKey => Flags.HasFlag(MpqFileFlags.FixKey);

    internal static MpqBlockEntry Read(BinaryReader reader)
    {
        return new MpqBlockEntry
        {
            FileOffset = reader.ReadUInt32(),
            CompressedSize = reader.ReadUInt32(),
            FileSize = reader.ReadUInt32(),
            Flags = (MpqFileFlags)reader.ReadUInt32(),
        };
    }
}

/// <summary>Thrown when an archive's header/tables can't be parsed - a fully expected outcome
/// against arbitrary user-supplied files, not a bug.</summary>
internal sealed class MpqParserException : Exception
{
    internal MpqParserException(string message)
        : base(message)
    {
    }
}
