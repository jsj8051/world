using System;
using System.Threading;
using World.Application.Components;
using World.Application.Generation;
using World.Application.Pages;
using World.Application.Storage;
using World.Data.Generation;

namespace World.Tests;

internal static class GenerationConfigControllerCheck
{
	sealed class Store : IFileStorage<GenerationProfileCatalog>
	{
		internal int Saves;
		internal bool FailWrites;
		public GenerationProfileCatalog Import(string path, IFileImportObserver observer = null, CancellationToken cancellation = default)
			=> new();
		public void Export(string path, GenerationProfileCatalog catalog)
		{
			if (FailWrites) throw new InvalidOperationException("Test store failure");
			catalog.Validate();
			Saves++;
		}
	}

	sealed class Actions : IGenerationConfigPageActions
	{
		internal int Starts;
		internal int Backs;
		internal bool FailGeneration;
		internal GenerationConfig Last;
		public void BackToMainMenu() => Backs++;
		public void StartGeneration(GenerationConfig config)
		{
			if (FailGeneration) throw new InvalidOperationException("Test navigation failure");
			Starts++;
			Last = config;
		}
	}

	internal static int Run()
	{
		int assertions = 0;
		void Require(bool condition, string message)
		{
			if (!condition) throw new InvalidOperationException("[CONFIG-CONTROLLER] " + message);
			assertions++;
		}

		var store = new Store();
		var session = new GenerationConfigSession(store, "profiles.json");
		var actions = new Actions();
		var controller = new GenerationConfigController(session, actions);
		int notifications = 0;
		controller.StateChanged += () => notifications++;
		Require(!controller.HasUnappliedChanges && controller.Draft.Seed == 42, "Defaults must start applied.");
		GenerationConfig copy = controller.Draft;
		copy.Seed = 900;
		Require(controller.Draft.Seed == 42, "View-facing draft must not expose mutable session state.");

		controller.UpdateDraft(new GenerationConfig { Seed = 123 });
		Require(controller.HasUnappliedChanges && !controller.PrepareGeneration(), "Dirty draft must block generation.");
		Require(controller.StatusIsError && actions.Starts == 0, "Rejected generation must report an error without navigation.");
		controller.SaveProfile("First", false);
		Guid firstId = controller.SelectedProfileId.Value;
		Require(store.Saves == 1 && session.Applied.Seed == 42, "Saving must not apply the draft.");
		controller.Apply();
		Require(session.Applied.Seed == 123 && store.Saves == 1, "Apply must not write persistent storage.");
		controller.UpdateDraft(new GenerationConfig { Seed = 456 });
		controller.SaveProfile("Second", true);
		Guid secondId = controller.SelectedProfileId.Value;
		Require(controller.Profiles.Length == 2 && firstId != secondId, "Save As must create a separate profile.");
		Require(controller.TrySelectProfile(firstId) == ProfileSelectOutcome.ConfirmationRequired,
			"Dirty selection must request confirmation.");
		Require(controller.TrySelectProfile(firstId) == ProfileSelectOutcome.ConfirmationRequired,
			"Requesting confirmation must not change the draft or selection.");
		Require(controller.Draft.Seed == 456 && controller.SelectedProfileId == secondId,
			"Pending confirmation must preserve the draft and selected profile.");
		controller.LoadProfile(firstId);
		Require(controller.Draft.Seed == 123 && controller.SelectedProfileId == firstId,
			"Confirmed load must select the requested profile.");
		controller.RenameSelectedProfile("Renamed");
		Require(controller.Profiles[0].Name == "Renamed", "Rename must update the selected profile.");
		store.FailWrites = true;
		controller.RenameSelectedProfile("Not committed");
		Require(controller.StatusIsError && controller.Profiles[0].Name == "Renamed", "Failed writes must leave in-memory profiles unchanged.");
		store.FailWrites = false;

		controller.DeleteProfile(firstId);
		Require(controller.Profiles.Length == 1 && session.Applied.Seed == 123, "Deleting a profile must preserve applied configuration.");
		controller.ResetDraft();
		Require(controller.Draft.Seed == 42 && session.Applied.Seed == 123, "Reset must change only the draft.");
		Require(!controller.UpdateDraft(new GenerationConfig { PlateCount = -1 }) && controller.StatusIsError,
			"Invalid input must return failure rather than escape the controller.");
		controller.UpdateDraft(new GenerationConfig { Seed = 777 });
		controller.Apply();
		Require(controller.PrepareGeneration() && actions.Starts == 0, "Preparation must not navigate before the view gets a frame.");
		Require(!controller.PrepareGeneration(), "Repeated generation must be gated.");
		controller.CommitGeneration();
		controller.CommitGeneration();
		Require(actions.Starts == 1 && actions.Last.Seed == 777, "Commit must submit the applied configuration exactly once.");

		var cancelled = new GenerationConfigController(session, actions);
		Require(cancelled.PrepareGeneration(), "Fresh page controller must allow generation.");
		cancelled.OnHidden();
		cancelled.CommitGeneration();
		Require(actions.Starts == 1 && !cancelled.PrepareGeneration(), "Hidden page must not commit deferred generation.");
		var retry = new GenerationConfigController(session, actions);
		actions.FailGeneration = true;
		retry.PrepareGeneration();
		retry.CommitGeneration();
		Require(retry.StatusIsError && !retry.IsStarting, "Navigation failure must release the generation gate.");
		actions.FailGeneration = false;
		Require(retry.PrepareGeneration(), "Failed navigation must remain retryable.");
		retry.BackToMainMenu();
		Require(actions.Backs == 1 && notifications > 0, "Back action and state notifications must reach injected collaborators.");
		var standalone = new GenerationConfigController(session, null);
		Require(!standalone.PrepareGeneration() && standalone.StatusIsError, "Missing host capability must reject generation.");
		return assertions;
	}
}
