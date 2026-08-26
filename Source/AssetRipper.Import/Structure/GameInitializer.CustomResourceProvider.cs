using AssetRipper.Assets.Bundles;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Platforms;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.Endfield;
using AssetRipper.IO.Files.ResourceFiles;
using AssetRipper.IO.Files.Streams.Smart;

namespace AssetRipper.Import.Structure;

internal sealed partial record class GameInitializer
{
	private sealed record class CustomResourceProvider(
		PlatformGameStructure? PlatformStructure,
		PlatformGameStructure? MixedStructure,
		FileSystem FileSystem)
		: IResourceProvider
	{
		public ResourceFile? FindResource(string resName)
		{
			string fixedName = SpecialFileNames.FixResourcePath(resName);
			string? resPath = RequestResource(fixedName);
			if (resPath is not null)
			{
				ResourceFile resourceFile = new ResourceFile(resPath, fixedName, FileSystem);
				Logger.Info(LogCategory.Import, $"Resource file '{resName}' has been loaded");
				return resourceFile;
			}

			if (TryOpenEndfieldResource(PlatformStructure, fixedName, out ResourceFile? endfieldResource)
				|| TryOpenEndfieldResource(MixedStructure, fixedName, out endfieldResource))
			{
				Logger.Info(LogCategory.Import, $"Resource file '{resName}' has been loaded from Endfield VFS");
				return endfieldResource;
			}

			Logger.Log(LogType.Warning, LogCategory.Import, $"Resource file '{resName}' hasn't been found");
			return null;
		}

		private static bool TryOpenEndfieldResource(
			PlatformGameStructure? structure,
			string resourceName,
			[NotNullWhen(true)] out ResourceFile? resourceFile)
		{
			if (structure?.GameDataPath is not string gameDataPath
				|| !EndfieldVfsCatalog.TryGet(gameDataPath, out EndfieldVfsCatalog? catalog)
				|| !catalog.TryOpenFile(resourceName, out Stream? stream, out string? virtualPath))
			{
				resourceFile = null;
				return false;
			}

			using SmartStream smartStream = SmartStream.Create(stream);
			resourceFile = new ResourceFile(smartStream, virtualPath, resourceName);
			return true;
		}

		private string? RequestResource(string resource)
		{
			return PlatformStructure?.RequestResource(resource) ?? MixedStructure?.RequestResource(resource);
		}
	}
}
