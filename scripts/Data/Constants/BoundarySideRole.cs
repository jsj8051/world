namespace World.Data;

/// <summary>汇聚边界两侧的运动关系；具体地貌另由两侧壳型决定。</summary>
public enum BoundarySideRole : byte
{
    None,
    Overriding,
    Underthrusting,
}
