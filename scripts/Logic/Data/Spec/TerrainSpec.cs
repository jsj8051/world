namespace World.Data;

/// <param name="TargetRegionAreaKm2">地质区域粒度（km²/区域）。</param>
public readonly record struct TerrainSpec(
	float TargetRegionAreaKm2);
