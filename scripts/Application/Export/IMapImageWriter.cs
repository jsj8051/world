namespace World.Application.Export;

/// <summary>Writes RGBA8 map pixels and returns the saved image path.</summary>
public interface IMapImageWriter
{
	string Write(byte[] pixels, int width, int height);
}
