using System;
using System.IO;
using System.IO.Compression;

namespace World.Utils;

// 纯 C# PNG 编码器（批量判读图用）：测试宿主没有 Godot native，不能走 Godot.Image.SavePng。
// 8-bit RGB、filter 0；IDAT = ZLibStream 全帧（.NET 6+ 自带 zlib 头尾，RFC1950）——零外部依赖。
public static class PngWriter
{
	/// <summary>写出 RGB8 PNG。rgb 长度 = width × height × 3，行优先、RGB 逐像素。</summary>
	public static void WriteRgb(string path, int width, int height, byte[] rgb)
	{
		if (rgb.Length != width * height * 3)
			throw new ArgumentException($"rgb 长度 {rgb.Length} ≠ {width}×{height}×3", nameof(rgb));

		using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
		fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });   // PNG 签名

		// IHDR：宽、高、位深 8、色型 2（真彩）、压缩 0、滤波 0、隔行 0
		var ihdr = new byte[13];
		WriteBE(ihdr, 0, width);
		WriteBE(ihdr, 4, height);
		ihdr[8] = 8; ihdr[9] = 2;
		WriteChunk(fs, "IHDR", ihdr);

		// 原始像素流：每行前置 filter 0
		int stride = 1 + width * 3;
		var raw = new byte[height * stride];
		for (int y = 0; y < height; y++)
			Array.Copy(rgb, y * width * 3, raw, y * stride + 1, width * 3);

		// IDAT：ZLibStream 全帧压缩
		using (var ms = new MemoryStream())
		{
			using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
				z.Write(raw);
			WriteChunk(fs, "IDAT", ms.ToArray());
		}

		WriteChunk(fs, "IEND", Array.Empty<byte>());
	}

	private static void WriteBE(byte[] b, int off, int v)
	{
		b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16);
		b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
	}

	private static void WriteChunk(Stream fs, string type, byte[] data)
	{
		var len = new byte[4];
		WriteBE(len, 0, data.Length);
		fs.Write(len);
		var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
		fs.Write(typeBytes);
		fs.Write(data);
		uint crc = 0xFFFFFFFF;
		foreach (byte t in typeBytes) crc = UpdateCrc(crc, t);
		foreach (byte t in data) crc = UpdateCrc(crc, t);
		crc ^= 0xFFFFFFFF;
		var crcB = new byte[4];
		crcB[0] = (byte)(crc >> 24); crcB[1] = (byte)(crc >> 16);
		crcB[2] = (byte)(crc >> 8); crcB[3] = (byte)crc;
		fs.Write(crcB);
	}

	private static uint UpdateCrc(uint crc, byte x)
	{
		crc ^= x;
		for (int k = 0; k < 8; k++)
			crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
		return crc;
	}
}
