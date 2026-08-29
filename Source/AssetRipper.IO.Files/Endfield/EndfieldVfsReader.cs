using System.Buffers.Binary;
using System.Text;

namespace AssetRipper.IO.Files.Endfield;

/// <summary>
/// Known Endfield VFS block types. The bundle values are the ones consumed by the proof-of-concept reader.
/// </summary>
public enum EndfieldVfsBlockType : byte
{
	None = 0,
	InitialAudio = 1,
	InitialBundle = 2,
	InitialExtendData = 3,
	BundleManifest = 4,
	IFixPatch = 5,
	AuditStreaming = 6,
	AuditDynamicStreaming = 7,
	AuditIV = 8,
	AuditAudio = 9,
	AuditVideo = 10,
	Bundle = 11,
	Audio = 12,
	Video = 13,
	IV = 14,
	Streaming = 15,
	DynamicStreaming = 16,
	Lua = 17,
	Table = 18,
	JsonData = 19,
	ExtendData = 20,
	HotfixAudio = 21,
	AudioChinese = 101,
	AudioEnglish = 102,
	AudioJapanese = 103,
	AudioKorean = 104,
	Raw = 255,
}

public sealed record EndfieldVfsFileInfo(
	string Name,
	long Offset,
	long Length,
	EndfieldVfsBlockType BlockType,
	bool IsEncrypted,
	long IvSeed);

public sealed record EndfieldVfsChunkInfo(
	byte[] Md5Name,
	long Length,
	EndfieldVfsBlockType BlockType,
	IReadOnlyList<EndfieldVfsFileInfo> Files)
{
	public string FileName => $"{Convert.ToHexString(Md5Name)}.chk";
}

public sealed record EndfieldVfsBlockInfo(
	string BlockFilePath,
	string DirectoryPath,
	int Version,
	int CodeVersion,
	string GroupConfigName,
	EndfieldVfsBlockType BlockType,
	IReadOnlyList<EndfieldVfsChunkInfo> Chunks);

public sealed record EndfieldVfsExtractedFile(
	string Name,
	string ChunkPath,
	byte[] Data);

/// <summary>
/// Reads Endfield VFS block metadata and extracts virtual files from chunk files.
/// This proof of concept intentionally discovers blocks by enumerating *.blc files instead of
/// reproducing the game's block-directory hash function.
/// </summary>
public static class EndfieldVfsReader
{
	private const int MetadataNonceLength = 12;
	private const int VfsProtocolVersion = 3;
	private const int MaximumEntryCount = 1_000_000;
	private static readonly byte[] ChaChaKey = Convert.FromBase64String("6VsxesT4KFadI6hr8nHctT6Eb6dckk1nHbqOOPTKUuE=");

