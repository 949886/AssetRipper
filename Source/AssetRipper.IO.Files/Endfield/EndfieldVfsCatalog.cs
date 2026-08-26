using System.Collections.Concurrent;

namespace AssetRipper.IO.Files.Endfield;

/// <summary>
/// Lazily cached name index over all Endfield VFS blocks. This is used for external Unity resources
/// such as .resS/.resource files which are requested by logical name after bundles have been loaded.
/// </summary>
public sealed class EndfieldVfsCatalog
{
	private static readonly ConcurrentDictionary<string, Lazy<EndfieldVfsCatalog?>> Cache = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, CatalogEntry> m_entriesByName = new(StringComparer.OrdinalIgnoreCase);

	private EndfieldVfsCatalog(string gameDataPath)
	{
		GameDataPath = Path.GetFullPath(gameDataPath);
		string streamingVfsPath = Path.Combine(GameDataPath, "StreamingAssets", "VFS");
		if (!Directory.Exists(streamingVfsPath))
		{
			return;
		}

		string persistentVfsPath = Path.Combine(GameDataPath, "Persistent", "VFS");
		string? fallbackVfsPath = Directory.Exists(persistentVfsPath) ? persistentVfsPath : null;
		HashSet<string> visitedBlocks = new(StringComparer.OrdinalIgnoreCase);
		IndexBlocks(streamingVfsPath, fallbackVfsPath, visitedBlocks);
		if (!string.IsNullOrEmpty(fallbackVfsPath))
		{
			IndexBlocks(fallbackVfsPath, streamingVfsPath, visitedBlocks);
		}
	}

	public string GameDataPath { get; }
	public int IndexedNameCount => m_entriesByName.Count;

	/// <summary>
	/// Gets or builds the VFS catalog for an Endfield data directory.
	/// </summary>
	public static bool TryGet(string gameDataPath, [NotNullWhen(true)] out EndfieldVfsCatalog? catalog)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataPath);
		string fullPath = Path.GetFullPath(gameDataPath);
		if (!Directory.Exists(Path.Combine(fullPath, "StreamingAssets", "VFS")))
		{
			catalog = null;
			return false;
		}

		Lazy<EndfieldVfsCatalog?> lazy = Cache.GetOrAdd(
			fullPath,
			static path => new Lazy<EndfieldVfsCatalog?>(() => new EndfieldVfsCatalog(path), LazyThreadSafetyMode.ExecutionAndPublication));
		catalog = lazy.Value;
		return catalog is not null;
	}

	/// <summary>
	/// Opens a VFS file by logical path, normalized identifier, or basename without materializing it.
	/// </summary>
	public bool TryOpenFile(
		string fileName,
		[NotNullWhen(true)] out Stream? stream,
		[NotNullWhen(true)] out string? virtualPath)
	{
		if (TryFindEntry(fileName, out CatalogEntry? entry))
		{
			stream = new EndfieldVfsEntryStream(entry.ChunkPath, entry.File);
			virtualPath = $"endfield-vfs://{NormalizeDisplayPath(entry.File.Name)}";
			return true;
		}

		stream = null;
		virtualPath = null;
		return false;
	}

	private bool TryFindEntry(string fileName, [NotNullWhen(true)] out CatalogEntry? entry)
	{
		if (string.IsNullOrWhiteSpace(fileName))
		{
			entry = null;
			return false;
		}

		string normalized = NormalizeLookupKey(fileName);
		if (m_entriesByName.TryGetValue(normalized, out entry))
		{
			return true;
		}

		string basename = GetVfsFileName(normalized);
		if (!string.Equals(basename, normalized, StringComparison.Ordinal) && m_entriesByName.TryGetValue(basename, out entry))
		{
			return true;
		}

		string fixedIdentifier = SpecialFileNames.FixFileIdentifier(fileName);
		return m_entriesByName.TryGetValue(NormalizeLookupKey(fixedIdentifier), out entry);
	}

	private void IndexBlocks(string sourceVfsPath, string? fallbackVfsPath, HashSet<string> visitedBlocks)
	{
		foreach (string blockFilePath in Directory
			.EnumerateFiles(sourceVfsPath, "*.blc", SearchOption.AllDirectories)
			.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
		{
			string relativeBlockPath = Path.GetRelativePath(sourceVfsPath, blockFilePath);
			if (!visitedBlocks.Add(relativeBlockPath))
			{
				continue;
			}

			EndfieldVfsBlockInfo block;
			try
			{
				block = EndfieldVfsReader.ReadBlock(blockFilePath);
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
			{
				continue;
			}

			string relativeBlockDirectory = Path.GetRelativePath(sourceVfsPath, block.DirectoryPath);
			string? fallbackBlockDirectory = string.IsNullOrEmpty(fallbackVfsPath)
				? null
				: Path.Combine(fallbackVfsPath, relativeBlockDirectory);

			foreach (EndfieldVfsChunkInfo chunk in block.Chunks)
			{
				string? chunkPath = ResolveChunkPath(block.DirectoryPath, fallbackBlockDirectory, chunk.FileName);
				if (chunkPath is null)
				{
					continue;
				}

				foreach (EndfieldVfsFileInfo file in chunk.Files)
				{
					if (string.IsNullOrWhiteSpace(file.Name) || file.Name.EndsWith('/') || file.Name.EndsWith('\\'))
					{
						continue;
					}
					RegisterEntry(new CatalogEntry(chunkPath, file));
				}
			}
		}
	}

	private void RegisterEntry(CatalogEntry entry)
	{
		string normalized = NormalizeLookupKey(entry.File.Name);
		TryAdd(normalized, entry);
		TryAdd(GetVfsFileName(normalized), entry);
		TryAdd(NormalizeLookupKey(SpecialFileNames.FixFileIdentifier(entry.File.Name)), entry);
	}

	private void TryAdd(string key, CatalogEntry entry)
	{
		if (!string.IsNullOrWhiteSpace(key))
		{
			m_entriesByName.TryAdd(key, entry);
		}
	}

	private static string? ResolveChunkPath(string blockDirectory, string? fallbackBlockDirectory, string chunkFileName)
	{
		string primary = Path.Combine(blockDirectory, chunkFileName);
		if (File.Exists(primary))
		{
			return primary;
		}
		if (!string.IsNullOrEmpty(fallbackBlockDirectory))
		{
			string fallback = Path.Combine(fallbackBlockDirectory, chunkFileName);
			if (File.Exists(fallback))
			{
				return fallback;
			}
		}
		return null;
	}

	private static string NormalizeLookupKey(string value)
	{
		string normalized = NormalizeDisplayPath(value).Trim();
		const string ArchivePrefix = "archive:/";
		if (normalized.StartsWith(ArchivePrefix, StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[ArchivePrefix.Length..];
		}
		return normalized.TrimStart('/');
	}

	private static string NormalizeDisplayPath(string value) => value.Replace('\\', '/');

	private static string GetVfsFileName(string value)
	{
		string normalized = NormalizeDisplayPath(value);
		int slashIndex = normalized.LastIndexOf('/');
		return slashIndex >= 0 ? normalized[(slashIndex + 1)..] : normalized;
	}

	private sealed record CatalogEntry(string ChunkPath, EndfieldVfsFileInfo File);
}
