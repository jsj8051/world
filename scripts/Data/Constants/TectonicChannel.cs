namespace World.Data;

/// <summary>构造作用的来源通道；每个通道独立求最近边界侧，避免异类边界相互抵消。</summary>
public enum TectonicChannel : byte
{
    ContinentalCollision,
    ContinentalArc,
    OceanicArc,
    Subduction,
    ContinentalRift,
    OceanicRidge,
}
