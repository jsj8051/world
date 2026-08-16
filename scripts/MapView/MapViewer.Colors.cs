// Slice: MapViewer.Colors.cs - verbatim member extraction from MapViewer.cs (pure refactor, 2026-08-19).
using Godot;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using World.Biome;
using World.MapGen;
using World.HexPlanet;
using World.PlanetLOD;
using World.Surface;
using World.UI;
using World.Camera;

namespace World.MapView;

public partial class MapViewer
{

    /// <summary>图层 → 颜色函数（查预计算缓存，零采样）。</summary>
    /// ⚠️ 2026-08-02 大改进：参数化 layer（不读共享字段）——原内部 switch(Layer) 在后台
    ///   线程每次调用读 _layer，主线程切图层写它 → 竞态 → 偶发颜色错图层/"未切换成功"。</summary>
    private Func<HexTile, Color> MakeColorFn(int layer)
    {
    	return t =>
    	{
    		int id = t.Id;
    		switch (layer)
    		{
    			case 1: // 温度
    				return BiomeColors.TemperatureToColor(_tileTemp[id]);
    						case 2: // 降水：自适应色带（陆地 min-max 归一化，用户拍板；固定 2000mm 已被批）
    							{
    								float x = Mathf.Clamp((_tilePrecip[id] - _precipMin) / (_precipMax - _precipMin), 0f, 1f);
    								return new Color(0.90f, 0.80f, 0.40f).Lerp(new Color(0.10f, 0.30f, 0.70f), x);
    							}
    			case 3: // biome
    				return BiomeColors.BiomeToColor((BiomeType)_tileBiome[id]);
    						case 4: // 风场：浅色底（统一风场箭头由 _monsoonArrows 3D 网格显示，月份滑块切换）
    						case 5: // 洋流：浅色底（箭头由 _currentArrows 3D 网格显示）
    			case 6: // 河流：浅色底（河道由 _riverMesh 3D 网格显示，湖格填湖蓝）
    				{
    					// ⚠️ 2026-08-02：湖泊 = 陆地盆地 + 水量≥阈值（RiverSystem 已过滤干湖）。
    					//   湖格单色湖蓝（用户确认：单色、放河流图层）；其他格淡色底突出河道。
    					if (_tileLake[id] > 0)
    						return new Color(0.25f, 0.45f, 0.75f);   // 湖蓝（单色）
    					float h = _tileElev[id];
    					bool ocean = IsDisplaySea(id);   // ⚠️ 2026-08-17：统一海陆判定（近海逻辑陆地=陆地）
    					return ocean ? SeaColor : new Color(0.72f, 0.68f, 0.55f);
    				}
    			case 7: // 流域：每流域独立颜色（黄金角）；海洋浅蓝、边缘排水区灰绿
    				{
    					int ws = _tileWatershed[id];
    					if (ws < 0)
    						return IsDisplaySea(id)   // ⚠️ 2026-08-17：统一海陆判定
    							? SeaColor   // 海洋
    							: new Color(0.60f, 0.58f, 0.50f);  // 边缘排水区（直接入海，非河）
    										return HslToRgb(GoldenHue(ws), 0.55f, 0.62f);
    				}
    			case 8: // 矿藏：矿种固定色 × 富度明度（贫暗/富中/巨型亮）；无矿淡地形底
    				{
    					byte m = _tileMineral[id];
    					if (m == 0)
    					{
    						return IsDisplaySea(id)   // ⚠️ 2026-08-17：统一海陆判定
    							? SeaColor
    							: new Color(0.55f, 0.52f, 0.42f);
    					}
    					var baseC = MineralColors[MineralSystem.TypeOf(m) % MineralColors.Length];
    					float bright = MineralSystem.RichnessOf(m) switch { 1 => 0.55f, 2 => 0.78f, _ => 1.0f };
    					return baseC * bright;
    				}
    			case 9: // 土壤肥力：5 档色带（深绿=肥沃 → 灰=贫瘠）；海洋浅蓝
    			{
    			byte s = _tileSoil[id];
    			if (s == 0)
    			return SeaColor;   // 海洋
    			return SoilColors[Mathf.Clamp(s, 1, 5)];
    			}
    																			case 10: // 月降水：和总降水同一自适应色带（当月陆地 min-max 归一化；月份滑块切换）
    																				{   // ⚠️ 2026-08-16 v3（用户拍板）：与总降水同色带同统计方式；×12 换算回年尺度
    																					//   → 非季风区≈年降水色，季风区 7 月深蓝 / 1 月枯黄；min-max 自适应当月分布
    																					if (_tileMonthPrecip == null || _map == null || _map.MonthPrecip == null)
    																						return IsDisplaySea(id)   // ⚠️ 2026-08-17：统一海陆判定
    																							? SeaColor
    																							: new Color(0.72f, 0.70f, 0.58f);
    																					if (IsDisplaySea(id)) return SeaColor;
    																					float mm = FieldCodec.ByteMonthPrecipToMm(_tileMonthPrecip[id], _tilePrecip[id]) * 12f;   // 等效年尺度（比例×年降水×12）
    																					float x = Mathf.Clamp((mm - _monthPrecipMin) / (_monthPrecipMax - _monthPrecipMin), 0f, 1f);
    																					return new Color(0.90f, 0.80f, 0.40f).Lerp(new Color(0.10f, 0.30f, 0.70f), x);
    																				}
    						case 11: // 月温度：当月均温色块（MonthTemp −60~60°C→0-255；月份滑块切换）
    							{
    								if (_tileMonthTemp == null || _map == null || _map.MonthTemp == null)
    									return IsDisplaySea(id)   // ⚠️ 2026-08-17：统一海陆判定
    										? SeaColor
    										: new Color(0.72f, 0.70f, 0.58f);
    								float tC = FieldCodec.ByteToTemp(_tileMonthTemp[id]);   // byte → °C
    								return BiomeColors.TemperatureToColor(tC);
    								}
    								case 12: // 人口：log 压缩 + P1/P99 分位自适应色带（无人=暗灰；黄→橙红）
    								{
    								    if (IsDisplaySea(id) && _tilePop[_tileIndex.FaceToVertex(id)] <= 0f) return SeaColor;   // ⚠️ 显示海（真海）；近海逻辑陆地=陆地底
    								    float p = _tilePop[_tileIndex.FaceToVertex(id)];
    								    if (p <= 0f) return new Color(0.25f, 0.25f, 0.28f);   // 无人陆地
    								    float x = Mathf.Clamp((Mathf.Log(p + 1f) - _popLogMin) / (_popLogMax - _popLogMin), 0f, 1f);
    								    return new Color(0.95f, 0.75f, 0.25f).Lerp(new Color(0.80f, 0.15f, 0.05f), x);
    								}
    								case 13: // 文化：同语言群同色系（hue=群，深浅=具体文化）——2026-08-19 修复"大量飞地"：
    								    //   分裂漂变产生数百微文化（n128 实测 581 种）→ 每文化独立色=彩虹孤岛；
    								    //   按语言群分色系 → 相关文化可见相关（同族同色渐变），族域连贯无飞地。
    								    //   2026-08-19 定案：统一着色（无定居亮/领地淡深浅区分——用户"直接补齐"）
    								{
    								    if (IsDisplaySea(id) && _tileCulture[id] == 0) return SeaColor;
    								    int cult = _tileCulture[id];
    								    if (cult == 0) return new Color(0.25f, 0.25f, 0.28f);
    								    int grp = _tileTerritory != null && id < _tileTerritory.Length ? _tileTerritory[id] : 0;
    								    return FamilyColor(grp, cult, 0.55f, 0.20f);
    								}
    								case 14: // 独立势力（2026-08-17）：每势力独立色——**最远点采样调色板**（2026-08-16 定案）
    								    //   最高聚合层显示：酋邦（跨部落联盟）> 部落（领地≥2）> 独立 band
    								    {
    								        if (IsDisplaySea(id) && _tilePower[id] == 0) return SeaColor;
    								        int powerId = _tilePower[id];
    								        if (powerId == 0) return new Color(0.25f, 0.25f, 0.28f);
    								        if (_powerPalette != null && _powerPalette.TryGetValue(powerId, out var pc)) return pc;
    								        return PowerColor(powerId);   // 兜底（理论不触发——调色板覆盖全部显示 id）
    								    }
    								    case 15: // 科技：主导部落最高技术时代色带（石器棕→新石器绿→青铜橙→铁器蓝→古典紫）
    								    {
    								        if (IsDisplaySea(id) && _tileTribe[id] < 0) return SeaColor;
    								        if (_tileTribe[id] < 0) return new Color(0.25f, 0.25f, 0.28f);   // 无人
    								        byte ep = _tileTechEpoch[id];
    								        return ep == 0 ? new Color(0.55f, 0.42f, 0.28f)   // 石器：棕（有基础技术，非"无"）
    								            : TechEpochColors[Mathf.Clamp(ep - 1, 0, TechEpochColors.Length - 1)];
    								    }
    								case 16: // 宗教：同语言群同色系（hue=群，深浅=具体派别）——2026-08-19 与 13 同修"大量飞地"
    								{
    								    if (IsDisplaySea(id) && _tileTribe[id] < 0) return SeaColor;
    								    if (_tileTribe[id] < 0) return new Color(0.25f, 0.25f, 0.28f);   // 无人
    								    int rel = _tileReligion[id];
    								    if (rel == 0) return new Color(0.25f, 0.25f, 0.28f);
    								    int grp = _tileTerritory != null && id < _tileTerritory.Length ? _tileTerritory[id] : 0;
    								    return FamilyColor(grp, rel, 0.55f, 0.20f);
    								}
    								case 17: // 势力范围：每领地独立色（最远点采样调色板——2026-08-16 修复"全白"：
    								    //   旧版明度 0.85 近白 + 散列近撞色；无领地/无人灰）
    								{
    								    if (IsDisplaySea(id) && _tileTerritory[id] == 0) return SeaColor;
    								    int terr = _tileTerritory[id];
    								    // ⚠️ 2026-08-17：领地按归属显示全领地（不能再用人口判"无人"——
    								    //   人口图层已改只在驻扎格显示，采集格人口=0）
    								    if (terr == 0) return new Color(0.30f, 0.32f, 0.36f);
    								    if (_territoryPalette != null && _territoryPalette.TryGetValue(terr, out var tc)) return tc;
    								    return HslToRgb(AvoidSeaHue(GoldenHue(terr)), 0.55f, 0.62f);   // 兜底（理论不触发）
    								}
    								case 18: // 政体（2026-08-17）：独立势力基础上按政体类型分色——
    								    //   band=灰蓝 部落=绿 酋邦=红橙 国家=金（2026-08-16 阶段4 国家涌现）
    								    //   纯政体色（2026-08-18 用户：部落为何多色——去掉势力微扰——
    								    //   政体地图=政体类型色，势力区分看独立势力图层 14）
    								    {
    								        if (IsDisplaySea(id) && _tilePower[id] == 0) return SeaColor;
    								        int powerId = _tilePower[id];
    								        if (powerId == 0) return new Color(0.25f, 0.25f, 0.28f);
    								        float hue = _tilePolity[id] switch
    								        {
    								            3 => 0.12f,    // 国家：金（王权/官僚——制度化）
    								            2 => 0.045f,   // 酋邦：红橙
    								            1 => 0.35f,    // 部落：绿
    								            _ => 0.60f,    // band：灰蓝
    								        };
    								        return HslToRgb(hue, 0.45f, 0.55f);
    								        }
    								        case 19: // 聚落（2026-08-19 阶段3 聚落设计）：新村→城市分级色 + 废墟灰；无聚落暗底
    								        {
    								            if (IsDisplaySea(id) && _tileSettlement[id] == 0) return SeaColor;
    								            byte sl = _tileSettlement[id];
    								            if (sl == 0) return new Color(0.22f, 0.22f, 0.25f);   // 无聚落陆地（暗底——突出聚落）
    								            return SettlementLevelColors[Mathf.Clamp(sl - 1, 0, SettlementLevelColors.Length - 1)];
    								        }
    								        default: // 海拔（2026-08-18 用户拍板）：按实际米分色——
    								            //   海：<-200m 深海（深蓝）/ -200~0m 浅海（亮蓝——大陆架 200m 等深线）
    								            //   陆：连续色带（0m→最高——沙→绿→棕→白——无分段）
    								        {
    								        	float h = _tileElev[id];
    								        	int vidE = _tileIndex != null ? _tileIndex.FaceToVertex(id) : id;
    								        	float elevM = _map.Elev != null ? _map.Elev[vidE] : (h - _hSea) * (_map.MaxElev - _map.MinElev);   // 米（0=海平面）
    								        	if (IsDisplaySea(id))
    								        	{
    								        		// ⚠️ 2026-08-18 海冰（用户：两极应该冰盖不是海洋）：温度 ≤-5°C 的海 = 海冰（极地冰盖——白）。
    								        		//   注意：此为【显示层】海冰判据（-5°C，地形定案 08-18），不同于 BiomeClassifier.SeaIceTempC（-2°C，柯本 FrigidOcean 分类）——两者语义不同，勿合并。
    								        		float seaTemp = _map.Temp != null ? _map.Temp[vidE] : 15f;
    								        		if (seaTemp <= -5f) return new Color(0.92f, 0.95f, 1.00f);   // 海冰（白——极地冰盖）
    								        		if (elevM < -200f) return new Color(0.01f, 0.05f, 0.18f);   // 深海 <-200m
    								        		return new Color(0.20f, 0.45f, 0.68f);                      // 浅海 -200~0m（大陆架）
    								        	}
    								        	// 陆地：海拔色带（沙/绿/棕按米）——雪（白）由实际温度驱动（2026-08-18 用户：雪线按实际温度）
								        	//   0°C 以下全白（雪线=0°C 等温线——纬度/气候决定——非固定 3300m）；0~2°C 渐变
								        	float tempC = _map.Temp != null ? _map.Temp[vidE] : 15f;
								        	Color baseC;
								        	if (elevM <= 0f) baseC = new Color(0.76f, 0.70f, 0.50f);
								        	else if (elevM < 100f) baseC = new Color(0.76f, 0.70f, 0.50f).Lerp(new Color(0.30f, 0.65f, 0.10f), elevM / 100f);
								        	else if (elevM < 800f) baseC = new Color(0.30f, 0.65f, 0.10f).Lerp(new Color(0.60f, 0.50f, 0.35f), (elevM - 100f) / 700f);
								        	else baseC = new Color(0.60f, 0.50f, 0.35f);
								        	float snowT = Mathf.Clamp(1f - tempC / 2f, 0f, 1f);   // ≤0°C 全白；0~2°C 渐变；>2°C 无雪
								        	return baseC.Lerp(new Color(0.95f, 0.97f, 1.00f), snowT);
    								        }
    			}
    			};
    			}


