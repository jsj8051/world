using World.Data.Generation;

namespace World.Application.Pages;

internal interface IGenerationConfigPageActions
{
	void BackToMainMenu();
	void StartGeneration(GenerationConfig config);
}
