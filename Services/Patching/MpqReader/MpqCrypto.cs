using System;

namespace TeronWoWLauncher.Services.Patching.MpqReader;

/// <summary>
/// MPQ's fixed 0x500-entry crypto table plus the hash/decrypt primitives built on it. Every MPQ
/// archive's hash table and block table are ALWAYS encrypted with fixed, well-known keys
/// ("(hash table)"/"(block table)", hashed at offset 0x300) regardless of whether any individual
/// file inside is encrypted - this table is a hard requirement just to read an archive's directory
/// at all, not an optional feature.
///
/// Adapted (read-path only - no write/encrypt/seed-detection-for-unknown-filenames support, none of
/// which this reader needs) from War3Net.IO.Mpq's StormBuffer.cs
/// (https://github.com/Drake53/War3Net, MIT License, Copyright (c) Drake53), itself an
/// implementation of Blizzard's original "Storm" hashing/encryption algorithm.
/// </summary>
internal static class MpqCrypto
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[0x500];
        uint seed = 0x00100001;

        for (uint index1 = 0; index1 < 0x100; index1++)
        {
            uint index2 = index1;
            for (int i = 0; i < 5; i++, index2 += 0x100)
            {
                seed = ((seed * 125) + 3) % 0x2AAAAB;
                uint temp1 = (seed & 0xFFFF) << 16;

                seed = ((seed * 125) + 3) % 0x2AAAAB;
                uint temp2 = seed & 0xFFFF;

                table[index2] = temp1 | temp2;
            }
        }

        return table;
    }

    /// <summary>
    /// Hashes <paramref name="input"/> using the given table offset (0 = table-index/probe-start
    /// hash, 0x100/0x200 = the two name-verification hashes combined into MpqHashEntry.Name,
    /// 0x300 = table/file encryption key derivation).
    /// </summary>
    internal static uint HashString(string input, int offset)
    {
        uint seed1 = 0x7FED7FED;
        uint seed2 = 0xEEEEEEEE;

        foreach (char c in input.ToUpperInvariant())
        {
            int val = c;
            seed1 = Table[offset + val] ^ (seed1 + seed2);
            seed2 = (uint)val + seed1 + seed2 + (seed2 << 5) + 3;
        }

        return seed1;
    }

    internal static void DecryptBlock(byte[] data, uint seed1)
    {
        uint seed2 = 0xEEEEEEEE;

        // If the block isn't an exact multiple of 4, the remainder is left as-is (unencrypted) -
        // matches the reference implementation exactly.
        for (int i = 0; i < data.Length - 3; i += 4)
        {
            seed2 += Table[0x400 + (seed1 & 0xFF)];

            uint value = BitConverter.ToUInt32(data, i);
            uint result = value ^ (seed1 + seed2);

            seed1 = ((~seed1 << 21) + 0x11111111) | (seed1 >> 11);
            seed2 = result + seed2 + (seed2 << 5) + 3;

            data[i] = (byte)(result & 0xFF);
            data[i + 1] = (byte)((result >> 8) & 0xFF);
            data[i + 2] = (byte)((result >> 16) & 0xFF);
            data[i + 3] = (byte)((result >> 24) & 0xFF);
        }
    }

    internal static void DecryptBlock(uint[] data, uint seed1)
    {
        uint seed2 = 0xEEEEEEEE;

        for (int i = 0; i < data.Length; i++)
        {
            seed2 += Table[0x400 + (seed1 & 0xFF)];

            uint value = data[i];
            uint result = value ^ (seed1 + seed2);

            seed1 = ((~seed1 << 21) + 0x11111111) | (seed1 >> 11);
            seed2 = result + seed2 + (seed2 << 5) + 3;

            data[i] = result;
        }
    }

    /// <summary>The per-file encryption seed for a known file name (offset 0x300 hash) - only
    /// meaningful when the file's Encrypted flag is set. Since this reader only ever looks up
    /// candidate names it already knows, it never needs the "recover the seed for an unknown
    /// filename" fallback the reference implementation supports.</summary>
    internal static uint CalculateFileSeed(string fileName) => HashString(fileName, 0x300);

    /// <summary>Adjusts a file's base seed by its own position/size in the archive - only applied
    /// when the file's FixKey (BlockOffsetAdjustedKey) flag is set.</summary>
    internal static uint AdjustFileSeed(uint baseSeed, uint fileOffset, uint fileSize) => (baseSeed + fileOffset) ^ fileSize;
}
