using AssetRipper.Import.Platforms;
using AssetRipper.Import.Structure.Platforms;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.Endfield;
using NUnit.Framework;

namespace AssetRipper.Tests;

internal sealed class EndfieldWindowsPlatformTests
{
	private const string BlockFixtureBase64 = "ICEiIyQlJicoKSorDY2Hww9SpfQM7mUm4ecvCZ5s31DWX+MFJ28U4JlJTl08gNgzAsM//gK15uZ0mj5FQoQSxc9fitLthIEdWoIIakQcoLjhHdwkhyr+QJIoDswZ94OiEQVXQUflxE1MRozzVnE8cS1UzKtClgJAKIXyVg/ldaimn1YiGJOWIZZ7QhFzndAJMThNKDJorxPfur+cbyffgDLIhDNxQOx7OHIcQ0crm7YFW6E9wHU=";
	private const string ChunkFixtureBase64 = "RU5EVkZTQ0hVTktEQVRBIVVuaXR5RlMAAQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";
	private const string ChunkFileName = "000102030405060708090A0B0C0D0E0F.chk";

	[Test]
	public void WindowsPlatformDetectionAutomaticallyCollectsEndfieldVfsBundle()
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
			EndfieldOverlayFileSystem overlay = (EndfieldOverlayFileSystem)platform.FileSystem;
			Assert.That(overlay.MountedFileCount, Is.EqualTo(1));

			platform.CollectFiles(false);

			Assert.That(
				platform.Files.Any(pair => pair.Value.StartsWith(overlay.VirtualRootPath, StringComparison.OrdinalIgnoreCase)),
				Is.True,
				"CollectStreamingAssets should discover the virtual Endfield bundle through BundleHeader.IsBundleHeader.");
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}
}
