using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace World.Data.Maps;

internal enum MapIssueCode
{
	UnknownFormat, UnsupportedVersion, UnsupportedScale, MissingBlock, UnexpectedBlock,
	DuplicateEntry, SizeLimit, InvalidShape, ChecksumMismatch, InvalidValue,
	InvalidReference, Cancelled, IoFailure,
}

internal sealed record MapIssue
{
	public MapIssueCode Code { get; }
	public string Location { get; }
	public long? ElementIndex { get; }
	public string Message { get; }

	public MapIssue(MapIssueCode code, string location, long? elementIndex, string message)
	{
		if (!Enum.IsDefined(code)) throw new ArgumentOutOfRangeException(nameof(code));
		if (string.IsNullOrWhiteSpace(location)) throw new ArgumentException("A location is required.", nameof(location));
		if (elementIndex < 0) throw new ArgumentOutOfRangeException(nameof(elementIndex));
		if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("A message is required.", nameof(message));
		(Code, Location, ElementIndex, Message) = (code, location, elementIndex, message);
	}
}

internal sealed class MapStructureException : ArgumentException
{
	public MapIssue Issue { get; }

	public MapStructureException(MapIssue issue) : base(issue?.Message)
		=> Issue = issue ?? throw new ArgumentNullException(nameof(issue));
}

// The clone is required: current WorldFacts arrays remain mutable during generation.
internal sealed class SnapshotArray<T> : IReadOnlyList<T> where T : struct
{
	readonly T[] _values;
	public int Count => _values.Length;
	public T this[int index] => _values[index];

	public SnapshotArray(IEnumerable<T> values)
		=> _values = (values ?? throw new ArgumentNullException(nameof(values))).ToArray();

	public ReadOnlySpan<T> AsSpan() => _values;
	public T[] ToArray() => (T[])_values.Clone();
	public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_values).GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class MapStructure
{
	public static void Require(bool condition, string location, string message,
		MapIssueCode code = MapIssueCode.InvalidValue, long? index = null)
	{
		if (!condition) throw new MapStructureException(new MapIssue(code, location, index, message));
	}

	public static T Present<T>(T value, string location) where T : class
	{
		Require(value != null, location, "A required object is missing.", MapIssueCode.MissingBlock);
		return value;
	}

	public static void Length(int actual, int expected, string location)
		=> Require(actual == expected, location, $"Expected {expected} values, received {actual}.", MapIssueCode.InvalidShape);

	public static void Finite(double value, string location, long? index = null)
		=> Require(double.IsFinite(value), location, "A finite number is required.", index: index);

	public static void Range(double value, double minimum, double maximum, string location, long? index = null)
		=> Require(double.IsFinite(value) && value >= minimum && value <= maximum,
			location, $"Expected a value in [{minimum}, {maximum}].", index: index);

	public static void Index(int value, int count, string location, bool allowMissing = false, long? index = null)
		=> Require((allowMissing && value == -1) || (uint)value < (uint)count,
			location, "The referenced index is out of range.", MapIssueCode.InvalidReference, index);

	public static void Acyclic(IReadOnlyList<int> next, string location)
	{
		var colors = new byte[next.Count];
		for (int start = 0; start < next.Count; start++)
		{
			if (colors[start] != 0) continue;
			int cell = start;
			while (cell != -1 && colors[cell] == 0)
			{
				colors[cell] = 1;
				int destination = next[cell];
				Index(destination, next.Count, location, allowMissing: true, index: cell);
				cell = destination;
			}
			Require(cell == -1 || colors[cell] != 1, location, "A directed cycle is not allowed.",
				MapIssueCode.InvalidReference, start);
			cell = start;
			while (cell != -1 && colors[cell] == 1)
			{
				colors[cell] = 2;
				cell = next[cell];
			}
		}
	}
}