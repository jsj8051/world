using System;

namespace World.Application.Pages;

internal sealed class MainMenuController
{
	readonly IMainMenuActions _actions;
	readonly Action _quitApplication;

	internal MainMenuController(IMainMenuActions actions, Action quitApplication)
	{
		_actions = actions ?? throw new ArgumentNullException(nameof(actions));
		_quitApplication = quitApplication ?? throw new ArgumentNullException(nameof(quitApplication));
	}

	internal void OpenGenerationConfig() => _actions.OpenGenerationConfig();
	internal void OpenMapLibrary() => _actions.OpenMapLibrary();
	internal void QuitApplication() => _quitApplication();
}
