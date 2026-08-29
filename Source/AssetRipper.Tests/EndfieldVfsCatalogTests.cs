using AssetRipper.IO.Files.Endfield;
using NUnit.Framework;

namespace AssetRipper.Tests;

internal sealed class EndfieldVfsCatalogTests
{
	private const string BlockFixtureBase64 = "MDEyMzQ1Njc4OTo7vRpkwwgM9vPngEjslX81Re68JYbbWsSv0+s9CZ/nU10L/ZiIhP77Yrbdk284PlPxxdHd7HJJiWUMY8HEEF+aVJ2Myiiv0kX6SFQ+Ff4HoX135EZHrx/OdsQfLXCH+BwMXnOAyZb9XTi+5+6458EIvQ0IUh6ogHPX8LbqDEAvtsC1yUV2OKbA1sEBkd8SirbWB8u2ZU8mXqp9YVNsVtzEv/Bl3b+WjwCR3O/6Jl8kUWsGMfolC1Wn/ZbI";
	private const string ChunkFixtureBase64 = "UlNDQ0hVTktyZXNvdXJjZS1ieXRlcy1mb3ItcmVzcw==";
	private const string ChunkFileName = "101112131415161718191A1B1C1D1E1F.chk";

	[Test]
	public void CatalogOpensExternalResourceByBasenameWithoutMaterializingIt()
	{
		string root = Path.Combine(Path.GetTempPath(), "AssetRipper-EndfieldCatalogTests", Guid.NewGuid().ToString("N"));
		string gameDataPath = Path.Combine(root, "Endfield_Data");
		string blockDirectoryName = EndfieldVfsHash.GetBlockDirectoryName(EndfieldVfsBlockType.Streaming);
		string blockPath = Path.Combine(gameDataPath, "StreamingAssets", "VFS", blockDirectoryName);
		Directory.CreateDirectory(blockPath);
		File.WriteAllBytes(Path.Combine(blockPath, $"{blockDirectoryName}.blc"), Convert.FromBase64String(BlockFixtureBase64));
		File.WriteAllBytes(Path.Combine(blockPath, ChunkFileName), Convert.FromBase64String(ChunkFixtureBase64));

		try
		{
			Assert.That(EndfieldVfsCatalog.TryGet(gameDataPath, out EndfieldVfsCatalog? catalog), Is.True);
			Assert.That(catalog, Is.Not.Null);
			Assert.That(catalog!.TryOpenFile("shared.resS", out Stream? stream, out string? virtualPath), Is.True);
			Assert.That(virtualPath, Is.EqualTo("endfield-vfs://assets/resources/shared.resS"));
			using (stream)
			{
				Assert.That(stream, Is.Not.TypeOf<MemoryStream>());
				byte[] data = new byte[stream!.Length];
				stream.ReadExactly(data);
				Assert.That(data, Is.EqualTo("resource-bytes-for-ress"u8.ToArray()));
			}

			Assert.That(catalog.TryOpenFile("assets/resources/shared.resS", out Stream? fullPathStream, out _), Is.True);
			fullPathStream!.Dispose();
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}
}
