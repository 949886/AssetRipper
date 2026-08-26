using AssetRipper.IO.Files;
using AssetRipper.IO.Files.Endfield;
using NUnit.Framework;

namespace AssetRipper.Tests;

internal sealed class EndfieldOverlayFileSystemTests
{
	private const string BlockFixtureBase64 = "EBESExQVFhcYGRob1OtPf6baNBQ1+GFlzh9xQcsNBYy53gxLYm7HitDFpS/Ct9Av2gO14+7kYdB4IFcghCvbrly0hps6oRJg/NZ0/GGMmmNmVesmYkK69WmsLUTJa7yZ+2qUEwMZFD6R1KkAN1jpdvBE3+ob2JQeNaXJnKXQkav0QrcGQlPZLhTOoJUwEs6yySfAkq3OMuLvHP3mMMK29754ngImFGLeRab7xB+wcC8OmTdVUiuyTXwZIoMx1mC556BCJgzvl3L9Yqz20nMl3igSuQTOIVuj3G3TBWfGXaYTa3RD0ffPBbUTBxRKDkzYouyU21Uc8sWp8SBrn5VD+Aa+48ojhSjcjy/qHQ==";
	private const string ChunkFixtureBase64 = "VW5pdHlGUy1wbGFpbi1maXh0dXJlDOsJxGEH5mSYZHnNSYQyVfjzQoMLSgGXKQ==";
	private const string ChunkFileName = "000102030405060708090A0B0C0D0E0F.chk";

	[Test]
	public void OverlayMountsBundleFilesAndUsesPersistentChunkFallback()
	{
		string root = Path.Combine(Path.GetTempPath(), "AssetRipper-EndfieldOverlayTests", Guid.NewGuid().ToString("N"));
		string gameDataPath = Path.Combine(root, "Endfield_Data");
		string streamingAssetsPath = Path.Combine(gameDataPath, "StreamingAssets");
		string blockDirectoryName = EndfieldVfsHash.GetBlockDirectoryName(EndfieldVfsBlockType.Bundle);
		string streamingBlockPath = Path.Combine(streamingAssetsPath, "VFS", blockDirectoryName);
		string persistentBlockPath = Path.Combine(gameDataPath, "Persistent", "VFS", blockDirectoryName);
		Directory.CreateDirectory(streamingBlockPath);
		Directory.CreateDirectory(persistentBlockPath);
		File.WriteAllBytes(Path.Combine(streamingBlockPath, $"{blockDirectoryName}.blc"), Convert.FromBase64String(BlockFixtureBase64));
		File.WriteAllBytes(Path.Combine(persistentBlockPath, ChunkFileName), Convert.FromBase64String(ChunkFixtureBase64));

		try
		{
			bool created = EndfieldOverlayFileSystem.TryCreate(LocalFileSystem.Instance, gameDataPath, out EndfieldOverlayFileSystem? overlay);

			Assert.That(created, Is.True);
			Assert.That(overlay, Is.Not.Null);
			Assert.That(overlay!.MountedFileCount, Is.EqualTo(2));
			Assert.That(overlay.Directory.GetDirectories(streamingAssetsPath), Does.Contain(overlay.VirtualRootPath));

			string[] virtualFiles = overlay.Directory.GetFiles(overlay.VirtualRootPath);
			Assert.That(virtualFiles, Has.Length.EqualTo(2));
			Assert.That(overlay.File.Exists(virtualFiles[0]), Is.True);
			Assert.That(overlay.File.Exists(virtualFiles[1]), Is.True);
			Assert.That(overlay.File.ReadAllBytes(virtualFiles[0]), Is.EqualTo("UnityFS-plain-fixture"u8.ToArray()));
			Assert.That(overlay.File.ReadAllBytes(virtualFiles[1]), Is.EqualTo("UnityFS-encrypted-fixture"u8.ToArray()));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Test]
	public void OverlayDoesNotEnumerateUnrelatedBlockDirectories()
	{
		string root = Path.Combine(Path.GetTempPath(), "AssetRipper-EndfieldOverlayTests", Guid.NewGuid().ToString("N"));
		string gameDataPath = Path.Combine(root, "Endfield_Data");
		string unrelatedBlockPath = Path.Combine(gameDataPath, "StreamingAssets", "VFS", "DEADBEEF");
		Directory.CreateDirectory(unrelatedBlockPath);
		File.WriteAllBytes(Path.Combine(unrelatedBlockPath, "DEADBEEF.blc"), Convert.FromBase64String(BlockFixtureBase64));
		File.WriteAllBytes(Path.Combine(unrelatedBlockPath, ChunkFileName), Convert.FromBase64String(ChunkFixtureBase64));
		try
		{
			Assert.That(EndfieldOverlayFileSystem.TryCreate(LocalFileSystem.Instance, gameDataPath, out _), Is.False);
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Test]
	public void TryCreateReturnsFalseWithoutStreamingVfs()
	{
		string root = Path.Combine(Path.GetTempPath(), "AssetRipper-EndfieldOverlayTests", Guid.NewGuid().ToString("N"));
		string gameDataPath = Path.Combine(root, "Endfield_Data");
		Directory.CreateDirectory(Path.Combine(gameDataPath, "StreamingAssets"));
		try
		{
			Assert.That(EndfieldOverlayFileSystem.TryCreate(LocalFileSystem.Instance, gameDataPath, out _), Is.False);
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}
}
