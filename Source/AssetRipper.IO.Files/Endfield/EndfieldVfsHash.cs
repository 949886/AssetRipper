using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace AssetRipper.IO.Files.Endfield;

/// <summary>
/// Computes Endfield VFS block directory names from their logical block names.
/// The implementation is limited to the short ASCII block names used by Endfield VFS.
/// </summary>
public static class EndfieldVfsHash
{
	private static ReadOnlySpan<byte> UnityHashSecret =>
	[
		0xB8, 0xFE, 0x6C, 0x39, 0x23, 0xA4, 0x4B, 0xBE, 0x7C, 0x01, 0x81, 0x2C, 0xF7, 0x21, 0xAD, 0x1C,
		0xDE, 0xD4, 0x6D, 0xE9, 0x83, 0x90, 0x97, 0xDB, 0x72, 0x40, 0xA4, 0xA4, 0xB7, 0xB3, 0x67, 0x1F,
		0xCB, 0x79, 0xE6, 0x4E, 0xCC, 0xC0, 0xE5, 0x78, 0x82, 0x5A, 0xD0, 0x7D, 0xCC, 0xFF, 0x72, 0x21,
		0xB8, 0x08, 0x46, 0x74, 0xF7, 0x43, 0x24, 0x8E, 0xE0, 0x35, 0x90, 0xE6, 0x81, 0x3A, 0x26, 0x4C,
		0x3C, 0x28, 0x52, 0xBB, 0x91, 0xC3, 0x00, 0xCB, 0x88, 0xD0, 0x65, 0x8B, 0x1B, 0x53, 0x2E, 0xA3,
		0x71, 0x64, 0x48, 0x97, 0xA2, 0x0D, 0xF9, 0x4E, 0x38, 0x19, 0xEF, 0x46, 0xA9, 0xDE, 0xAC, 0xD8,
		0xA8, 0xFA, 0x76, 0x3F, 0xE3, 0x9C, 0x34, 0x3F, 0xF9, 0xDC, 0xBB, 0xC7, 0xC7, 0x0B, 0x4F, 0x1D,
		0x8A, 0x51, 0xE0, 0x4B, 0xCD, 0xB4, 0x59, 0x31, 0xC8, 0x9F, 0x7E, 0xC9, 0xD9, 0x78, 0x73, 0x64,
		0xEA, 0xC5, 0xAC, 0x83, 0x34, 0xD3, 0xEB, 0xC3, 0xC5, 0x81, 0xA0, 0xFF, 0xFA, 0x13, 0x63, 0xEB,
		0x17, 0x0D, 0xDD, 0x51, 0xB7, 0xF0, 0xDA, 0x49, 0xD3, 0x16, 0x55, 0x26, 0x29, 0xD4, 0x68, 0x9E,
		0x2B, 0x16, 0xBE, 0x58, 0x7D, 0x47, 0xA1, 0xFC, 0x8F, 0xF8, 0xB8, 0xD1, 0x7A, 0xD0, 0x31, 0xCE,
		0x45, 0xCB, 0x3A, 0x8F, 0x95, 0x16, 0x04, 0x28, 0xAF, 0xD7, 0xFB, 0xCA, 0xBB, 0x4B, 0x40, 0x7E,
	];

	public static string GetBlockDirectoryName(EndfieldVfsBlockType blockType)
	{
		string blockName = GetBlockName(blockType);
		Span<byte> nameBytes = stackalloc byte[Encoding.ASCII.GetByteCount(blockName)];
		Encoding.ASCII.GetBytes(blockName, nameBytes);
		ulong hash64 = Hash64(nameBytes, UnityHashSecret, 0);
		uint hash32 = (uint)(hash64 & uint.MaxValue) ^ (uint)(hash64 >> 32);
		return BinaryPrimitives.ReverseEndianness(hash32).ToString("X8");
	}

	public static string GetBlockName(EndfieldVfsBlockType blockType) => blockType switch
	{
		EndfieldVfsBlockType.None => "None",
		EndfieldVfsBlockType.InitialAudio => "InitAudio",
		EndfieldVfsBlockType.InitialBundle => "InitBundle",
		EndfieldVfsBlockType.InitialExtendData => "InitialExtendData",
		EndfieldVfsBlockType.BundleManifest => "BundleManifest",
		EndfieldVfsBlockType.IFixPatch => "IFixPatchOut",
		EndfieldVfsBlockType.AuditStreaming => "AuditStreaming",
		EndfieldVfsBlockType.AuditDynamicStreaming => "AuditDynamicStreaming",
		EndfieldVfsBlockType.AuditIV => "AuditIV",
		EndfieldVfsBlockType.AuditAudio => "AuditAudio",
		EndfieldVfsBlockType.AuditVideo => "AuditVideo",
		EndfieldVfsBlockType.Bundle => "Bundle",
		EndfieldVfsBlockType.Audio => "Audio",
		EndfieldVfsBlockType.Video => "Video",
		EndfieldVfsBlockType.IV => "IV",
		EndfieldVfsBlockType.Streaming => "Streaming",
		EndfieldVfsBlockType.DynamicStreaming => "DynamicStreaming",
		EndfieldVfsBlockType.Lua => "Lua",
		EndfieldVfsBlockType.Table => "Table",
		EndfieldVfsBlockType.JsonData => "JsonData",
		EndfieldVfsBlockType.ExtendData => "ExtendData",
		EndfieldVfsBlockType.HotfixAudio => "HotfixAudio",
		EndfieldVfsBlockType.AudioChinese => "AudioChinese",
		EndfieldVfsBlockType.AudioEnglish => "AudioEnglish",
		EndfieldVfsBlockType.AudioJapanese => "AudioJapanese",
		EndfieldVfsBlockType.AudioKorean => "AudioKorean",
		_ => "Raw",
	};

