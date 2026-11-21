namespace World.Data;

/// <summary>本轮地壳搬运的碰撞或张裂结果，供地形修整读取。</summary>
internal enum SculptingCrustContact : byte
{
	None,
	ContinentalCollision,
	ContinentalMargin,
	OceanicCollision,
	ContinentalStretch,
	OceanSpreading,
}
