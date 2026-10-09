namespace World.Data;

// 数据层 `World.Data`（2026-10-09 建立）：见 ContinentAnchor.cs 头部的入层判据与依赖方向。
/// <summary>特征尺度三元组：纵（沿走向）/ 横（垂直走向）/ 垂（高度幅）——每种特征自己填。</summary>
public readonly struct Scale3
{
	public readonly float LongitudinalKm;   // 山脉 1000-3000 / 山间盆地 50-300 / 火山 5-30（决策 06 §二表）
	public readonly float TransversalKm;    // 山脉 100-400 / 盆地 20-100 / 峡谷 1-20
	public readonly float VerticalM;        // +2500（山）/ −800（盆地）/ −500（峡谷）…

	public Scale3(float longitudinalKm, float transversalKm, float verticalM)
	{
		LongitudinalKm = longitudinalKm;
		TransversalKm = transversalKm;
		VerticalM = verticalM;
	}
}
