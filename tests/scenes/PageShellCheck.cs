using System;
using Godot;
using World.Application.Pages;

namespace World.Tests;

/// <summary>不实例化页面的主菜单控制器动作与依赖合同检查。</summary>
internal static class PageShellCheck
{
	sealed class MenuActions : IMainMenuActions
	{
		public int OpenedConfig;
		public int OpenedLibrary;
		public void OpenGenerationConfig() => OpenedConfig++;
		public void OpenMapLibrary() => OpenedLibrary++;
	}

	static int _assertions;

	internal static void Run()
	{
		_assertions = 0;
		var actions = new MenuActions();
		int quitCalls = 0;
		var controller = new MainMenuController(actions, () => quitCalls++);
		Require(actions.OpenedConfig == 0 && actions.OpenedLibrary == 0 && quitCalls == 0,
			"Constructing the controller must not invoke actions.");
		controller.OpenGenerationConfig();
		Require(actions.OpenedConfig == 1 && actions.OpenedLibrary == 0 && quitCalls == 0,
			"The geography action must reach only the config action once.");
		controller.OpenMapLibrary();
		Require(actions.OpenedConfig == 1 && actions.OpenedLibrary == 1 && quitCalls == 0,
			"The library action must reach only the library action once.");
		controller.QuitApplication();
		Require(quitCalls == 1, "Quit must invoke its callback once.");
		controller.OpenGenerationConfig();
		controller.OpenMapLibrary();
		Require(actions.OpenedConfig == 2 && actions.OpenedLibrary == 2 && quitCalls == 1,
			"Repeated requests must be forwarded independently without quitting.");
		Expect<ArgumentNullException>(() => new MainMenuController(null, () => { }), "Null menu actions");
		Expect<ArgumentNullException>(() => new MainMenuController(actions, null), "Null quit callback");
		GD.Print($"[MAIN-MENU-CONTROLLER] PASS {_assertions} assertions");
	}

	static void Require(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException("[MAIN-MENU-CONTROLLER] " + message);
		_assertions++;
	}

	static void Expect<TException>(Action action, string scenario) where TException : Exception
	{
		try { action(); }
		catch (Exception error)
		{
			if (error.GetType() != typeof(TException))
				throw new InvalidOperationException($"[MAIN-MENU-CONTROLLER] {scenario}: expected {typeof(TException).Name}, got {error.GetType().Name}.", error);
			_assertions++;
			return;
		}
		throw new InvalidOperationException($"[MAIN-MENU-CONTROLLER] {scenario}: expected {typeof(TException).Name}, but no exception was thrown.");
	}
}
