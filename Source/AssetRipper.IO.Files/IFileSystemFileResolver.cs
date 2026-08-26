namespace AssetRipper.IO.Files;

/// <summary>
/// Optional extension for file systems that can resolve logical dependency/resource names to
/// readable paths that are not directly present in the physical data directories.
/// </summary>
public interface IFileSystemFileResolver
{
	bool TryResolveFile(string fileName, [NotNullWhen(true)] out string? path);
}
