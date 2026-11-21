using System;
using System.Collections.Generic;
using System.Linq;
using World.Utils;
using World.Utils.H3;

namespace World.Data;

/// <summary>上次地表采样快照；格序固定，采样姿态与当前板块姿态分离，等待搬运模块重采样。</summary>
public sealed class SculptingSurfaceState
{
	public IReadOnlyList<ulong> CellIds { get; }
	public IReadOnlyList<int> PlateOfCell { get; }
	public IReadOnlyList<float> HeightM { get; }
	public IReadOnlyList<CrustType> CrustTypes { get; }
	public IReadOnlyList<int> CrustProvinceIds { get; }
	public IReadOnlyList<CrustProvince> CrustProvinces { get; }
	public IReadOnlyList<RotationD> SampledPlateOrientations { get; }
	public float SeaLevelM { get; }
	public int CellCount => CellIds.Count;
	public int H3Resolution { get; }

	internal SculptingSurfaceState WithHeights(float[] heights)
		=> new(this, heights);

	SculptingSurfaceState(SculptingSurfaceState source, float[] heights)
	{
		if (heights.Length != source.CellCount || heights.Any(height => !float.IsFinite(height)))
			throw new ArgumentException("雕刻高度必须有限且与格序等长", nameof(heights));
		H3Resolution = source.H3Resolution;
		CellIds = source.CellIds;
		PlateOfCell = source.PlateOfCell;
		HeightM = Array.AsReadOnly((float[])heights.Clone());
		CrustTypes = source.CrustTypes;
		CrustProvinceIds = source.CrustProvinceIds;
		CrustProvinces = source.CrustProvinces;
		SampledPlateOrientations = source.SampledPlateOrientations;
		SeaLevelM = source.SeaLevelM;
	}

	/// <summary>复制输入缓冲；板块号与采样姿态表同域，高度和海平面必须有限。</summary>
	/// <example><code>var surface = new SculptingSurfaceState(res, ids, owners, heights, crust, provinceIds, provinces, poses, 0);</code></example>
	public SculptingSurfaceState(int h3Resolution, IEnumerable<ulong> cellIds, IEnumerable<int> plateOfCell,
		IEnumerable<float> heightM, IEnumerable<CrustType> crustTypes, IEnumerable<int> crustProvinceIds,
		IEnumerable<CrustProvince> crustProvinces, IEnumerable<RotationD> sampledPlateOrientations, float seaLevelM)
	{
		if (h3Resolution is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(h3Resolution));
		if (cellIds == null || plateOfCell == null || heightM == null || crustTypes == null
			|| crustProvinceIds == null || crustProvinces == null || sampledPlateOrientations == null)
			throw new ArgumentNullException("surfaceBuffers");
		ulong[] ids = cellIds.ToArray();
		int[] owners = plateOfCell.ToArray();
		float[] heights = heightM.ToArray();
		CrustType[] crust = crustTypes.ToArray();
		int[] provinceIds = crustProvinceIds.ToArray();
		CrustProvince[] provinces = crustProvinces.ToArray();
		RotationD[] poses = sampledPlateOrientations.ToArray();
		if (ids.Length == 0 || owners.Length != ids.Length || heights.Length != ids.Length
			|| crust.Length != ids.Length || provinceIds.Length != ids.Length)
			throw new ArgumentException("地表各列必须非空且与格序等长");
		if (poses.Length == 0 || poses.Any(pose => pose == null))
			throw new ArgumentException("地表必须保留每块板块的采样姿态");
		if (!float.IsFinite(seaLevelM)) throw new ArgumentOutOfRangeException(nameof(seaLevelM));
		var unique = new HashSet<ulong>();
		for (int i = 0; i < ids.Length; i++)
		{
			if (!H3.IsValidCell(ids[i]) || H3.GetResolution(ids[i]) != h3Resolution
				|| !unique.Add(ids[i])) throw new ArgumentException($"格 ID 非法、分辨率不符或重复：{i}");
			if ((uint)owners[i] >= (uint)poses.Length) throw new ArgumentException($"地表板块号越界：{i}");
			if (!float.IsFinite(heights[i]) || !Enum.IsDefined(crust[i]))
				throw new ArgumentException($"地表高度或壳型非法：{i}");
		}
		_ = new CrustField(crust, provinceIds, provinces);
		H3Resolution = h3Resolution;
		CellIds = Array.AsReadOnly(ids);
		PlateOfCell = Array.AsReadOnly(owners);
		HeightM = Array.AsReadOnly(heights);
		CrustTypes = Array.AsReadOnly(crust);
		CrustProvinceIds = Array.AsReadOnly(provinceIds);
		CrustProvinces = Array.AsReadOnly(provinces);
		SampledPlateOrientations = Array.AsReadOnly(poses);
		SeaLevelM = seaLevelM;
	}
}
