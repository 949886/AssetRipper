using AssetRipper.IO.Files;
using AssetRipper.IO.Files.Endfield;
using AssetRipper.IO.Files.Streams.Smart;
using NUnit.Framework;

namespace AssetRipper.Tests;

internal sealed class EndfieldLazyStreamTests
{
	private const string BlockFixtureBase64 = "EBESExQVFhcYGRob1OtPf6baNBQ1+GFlzh9xQcsNBYy53gxLYm7HitDFpS/Ct9Av2gO14+7kYdB4IFcghCvbrly0hps6oRJg/NZ0/GGMmmNmVesmYkK69WmsLUTJa7yZ+2qUEwMZFD6R1KkAN1jpdvBE3+ob2JQeNaXJnKXQkav0QrcGQlPZLhTOoJUwEs6yySfAkq3OMuLvHP3mMMK29754ngImFGLeRab7xB+wcC8OmTdVUiuyTXwZIoMx1mC556BCJgzvl3L9Yqz20nMl3igSuQTOIVuj3G3TBWfGXaYTa3RD0ffPBbUTBxRKDkzYouyU21Uc8sWp8SBrn5VD+Aa+48ojhSjcjy/qHQ==";
	private const string ChunkFixtureBase64 = "VW5pdHlGUy1wbGFpbi1maXh0dXJlDOsJxGEH5mSYZHnNSYQyVfjzQoMLSgGXKQ==";
	private const string ChunkFileName = "000102030405060708090A0B0C0D0E0F.chk";

	[Test]
	public void EncryptedVirtualFileSupportsRandomSeek()
	{
		string root = CreateOverlay(out EndfieldOverlayFileSystem overlay, out string encryptedFile);
		try
		{
			using Stream stream = overlay.File.OpenRead(encryptedFile);
			Assert.That(stream, Is.Not.TypeOf<MemoryStream>());
			Assert.That(stream.Length, Is.EqualTo("UnityFS-encrypted-fixture"u8.Length));

			stream.Seek(8, SeekOrigin.Begin);
			byte[] buffer = new byte[9];
			stream.ReadExactly(buffer);
			Assert.That(buffer, Is.EqualTo("encrypted"u8.ToArray()));

			stream.Position = 0;
			byte[] complete = new byte[stream.Length];
			stream.ReadExactly(complete);
			Assert.That(complete, Is.EqualTo("UnityFS-encrypted-fixture"u8.ToArray()));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Test]
	public void SmartStreamPartialUsesLazySlice()
	{
		string root = CreateOverlay(out EndfieldOverlayFileSystem overlay, out string encryptedFile);
		try
		{
			using SmartStream stream = SmartStream.OpenRead(encryptedFile, overlay);
			Assert.That(stream.StreamType, Is.EqualTo(SmartStreamType.File));
			using SmartStream partial = stream.CreatePartial(8, 9);
			Assert.That(partial.StreamType, Is.EqualTo(SmartStreamType.File));
			Assert.That(partial.ToArray(), Is.EqualTo("encrypted"u8.ToArray()));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	private static string CreateOverlay(out EndfieldOverlayFileSystem overlay, out string encryptedFile)
	{
		string root = Path.Combine(Path.GetTempPath(), "AssetRipper-EndfieldLazyStreamTests", Guid.NewGuid().ToString("N"));
		string gameDataPath = Path.Combine(root, "Endfield_Data");
		string blockDirectoryName = EndfieldVfsHash.GetBlockDirectoryName(EndfieldVfsBlockType.Bundle);
		string blockPath = Path.Combine(gameDataPath, "StreamingAssets", "VFS", blockDirectoryName);
		Directory.CreateDirectory(blockPath);
		File.WriteAllBytes(Path.Combine(blockPath, $"{blockDirectoryName}.blc"), Convert.FromBase64String(BlockFixtureBase64));
		File.WriteAllBytes(Path.Combine(blockPath, ChunkFileName), Convert.FromBase64String(ChunkFixtureBase64));

		if (!EndfieldOverlayFileSystem.TryCreate(LocalFileSystem.Instance, gameDataPath, out EndfieldOverlayFileSystem? createdOverlay))
		{
			throw new InvalidOperationException("Synthetic Endfield VFS overlay could not be created.");
		}
		overlay = createdOverlay;
		string[] virtualFiles = overlay.Directory.GetFiles(overlay.VirtualRootPath);
		if (virtualFiles.Length != 2)
		{
			throw new InvalidOperationException($"Expected two synthetic VFS files, got {virtualFiles.Length}.");
		}
		encryptedFile = virtualFiles[1];
		return root;
	}
}