    			/// <summary>切图层：几何缓存命中 → 只重算颜色（查表，秒级）；无缓存（首次/GridN 刚变）→ 全量。
    /// ⚠️ 2026-08-02：几何未就绪时【禁止】调用 Generate()——构建中切图层会取消当前构建并重启，
    ///   快速连点=无限取消重启，几何永远构建不完 → 图层不切换。改为设置 _pendingRecolor，
    ///   等当前构建完成（FinishGenerate）后自动应用最新图层。</summary>
    private void RebuildColors()
    {
        if (!_geometryReady || _tiles == null)
        {
            _pendingRecolor = true;   // 构建完成后自动重算颜色（用最新 Layer）
            GD.Print($"[MapViewer] RebuildColors: 几何未就绪 → pendingRecolor（Layer={_layer}）");
            return;
        }

        int version = ++_buildVersion;
        int layer = _layer;   // ⚠️ 主线程快照（后台只读快照，不碰共享字段）
        _cts?.Cancel();   // 取消旧重算任务
        _cts = new System.Threading.CancellationTokenSource();
        var token = _cts.Token;
        _progress = 0f;
        _phase = "重算颜色";
        ShowProgress();
        GD.Print($"[MapViewer] RebuildColors: v{version} Layer={layer} 启动后台着色");
        _buildTask = Task.Run(() => BuildColorsTask(_map, version, token, layer), token);
        _buildTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
                GD.PrintErr($"[MapViewer] recolor failed: {t.Exception?.GetBaseException().Message}\n{t.Exception?.GetBaseException().StackTrace}");
            CallDeferred(nameof(FinishGenerate), version);
        });
    }


    /// <summary>后台线程：只重算颜色（查预计算缓存，零采样）。
    /// ⚠️ 2026-08-02 大改进：layer 参数化快照——后台不读 _layer 字段（消除竞态）；
    ///   进度回调查 token，取消可中断（旧任务快速让位新图层）。</summary>
    private MeshData BuildColorsTask(MapData map, int version, System.Threading.CancellationToken token, int layer)
    {
        var geometry = _geometry; // 已就绪（_geometryReady 保证，不碰 Godot 对象）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var colors = ChunkMeshBuilder.BuildColors(_tiles, MakeColorFn(layer), geometry,
            p =>
            {
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException(token);   // 取消中断（快速让位）
                _progress = 0.05f + p * 0.9f;
            });
        if (token.IsCancellationRequested) return default;
        _progress = 1f;
        return new MeshData
        {
            Verts = geometry.Verts,
            Normals = geometry.Normals,
            Colors = colors,
            Indices = geometry.Indices
        };
    }


    /// <summary>独立势力颜色**兜底散列**（2026-08-16）：主路径已改用最远点采样调色板 _powerPalette
    /// （任意两势力色距有下界）；此处仅覆盖调色板未收录的 id（理论不触发）。hue=黄金角（避开海蓝）
    /// + S/L=独立乘法散列（Knuth/素数，与色相 φ 解耦——低位段对相近 id 高度相关是原 3D 版撞色根源）。</summary>
    private static Color PowerColor(int powerId)
    {
        uint h = (uint)powerId;
        float hue = AvoidSeaHue(GoldenHue(powerId));
        uint s1 = h * 2654435761u;   // 乘法散列（uint 回绕）
        uint s2 = h * 40503u;
        float sat = 0.35f + 0.55f * (s1 >> 24) / 255f;    // 饱和度 0.35-0.90
        float lig = 0.30f + 0.50f * (s2 >> 24) / 255f;    // 明度 0.30-0.80
        return HslToRgb(hue, sat, lig);
    }


    /// <summary>族系分色（2026-08-19 "大量飞地"修复）：hue = 语言群哈希（族色相），明度 = 具体文化/派别哈希（族内深浅）。
    /// 分裂漂变产生数百微文化 → 每文化独立色=彩虹孤岛；同群同色系 → 相关文化可见相关、族域连贯（类语言族地图）。</summary>
    private static Color FamilyColor(int groupHash, int itemHash, float lightBase, float lightSpan)
    {
        float hue = GoldenHue(groupHash != 0 ? groupHash : itemHash);
        float shade = (itemHash & 0xFF) / 255f;
        return HslToRgb(hue, 0.55f, lightBase + lightSpan * shade);
    }


    private static Texture2D MakeLayerIcon(int idx)
    {
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(LayerIcons[idx]);
            var img = new Image();   // LoadSvgFromBuffer 是实例方法（返回 Error）
            if (img.LoadSvgFromBuffer(bytes) != Error.Ok)
            {
                GD.PrintErr($"[MapViewer] SVG icon {idx} load failed");
                return null;
            }
            img.Resize(28, 28, Image.Interpolation.Bilinear);
            return ImageTexture.CreateFromImage(img);
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"[MapViewer] SVG icon {idx} failed: {e.Message}");
            return null;
        }
    }

}
