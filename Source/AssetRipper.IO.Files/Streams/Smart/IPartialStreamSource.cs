namespace AssetRipper.IO.Files.Streams.Smart;

/// <summary>
/// Implemented by seekable streams that can create an independent view over a sub-range without
/// copying the range into memory.
/// </summary>
public interface IPartialStreamSource
{
	Stream CreatePartial(long offset, long size);
}
