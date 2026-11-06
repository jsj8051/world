namespace World.Data;

/// <summary>一条活动边界在某侧格上的来源；运动和两侧壳型通过 EdgeIndex 查询。</summary>
public readonly struct TectonicSource
{
    public readonly int EdgeIndex;
    public readonly int SideCell;
    public readonly TectonicChannel Channel;

    public TectonicSource(int edgeIndex, int sideCell, TectonicChannel channel)
    {
        EdgeIndex = edgeIndex;
        SideCell = sideCell;
        Channel = channel;
    }
}