	/// <summary>
	/// Enumerates and parses all VFS block metadata files beneath <paramref name="vfsDirectory"/>.
	/// </summary>
	public static IEnumerable<EndfieldVfsBlockInfo> EnumerateBlocks(string vfsDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(vfsDirectory);
		if (!Directory.Exists(vfsDirectory))
		{
			throw new DirectoryNotFoundException($"Endfield VFS directory was not found: {vfsDirectory}");
		}

		foreach (string blockFilePath in Directory
			.EnumerateFiles(vfsDirectory, "*.blc", SearchOption.AllDirectories)
			.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
		{
			yield return ReadBlock(blockFilePath);
		}
	}

	/// <summary>
	/// Extracts files from InitialBundle and Bundle blocks. Unity bundle decompression remains the
	/// responsibility of AssetRipper's normal bundle schemes.
	/// </summary>
	public static IEnumerable<EndfieldVfsExtractedFile> ExtractBundleFiles(string vfsDirectory)
	{
		foreach (EndfieldVfsBlockInfo block in EnumerateBlocks(vfsDirectory))
		{
			if (block.BlockType is not (EndfieldVfsBlockType.InitialBundle or EndfieldVfsBlockType.Bundle))
			{
				continue;
			}

			foreach (EndfieldVfsChunkInfo chunk in block.Chunks)
			{
				string chunkPath = Path.Combine(block.DirectoryPath, chunk.FileName);
				foreach (EndfieldVfsFileInfo file in chunk.Files)
				{
					yield return new EndfieldVfsExtractedFile(file.Name, chunkPath, ExtractFile(chunkPath, file));
				}
			}
		}
	}

	/// <summary>
	/// Extracts bundle payloads and immediately feeds them into AssetRipper's standard scheme reader.
	/// </summary>
	public static IEnumerable<FileBase> ReadBundleFiles(string vfsDirectory)
	{
		foreach (EndfieldVfsExtractedFile file in ExtractBundleFiles(vfsDirectory))
		{
			string fileName = GetVirtualFileName(file.Name);
			yield return SchemeReader.ReadFile(file.Data, file.Name, fileName);
		}
	}

	/// <summary>
	/// Decrypts, validates, and parses a single Endfield VFS block metadata file.
	/// </summary>
	public static EndfieldVfsBlockInfo ReadBlock(string blockFilePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(blockFilePath);
		byte[] blockData = File.ReadAllBytes(blockFilePath);
		if (blockData.Length < MetadataNonceLength + sizeof(uint))
		{
			throw new InvalidDataException($"Endfield VFS block is too small: {blockFilePath}");
		}

		ReadOnlySpan<byte> nonce = blockData.AsSpan(0, MetadataNonceLength);
		byte[] decrypted = blockData.AsSpan(MetadataNonceLength).ToArray();
		ChaCha20Xor(decrypted, ChaChaKey, nonce, 1);

		int metadataLength = decrypted.Length - sizeof(uint);
		uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(decrypted.AsSpan(metadataLength));
		uint actualCrc = ComputeCrc32(decrypted.AsSpan(0, metadataLength));
		if (actualCrc != expectedCrc)
		{
			throw new InvalidDataException($"Endfield VFS block CRC32 mismatch for {blockFilePath}: expected 0x{expectedCrc:X8}, got 0x{actualCrc:X8}.");
		}

		string directoryPath = Path.GetDirectoryName(blockFilePath)
			?? throw new InvalidDataException($"Could not determine the VFS block directory for {blockFilePath}.");
		return ParseBlock(blockFilePath, directoryPath, decrypted.AsSpan(0, metadataLength));
	}

	/// <summary>
	/// Extracts a single virtual file from its chunk and applies the per-file VFS cipher when needed.
	/// </summary>
	public static byte[] ExtractFile(string chunkPath, EndfieldVfsFileInfo file)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(chunkPath);
		ArgumentNullException.ThrowIfNull(file);
		if (file.Offset < 0 || file.Length < 0 || file.Length > int.MaxValue)
		{
			throw new InvalidDataException($"Invalid Endfield VFS file range for {file.Name}: offset={file.Offset}, length={file.Length}.");
		}

		using FileStream stream = File.OpenRead(chunkPath);
		if (file.Offset > stream.Length || file.Length > stream.Length - file.Offset)
		{
			throw new InvalidDataException($"Endfield VFS file range exceeds chunk bounds for {file.Name} in {chunkPath}.");
		}

		stream.Position = file.Offset;
		byte[] data = new byte[(int)file.Length];
		stream.ReadExactly(data);

		if (file.IsEncrypted)
		{
			Span<byte> nonce = stackalloc byte[MetadataNonceLength];
			BinaryPrimitives.WriteInt32LittleEndian(nonce[..sizeof(int)], VfsProtocolVersion);
			BinaryPrimitives.WriteInt64LittleEndian(nonce[sizeof(int)..], file.IvSeed);
			ChaCha20Xor(data, ChaChaKey, nonce, 1);
		}

		return data;
	}

