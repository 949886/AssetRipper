using System.Buffers.Binary;

namespace AssetRipper.IO.Files.Endfield;

/// <summary>
/// Seekable, read-only view over one Endfield VFS file entry. Data is read directly from the
/// backing chunk and decrypted only for the requested range.
/// </summary>
internal sealed class EndfieldVfsEntryStream : Stream
{
	private const int VfsProtocolVersion = 3;
	private const int ChaChaBlockSize = 64;
	private static readonly byte[] ChaChaKey = Convert.FromBase64String("6VsxesT4KFadI6hr8nHctT6Eb6dckk1nHbqOOPTKUuE=");

	private readonly FileStream m_chunkStream;
	private readonly EndfieldVfsFileInfo m_file;
	private readonly long m_sliceOffset;
	private readonly long m_length;
	private long m_position;

	public EndfieldVfsEntryStream(string chunkPath, EndfieldVfsFileInfo file)
		: this(chunkPath, file, 0, file.Length)
	{
	}

	private EndfieldVfsEntryStream(string chunkPath, EndfieldVfsFileInfo file, long sliceOffset, long length)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(chunkPath);
		ArgumentNullException.ThrowIfNull(file);
		ArgumentOutOfRangeException.ThrowIfNegative(sliceOffset);
		ArgumentOutOfRangeException.ThrowIfNegative(length);
		if (sliceOffset > file.Length || length > file.Length - sliceOffset)
		{
			throw new ArgumentOutOfRangeException(nameof(length), "The requested slice exceeds the Endfield VFS entry bounds.");
		}

		m_file = file;
		m_sliceOffset = sliceOffset;
		m_length = length;
		m_chunkStream = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
		long physicalOffset = checked(file.Offset + sliceOffset);
		if (physicalOffset > m_chunkStream.Length || length > m_chunkStream.Length - physicalOffset)
		{
			m_chunkStream.Dispose();
			throw new InvalidDataException($"Endfield VFS file range exceeds chunk bounds for {file.Name} in {chunkPath}.");
		}
	}

	public EndfieldVfsEntryStream CreatePartial(long offset, long size)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(offset);
		ArgumentOutOfRangeException.ThrowIfNegative(size);
		if (offset > Length || size > Length - offset)
		{
			throw new ArgumentOutOfRangeException(nameof(size), "The requested partial stream exceeds the current stream bounds.");
		}
		return new EndfieldVfsEntryStream(m_chunkStream.Name, m_file, checked(m_sliceOffset + offset), size);
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		ArgumentNullException.ThrowIfNull(buffer);
		ArgumentOutOfRangeException.ThrowIfNegative(offset);
		ArgumentOutOfRangeException.ThrowIfNegative(count);
		if (offset > buffer.Length - count)
		{
			throw new ArgumentException("Offset and count exceed the destination buffer.");
		}
		return Read(buffer.AsSpan(offset, count));
	}

	public override int Read(Span<byte> buffer)
	{
		long remaining = m_length - m_position;
		if (remaining <= 0 || buffer.IsEmpty)
		{
			return 0;
		}

		int requested = (int)Math.Min(buffer.Length, remaining);
		long logicalEntryOffset = checked(m_sliceOffset + m_position);
		m_chunkStream.Position = checked(m_file.Offset + logicalEntryOffset);
		int read = m_chunkStream.Read(buffer[..requested]);
		if (read > 0 && m_file.IsEncrypted)
		{
			Span<byte> nonce = stackalloc byte[12];
			BinaryPrimitives.WriteInt32LittleEndian(nonce[..4], VfsProtocolVersion);
			BinaryPrimitives.WriteInt64LittleEndian(nonce[4..], m_file.IvSeed);
			ApplyChaCha20AtOffset(buffer[..read], nonce, logicalEntryOffset);
		}
		m_position += read;
		return read;
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		long position = origin switch
		{
			SeekOrigin.Begin => offset,
			SeekOrigin.Current => checked(m_position + offset),
			SeekOrigin.End => checked(m_length + offset),
			_ => throw new ArgumentOutOfRangeException(nameof(origin)),
		};
		if (position < 0)
		{
			throw new IOException("Cannot seek before the beginning of an Endfield VFS entry.");
		}
		m_position = position;
		return m_position;
	}

	private static void ApplyChaCha20AtOffset(Span<byte> data, ReadOnlySpan<byte> nonce, long streamOffset)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(streamOffset);
		ulong blockIndex = (ulong)streamOffset / ChaChaBlockSize;
		int blockSkip = (int)((ulong)streamOffset % ChaChaBlockSize);

		Span<uint> state = stackalloc uint[16];
		state[0] = 0x61707865;
		state[1] = 0x3320646E;
		state[2] = 0x79622D32;
		state[3] = 0x6B206574;
		for (int i = 0; i < 8; i++)
		{
			state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(ChaChaKey.AsSpan(i * sizeof(uint), sizeof(uint)));
		}

		ulong counter = blockIndex + 1;
		state[12] = (uint)counter;
		state[13] = unchecked(BinaryPrimitives.ReadUInt32LittleEndian(nonce[..4]) + (uint)(counter >> 32));
		state[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[4..8]);
		state[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[8..12]);

		Span<uint> working = stackalloc uint[16];
		Span<byte> keyStream = stackalloc byte[ChaChaBlockSize];
		int dataOffset = 0;
		while (dataOffset < data.Length)
		{
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
				BinaryPrimitives.WriteUInt32LittleEndian(keyStream.Slice(i * sizeof(uint), sizeof(uint)), unchecked(working[i] + state[i]));
			}

			int available = ChaChaBlockSize - blockSkip;
			int count = Math.Min(available, data.Length - dataOffset);
			for (int i = 0; i < count; i++)
			{
				data[dataOffset + i] ^= keyStream[blockSkip + i];
			}
			dataOffset += count;
			blockSkip = 0;

			unchecked
			{
				state[12]++;
				if (state[12] == 0)
				{
					state[13]++;
				}
			}
		}
	}

	private static void QuarterRound(Span<uint> state, int a, int b, int c, int d)
	{
		unchecked
		{
			state[a] += state[b]; state[d] = RotateLeft(state[d] ^ state[a], 16);
			state[c] += state[d]; state[b] = RotateLeft(state[b] ^ state[c], 12);
			state[a] += state[b]; state[d] = RotateLeft(state[d] ^ state[a], 8);
			state[c] += state[d]; state[b] = RotateLeft(state[b] ^ state[c], 7);
		}
	}

	private static uint RotateLeft(uint value, int count) => (value << count) | (value >> (32 - count));

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			m_chunkStream.Dispose();
		}
		base.Dispose(disposing);
	}

	public override bool CanRead => true;
	public override bool CanSeek => true;
	public override bool CanWrite => false;
	public override long Length => m_length;
	public override long Position
	{
		get => m_position;
		set
		{
			ArgumentOutOfRangeException.ThrowIfNegative(value);
			m_position = value;
		}
	}
	public override void Flush() { }
	public override void SetLength(long value) => throw new NotSupportedException();
	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
