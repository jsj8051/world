using World.Application.Loading;
using World.Data;

namespace World.Application.Pages;

/// <summary>请求宿主起一次世界生成：后台只算纯数据，完成后由宿主挂载世界页。</summary>
internal interface IWorldGenerator
{
	void StartGeneration(WorldSpec spec, WorldGenerationOptions options);
}
