using System.IO.Enumeration;
using System.Text;

namespace AssetRipper.IO.Files.Endfield;

/// <summary>
/// Read-through file-system overlay that exposes Endfield VFS bundle entries as ordinary files
/// beneath StreamingAssets while delegating all physical file operations to the original file system.
/// </summary>
public sealed class EndfieldOverlayFileSystem : FileSystem
{
	private const string VirtualDirectoryName = "__EndfieldVfs";
	private readonly FileSystem m_inner;
	private readonly Dictionary<string, MountedEntry> m_virtualFiles;
	private readonly HashSet<string> m_virtualDirectories;
	private readonly StringComparer m_pathComparer;

	private EndfieldOverlayFileSystem(FileSystem inner, string virtualRootPath, IReadOnlyList<MountedEntry> entries)
	{
		m_inner = inner;
		m_pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		VirtualRootPath = NormalizePath(virtualRootPath);
		m_virtualFiles = new Dictionary<string, MountedEntry>(m_pathComparer);
		m_virtualDirectories = new HashSet<string>(m_pathComparer) { VirtualRootPath };

		foreach (MountedEntry entry in entries)
		{
			string normalizedPath = NormalizePath(entry.VirtualPath);
			m_virtualFiles.Add(normalizedPath, entry with { VirtualPath = normalizedPath });
			AddParentDirectories(normalizedPath);
		}

		File = new EndfieldFileImplementation(this);
		Directory = new EndfieldDirectoryImplementation(this);
		Path = new EndfieldPathImplementation(this);
	}

	public string VirtualRootPath { get; }
	public int MountedFileCount => m_virtualFiles.Count;
	public override FileImplementation File { get; }
	public override DirectoryImplementation Directory { get; }
	public override PathImplementation Path { get; }

	public override string TemporaryDirectory
	{
		get => m_inner.TemporaryDirectory;
		set => m_inner.TemporaryDirectory = value;
	}

	/// <summary>
	/// Creates an overlay for an installed Endfield data directory. The regular StreamingAssets VFS is
	/// the primary source and Persistent is used as a fallback, matching AnimeStudio's loader behavior.
	/// Blocks present only in Persistent are mounted as an additional fallback pass.
	/// </summary>
	public static bool TryCreate(FileSystem fileSystem, string gameDataPath, [NotNullWhen(true)] out EndfieldOverlayFileSystem? overlay)
	{
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataPath);

		// The current VFS reader operates on native file paths. Other FileSystem implementations can be
		// supported once the VFS reader itself is abstracted over FileSystem.
		if (fileSystem is not LocalFileSystem)
		{
			overlay = null;
			return false;
		}

		string streamingAssetsPath = fileSystem.Path.Join(gameDataPath, "StreamingAssets");
		string streamingVfsPath = fileSystem.Path.Join(streamingAssetsPath, "VFS");
		if (!fileSystem.Directory.Exists(streamingVfsPath))
		{
			overlay = null;
			return false;
		}

		string persistentPath = fileSystem.Path.Join(gameDataPath, "Persistent");
		string persistentVfsPath = fileSystem.Path.Join(persistentPath, "VFS");
		string? fallbackVfsPath = fileSystem.Directory.Exists(persistentVfsPath) ? persistentVfsPath : null;
		string virtualRootPath = fileSystem.Path.Join(streamingAssetsPath, VirtualDirectoryName);
		List<MountedEntry> entries = BuildEntries(streamingVfsPath, fallbackVfsPath, virtualRootPath);
		if (entries.Count == 0)
		{
			overlay = null;
			return false;
		}

