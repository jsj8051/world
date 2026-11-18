using System;

namespace World.Application.Components;

/// <summary>配置库列表所需的最小显示投影，不暴露领域档案类型。</summary>
internal readonly record struct ProfileLibraryItem(Guid Id, string Name);
