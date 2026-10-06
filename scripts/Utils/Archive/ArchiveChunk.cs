using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace World.Utils;

/// <summary>
/// 段表档案容器（2026-08-23 存档段表化）：ZIP 式尾目录布局，顺序写、随机读。
///
/// 布局（见 docs/存档段表格式设计.md §2）：
///   [4B] magic          "MPA1" / "CMP1" / "GMP1"
///   [2B] skeletonVer    容器骨架版本（.mpa=6 / .cmp=15 / .gmp=3）
///   [2B] reserved       保留（当前 0）
///   [..] 数据区         各段依次排列（每段：[4B] segMagic + [2B] segVer + 数据）
///   [12B×K] 段表        { [4B] type | [4B] offset | [4B] length }，offset 相对文件头
///   [12B] 尾目录        { [4B] 段表偏移 | [4B] 段表字节数 | [4B] tailMagic "CHKT" }
///
/// 设计要点：
/// 1. 段表放文件尾（ZIP central directory 风格）：写端顺序写头+数据区，边写边记各段
///    offset/length，最后写段表+尾目录——无需回填；读端 Seek(Length-12) 读尾目录 → 定位段表。
/// 2. 加新系统 = 注册新 segMagic，读端 switch 按 type 分发，未知 type 跳过（不会整档错位）。
/// 3. IO 层用 System.IO（BinaryReader/Writer，小端序）替代 Godot FileAccess——存档往返可进
///    单元测试（旧约束：测试进程禁碰 FileAccess）。路径：ProjectSettings.GlobalizePath 后 File.Open。
/// 4. ChunkWriter/ChunkReader 提供与 FileAccess 同名的 Store8/Store32/GetFloat/Seek 等方法，
///    存量字段读写代码只是类型替换，调用体基本不动。
/// </summary>
public sealed class ChunkWriter : IDisposable
{
    private readonly BinaryWriter _w;
    private readonly List<ChunkEntry> _entries = new();
    private int _segStart;          // 当前段起始偏移（相对文件头）
    private bool _inSegment;
    private string _segMagicCache;  // BeginSegment 的 segMagic，EndSegment 时记入段表
    private bool _finished;

    /// <summary>写入模式构造：写 magic + skeletonVer + reserved 头。</summary>
    public ChunkWriter(Stream s, string magic, ushort skeletonVer)
    {
        if (magic.Length != 4) throw new ArgumentException("magic 必须 4 字符", nameof(magic));
        _w = new BinaryWriter(s, Encoding.ASCII, leaveOpen: true);
        _w.Write(Encoding.ASCII.GetBytes(magic));
        _w.Write(skeletonVer);
        _w.Write((ushort)0);   // reserved
    }

    /// <summary>当前文件偏移（相对文件头）。</summary>
    public long Position => _w.BaseStream.Position;

    /// <summary>开始一段：写段头（segMagic + segVer），记录段起始偏移。返回段数据写入起点。</summary>
    public long BeginSegment(string segMagic, ushort segVer)
    {
        if (_inSegment) throw new InvalidOperationException("上一段未 EndSegment");
        if (segMagic.Length != 4) throw new ArgumentException("segMagic 必须 4 字符", nameof(segMagic));
        _segStart = (int)_w.BaseStream.Position;
        _segMagicCache = segMagic;
        _w.Write(Encoding.ASCII.GetBytes(segMagic));
        _w.Write(segVer);
        _inSegment = true;
        return _segStart;
    }

    /// <summary>结束当前段：记录 (type, offset, length) 到段表。</summary>
    public void EndSegment()
    {
        if (!_inSegment) throw new InvalidOperationException("没有进行中的段");
        _entries.Add(new ChunkEntry(ChunkTable.AsciiUint(_segMagicCache), _segStart, (int)(_w.BaseStream.Position - _segStart)));
        _inSegment = false;
        _segMagicCache = null;
    }

    /// <summary>全部段写完，写段表 + 尾目录，收尾。</summary>
    public void Finish()
    {
        if (_inSegment) throw new InvalidOperationException("有段未 EndSegment");
        if (_finished) return;   // 幂等：Dispose 兜底已 Finish 过
        _finished = true;
        long tableOffset = _w.BaseStream.Position;
        foreach (var e in _entries)
        {
            _w.Write(e.Type);
            _w.Write(e.Offset);
            _w.Write(e.Length);
        }
        _w.Write((uint)tableOffset);
        _w.Write((uint)(_entries.Count * ChunkTable.EntryBytes));
        _w.Write(Encoding.ASCII.GetBytes(ChunkTable.TailMagic));
        _w.Flush();
    }

    // ── FileAccess 同名方法（小端序）──
    public void Store8(byte v) => _w.Write(v);
    public void Store16(ushort v) => _w.Write(v);
    public void Store32(uint v) => _w.Write(v);
    public void Store64(ulong v) => _w.Write(v);
    public void StoreFloat(float v) => _w.Write(v);
    public void StoreBytes(byte[] v) => _w.Write(v);