		overlay = new EndfieldOverlayFileSystem(fileSystem, virtualRootPath, entries);
		return true;
	}

	private static List<MountedEntry> BuildEntries(string primaryVfsPath, string? fallbackVfsPath, string virtualRootPath)
	{
		List<MountedEntry> entries = [];
		HashSet<string> visitedBlocks = new(StringComparer.OrdinalIgnoreCase);
		MountBlocks(primaryVfsPath, fallbackVfsPath, virtualRootPath, visitedBlocks, entries);
		if (!string.IsNullOrEmpty(fallbackVfsPath))
		{
			MountBlocks(fallbackVfsPath, primaryVfsPath, virtualRootPath, visitedBlocks, entries);
		}
		return entries;
	}

	private static void MountBlocks(
		string sourceVfsPath,
		string? fallbackVfsPath,
		string virtualRootPath,
		HashSet<string> visitedBlocks,
		List<MountedEntry> entries)
	{
		foreach (string blockFilePath in System.IO.Directory
			.EnumerateFiles(sourceVfsPath, "*.blc", SearchOption.AllDirectories)
			.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
		{
			string relativeBlockPath = System.IO.Path.GetRelativePath(sourceVfsPath, blockFilePath);
			if (!visitedBlocks.Add(relativeBlockPath))
			{
				continue;
			}

			EndfieldVfsBlockInfo block = EndfieldVfsReader.ReadBlock(blockFilePath);
			if (block.BlockType is not (EndfieldVfsBlockType.InitialBundle or EndfieldVfsBlockType.Bundle))
			{
				continue;
			}

			string relativeBlockDirectory = System.IO.Path.GetRelativePath(sourceVfsPath, block.DirectoryPath);
			string? fallbackBlockDirectory = string.IsNullOrEmpty(fallbackVfsPath)
				? null
				: System.IO.Path.Combine(fallbackVfsPath, relativeBlockDirectory);

			foreach (EndfieldVfsChunkInfo chunk in block.Chunks)
			{
				string chunkPath = System.IO.Path.Combine(block.DirectoryPath, chunk.FileName);
				if (!System.IO.File.Exists(chunkPath) && !string.IsNullOrEmpty(fallbackBlockDirectory))
				{
					string fallbackChunkPath = System.IO.Path.Combine(fallbackBlockDirectory, chunk.FileName);
					if (System.IO.File.Exists(fallbackChunkPath))
					{
						chunkPath = fallbackChunkPath;
					}
				}

				if (!System.IO.File.Exists(chunkPath))
				{
					throw new FileNotFoundException($"Endfield VFS chunk was not found: {chunk.FileName}", chunkPath);
				}

				foreach (EndfieldVfsFileInfo file in chunk.Files)
				{
					string safeName = CreateSafeVirtualFileName(file.Name, entries.Count);
					string virtualPath = System.IO.Path.Combine(virtualRootPath, safeName);
					entries.Add(new MountedEntry(virtualPath, chunkPath, file));
				}
			}
		}
	}

	private static string CreateSafeVirtualFileName(string originalName, int index)
	{
		string normalized = originalName.Replace('\\', '/');
		int slashIndex = normalized.LastIndexOf('/');
		string fileName = slashIndex >= 0 ? normalized[(slashIndex + 1)..] : normalized;
		fileName = FixInvalidFileNameCharacters(fileName);
		if (string.IsNullOrWhiteSpace(fileName))
		{
			fileName = "bundle";
		}
		return $"{index:D8}_{fileName}";
	}

	private string NormalizePath(string path) => m_inner.Path.GetFullPath(path);

	private void AddParentDirectories(string filePath)
	{
		string? current = System.IO.Path.GetDirectoryName(filePath);
		while (!string.IsNullOrEmpty(current) && IsSameOrChildPath(current, VirtualRootPath))
		{
			m_virtualDirectories.Add(current);
			if (m_pathComparer.Equals(current, VirtualRootPath))
			{
				break;
			}
			current = System.IO.Path.GetDirectoryName(current);
		}
	}

	private bool TryGetVirtualFile(string path, [NotNullWhen(true)] out MountedEntry? entry)
	{
		return m_virtualFiles.TryGetValue(NormalizePath(path), out entry);
	}

	private bool IsVirtualDirectory(string path) => m_virtualDirectories.Contains(NormalizePath(path));

	private IEnumerable<string> EnumerateVirtualFiles(string path, string searchPattern, SearchOption searchOption)
	{
		string root = NormalizePath(path);
		foreach (string filePath in m_virtualFiles.Keys.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
		{
			if (IsSelectedPath(root, filePath, searchOption) && MatchesPattern(filePath, searchPattern))
			{
				yield return filePath;
			}
		}
	}

	private IEnumerable<string> EnumerateVirtualDirectories(string path, string searchPattern, SearchOption searchOption)
	{
		string root = NormalizePath(path);
		foreach (string directoryPath in m_virtualDirectories.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
		{
			if (!m_pathComparer.Equals(root, directoryPath) &&
				IsSelectedPath(root, directoryPath, searchOption) &&
				MatchesPattern(directoryPath, searchPattern))
			{
				yield return directoryPath;
			}
		}
	}

	private static bool MatchesPattern(string path, string searchPattern)
	{
		string name = System.IO.Path.GetFileName(path);
		return FileSystemName.MatchesSimpleExpression(searchPattern, name, OperatingSystem.IsWindows());
	}

	private bool IsSelectedPath(string root, string candidate, SearchOption searchOption)
	{
		if (!IsSameOrChildPath(candidate, root) || m_pathComparer.Equals(root, candidate))
		{
			return false;
		}

		string relative = System.IO.Path.GetRelativePath(root, candidate);
		if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal))
		{
			return false;
		}
		return searchOption == SearchOption.AllDirectories || !ContainsDirectorySeparator(relative);
	}

	private bool IsSameOrChildPath(string candidate, string root)
	{
		if (m_pathComparer.Equals(candidate, root))
		{
			return true;
		}

		string relative = System.IO.Path.GetRelativePath(root, candidate);
		return relative != "." &&
			!relative.Equals("..", StringComparison.Ordinal) &&
			!relative.StartsWith($"..{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
			!relative.StartsWith($"..{System.IO.Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
	}

	private static bool ContainsDirectorySeparator(string relativePath)
	{
		return relativePath.Contains(System.IO.Path.DirectorySeparatorChar) ||
			relativePath.Contains(System.IO.Path.AltDirectorySeparatorChar);
	}

	private IEnumerable<string> EnumerateFilesCore(string path, string searchPattern, SearchOption searchOption)
	{
		HashSet<string> results = new(m_pathComparer);
		if (m_inner.Directory.Exists(path))
		{
			foreach (string file in m_inner.Directory.EnumerateFiles(path, searchPattern, searchOption))
			{
				if (results.Add(file))
				{
					yield return file;
				}
			}
		}
		foreach (string file in EnumerateVirtualFiles(path, searchPattern, searchOption))
		{
			if (results.Add(file))
			{
				yield return file;
			}
		}
	}

	private IEnumerable<string> EnumerateDirectoriesCore(string path, string searchPattern, SearchOption searchOption)
	{
		HashSet<string> results = new(m_pathComparer);
		if (m_inner.Directory.Exists(path))
		{
			foreach (string directory in m_inner.Directory.EnumerateDirectories(path, searchPattern, searchOption))
			{
				if (results.Add(directory))
				{
					yield return directory;
				}
			}
		}
		foreach (string directory in EnumerateVirtualDirectories(path, searchPattern, searchOption))
		{
			if (results.Add(directory))
			{
				yield return directory;
			}
		}
	}

	private sealed record MountedEntry(string VirtualPath, string ChunkPath, EndfieldVfsFileInfo File);

	private sealed class EndfieldFileImplementation(EndfieldOverlayFileSystem fileSystem) : FileImplementation(fileSystem)
	{
		private EndfieldOverlayFileSystem Owner => (EndfieldOverlayFileSystem)Parent;

		public override Stream Create(string path) => Owner.m_inner.File.Create(path);

		public override void Delete(string path)
		{
			if (Owner.TryGetVirtualFile(path, out _))
			{
				throw new NotSupportedException("Endfield VFS virtual files are read-only.");
			}
			Owner.m_inner.File.Delete(path);
		}

		public override bool Exists(string path) => Owner.TryGetVirtualFile(path, out _) || Owner.m_inner.File.Exists(path);

		public override Stream OpenRead(string path)
		{
			if (Owner.TryGetVirtualFile(path, out MountedEntry? entry))
			{
				return new EndfieldVfsEntryStream(entry.ChunkPath, entry.File);
			}
			return Owner.m_inner.File.OpenRead(path);
		}

		public override Stream OpenWrite(string path)
		{
			if (Owner.TryGetVirtualFile(path, out _))
			{
				throw new NotSupportedException("Endfield VFS virtual files are read-only.");
			}
			return Owner.m_inner.File.OpenWrite(path);
		}

		public override byte[] ReadAllBytes(string path)
		{
			return Owner.TryGetVirtualFile(path, out MountedEntry? entry)
				? EndfieldVfsReader.ExtractFile(entry.ChunkPath, entry.File)
				: Owner.m_inner.File.ReadAllBytes(path);
		}

		public override string ReadAllText(string path) => Encoding.UTF8.GetString(ReadAllBytes(path));
		public override string ReadAllText(string path, Encoding encoding) => encoding.GetString(ReadAllBytes(path));
		public override void WriteAllBytes(string path, ReadOnlySpan<byte> bytes) => Owner.m_inner.File.WriteAllBytes(path, bytes);
		public override void WriteAllText(string path, ReadOnlySpan<char> contents) => Owner.m_inner.File.WriteAllText(path, contents);
		public override void WriteAllText(string path, ReadOnlySpan<char> contents, Encoding encoding) => Owner.m_inner.File.WriteAllText(path, contents, encoding);
	}

	private sealed class EndfieldDirectoryImplementation(EndfieldOverlayFileSystem fileSystem) : DirectoryImplementation(fileSystem)
	{
		private EndfieldOverlayFileSystem Owner => (EndfieldOverlayFileSystem)Parent;

		public override void Create(string path) => Owner.m_inner.Directory.Create(path);

		public override void Delete(string path)
		{
			if (Owner.IsVirtualDirectory(path))
			{
				throw new NotSupportedException("Endfield VFS virtual directories are read-only.");
			}
			Owner.m_inner.Directory.Delete(path);
		}

		public override bool Exists(string path) => Owner.IsVirtualDirectory(path) || Owner.m_inner.Directory.Exists(path);
		public override IEnumerable<string> EnumerateDirectories(string path, string searchPattern, SearchOption searchOption) => Owner.EnumerateDirectoriesCore(path, searchPattern, searchOption);
		public override IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption) => Owner.EnumerateFilesCore(path, searchPattern, searchOption);
		public override string[] GetDirectories(string path, string searchPattern, SearchOption searchOption) => EnumerateDirectories(path, searchPattern, searchOption).ToArray();
		public override string[] GetFiles(string path, string searchPattern, SearchOption searchOption) => EnumerateFiles(path, searchPattern, searchOption).ToArray();
		public override IEnumerable<string> EnumerateDirectories(string path, string searchPattern) => EnumerateDirectories(path, searchPattern, SearchOption.TopDirectoryOnly);
		public override IEnumerable<string> EnumerateFiles(string path, string searchPattern) => EnumerateFiles(path, searchPattern, SearchOption.TopDirectoryOnly);
		public override string[] GetDirectories(string path, string searchPattern) => EnumerateDirectories(path, searchPattern).ToArray();
		public override string[] GetFiles(string path, string searchPattern) => EnumerateFiles(path, searchPattern).ToArray();
		public override IEnumerable<string> EnumerateDirectories(string path) => EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly);
		public override IEnumerable<string> EnumerateFiles(string path) => EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly);
		public override string[] GetDirectories(string path) => EnumerateDirectories(path).ToArray();
		public override string[] GetFiles(string path) => EnumerateFiles(path).ToArray();
	}

	private sealed class EndfieldPathImplementation(EndfieldOverlayFileSystem fileSystem) : PathImplementation(fileSystem)
	{
		private EndfieldOverlayFileSystem Owner => (EndfieldOverlayFileSystem)Parent;
		public override string GetFullPath(string path) => Owner.m_inner.Path.GetFullPath(path);
		public override bool IsPathRooted(ReadOnlySpan<char> path) => System.IO.Path.IsPathRooted(path);
	}
}
