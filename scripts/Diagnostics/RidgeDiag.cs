using Godot;
using World.Biome;

namespace World.Diagnostics;

/// <summary>
/// 山脉走向 vs 雨影诊断：
/// 构造一个理想高斯山脊（沿经线方向延伸），测垂直/平行风向下的雨影强度。
/// 物理期望：垂直风向 → 沿风向坡度大 → 强雨影；平行风向 → 坡度≈0 → 无雨影。
/// </summary>
public partial class RidgeDiag : Node
{
    public override void _Ready()
    {
        // 理想山脊：以 (lat=20°N, lon=20°E) 为中心，沿经线（南北）延伸的高斯山脊
        // 脊线沿南北 → 山脉走向 = 南北方向
        System.Func<Vector3, float> ridgeElev = p =>
        {
            Vector3 dir = p.Normalized();
            float lat = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(dir.Y, -1f, 1f)));
            float lon = Mathf.RadToDeg(Mathf.Atan2(dir.Z, dir.X));
            // 距脊线（lon=20°E）的经度差 → 山脊宽度；沿脊线方向（lat 变化）不衰减
            float dLon = Mathf.Abs(Mathf.Wrap(lon - 20f, -180f, 180f));
            float dLat = Mathf.Abs(lat - 20f);
            float h = 1f * Mathf.Exp(-(dLon * dLon) / (2f * 3f * 3f)); // σ=3° 宽
            h *= Mathf.Exp(-(dLat * dLat) / (2f * 20f * 20f));          // 沿脊线方向 σ=20°（长脊）
            return h * 0.8f - 0.2f;  // 山脊高 0.8，周围 -0.2（海洋）
        };

        // 山脊东侧一点（迎风侧候选）
        float la = Mathf.DegToRad(20f), lo = Mathf.DegToRad(24f); // 脊线东 4°
        var p = new Vector3(Mathf.Cos(la) * Mathf.Cos(lo), Mathf.Sin(la), Mathf.Cos(la) * Mathf.Sin(lo));

        WindField.Prograde = true;
        WindField.RotationSpeed = 1f;

        // 风向 1：西风（垂直山脉——山脉南北走向，西风从西吹来，垂直撞山）
        // 风向 2：南风（平行山脉——沿脊线吹）
        // 风向 3：东风（垂直，但背风侧）
        foreach (var (name, latDeg, lonDeg) in new[] { ("西风(垂直撞山)", 20f, 20f + 90f), ("东风(垂直，背风侧)", 20f, 20f - 90f), ("南风(平行山脊)", 20f - 90f, 20f) })
        {
            _ = (latDeg, lonDeg); // 风向用 lat/lon 定点构造
        }

        // 手动构造三个风向（切平面）
        // 直接测沿风向坡度：s = elev(p + wind*0.12) - elev(p - wind*0.12)
        void Test(string name, Vector3 wind)
        {
            Vector3 up = (p - wind * 0.12f).Normalized();   // 上风向
            Vector3 dn = (p + wind * 0.12f).Normalized();   // 下风向
            float slope = ridgeElev(dn) - ridgeElev(up);    // 沿风向坡度（+ = 爬坡 = 迎风增雨）
            float score = Mathf.Clamp(slope * 5f, -0.45f, 0.45f);  // 同雨影公式
            GD.Print($"[RidgeDiag] {name}: 上风向海拔={ridgeElev(up):F2} 下风向海拔={ridgeElev(dn):F2} 沿风向坡度={slope:F3} 雨影修正={score:+.2f}");
        }

        // 球面东向（西风）、南向（南风）、西向（东风）
        var east = new Vector3(-Mathf.Sin(lo), 0f, Mathf.Cos(lo)).Normalized();
        var west = -east;
        var north = p.Cross(east).Normalized();
        var south = -north;

        Test("西风（垂直撞山脊）", west);
        Test("东风（垂直，山脊另一侧）", east);
        Test("南风（平行山脊）", south);

        GetTree().Quit();
    }
}
