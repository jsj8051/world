namespace World.Application.Pages;

/// <summary>返回主界面的宿主意图：只给"回主界面"这一件事，不给任意跳转能力。</summary>
internal interface IMainMenuReturn
{
	void BackToMainMenu();
}
