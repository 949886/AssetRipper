using AssetRipper.IO.Files.Endfield;
using NUnit.Framework;

namespace AssetRipper.Tests;

internal sealed class EndfieldVfsReaderTests
{
	private const string BlockFixtureBase64 = "EBESExQVFhcYGRob1OtPf6baNBQ1+GFlzh9xQcsNBYy53gxLYm7HitDFpS/Ct9Av2gO14+7kYdB4IFcghCvbrly0hps6oRJg/NZ0/GGMmmNmVesmYkK69WmsLUTJa7yZ+2qUEwMZFD6R1KkAN1jpdvBE3+ob2JQeNaXJnKXQkav0QrcGQlPZLhTOoJUwEs6yySfAkq3OMuLvHP3mMMK29754ngImFGLeRab7xB+wcC8OmTdVUiuyTXwZIoMx1mC556BCJgzvl3L9Yqz20nMl3igSuQTOIVuj3G3TBWfGXaYTa3RD0ffPBbUTBxRKDkzYouyU21Uc8sWp8SBrn5VD+Aa+48ojhSjcjy/qHQ==";
	private const string ChunkFixtureBase64 = "VW5pdHlGUy1wbGFpbi1maXh0dXJlDOsJxGEH5mSYZHnNSYQyVfjzQoMLSgGXKQ==";
	private const string ChunkFileName = "000102030405060708090A0B0C0D0E0F.chk";

	[Test]
	public void ReadBlockParsesSyntheticBundleMetadata()
	{
		string root = CreateFixtureDirectory(out string vfsDirectory, out string blockFilePath);
		try
		{
			EndfieldVfsBlockInfo block = EndfieldVfsReader.ReadBlock(blockFilePath);

			Assert.That(block.Version, Is.EqualTo(123));
			Assert.That(block.CodeVersion, Is.EqualTo(3));
			Assert.That(block.GroupConfigName, Is.EqualTo("synthetic-bundle"));
			Assert.That(block.BlockType, Is.EqualTo(EndfieldVfsBlockType.Bundle));
			Assert.That(block.Chunks, Has.Count.EqualTo(1));
			Assert.That(block.Chunks[0].FileName, Is.EqualTo(ChunkFileName));
			Assert.That(block.Chunks[0].Files, Has.Count.EqualTo(2));
			Assert.That(block.Chunks[0].Files[0].IsEncrypted, Is.False);
			Assert.That(block.Chunks[0].Files[1].IsEncrypted, Is.True);
			Assert.That(EndfieldVfsReader.EnumerateBlocks(vfsDirectory).Single().GroupConfigName, Is.EqualTo("synthetic-bundle"));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Test]
	public void ExtractBundleFilesHandlesPlainAndEncryptedRanges()
	{
		string root = CreateFixtureDirectory(out string vfsDirectory, out _);
		try
		{
			EndfieldVfsExtractedFile[] files = EndfieldVfsReader.ExtractBundleFiles(vfsDirectory).ToArray();

			Assert.That(files, Has.Length.EqualTo(2));
			Assert.That(files[0].Name, Is.EqualTo("plain.bundle"));
			Assert.That(files[0].Data, Is.EqualTo("UnityFS-plain-fixture"u8.ToArray()));
			Assert.That(files[1].Name, Is.EqualTo("encrypted.bundle"));
			Assert.That(files[1].Data, Is.EqualTo("UnityFS-encrypted-fixture"u8.ToArray()));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Test]
	public void ReadBlockRejectsCorruptedMetadata()
	{
		string root = CreateFixtureDirectory(out _, out string blockFilePath);
		try
		{
			byte[] blockData = File.ReadAllBytes(blockFilePath);
			blockData[^1] ^= 0x01;
			File.WriteAllBytes(blockFilePath, blockData);

			Assert.Throws<InvalidDataException>(() => EndfieldVfsReader.ReadBlock(blockFilePath));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	private static string CreateFixtureDirectory(out string vfsDirectory, out string blockFilePath)
	{
		string root = Path.Combine(Path.GetTempPath(), "AssetRipper-EndfieldVfsReaderTests", Guid.NewGuid().ToString("N"));
		vfsDirectory = Path.Combine(root, "VFS");
		string blockDirectory = Path.Combine(vfsDirectory, "DEADBEEF");
		Directory.CreateDirectory(blockDirectory);

		blockFilePath = Path.Combine(blockDirectory, "DEADBEEF.blc");
		File.WriteAllBytes(blockFilePath, Convert.FromBase64String(BlockFixtureBase64));
		File.WriteAllBytes(Path.Combine(blockDirectory, ChunkFileName), Convert.FromBase64String(ChunkFixtureBase64));
		return root;
	}
}
