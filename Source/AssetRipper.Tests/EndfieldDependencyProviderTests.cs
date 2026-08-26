using AssetRipper.Assets.Bundles;
using AssetRipper.Import.Platforms;
using AssetRipper.Import.Structure;
using AssetRipper.Import.Structure.Platforms;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.ResourceFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using NUnit.Framework;

namespace AssetRipper.Tests;

internal sealed class EndfieldDependencyProviderTests
{
	private const string BlockFixtureBase64 = "MDEyMzQ1Njc4OTo7vRpkwwgM9vPngEjslX81Re68JYbbWsSv0+s9CZ/nU10L/ZiIhP77Yrbdk284PlPxxdHd7HJJiWUMY8HEEF+aVJ2Myiiv0kX6SFQ+Ff4HoX135EZHrx/OdsQfLXCH+BwMXnOAyZb9XTi+5+6458EIvQ0IUh6ogHPX8LbqDEAvtsC1yUV2OKbA1sEBkd8SirbWB8u2ZU8mXqp9YVNsVtzEv/Bl3b+WjwCR3O/6Jl8kUWsGMfolC1Wn/ZbI";
	private const string ChunkFixtureBase64 = "UlNDQ0hVTktyZXNvdXJjZS1ieXRlcy1mb3ItcmVzcw==";
	private const string ChunkFileName = "101112131415161718191A1B1C1D1E1F.chk";

	[Test]
	public void DependencyProviderFallsBackToEndfieldVfsCatalog()
	{
		string root = Path.Combine(Path.GetTempPath(), "AssetRipper-EndfieldDependencyTests", Guid.NewGuid().ToString("N"));
		string gameDataPath = Path.Combine(root, "Endfield_Data");
		string blockPath = Path.Combine(gameDataPath, "StreamingAssets", "VFS", "CAFEBABE");
		Directory.CreateDirectory(blockPath);
		File.WriteAllBytes(Path.Combine(root, "Endfield.exe"), []);
		File.WriteAllBytes(Path.Combine(blockPath, "CAFEBABE.blc"), Convert.FromBase64String(BlockFixtureBase64));
		File.WriteAllBytes(Path.Combine(blockPath, ChunkFileName), Convert.FromBase64String(ChunkFixtureBase64));

		try
		{
			List<string> paths = [root];
			Assert.That(PlatformChecker.CheckPlatform(paths, LocalFileSystem.Instance, out PlatformGameStructure? platform, out _), Is.True);
			Assert.That(platform, Is.Not.Null);

			GameInitializer initializer = new(platform, null, platform!.FileSystem, default, default);
			IDependencyProvider dependencyProvider = initializer.DependencyProvider!;
			FileIdentifier identifier = new()
			{
				PathName = "shared.resS",
				PathNameOrigin = "shared.resS",
			};

			using FileBase? dependency = dependencyProvider.FindDependency(identifier);
			Assert.That(dependency, Is.TypeOf<ResourceFile>());
			Assert.That(dependency!.FilePath, Is.EqualTo("endfield-vfs://assets/resources/shared.resS"));
			Assert.That(dependency.ToByteArray(), Is.EqualTo("resource-bytes-for-ress"u8.ToArray()));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}
}
