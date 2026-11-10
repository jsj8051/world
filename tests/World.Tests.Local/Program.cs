using System;

namespace World.Tests;

/// <summary>
/// 本地测试跑器：`dotnet run --project tests/World.Tests.Local`，退出码 0 = 全绿。
/// 纯 C#（不启引擎、不碰 H3 原生库），pre-commit 与 CI 共用同一入口。
/// </summary>
static class Program
{
	static int _passed;
	static int _failed;

	static int Main(string[] args)
	{
		if (args.Length > 0 && args[0] == "--terrain")
		{
			WorldSmoke.ExportTerrainMap(args.Length > 1 ? int.Parse(args[1]) : 3);
			return 0;
		}

		if (args.Length > 0 && args[0] == "--crust")
		{
			WorldSmoke.ExportCrust(args.Length > 1 ? int.Parse(args[1]) : 3);
			return 0;
		}

		if (args.Length > 0 && args[0] == "--world")
		{
			WorldSmoke.Run(args.Length > 1 ? int.Parse(args[1]) : 3,
				args.Length > 2 && !args[2].StartsWith("--") ? int.Parse(args[2]) : 0,
				Array.IndexOf(args, "--csv") >= 0);
			return 0;
		}

		if (args.Length > 0 && args[0] == "--evolve")
		{
			EvolutionSmoke.Run(args.Length > 1 ? int.Parse(args[1]) : 1,
				args.Length > 2 ? int.Parse(args[2]) : 1);
			return 0;
		}

		if (args.Length > 0 && args[0] == "--evolve-world")
		{
			EvolutionSmoke.CheckWorld(args.Length > 1 ? int.Parse(args[1]) : 4,
				args.Length > 2 ? int.Parse(args[2]) : 100);
			return 0;
		}

		if (args.Length > 0 && args[0] == "--evolve-session")
		{
			EvolutionSessionSmoke.Run();
			return 0;
		}

		if (args.Length > 0 && args[0] == "--evolve-audit")
		{
			EvolutionSmoke.Audit(args.Length > 1 ? int.Parse(args[1]) : 4);
			return 0;
		}

		if (args.Length == 2 && args[0] == "--ridge-transport")
		{
			RidgeTransportAudit.Run(args[1]);
			return 0;
		}

		Console.WriteLine("World.Tests.Local");

		Run("年基温随纬度下降", ClimateTests.LatitudeCoolsTowardPoles);
		Run("零倾角无季节", ClimateTests.ZeroObliquityRemovesSeasons);
		Run("南北半球季节反相", ClimateTests.ObliquityFlipsHemispheres);
		Run("零倾角无季风", ClimateTests.MonsoonVanishesWithoutSeasons);
		Run("气压随季节海陆反转", ClimateTests.PressureFlipsLandSeaSign);
		Run("气压中心落在大陆内部", ClimateTests.PressureCenterSitsInland);
		Run("气压廓线不过冲", ClimateTests.PressureProfileHasNoOvershoot);
		Run("摩擦解在赤道顺梯度", ClimateTests.FrictionSolutionFollowsPressureAtEquator);
		Run("摩擦偏转角随纬度连续", ClimateTests.FrictionTurnsWithLatitude);
		Run("风带从气压梯度涌现", ClimateTests.WindBandsEmergeFromPressure);
		Run("三套风按摩擦单调", ClimateTests.ThreeWindSetsFollowFrictionOrder);
		Run("年均风速是标量均值", ClimateTests.AnnualSpeedIsScalarMean);
		Run("季风指数只在反号时成立", ClimateTests.MonsoonIndexNeedsReversal);
		Run("季风强度跟随气压", ClimateTests.MonsoonStrengthFollowsPressure);
		Run("年降水 = Σ 月", ClimateTests.AnnualPrecipIsMonthlySum);
		Run("ITCZ 随太阳北移", ClimateTests.ItczFollowsTheSun);
		Run("内陆季节摆幅大于海岸", ClimateTests.InteriorHasLargerSeasonalSwing);
		Run("纬度剖面没有台阶", ClimateTests.NoLatitudeRingsInTemperature);
		Run("赤道没有温度台阶", ClimateTests.NoEquatorialStepInTemperature);
		Run("风场切向且有界", ClimateTests.WindIsTangentAndBounded);
		Run("洋流只在海上且切向", ClimateTests.OceanCurrentsStayInTheOcean);
		Run("洋流热启动按原格序清零陆地", ClimateTests.OceanWarmStartResetsLandInGridOrder);
		Run("湿润降温", ClimateTests.WetCoolingLowersTemperature);
		Run("降水估计向内陆衰减", ClimateTests.CoastFactorDecaysInland);
		Run("陆陆汇聚两侧成源", TectonicTests.ContinentalCollisionHasBothSides);
		Run("陆洋汇聚陆侧恒上盘", TectonicTests.MixedCrustPutsArcOnContinentalSide);
		Run("重洋壳俯冲", TectonicTests.DenseOceanPlateSubducts);
		Run("等密度按板块号定上盘", TectonicTests.EqualDensityTieBreakFavorsLowerPlateId);
		Run("同一板块对逐边壳型", TectonicTests.SamePlatePairUsesPerEdgeCrust);
		Run("壳型省格数校验", TectonicTests.CrustFieldRejectsWrongProvinceCount);
		Run("陆壳张裂两侧成裂谷", TectonicTests.ContinentalRiftUsesLocalCrust);
		Run("陆洋张裂只在洋侧成脊", TectonicTests.MixedDivergenceOnlyRidgesOceanSide);
		Run("走滑不产构造来源", TectonicTests.TransformYieldsNoSources);
		Run("非法逐边运动被拒", TectonicTests.BadMotionIsRejected);
		Run("最近来源胜出", TectonicTests.NearestSeedWins);
		Run("通行谓词阻断传播", TectonicTests.PassableBlocksPropagation);
		Run("截断半径停止传播", TectonicTests.CapStopsPropagation);
		Run("同格多源取更短起始距离", TectonicTests.SameCellSeedKeepsSmallerDistanceAndId);
		Run("自由板偏置向内陆递增", TerrainTests.FreeboardRisesInland);
		Run("自由板偏置可关断", TerrainTests.FreeboardGoesSilent);
		Run("自由板偏置校验掩码长度", TerrainTests.FreeboardRejectsWrongMaskLength);

		Run("演化默认参数合法", EvolutionStructureTests.DefaultsAreValid);
		Run("零步长被拒绝", EvolutionStructureTests.RejectsBadStepYears);
		Run("密度顺序被拒绝", EvolutionStructureTests.RejectsDensityOrder);
		Run("参数档含演化段", EvolutionStructureTests.JsonSuppliesEvolutionDefaults);
		Run("参数档与代码默认一致", EvolutionStructureTests.JsonEvolutionMatchesCodeDefaults);
		Run("非法演化参数整层拒绝", EvolutionStructureTests.InvalidJsonEvolutionIsRejectedAtomically);
		Run("球面类型校验不变量", EvolutionStructureTests.SphereTypesEnforceInvariants);
		Run("零步陆壳材料合法", EvolutionStructureTests.ZeroStepContinentalSlotIsValid);
		Run("零步老洋壳保留年龄", EvolutionStructureTests.OldOceanSlotKeepsItsBirthTime);
		Run("洋壳出生晚于当前被拒绝", EvolutionStructureTests.OceanSlotRejectsFutureBirth);
		Run("负壳厚被拒绝", EvolutionStructureTests.SlotRejectsNegativeThickness);
		Run("两种壳型精确覆盖宿主格", EvolutionStructureTests.TwoSlotsCoverCellExactly);
		Run("覆盖不足或超额被拒绝", EvolutionStructureTests.InvalidCoverageIsRejected);
		Run("负地幔库存被拒绝", EvolutionStructureTests.MantleRejectsNegativeStock);
		Run("池转移账本自洽", EvolutionStructureTests.BudgetAcceptsPoolTransfer);
		Run("库存恒等式失配被拒绝", EvolutionStructureTests.BudgetRejectsMantleIdentityMismatch);
		Run("投影长度不一致被拒绝", EvolutionStructureTests.ProjectionRejectsLengthMismatch);
		Run("零步帧对齐校验", EvolutionStructureTests.FrameValidatesAlignedZeroStep);
		Run("跳转中间帧延迟生成地表", EvolutionStructureTests.FrameCanDeferSurfaceDuringSeek);
		Run("帧库存不一致被拒绝", EvolutionStructureTests.FrameRejectsMantleMismatch);
		Run("时钟不足一步向上取整", EvolutionStructureTests.ClockRoundsUpPartialSteps);

		Run("零步搬运体积守恒", EvolutionLogicTests.ZeroStepConservesVolume);
		Run("海底年龄随时间增加", EvolutionLogicTests.OceanAgeAdvancesWithTime);
		Run("板块旋转并混合材料", EvolutionLogicTests.PlatesRotateAndMixMaterial);
		Run("共同旋转无伪构造", EvolutionLogicTests.CommonRotationDoesNotCreateTectonics);
		Run("多子步输运保留来源帧", EvolutionLogicTests.MultiSubstepTransportKeepsSourceFrame);
		Run("跨掩码字的板块仍能输运", EvolutionLogicTests.TransportSupportsPlateIdsAboveFirstMaskWord);
		Run("极小材料尾量同时舍入面积和体积", EvolutionLogicTests.TransportRoundsUnderflowingSlotAsOneContribution);
		Run("大陆裂谷先减薄再破裂", EvolutionLogicTests.ContinentalRiftThinsBeforeMakingOcean);
		Run("陆陆汇聚增厚抬升", EvolutionLogicTests.ContinentalCollisionThickensCrust);
		Run("生产格心重分布碰撞抬升", EvolutionLogicTests.CollisionReliefRedistributesOnTheSimulationGrid);
		Run("俯冲余量保留陆壳占地", EvolutionLogicTests.SubductionOverflowKeepsContinentalArea);
		Run("混合格覆盖修正保留陆壳占地", EvolutionLogicTests.MixedCoverageCorrectionKeepsContinentalArea);
		Run("混合海岸保留实际露出面积", EvolutionLogicTests.MixedCoastKeepsItsEmergentArea);
		Run("库存耗尽原子回退", EvolutionLogicTests.InsufficientMantleKeepsPreviousFrame);
		Run("汇聚俯冲消耗洋壳", EvolutionLogicTests.ConvergenceSubductsOceanicCrust);
		Run("张裂生成新洋壳", EvolutionLogicTests.DivergenceCreatesNewOceanicCrust);
		Run("边界分类识别俯冲", EvolutionLogicTests.BoundaryFieldClassifiesSubduction);
		Run("陆洋汇聚形成陆弧和海沟", () => EvolutionLogicTests.SubductionFormsAnUpperArc(continentalUpper: true));
		Run("洋洋汇聚形成岛弧和海沟", () => EvolutionLogicTests.SubductionFormsAnUpperArc(continentalUpper: false));
		Run("陆洋张裂仅在洋侧生成洋脊", () => EvolutionLogicTests.DivergenceFollowsCrustOnEachSide(continentalSide: true));
		Run("洋洋张裂生成洋脊和年轻洋壳", () => EvolutionLogicTests.DivergenceFollowsCrustOnEachSide(continentalSide: false));
		Run("表层陆壳保留在混合格上盘", EvolutionLogicTests.SurfaceCrustFollowsItsPlateInAMixedCell);
		Run("重复运行确定", EvolutionLogicTests.RepeatedRunIsDeterministic);
		Run("分步与跳转一致", EvolutionLogicTests.TwoStepsEqualSeekToStepTwo);
		Run("取消候选计算后可从原帧重试", EvolutionLogicTests.CandidateCancellationKeepsSourceFrame);
		Run("大地质步自动拆成安全构造子步", EvolutionLogicTests.OversizedStepSplitsIntoTectonicSubsteps);

		Run("地图清单接受头信息而不冒充完整校验", MapManifestTests.ManifestAcceptsHeaderWithoutDirectory);
		Run("地图清单拒绝未知格式", MapManifestTests.ManifestRejectsUnknownFormat);
		Run("地图清单拒绝不支持版本", MapManifestTests.ManifestRejectsUnsupportedVersion);
		Run("数据块描述复制形状并校验字节数", MapManifestTests.DescriptorCopiesShapeAndChecksByteLength);
		Run("完整布局拒绝缺少必要块", MapManifestTests.ManifestLayoutRejectsMissingRequiredBlocks);
		Run("地图清单拒绝重复块", MapManifestTests.ManifestRejectsDuplicateDescriptors);
		Run("不支持探测结果必须带问题", MapManifestTests.ProbeRequiresIssueForUnsupportedResults);

		Run("气候结构拒绝错误长度", MapClimateTests.RejectsWrongLengths);
		Run("气候结构拒绝越界数值", MapClimateTests.RejectsOutOfRangeValues);
		Run("气候结构拒绝非法向量", MapClimateTests.RejectsInvalidVectors);
		Run("气候结构冻结缓冲并保留浮点位", MapClimateTests.ClonesBuffersAndPreservesFloatBits);

		Run("表层结构冻结输入与返回副本", MapGeologyTests.SurfaceFreezesInputAndReturnedCopies);
		Run("投影表层保留全部事实与空年龄", MapGeologyTests.ProjectedSurfacePreservesAllFactsAndNullAge);
		Run("表层拒绝缺组、错形状与未知壳型", MapGeologyTests.SurfaceRejectsMissingGroupMembersShapesAndUnknownCrust);
		Run("表层拒绝越界事实与运动不一致", MapGeologyTests.SurfaceRejectsOutOfRangeFactsAndMotionMismatch);
		Run("可空地质列校验掩码与有效值", MapGeologyTests.NullableGeologyColumnsValidateMasksAndActiveValues);
		Run("构造结构保留顺序且允许省种子移动", MapGeologyTests.TectonicsPreservesOrderingAndAllowsMovedProvinceSeed);
		Run("构造结构拒绝板块与省引用越界", MapGeologyTests.TectonicsRejectsPlateAndProvinceReferences);
		Run("构造结构拒绝错配边界对与CSR", MapGeologyTests.TectonicsRejectsMalformedBoundaryPairsAndCsr);
		Run("边界场保留分类与带符号物理速度", MapGeologyTests.BoundaryFieldsRetainClassificationsAndSignedPhysicalSpeeds);
		Run("构造影响保留空通道与缺源语义", MapGeologyTests.InfluenceChannelsPreserveEmptyAndMissingSourceSemantics);
		Run("构造影响拒绝不完整或非法结构", MapGeologyTests.InfluenceChannelsRejectIncompleteOrInvalidStructures);
		Run("地质线码按语义映射并拒绝未知值", MapGeologyTests.GeologyWireMappingsUseSemanticCodesAndRejectUnknowns);

		Run("快照缓冲拥有自身数据并保留浮点位", MapCoreStructureTests.BuffersOwnTheirValuesAndKeepFloatBits);
		Run("结构错误保留位置与下标", MapCoreStructureTests.StructuralErrorsKeepTheirLocationAndIndex);
		Run("网格拒绝重复ID且不重新枚举", MapCoreStructureTests.GridRejectsDuplicateIdentitiesWithoutEnumeration);
		Run("地形校验掩码与海平面", MapCoreStructureTests.TerrainValidatesMasksAndSeaLevel);
		Run("地形保留无来源距离哨兵", MapCoreStructureTests.TerrainKeepsAbsentDistanceSources);
		Run("查询标签与距离必须一致", MapCoreStructureTests.QueryLabelsAndDistancesMustAgree);
		Run("图结构拒绝环与非法链接", MapCoreStructureTests.GraphStructureRejectsCyclesAndInvalidLinks);
		Run("场景契约不暴露未实现入口", MapCoreStructureTests.SceneContractsKeepUnsupportedScreensUnreachable);
		Run("操作拒绝过期与已取消提交", MapCoreStructureTests.OperationsRejectStaleAndCancelledPublication);
		Run("浏览请求不回退到生成", MapCoreStructureTests.BrowseRequestNeverFallsBackToGeneration);
		Run("原生网格保持文件序并拒绝非法格", MapCoreStructureTests.NativeGridKeepsFileOrderAndRejectsInvalidCells);

		Run("最小水文结构合法", MapHydrologyTests.MinimalHydrologyValidates);
		Run("原始洼地与路由流域命名空间分离", MapHydrologyTests.RawDepressionsStaySeparateFromRoutedBasins);
		Run("湖泊保留无溢口标记", MapHydrologyTests.LakeLayersPreserveNoRimMarker);
		Run("水文拒绝错误长度", MapHydrologyTests.HydrologyRejectsWrongLengths);
		Run("水文拒绝循环", MapHydrologyTests.HydrologyRejectsCycles);
		Run("水文拒绝错配河线与引用", MapHydrologyTests.HydrologyRejectsMalformedLinesAndReferences);

		Run("生成世界经快照往返保持一致", MapRoundTripTests.GeneratedWorldRoundTripsThroughSnapshot);
		Run("地图包写入读出保持事实", MapRoundTripTests.PackageWriteReadPreservesFacts);
		Run("地图包拒绝缺失与损坏文件", MapRoundTripTests.PackageRejectsMissingAndCorruptFiles);
		Run("加载操作身份与初始状态", LoadStructureTests.OperationValidatesIdentityAndState);
		Run("进度快照终态不变量", LoadStructureTests.SnapshotEnforcesTerminalInvariants);
		Run("阶段计划权重与唯一性", LoadStructureTests.StagePlansAreWellFormed);
		Run("演化进度与换帧边界", LoadStructureTests.EvolutionAndSwapBoundaries);
		Run("进度单调与终态吸收", LoadStructureTests.OperationTransitionsAreMonotonic);
		Run("取消触发令牌", LoadStructureTests.CancelCancelsToken);
		Run("并行读取按块上报进度", MapRoundTripTests.ParallelReadReportsBlockProgress);
		Run("浏览 Job 上报进度并装配事实", MapRoundTripTests.BrowseJobReportsProgressAndBuildsFacts);
		Run("监控器按信号推进进度", LoadProgressMonitorTests.MonitorAdvancesFromSignals);
		Run("监控器独立完成或失败", LoadProgressMonitorTests.MonitorCompletesOrFailsIndependently);
		Run("生成 Job 不暴露进度类型", LoadProgressMonitorTests.GenerationJobDoesNotExposeProgress);

		Run("Generation config defaults", GenerationConfigStructureTests.DefaultsAndEnumSelectionsAreValid);
		Run("Generation config plate bounds", GenerationConfigStructureTests.PlateCountEnforcesApprovedBounds);
		Run("Generation config unknown enums", GenerationConfigStructureTests.UnknownEnumValuesAreRejected);
		Run("Generation profile metadata", GenerationConfigStructureTests.ProfileRequiresIdentityNameAndValidConfig);
		Run("Generation catalog uniqueness", GenerationConfigStructureTests.CatalogEnforcesVersionAndUniqueProfiles);
		Run("Generation profile file version", GenerationConfigStructureTests.SingleProfileFileValidatesVersionAndProfile);
		Run("Generation JSON structure", GenerationConfigStructureTests.JsonRequiresCompleteKnownFields);
		Run("Generation structure boundaries", GenerationConfigStructureTests.AdditionalStructureBoundariesAreEnforced);

		Run("Config apply and generation gate", GenerationConfigLogicTests.ApplyIsMemoryOnlyAndGenerationRequiresCleanAppliedConfig);
		Run("Config named profile lifecycle", GenerationConfigLogicTests.ProfileNamingSelectionAndDeletionRemainSeparateFromApply);
		Run("Config strict import export", GenerationConfigLogicTests.ImportExportAreStrictAndDoNotApplyOrPersist);
		Run("Config failed storage rollback", GenerationConfigLogicTests.StoreFailuresDoNotPartiallyCommitSession);
		Run("Config damaged catalog protection", GenerationConfigLogicTests.InvalidCatalogIsProtectedButTemporaryGenerationWorks);
		Run("Config copy isolation", GenerationConfigLogicTests.SessionClonesAllInboundAndOutboundData);
		Run("Config parameter mapping", GenerationConfigLogicTests.MapperCopiesDefaultsAndMapsEveryPreset);
		Run("Config atomic file persistence", GenerationConfigLogicTests.FileStoreRoundTripsAndFailedReplacePreservesOriginal);

		Console.WriteLine($"\n{_passed} passed, {_failed} failed");
		return _failed == 0 ? 0 : 1;
	}

	static void Run(string name, Action test)
	{
		try
		{
			test();
			_passed++;
			Console.WriteLine($"  ok   {name}");
		}
		catch (Exception e)
		{
			_failed++;
			Console.WriteLine($"  FAIL {name}: {e}");
		}
	}

	/// <summary>条件不成立即失败。</summary>
	public static void True(bool condition, string message)
	{
		if (!condition) throw new Exception(message);
	}

	/// <summary>数值落在容差内。</summary>
	public static void Near(double actual, double expected, double tolerance, string message)
	{
		if (Math.Abs(actual - expected) > tolerance)
			throw new Exception($"{message}: 实得 {actual:G6}，期望 {expected:G6} ± {tolerance:G3}");
	}

	/// <summary>动作必须抛异常。</summary>
	public static void Throws(Action action, string message)
	{
		try { action(); }
		catch { return; }
		throw new Exception($"{message}（没有抛异常）");
	}
}