	private static EndfieldVfsBlockInfo ParseBlock(string blockFilePath, string directoryPath, ReadOnlySpan<byte> metadata)
	{
		using MemoryStream memoryStream = new(metadata.ToArray(), false);
		using BinaryReader reader = new(memoryStream, Encoding.UTF8, true);

		try
		{
			int rawVersion = reader.ReadInt32();
			int version;
			int codeVersion;
			if (rawVersion < 11)
			{
				version = reader.ReadInt32();
				codeVersion = rawVersion;
			}
			else
			{
				version = rawVersion;
				codeVersion = 3;
			}

			string groupConfigName = ReadUtf8String16(reader);
			_ = reader.ReadInt64(); // GroupConfigHashName
			_ = reader.ReadInt32(); // GroupFileInfoNum
			_ = reader.ReadInt64(); // GroupChunksLength
			EndfieldVfsBlockType blockType = (EndfieldVfsBlockType)reader.ReadByte();
			int chunkCount = ReadCount(reader, "chunk");
			EndfieldVfsChunkInfo[] chunks = new EndfieldVfsChunkInfo[chunkCount];

			for (int chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
			{
				byte[] md5Name = ReadExactBytes(reader, 16);
				_ = ReadExactBytes(reader, 16); // ContentMd5
				long chunkLength = reader.ReadInt64();
				if (chunkLength < 0)
				{
					throw new InvalidDataException($"Negative Endfield VFS chunk length in {blockFilePath}.");
				}
				EndfieldVfsBlockType chunkBlockType = (EndfieldVfsBlockType)reader.ReadByte();
				if (codeVersion > 3)
				{
					_ = reader.ReadInt32(); // MainTag
				}

				int fileCount = ReadCount(reader, "file");
				EndfieldVfsFileInfo[] files = new EndfieldVfsFileInfo[fileCount];
				for (int fileIndex = 0; fileIndex < files.Length; fileIndex++)
				{
					string fileName = ReadUtf8String16(reader);
					_ = reader.ReadInt64(); // FileNameHash
					_ = ReadExactBytes(reader, 16); // FileChunkMd5
					_ = ReadExactBytes(reader, 16); // FileDataMd5
					long offset = reader.ReadInt64();
					long length = reader.ReadInt64();
					EndfieldVfsBlockType fileBlockType = (EndfieldVfsBlockType)reader.ReadByte();
					bool isEncrypted = reader.ReadByte() != 0;
					long ivSeed = isEncrypted ? reader.ReadInt64() : 0;
					if (codeVersion > 3)
					{
						_ = reader.ReadInt32(); // FileTag
					}

					if (offset < 0 || length < 0)
					{
						throw new InvalidDataException($"Negative Endfield VFS file range for {fileName} in {blockFilePath}.");
					}
					files[fileIndex] = new EndfieldVfsFileInfo(fileName, offset, length, fileBlockType, isEncrypted, ivSeed);
				}

				chunks[chunkIndex] = new EndfieldVfsChunkInfo(md5Name, chunkLength, chunkBlockType, files);
			}

			if (memoryStream.Position != memoryStream.Length)
			{
				throw new InvalidDataException($"Unexpected trailing Endfield VFS metadata in {blockFilePath}: {memoryStream.Length - memoryStream.Position} byte(s).");
			}

			return new EndfieldVfsBlockInfo(blockFilePath, directoryPath, version, codeVersion, groupConfigName, blockType, chunks);
		}
		catch (EndOfStreamException exception)
		{
			throw new InvalidDataException($"Truncated Endfield VFS block metadata: {blockFilePath}", exception);
		}
	}

	private static int ReadCount(BinaryReader reader, string description)
	{
		int count = reader.ReadInt32();
		if (count < 0 || count > MaximumEntryCount)
		{
			throw new InvalidDataException($"Invalid Endfield VFS {description} count: {count}.");
		}
		return count;
	}

	private static string ReadUtf8String16(BinaryReader reader)
	{
		int length = reader.ReadUInt16();
		return Encoding.UTF8.GetString(ReadExactBytes(reader, length));
	}

	private static byte[] ReadExactBytes(BinaryReader reader, int length)
	{
		byte[] data = reader.ReadBytes(length);
		if (data.Length != length)
		{
			throw new EndOfStreamException();
		}
		return data;
	}

	private static string GetVirtualFileName(string path)
	{
		int slashIndex = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
		return slashIndex >= 0 ? path[(slashIndex + 1)..] : path;
	}

	private static uint ComputeCrc32(ReadOnlySpan<byte> data)
	{
		const uint polynomial = 0xEDB88320u;
		uint crc = uint.MaxValue;
		foreach (byte value in data)
		{
			crc ^= value;
			for (int bit = 0; bit < 8; bit++)
			{
				crc = (crc & 1) != 0 ? (crc >> 1) ^ polynomial : crc >> 1;
			}
		}
		return ~crc;
	}

	private static void ChaCha20Xor(Span<byte> data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter)
	{
		if (key.Length != 32)
		{
			throw new ArgumentException("ChaCha20 requires a 32-byte key.", nameof(key));
		}
		if (nonce.Length != MetadataNonceLength)
		{
			throw new ArgumentException("Endfield ChaCha20 requires a 12-byte nonce.", nameof(nonce));
		}

		Span<uint> state = stackalloc uint[16];
		state[0] = 0x61707865;
		state[1] = 0x3320646E;
		state[2] = 0x79622D32;
		state[3] = 0x6B206574;
		for (int i = 0; i < 8; i++)
		{
			state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * sizeof(uint), sizeof(uint)));
		}
		state[13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[..4]);
		state[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[4..8]);
		state[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[8..12]);

		Span<uint> working = stackalloc uint[16];
		Span<byte> keyStream = stackalloc byte[64];
		int offset = 0;
		while (offset < data.Length)
		{
			state[12] = counter;
			state.CopyTo(working);
			for (int round = 0; round < 10; round++)
			{
				QuarterRound(working, 0, 4, 8, 12);
				QuarterRound(working, 1, 5, 9, 13);
				QuarterRound(working, 2, 6, 10, 14);
				QuarterRound(working, 3, 7, 11, 15);
				QuarterRound(working, 0, 5, 10, 15);
				QuarterRound(working, 1, 6, 11, 12);
				QuarterRound(working, 2, 7, 8, 13);
				QuarterRound(working, 3, 4, 9, 14);
			}

			for (int i = 0; i < 16; i++)
			{
				uint word = unchecked(working[i] + state[i]);
				BinaryPrimitives.WriteUInt32LittleEndian(keyStream.Slice(i * sizeof(uint), sizeof(uint)), word);
			}

			int blockLength = Math.Min(keyStream.Length, data.Length - offset);
			for (int i = 0; i < blockLength; i++)
			{
				data[offset + i] ^= keyStream[i];
			}
			offset += blockLength;
			counter = unchecked(counter + 1);
		}
	}

	private static void QuarterRound(Span<uint> state, int a, int b, int c, int d)
	{
		state[a] = unchecked(state[a] + state[b]);
		state[d] ^= state[a];
		state[d] = RotateLeft(state[d], 16);

		state[c] = unchecked(state[c] + state[d]);
		state[b] ^= state[c];
		state[b] = RotateLeft(state[b], 12);

		state[a] = unchecked(state[a] + state[b]);
		state[d] ^= state[a];
		state[d] = RotateLeft(state[d], 8);

		state[c] = unchecked(state[c] + state[d]);
		state[b] ^= state[c];
		state[b] = RotateLeft(state[b], 7);
	}

	private static uint RotateLeft(uint value, int count) => (value << count) | (value >> (32 - count));
}