    /// <summary>释放：未 Finish 时自动补写段表+尾目录（防止异常路径留下无尾目录的半成品文件）。</summary>
    public void Dispose()
    {
        if (!_finished)
        {
            try { Finish(); }
            catch { /* 释放路径不抛 */ }
        }
        _w.Dispose();
    }
}

/// <summary>段表条目：type（4 字符 ASCII 打包为 uint）+ 段起始偏移 + 段字节长度（含 6B 段头）。</summary>
public readonly record struct ChunkEntry(uint Type, int Offset, int Length)
{
    public string TypeName => ChunkTable.UintAscii(Type);
}

/// <summary>段表 + 尾目录常量与读取工具。</summary>
public static class ChunkTable
{
    public const string TailMagic = "CHKT";
    public const int EntryBytes = 12;   // type 4 + offset 4 + length 4
    public const int TailBytes = 12;    // 段表偏移 4 + 段表字节数 4 + tailMagic 4
    public const int SegHeaderBytes = 6;   // segMagic 4 + segVer 2

    public static uint AsciiUint(string s)
    {
        if (s == null || s.Length != 4) throw new ArgumentException("段 type 必须 4 字符", nameof(s));
        return (uint)((s[0] << 24) | (s[1] << 16) | (s[2] << 8) | s[3]);
    }

    public static string UintAscii(uint v) =>
        new string(new[] { (char)((v >> 24) & 0xFF), (char)((v >> 16) & 0xFF), (char)((v >> 8) & 0xFF), (char)(v & 0xFF) });
}

/// <summary>段表档案读取端：读头 → 尾目录 → 段表 → 任意段 Seek 直达。</summary>
public sealed class ChunkReader : IDisposable
{
    private readonly Stream _s;
    private readonly BinaryReader _r;
    private readonly List<ChunkEntry> _entries = new();
    public string Magic { get; private set; }
    public ushort SkeletonVer { get; private set; }

    /// <summary>构造并立即解析头/尾目录/段表。失败（坏 magic / 坏尾目录）抛 InvalidDataException。</summary>
    public ChunkReader(Stream s)
    {
        _s = s;
        _r = new BinaryReader(s, Encoding.ASCII, leaveOpen: true);
        if (s.Length < ChunkTable.TailBytes + 8) throw new InvalidDataException("文件过短，非段表格式");
        s.Seek(0, SeekOrigin.Begin);
        var magicBytes = _r.ReadBytes(4);
        Magic = Encoding.ASCII.GetString(magicBytes);
        SkeletonVer = _r.ReadUInt16();
        _r.ReadUInt16();   // reserved
        // 尾目录：文件尾固定 12B
        s.Seek(-ChunkTable.TailBytes, SeekOrigin.End);
        uint tableOffset = _r.ReadUInt32();
        uint tableBytes = _r.ReadUInt32();
        string tail = Encoding.ASCII.GetString(_r.ReadBytes(4));
        if (tail != ChunkTable.TailMagic) throw new InvalidDataException($"坏尾目录 '{tail}'（非段表文件或损坏）");
        if (tableBytes % ChunkTable.EntryBytes != 0) throw new InvalidDataException($"段表字节数非法 {tableBytes}");
        // 读段表
        s.Seek(tableOffset, SeekOrigin.Begin);
        int count = (int)(tableBytes / ChunkTable.EntryBytes);
        for (int i = 0; i < count; i++)
            _entries.Add(new ChunkEntry(_r.ReadUInt32(), (int)_r.ReadUInt32(), (int)_r.ReadUInt32()));
    }

    public IReadOnlyList<ChunkEntry> Entries => _entries;

    /// <summary>按 type 找段；不存在 → false。</summary>
    public bool TryGetEntry(string segMagic, out ChunkEntry entry)
    {
        uint t = ChunkTable.AsciiUint(segMagic);
        foreach (var e in _entries)
        {
            if (e.Type == t) { entry = e; return true; }
        }
        entry = default;
        return false;
    }

    /// <summary>定位到某段的数据起点（跳过 6B 段头）；返回 true。段不存在 → false。</summary>
    public bool SeekSegment(string segMagic)
    {
        if (!TryGetEntry(segMagic, out var e)) return false;
        _s.Seek(e.Offset + ChunkTable.SegHeaderBytes, SeekOrigin.Begin);
        return true;
    }

    /// <summary>段是否存在。</summary>
    public bool HasSegment(string segMagic) => TryGetEntry(segMagic, out _);

    // ── FileAccess 同名方法（小端序）──
    public byte Get8() => _r.ReadByte();
    public ushort Get16() => _r.ReadUInt16();
    public uint Get32() => _r.ReadUInt32();
    public ulong Get64() => _r.ReadUInt64();
    public float GetFloat() => _r.ReadSingle();
    public byte[] GetBytes(int n) => _r.ReadBytes(n);

    public long Position => _s.Position;
    public long Length => _s.Length;
    public void Seek(long pos) => _s.Seek(pos, SeekOrigin.Begin);

    public void Dispose() => _r.Dispose();
}