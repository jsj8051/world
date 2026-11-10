using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace World.Data.Maps;

internal static class MapPackageFormat
{
	public const string ManifestEntry = "manifest.json";
	public static readonly string[] ChannelNames =
		{ "continental_collision", "continental_arc", "oceanic_arc", "subduction", "continental_rift", "oceanic_ridge" };

	public static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = false,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
	};

	public static byte[] Bytes<T>(T[] values) where T : unmanaged
		=> MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

	public static byte[] Bytes(bool[] values)
	{
		var bytes = new byte[values.Length];
		for (int i = 0; i < values.Length; i++) bytes[i] = values[i] ? (byte)1 : (byte)0;
		return bytes;
	}

	public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

	public static long CheckedLength(int[] shape, MapBlockElementType type)
	{
		long length = 1;
		foreach (int dimension in shape) length = checked(length * dimension);
		return checked(length * MapBlockDescriptor.ElementSize(type));
	}

	public static MapStructureException Failure(MapIssueCode code, string location, string message, long? index = null)
		=> new(new MapIssue(code, location, index, message));

	public static byte[] ReadEntry(ZipArchiveEntry entry, long limit)
	{
		if (entry.Length < 0 || entry.Length > limit)
			throw Failure(MapIssueCode.SizeLimit, entry.FullName, "The entry exceeds the configured size limit.");
		using Stream stream = entry.Open();
		using var memory = new MemoryStream(checked((int)entry.Length));
		var buffer = new byte[81920];
		long total = 0;
		int read;
		while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
		{
			total += read;
			if (total > limit || total > entry.Length)
				throw Failure(MapIssueCode.SizeLimit, entry.FullName, "The entry exceeds its declared size.");
			memory.Write(buffer, 0, read);
		}
		if (total != entry.Length)
			throw Failure(MapIssueCode.InvalidShape, entry.FullName, "The entry length does not match the archive directory.");
		return memory.ToArray();
	}

	public static void WriteEntry(ZipArchive archive, string path, byte[] data)
	{
		ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Fastest);
		using Stream stream = entry.Open();
		stream.Write(data, 0, data.Length);
	}

	public static T[] Decode<T>(byte[] bytes, MapBlockElementType type, int[] shape, string location) where T : unmanaged
	{
		if (MapBlockDescriptor.ElementSize(type) != Marshal.SizeOf<T>())
			throw Failure(MapIssueCode.InvalidValue, location, "The wire element type does not match the decoder.");
		long expected = CheckedLength(shape, type);
		if (expected != bytes.Length)
			throw Failure(MapIssueCode.InvalidShape, location, "The block length does not match its shape.");
		var values = new T[bytes.Length / Marshal.SizeOf<T>()];
		bytes.CopyTo(MemoryMarshal.AsBytes(values.AsSpan()));
		return values;
	}

	public static void PutU64(Span<byte> destination, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(destination, value);
}