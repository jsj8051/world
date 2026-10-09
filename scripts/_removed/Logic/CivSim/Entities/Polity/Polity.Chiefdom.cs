namespace World.CivSim.Entities;

// ══════════════════════════════════════════════════════════════════
// Polity 酋邦层分区（概念 = 机制组合 2026-08-23 拍板 P3：partial 分文件）。
// 本文件：声望/贡赋/继承窗口/政治归属——酋邦配方选用的机制状态字段。
// 概念定义见 桌面 概念机制组合设计.html ③ 配方单：酋邦 = 部落常用 + Prestige/Tribute/
// Patronage/Succession/Absorption；字段均为持久（入档）或派生（重建）状态。
// ══════════════════════════════════════════════════════════════════
public partial class Polity
{
    // ── 酋邦层（2026-08-17：Sahlins 声望/Earle 贡赋/Kirch 联盟——酋邦 = 部落联盟第二层并查集）──
    // 声望/贡赋/继承窗口为累积状态（入档 v10）；BigMan/Chief 从声望+宗教派生（不存档）；
    // ChiefdomId/Size 由 ChiefdomModel 重建（同 Territory 模式，不存档）。
    public float Prestige;            // 声望：盈余→宴席（feasting）积累，Sahlins 1963——可逆、个人化
    public float Contributed;         // 贡赋累计贡献（互惠记录——灾年开仓资格，Halstead-O'Shea 1989）
    public int SuccessionUntil = -1;  // 继承窗口截止 tick（酋长更替→继承战争窗口，Kirch 1984）
    public bool IsBigMan;             // 派生：Prestige ≥ BigManPrestigeThreshold（Melanesia 声望型领袖）
    public bool IsChief;              // 派生：BigMan + 祖先宗教（Polynesia 谱系合法性——divine kingship）
    public int ChiefdomId = -1;       // 酋邦 id = 分量内最小部落 id（跨部落政治整合；-1=无）
    public int ChiefdomSize = 1;      // 酋邦内部落数（≥2 = 正式酋邦）

    // ── 阶段5 军事征服的政治归属（2026-08-19，docs/阶段5设计-军事征服.md）──
    // 战争是外交状态（War 段入档）；ConqueredBy 是战争的**持久效忠痕迹**（v14 入档）：
    public int ConqueredBy = -1;      // 被征服效忠对象（吞并后强制归属战胜国酋长——无视庇护半径；
                                      //   征服者死亡/失势 → ChiefdomModel 重建自动清空，效忠脱落）
}
