using AssetRipper.Assets.Bundles;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Platforms;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.Endfield;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files.Streams.Smart;

namespace AssetRipper.Import.Structure;

internal sealed partial record class GameInitializer
{
	private sealed record class StructureDependencyProvider(
		PlatformGameStructure? PlatformStructure,
		PlatformGameStructure? MixedStructure,
		FileSystem FileSystem)
		: IDependencyProvider
	{
		public FileBase? FindDependency(FileIdentifier identifier)
		{
			string? systemFilePath = RequestDependency(identifier.PathName);
			if (systemFilePath is not null)
			{
				return SchemeReader.LoadFile(systemFilePath, FileSystem);
			}

			if (TryOpenEndfieldDependency(PlatformStructure, identifier.PathName, out FileBase? dependency)
				|| TryOpenEndfieldDependency(MixedStructure, identifier.PathName, out dependency))
			{
				Logger.Info(LogCategory.Import, $"Dependency '{identifier.PathName}' has been loaded from Endfield VFS");
				return dependency;
			}

			return null;
		}

		private static bool TryOpenEndfieldDependency(
			PlatformGameStructure? structure,
			string dependencyName,
			[NotNullWhen(true)] out FileBase? dependency)
		{
			if (structure?.GameDataPath is not string gameDataPath
				|| !EndfieldVfsCatalog.TryGet(gameDataPath, out EndfieldVfsCatalog? catalog)
				|| !catalog.TryOpenFile(dependencyName, out Stream? stream, out string? virtualPath))
			{
				dependency = null;
				return false;
			}

			SmartStream smartStream = SmartStream.Create(stream);
			string fileName = GetVfsFileName(dependencyName);
			dependency = SchemeReader.ReadFile(smartStream, virtualPath, fileName);
			return true;
		}

		private static string GetVfsFileName(string value)
		{
			string normalized = value.Replace('\\', '/');
			int slashIndex = normalized.LastIndexOf('/');
			return slashIndex >= 0 ? normalized[(slashIndex + 1)..] : normalized;
		}

		/// <summary>
		/// Attempts to find the path for the dependency with that name.
		/// </summary>
		private string? RequestDependency(string dependency)
		{
			return PlatformStructure?.RequestDependency(dependency) ?? MixedStructure?.RequestDependency(dependency);
		}

		public void ReportMissingDependency(FileIdentifier identifier)
		{
			Logger.Log(LogType.Warning, LogCategory.Import, $"Dependency '{identifier}' wasn't found");
		}
	}
}
