using AssetRipper.IO.Files.Endfield;
using NUnit.Framework;

namespace AssetRipper.Tests;

internal sealed class EndfieldVfsHashTests
{
	[TestCase(EndfieldVfsBlockType.Bundle, "7064D8E2")]
	[TestCase(EndfieldVfsBlockType.InitialBundle, "0CE8FA57")]
	public void KnownBlockDirectoryNamesMatchReference(EndfieldVfsBlockType blockType, string expected)
	{
		Assert.That(EndfieldVfsHash.GetBlockDirectoryName(blockType), Is.EqualTo(expected));
	}

	[TestCase(EndfieldVfsBlockType.InitialBundle, "InitBundle")]
	[TestCase(EndfieldVfsBlockType.IFixPatch, "IFixPatchOut")]
	public void LogicalBlockNamesMatchEndfieldVfsNames(EndfieldVfsBlockType blockType, string expected)
	{
		Assert.That(EndfieldVfsHash.GetBlockName(blockType), Is.EqualTo(expected));
	}
}