	private static ulong Hash64(ReadOnlySpan<byte> data, ReadOnlySpan<byte> secret, ulong seed)
	{
		return data.Length < 16
			? Hash64Length0To16(data, secret, seed)
			: Hash64Length17To128(data, secret, seed);
	}

	private static ulong Hash64Length0To16(ReadOnlySpan<byte> data, ReadOnlySpan<byte> secret, ulong seed)
	{
		unchecked
		{
			int length = data.Length;
			if (length > 8)
			{
				ulong inputLow = ReadUInt64(data, 0);
				ulong inputHigh = ReadUInt64(data, length - 8);
				ulong keyedLow = ((ReadUInt64(secret, 0x20) ^ ReadUInt64(secret, 0x18)) + seed) ^ inputLow;
				ulong keyedHigh = ((ReadUInt64(secret, 0x30) ^ ReadUInt64(secret, 0x28)) - seed) ^ inputHigh;
				ulong folded = Multiply128Fold64(keyedLow, keyedHigh);
				ulong accumulator = folded + BinaryPrimitives.ReverseEndianness(keyedLow) + keyedHigh + (ulong)length;
				return Xxh3Avalanche(accumulator);
			}

			if (length >= 4)
			{
				ulong inputLow = ReadUInt32(data, 0);
				ulong inputHigh = ReadUInt32(data, length - 4);
				ulong combined = (inputLow << 32) | inputHigh;
				uint seedLow = (uint)seed;
				uint seedSwapped = BinaryPrimitives.ReverseEndianness(seedLow);
				ulong adjustedSeed = seed ^ ((ulong)seedSwapped << 32);
				ulong bitFlip = (ReadUInt64(secret, 0x08) ^ ReadUInt64(secret, 0x10)) - adjustedSeed;
				return Xxh3Rrmxmx(combined ^ bitFlip, length);
			}

			if (length >= 1)
			{
				uint c1 = data[0];
				uint c2 = data[length >> 1];
				uint c3 = data[length - 1];
				uint combined = ((((c1 | (c2 << 8)) << 8) | (uint)length) << 8) | c3;
				ulong bitFlip = (ulong)(ReadUInt32(secret, 0) ^ ReadUInt32(secret, 4)) + seed;
				return Xxh64Avalanche(combined ^ bitFlip);
			}

			ulong emptyBitFlip = (ReadUInt64(secret, 0x38) ^ ReadUInt64(secret, 0x40)) ^ seed;
			return Xxh64Avalanche(emptyBitFlip);
		}
	}

	private static ulong Hash64Length17To128(ReadOnlySpan<byte> data, ReadOnlySpan<byte> secret, ulong seed)
	{
		unchecked
		{
			int length = data.Length;
			ulong accumulator = (ulong)length * 0x9E3779B185EBCA87UL;

			if (length > 32)
			{
				if (length > 64)
				{
					if (length > 96)
					{
						accumulator += Mix16Bytes(data, 48, secret, 0x60, seed);
						accumulator += Mix16Bytes(data, length - 64, secret, 0x70, seed);
					}
					accumulator += Mix16Bytes(data, 32, secret, 0x40, seed);
					accumulator += Mix16Bytes(data, length - 48, secret, 0x50, seed);
				}
				accumulator += Mix16Bytes(data, 16, secret, 0x20, seed);
				accumulator += Mix16Bytes(data, length - 32, secret, 0x30, seed);
			}

			accumulator += Mix16Bytes(data, 0, secret, 0x00, seed);
			accumulator += Mix16Bytes(data, length - 16, secret, 0x10, seed);
			return Xxh3Avalanche(accumulator);
		}
	}

	private static ulong Mix16Bytes(ReadOnlySpan<byte> data, int dataOffset, ReadOnlySpan<byte> secret, int secretOffset, ulong seed)
	{
		unchecked
		{
			ulong inputLow = ReadUInt64(data, dataOffset);
			ulong inputHigh = ReadUInt64(data, dataOffset + 8);
			return Multiply128Fold64(
				inputLow ^ (ReadUInt64(secret, secretOffset) + seed),
				inputHigh ^ (ReadUInt64(secret, secretOffset + 8) - seed));
		}
	}

	private static ulong Multiply128Fold64(ulong left, ulong right)
	{
		UInt128 product = (UInt128)left * right;
		ulong low = (ulong)(product & ulong.MaxValue);
		ulong high = (ulong)(product >> 64);
		return low ^ high;
	}

	private static ulong Xxh3Avalanche(ulong hash)
	{
		unchecked
		{
			hash ^= hash >> 37;
			hash *= 0x165667919E3779F9UL;
			hash ^= hash >> 32;
			return hash;
		}
	}

	private static ulong Xxh64Avalanche(ulong hash)
	{
		unchecked
		{
			hash ^= hash >> 33;
			hash *= 0xC2B2AE3D27D4EB4FUL;
			hash ^= hash >> 29;
			hash *= 0x165667B19E3779F9UL;
			hash ^= hash >> 32;
			return hash;
		}
	}

	private static ulong Xxh3Rrmxmx(ulong hash, int length)
	{
		unchecked
		{
			hash = hash ^ BitOperations.RotateRight(hash, 15) ^ BitOperations.RotateRight(hash, 40);
			hash *= 0x9FB21C651E98DF25UL;
			hash = ((hash >> 35) + (ulong)length) ^ hash;
			hash *= 0x9FB21C651E98DF25UL;
			hash ^= hash >> 28;
			return hash;
		}
	}

	private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
		BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint)));

	private static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset) =>
		BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, sizeof(ulong)));
}
