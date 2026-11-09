using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using World.Data;

namespace World.Logic;

internal readonly struct CrustEntry(int key, in CrustSlot slot)
{
	public readonly int Key = key;
	public readonly CrustSlot Slot = slot;
}

/// <summary>固定大小的格块，每块连续存放有值的材料槽。每格按板块号和壳型排序。</summary>
internal sealed class CrustRows
{
	sealed record Block(int[] Starts, List<CrustEntry> Entries);
	readonly Block[] _blocks;
	internal static readonly CrustSlot EmptySlot;
	public int CellCount { get; }
	public int SlotsPerCell { get; }
	public long AllocatedEntries { get; }

	CrustRows(int cells, int slotsPerCell, Block[] blocks)
	{
		CellCount = cells;
		SlotsPerCell = slotsPerCell;
		_blocks = blocks;
		foreach (Block block in blocks) AllocatedEntries += block.Entries.Capacity;
	}

	public ReadOnlySpan<CrustEntry> Row(int cell)
	{
		Block block = _blocks[cell / EvolutionWork.CellsPerBatch];
		int local = cell % EvolutionWork.CellsPerBatch;
		int start = block.Starts[local];
		return CollectionsMarshal.AsSpan(block.Entries).Slice(start, block.Starts[local + 1] - start);
	}

	public ref readonly CrustSlot Get(int cell, int key)
	{
		var entries = Row(cell);
		for (int i = 0; i < entries.Length && entries[i].Key <= key; i++)
			if (entries[i].Key == key) return ref entries[i].Slot;
		return ref EmptySlot;
	}

	public CrustRows ReplaceRows(Dictionary<int, CrustRowBuffer> changed)
	{
		var blocks = (Block[])_blocks.Clone();
		var dirty = new bool[blocks.Length];
		foreach (int cell in changed.Keys) dirty[cell / EvolutionWork.CellsPerBatch] = true;
		for (int blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
		{
			if (!dirty[blockIndex]) continue;
			Block old = blocks[blockIndex];
			var starts = new int[old.Starts.Length];
			var entries = new List<CrustEntry>(old.Entries.Capacity);
			int first = blockIndex * EvolutionWork.CellsPerBatch;
			for (int local = 0; local < starts.Length - 1; local++)
			{
				if (changed.TryGetValue(first + local, out var replacement)) replacement.Drain(entries);
				else foreach (ref readonly CrustEntry entry in Row(first + local)) entries.Add(entry);
				starts[local + 1] = entries.Count;
			}
			blocks[blockIndex] = new Block(starts, entries);
		}
		return new CrustRows(CellCount, SlotsPerCell, blocks);
	}

	public static CrustRows Build(int cells, int slotsPerCell, CrustRows hint, Action<int, CrustRowBuffer> fill)
	{
		const int blockSize = EvolutionWork.CellsPerBatch;
		var blocks = new Block[cells == 0 ? 0 : (cells - 1) / blockSize + 1];
		EvolutionWork.ForCells(cells, (start, end) =>
		{
			var row = new CrustRowBuffer(slotsPerCell);
			for (int first = start; first < end; first += blockSize)
			{
				int blockIndex = first / blockSize;
				int count = Math.Min(blockSize, end - first);
				int prior = hint?._blocks[blockIndex].Entries.Count ?? 0;
				int capacity = prior == 0 ? count : prior + Math.Max(16, prior / 8);
				var entries = new List<CrustEntry>(capacity);
				var starts = new int[count + 1];
				for (int local = 0; local < count; local++)
				{
					fill(first + local, row);
					row.Drain(entries);
					starts[local + 1] = entries.Count;
				}
				blocks[blockIndex] = new Block(starts, entries);
			}
		});
		return new CrustRows(cells, slotsPerCell, blocks);
	}
}

/// <summary>工人复用的一格工作区。掩码只记录写过的槽，输出时按原密集槽顺序收口。</summary>
internal sealed class CrustRowBuffer(int slotCount)
{
	public readonly CrustSlot[] Slots = new CrustSlot[slotCount];
	readonly ulong[] _touched = new ulong[(slotCount + 63) / 64];
	public Keys WrittenKeys => new(_touched);

	internal readonly struct Keys(ulong[] words)
	{
		public KeyEnumerator GetEnumerator() => new(words);
	}

	internal struct KeyEnumerator(ulong[] words)
	{
		int _word = -1;
		ulong _bits;
		public int Current { get; private set; }

		public bool MoveNext()
		{
			while (_bits == 0)
			{
				if (++_word >= words.Length) return false;
				_bits = words[_word];
			}
			Current = (_word << 6) + BitOperations.TrailingZeroCount(_bits);
			_bits &= _bits - 1;
			return true;
		}
	}

	public void Set(int key, in CrustSlot slot)
	{
		Slots[key] = slot;
		_touched[key >> 6] |= 1UL << (key & 63);
	}

	public void Add(int key, in CrustSlot slot, double share)
	{
		Slots[key].Add(slot, share);
		_touched[key >> 6] |= 1UL << (key & 63);
	}

	public void Drain(List<CrustEntry> entries)
	{
		foreach (int key in WrittenKeys)
		{
			ref CrustSlot slot = ref Slots[key];
			if (!slot.IsDefault) entries.Add(new CrustEntry(key, slot));
			slot = default;
		}
		Array.Clear(_touched);
	}
}
