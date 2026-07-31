using Godot;

public class HexTile
{
    public int Id { get; set; }
    public Vector3 Center { get; set; }
    public Vector3[] Corners { get; set; }
    public int[] CornerFaceIndices { get; set; }
    public int[] Neighbors { get; set; }
    public bool IsPentagon { get; set; }
    public float Elevation { get; set; }
}
