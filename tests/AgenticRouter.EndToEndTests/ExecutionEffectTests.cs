using System.Text.Json;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class ExecutionEffectTests
{
  [TestMethod]
  public void HostNormalizesAmbiguousAndGenericTitlesWithoutArbitraryFallback()
  {
    Assert.AreEqual(
      ToolEffects.Inspected,
      ToolEffectRegistry.InferExpectedEffect("Inspect files selected for deletion")
    );

    var session = CreateSession("Create requested file");
    session.CreatePlan(
      new ExecutionPlanView(
        "Create requested file",
        [new ExecutionPlanStep("generic", "Implement requested file changes", "pending")],
        null,
        0,
        0
      )
    );
    var creation = Action("create-1", "create_file");

    Assert.IsTrue(session.RecordPlanActionStarted(creation.ActionId, creation.Tool));
    session.RecordFileChange(
      new ExecutionFileChange(
        "created.txt",
        "created",
        false,
        "empty",
        "created",
        null,
        "created",
        7,
        DateTimeOffset.UtcNow,
        true,
        true,
        null,
        0
      )
    );
    session.RecordAction(creation, "completed", "created");

    Assert.IsTrue(session.RecordPlanActionResult(creation.ActionId, creation.Tool, "completed"));
  }

  [TestMethod]
  public void IncompatibleToolDoesNotAdvanceAnotherPlanStep()
  {
    var session = CreateSession("Delete obsolete.txt");
    session.CreatePlan(
      new ExecutionPlanView(
        "Delete obsolete.txt",
        [
          new ExecutionPlanStep("inspect", "Inspect selected file", "pending"),
          new ExecutionPlanStep("delete", "Delete inspected file", "pending")
        ],
        null,
        0,
        0
      )
    );

    var firstInspection = Action("inspect-1", "read_file");
    Assert.IsTrue(session.RecordPlanActionStarted(firstInspection.ActionId, firstInspection.Tool));
    session.RecordAction(firstInspection, "completed", "observed");
    Assert.IsTrue(session.RecordPlanActionResult(firstInspection.ActionId, firstInspection.Tool, "completed"));

    var unrelatedInspection = Action("inspect-2", "list_files");
    Assert.IsFalse(session.RecordPlanActionStarted(unrelatedInspection.ActionId, unrelatedInspection.Tool));
    session.RecordAction(unrelatedInspection, "completed", "listed");
    Assert.IsFalse(session.RecordPlanActionResult(unrelatedInspection.ActionId, unrelatedInspection.Tool, "completed"));

    var plan = session.CreateReview().Summary.Plan;
    Assert.IsNotNull(plan);
    Assert.AreEqual("completed", plan.Steps.Single(step => step.Id == "inspect").Status);
    Assert.AreEqual("pending", plan.Steps.Single(step => step.Id == "delete").Status);
  }

  [TestMethod]
  public void DeleteStepRequiresVerifiedDeletedFileEffect()
  {
    var session = CreateSession("Delete obsolete.txt");
    session.CreatePlan(
      new ExecutionPlanView(
        "Delete obsolete.txt",
        [new ExecutionPlanStep("delete", "Delete obsolete.txt", "pending")],
        null,
        0,
        0
      )
    );
    var deletion = Action("delete-1", "delete_files");

    Assert.IsTrue(session.RecordPlanActionStarted(deletion.ActionId, deletion.Tool));
    session.RecordAction(deletion, "completed", "claimed deletion");
    Assert.IsFalse(session.RecordPlanActionResult(deletion.ActionId, deletion.Tool, "completed"));

    session.RecordFileChange(
      new ExecutionFileChange(
        "obsolete.txt",
        "deleted",
        true,
        "before",
        "after",
        "obsolete",
        string.Empty,
        0,
        DateTimeOffset.UtcNow,
        true,
        true,
        null,
        8
      )
    );

    Assert.IsTrue(session.RecordPlanActionResult(deletion.ActionId, deletion.Tool, "completed"));
    Assert.AreEqual("completed", session.CreateReview().Summary.Plan?.Steps.Single().Status);
  }

  [TestMethod]
  public void MutationObjectiveCannotCompleteWithoutVerifiedMutation()
  {
    var session = CreateSession("Edit app.js");
    session.CreatePlan(
      new ExecutionPlanView(
        "Edit app.js",
        [new ExecutionPlanStep("edit", "Edit app.js", "pending")],
        null,
        0,
        0
      )
    );

    session.Complete("completed");

    var review = session.CreateReview();
    Assert.AreEqual("blocked", review.Summary.State);
    Assert.AreEqual("blocked-mutation-not-performed", review.Summary.CompletionStatus);
  }

  [TestMethod]
  public void VerifiedDirectoryMutationIsNotReportedAsInspectionOnly()
  {
    var session = CreateSession("Create directory generated");
    session.CreatePlan(
      new ExecutionPlanView(
        "Create directory generated",
        [new ExecutionPlanStep("directory", "Create directory generated", "pending")],
        null,
        0,
        0
      )
    );
    var action = Action("directory-1", "create_directory");

    Assert.IsTrue(session.RecordPlanActionStarted(action.ActionId, action.Tool));
    session.RecordCreatedDirectory("generated");
    session.RecordAction(action, "completed", "created");
    Assert.IsTrue(session.RecordPlanActionResult(action.ActionId, action.Tool, "completed"));
    session.Complete("completed");

    var review = session.CreateReview();
    Assert.AreEqual("completed", review.Summary.State);
    Assert.AreEqual("verified-mutation-no-file-artifacts", review.Summary.CompletionStatus);
  }

  private static ExecutionSession CreateSession(string objective) => new(
    Guid.NewGuid().ToString("N"),
    "browser",
    "request",
    objective,
    "ask",
    Path.GetTempPath(),
    "specialist",
    "functiongemma:270m",
    "resident-action",
    new ExecutionSettings()
  );

  private static ValidatedLocalAction Action(string id, string tool)
  {
    using var document = JsonDocument.Parse("{}");
    return new ValidatedLocalAction(
      id,
      tool,
      document.RootElement.Clone(),
      null,
      null,
      tool,
      null,
      true,
      false
    );
  }
}
