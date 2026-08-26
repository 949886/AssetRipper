using System.Collections.Concurrent;

namespace AssetRipper.IO.Files.Endfield;

/// <summary>
/// Lazily cached name index over known Endfield VFS blocks. This is used for external Unity resources
/// and standalone dependencies requested by logical name after bundles have been loaded.
/// </summary>
public sealed class EndfieldVfsCatalog
{
	private static readonly EndfieldVfsBlockType[] IndexedBlockTypes =
	[
		EndfieldVfsBlockType.InitialAudio,
		EndfieldVfsBlockType.InitialBundle,
		EndfieldVfsBlockType.InitialExtendData,
		EndfieldVfsBlockType.BundleManifest,
		EndfieldVfsBlockType.IFixPatch,
		EndfieldVfsBlockType.AuditStreaming,
		EndfieldVfsBlockType.AuditDynamicStreaming,
		EndfieldVfsBlockType.AuditIV,
		EndfieldVfsBlockType.AuditAudio,
		EndfieldVfsBlockType.AuditVideo,
		EndfieldVfsBlockType.Bundle,
		EndfieldVfsBlockType.Audio,
		EndfieldVfsBlockType.Video,
		EndfieldVfsBlockType.IV,
		EndfieldVfsBlockType.Streaming,
		EndfieldVfsBlockType.DynamicStreaming,
		EndfieldVfsBlockType.Lua,
		EndfieldVfsBlockType.Table,
		EndfieldVfsBlockType.JsonData,
		EndfieldVfsBlockType.ExtendData,
		EndfieldVfsBlockType.HotfixAudio,
		EndfieldVfsBlockType.AudioChinese,
		EndfieldVfsBlockType.AudioEnglish,
		EndfieldVfsBlockType.AudioJapanese,
		EndfieldVfsBlockType.AudioKorean,
	];

	private static readonly ConcurrentDictionary<string, Lazy<EndfieldVfsCatalog?>> Cache = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, CatalogEntry> m_entriesByExactName = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, CatalogEntry?> m_entriesByBasename = new(StringComparer.OrdinalIgnoreCase);

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
		foreach (EndfieldVfsBlockType blockType in IndexedBlockTypes)
		{
			IndexBlock(blockType, streamingVfsPath, fallbackVfsPath);
		}
	}

	public string GameDataPath { get; }
	public int IndexedNameCount => m_entriesByExactName.Count;

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
	/// Opens a VFS file by logical path, normalized identifier, or unique basename without materializing it.
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
		if (m_entriesByExactName.TryGetValue(normalized, out entry))
		{
			return true;
		}

		string fixedIdentifier = NormalizeLookupKey(SpecialFileNames.FixFileIdentifier(fileName));
		if (m_entriesByExactName.TryGetValue(fixedIdentifier, out entry))
		{
			return true;
		}

		string basename = GetVfsFileName(normalized);
		if (m_entriesByBasename.TryGetValue(basename, out CatalogEntry? basenameEntry) && basenameEntry is not null)
		{
			entry = basenameEntry;
			return true;
		}

		entry = null;
		return false;
	}

	private void IndexBlock(EndfieldVfsBlockType blockType, string primaryVfsPath, string? fallbackVfsPath)
	{
		string directoryName = EndfieldVfsHash.GetBlockDirectoryName(blockType);
		string primaryBlockDirectory = Path.Combine(primaryVfsPath, directoryName);
		string? fallbackBlockDirectory = string.IsNullOrEmpty(fallbackVfsPath)
			? null
			: Path.Combine(fallbackVfsPath, directoryName);
		string blockFileName = $"{directoryName}.blc";

		string? blockFilePath = ResolveExistingPath(
			Path.Combine(primaryBlockDirectory, blockFileName),
			string.IsNullOrEmpty(fallbackBlockDirectory) ? null : Path.Combine(fallbackBlockDirectory, blockFileName));
		if (blockFilePath is null)
		{
			return;
		}

		EndfieldVfsBlockInfo block;
		try
		{
			block = EndfieldVfsReader.ReadBlock(blockFilePath);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			return;
		}

		if (block.BlockType != blockType)
		{
			return;
		}

		foreach (EndfieldVfsChunkInfo chunk in block.Chunks)
		{
			string? chunkPath = ResolveExistingPath(
				Path.Combine(primaryBlockDirectory, chunk.FileName),
				string.IsNullOrEmpty(fallbackBlockDirectory) ? null : Path.Combine(fallbackBlockDirectory, chunk.FileName));
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

	private void RegisterEntry(CatalogEntry entry)
	{
		string normalized = NormalizeLookupKey(entry.File.Name);
		TryAddExact(normalized, entry);
		TryAddExact(NormalizeLookupKey(SpecialFileNames.FixFileIdentifier(entry.File.Name)), entry);

		string basename = GetVfsFileName(normalized);
		if (!m_entriesByBasename.TryGetValue(basename, out CatalogEntry? existing))
		{
			m_entriesByBasename.Add(basename, entry);
		}
		else if (existing is not null && existing != entry)
		{
			// Basename-only lookup is unsafe when multiple logical files share the same name.
			m_entriesByBasename[basename] = null;
		}
	}

	private void TryAddExact(string key, CatalogEntry entry)
	{
		if (!string.IsNullOrWhiteSpace(key))
		{
			m_entriesByExactName.TryAdd(key, entry);
		}
	}

	private static string? ResolveExistingPath(string primaryPath, string? fallbackPath)
	{
		if (File.Exists(primaryPath))
		{
			return primaryPath;
		}
		if (!string.IsNullOrEmpty(fallbackPath) && File.Exists(fallbackPath))
		{
			return fallbackPath;
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
