using Godot;
using World.Biome;

namespace World.Tectonics;

/// <summary>温差诊断：不同倾角/距离下赤道 vs 极地温度。</summary>
public partial class TiltDiag : Node
{
    public override void _Ready()
    {
        GD.Print("[TiltDiag] 赤道(lat=0) vs 极地(lat=85) 温度（无噪声，纯基准）:");
        foreach (var tilt in new[] { 0f, 23.4f, 45f, 90f })
        {
            var c = new ClimateGenerator(42, tilt, 1.0f);
            float eq = TempAt(c, 0f);
            float pol = TempAt(c, 85f);
            GD.Print($"[TiltDiag] 倾角 {tilt,5:F1}°: 赤道={eq,6:F1}°C  极地={pol,6:F1}°C  温差={eq - pol,6:F1}°C");
        }
        GD.Print("[TiltDiag] 不同距离（倾角 23.4°）:");
        foreach (var (name, ins) in new[] { ("0.8AU", 1.5625f), ("1.0AU", 1f), ("1.2AU", 0.6944f) })
        {
            var c = new ClimateGenerator(42, 23.4f, ins);
            float eq = TempAt(c, 0f);
            float pol = TempAt(c, 85f);
            GD.Print($"[TiltDiag] {name}: 赤道={eq,6:F1}°C  极地={pol,6:F1}°C  温差={eq - pol,6:F1}°C");
        }
        GetTree().Quit();
    }

    private static float TempAt(ClimateGenerator c, float latDeg)
    {
        float la = Mathf.DegToRad(latDeg);
        var p = new Vector3(Mathf.Cos(la), Mathf.Sin(la), 0f);
        return c.ComputeTemperature(p * 6330f, 0f);
    }
}
