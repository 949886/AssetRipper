using AssetRipper.Import.Platforms;
using AssetRipper.Import.Structure.Platforms;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.Endfield;
using NUnit.Framework;

namespace AssetRipper.Tests;

internal sealed class EndfieldWindowsPlatformTests
{
	private const string BlockFixtureBase64 = "EBESExQVFhcYGRob1OtPf6baNBQ1+GFlzh9xQcsNBYy53gxLYm7HitDFpS/Ct9Av2gO14+7kYdB4IFcghCvbrly0hps6oRJg/NZ0/GGMmmNmVesmYkK69WmsLUTJa7yZ+2qUEwMZFD6R1KkAN1jpdvBE3+ob2JQeNaXJnKXQkav0QrcGQlPZLhTOoJUwEs6yySfAkq3OMuLvHP3mMMK29754ngImFGLeRab7xB+wcC8OmTdVUiuyTXwZIoMx1mC556BCJgzvl3L9Yqz20nMl3igSuQTOIVuj3G3TBWfGXaYTa3RD0ffPBbUTBxRKDkzYouyU21Uc8sWp8SBrn5VD+Aa+48ojhSjcjy/qHQ==";
	private const string ChunkFixtureBase64 = "VW5pdHlGUy1wbGFpbi1maXh0dXJlDOsJxGEH5mSYZHnNSYQyVfjzQoMLSgGXKQ==";
	private const string ChunkFileName = "000102030405060708090A0B0C0D0E0F.chk";

	[Test]
	public void WindowsPlatformDetectionAutomaticallyMountsEndfieldVfs()
	{
		string root = Path.Combine(Path.GetTempPath(), "AssetRipper-EndfieldWindowsTests", Guid.NewGuid().ToString("N"));
		string gameDataPath = Path.Combine(root, "Endfield_Data");
		string blockPath = Path.Combine(gameDataPath, "StreamingAssets", "VFS", "DEADBEEF");
		Directory.CreateDirectory(blockPath);
		File.WriteAllBytes(Path.Combine(root, "Endfield.exe"), []);
		File.WriteAllBytes(Path.Combine(blockPath, "DEADBEEF.blc"), Convert.FromBase64String(BlockFixtureBase64));
		File.WriteAllBytes(Path.Combine(blockPath, ChunkFileName), Convert.FromBase64String(ChunkFixtureBase64));

		try
		{
			List<string> paths = [root];
			bool detected = PlatformChecker.CheckPlatform(paths, LocalFileSystem.Instance, out PlatformGameStructure? platform, out _);

			Assert.That(detected, Is.True);
			Assert.That(platform, Is.Not.Null);
			Assert.That(platform!.GameDataPath, Is.EqualTo(gameDataPath));
			Assert.That(platform.FileSystem, Is.TypeOf<EndfieldOverlayFileSystem>());
			Assert.That(((EndfieldOverlayFileSystem)platform.FileSystem).MountedFileCount, Is.EqualTo(2));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}
}
