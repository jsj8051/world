using System;
using System.Threading;
using World.Application.Generation;
using World.Application.Pages;
using World.Application.Storage;
using World.Data.Generation;

namespace World.Tests;

internal static class LoadStructureModelTests
{
	sealed class Store : IFileStorage<GenerationProfileCatalog>
	{
		public GenerationProfileCatalog Import(string path, IFileImportObserver observer = null,
			CancellationToken cancellation = default) => new();
		public void Export(string path, GenerationProfileCatalog document) { }
	}

	sealed class Actions : IGenerationConfigPageActions
	{
		public int Starts;
		public void StartGeneration(GenerationConfig config) => Starts++;
		public void BackToMainMenu() { }
	}

	public static void GenerationFailureReleasesGate()
	{
		var actions = new Actions();
		var controller = new GenerationConfigController(new GenerationConfigSession(new Store(), "profiles.json"), actions);
		Program.True(controller.PrepareGeneration(), "Initial generation must be allowed.");
		controller.CommitGeneration();
		Program.True(controller.IsStarting && actions.Starts == 1, "Pending generation must remain gated.");
		controller.GenerationFailed(new InvalidOperationException("background failed"));
		Program.True(!controller.IsStarting && controller.StatusIsError && controller.Status == "background failed",
			"Background failure must release the gate and keep the error.");
		Program.True(controller.PrepareGeneration(), "Failed generation must be retryable.");
		controller.CommitGeneration();
		Program.True(actions.Starts == 2, "Retry must start a new generation.");
		controller.OnHidden();
		controller.GenerationFailed(new InvalidOperationException("late failure"));
		Program.True(!controller.PrepareGeneration() && !controller.StatusIsError,
			"Late failure must not reopen a page that has exited.");
	}
}
