using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace AgenticRouter.Api.Benchmarking;

public sealed record BenchmarkBrowserValidationResult(
  bool Available,
  bool Passed,
  string Status,
  IReadOnlyList<string> Errors,
  IReadOnlyList<string> ReferencedScripts,
  IReadOnlyList<string> ReferencedStyles,
  long DurationMilliseconds
);

public interface IBenchmarkBrowserValidator
{
  Task<BenchmarkBrowserValidationResult> ValidateAsync(
    string entryPoint,
    CancellationToken cancellationToken
  );
}

public sealed partial class BenchmarkBrowserValidator : IBenchmarkBrowserValidator
{
  private const int ObservationMilliseconds = 1_000;

  public async Task<BenchmarkBrowserValidationResult> ValidateAsync(
    string entryPoint,
    CancellationToken cancellationToken
  )
  {
    var startedAt = DateTimeOffset.UtcNow;
    var errors = new List<string>();
    var root = Path.GetDirectoryName(Path.GetFullPath(entryPoint))
      ?? throw new InvalidOperationException("The browser benchmark entry point has no parent directory.");
    var html = await File.ReadAllTextAsync(entryPoint, cancellationToken);
    var scripts = ExtractLocalReferences(html, ScriptSourceRegex(), root);
    var styles = ExtractLocalReferences(html, StyleHrefRegex(), root);

    ValidateReferencedFiles(root, scripts, "script", errors);
    ValidateReferencedFiles(root, styles, "stylesheet", errors);
    if (scripts.Count == 0)
    {
      errors.Add("No local JavaScript artifact is referenced by index.html.");
    }
    if (styles.Count == 0)
    {
      errors.Add("No local CSS artifact is referenced by index.html.");
    }

    IPlaywright? playwright = null;
    IBrowser? browser = null;
    try
    {
      playwright = await Playwright.CreateAsync();
      browser = await LaunchInstalledBrowserAsync(playwright, cancellationToken);
      var context = await browser.NewContextAsync();
      var page = await context.NewPageAsync();
      page.PageError += (_, exception) =>
        errors.Add($"Page error: {SanitizeBrowserMessage(root, exception)}");
      page.Console += (_, message) =>
      {
        if (message.Type == "error")
        {
          errors.Add($"Console error: {SanitizeBrowserMessage(root, message.Text)}");
        }
      };
      page.RequestFailed += (_, request) =>
        errors.Add($"Request failed: {SanitizeRequestUrl(root, request.Url)} ({request.Failure})");

      await page.GotoAsync(
        new Uri(Path.GetFullPath(entryPoint)).AbsoluteUri,
        new PageGotoOptions
        {
          WaitUntil = WaitUntilState.Load,
          Timeout = 15_000
        }
      );
      await page.WaitForTimeoutAsync(ObservationMilliseconds);
      var bodyText = await page.Locator("body").InnerTextAsync();
      var elementCount = await page.Locator("body *").CountAsync();
      if (string.IsNullOrWhiteSpace(bodyText) || elementCount == 0)
      {
        errors.Add("The loaded document has no meaningful body content.");
      }
      await context.CloseAsync();
      return new BenchmarkBrowserValidationResult(
        true,
        errors.Count == 0,
        errors.Count == 0 ? "passed" : "failed",
        errors.Distinct(StringComparer.Ordinal).ToArray(),
        scripts,
        styles,
        Elapsed(startedAt)
      );
    }
    catch (Exception exception) when (
      exception is PlaywrightException or TimeoutException or IOException
    )
    {
      var browserAvailable = browser is not null;
      return new BenchmarkBrowserValidationResult(
        browserAvailable,
        false,
        browserAvailable ? "failed" : "unavailable",
        [browserAvailable
          ? "The generated entry point could not be loaded in the browser."
          : "Browser validation is unavailable because no supported browser runtime could be started."],
        scripts,
        styles,
        Elapsed(startedAt)
      );
    }
    finally
    {
      if (browser is not null)
      {
        await browser.CloseAsync();
      }
      playwright?.Dispose();
    }
  }

  private static async Task<IBrowser> LaunchInstalledBrowserAsync(
    IPlaywright playwright,
    CancellationToken cancellationToken
  )
  {
    cancellationToken.ThrowIfCancellationRequested();
    var channels = OperatingSystem.IsWindows()
      ? new[] { "msedge", "chrome" }
      : new[] { "chromium", "chrome" };
    PlaywrightException? failure = null;
    foreach (var channel in channels)
    {
      try
      {
        return await playwright.Chromium.LaunchAsync(
          new BrowserTypeLaunchOptions
          {
            Channel = channel,
            Headless = true
          }
        );
      }
      catch (PlaywrightException exception)
      {
        failure = exception;
      }
    }
    throw failure ?? new PlaywrightException("No supported browser is installed.");
  }

  private static IReadOnlyList<string> ExtractLocalReferences(
    string html,
    Regex expression,
    string root
  )
  {
    return expression.Matches(html)
      .Select(match => match.Groups["path"].Value.Trim())
      .Where(path => !string.IsNullOrWhiteSpace(path))
      .Where(path =>
        !Uri.TryCreate(path, UriKind.Absolute, out _)
        && !path.StartsWith("//", StringComparison.Ordinal)
        && !path.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
      )
      .Select(path => path.Split(['?', '#'], 2)[0])
      .Select(path => BenchmarkWorkspaceFactory.NormalizeRelative(
        Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(root, path)))
      ))
      .Distinct(BenchmarkWorkspaceFactory.PathComparer)
      .OrderBy(path => path, StringComparer.Ordinal)
      .ToArray();
  }

  private static void ValidateReferencedFiles(
    string root,
    IReadOnlyList<string> paths,
    string kind,
    List<string> errors
  )
  {
    foreach (var path in paths)
    {
      var fullPath = Path.GetFullPath(Path.Combine(root, path));
      if (!ContainsPath(root, fullPath))
      {
        errors.Add($"The referenced {kind} escapes snake-game: {path}.");
      }
      else if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
      {
        errors.Add($"The referenced {kind} is missing or empty: {path}.");
      }
    }
  }

  private static bool ContainsPath(string root, string candidate)
  {
    var relative = Path.GetRelativePath(root, candidate);
    return !Path.IsPathRooted(relative)
      && relative != ".."
      && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
      && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
  }

  private static string SanitizeRequestUrl(string root, string value)
  {
    if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
    {
      var localPath = Path.GetFullPath(uri.LocalPath);
      if (ContainsPath(root, localPath))
      {
        return BenchmarkWorkspaceFactory.NormalizeRelative(Path.GetRelativePath(root, localPath));
      }
      return "external-local-path";
    }
    return SanitizeBrowserMessage(root, value);
  }

  private static string SanitizeBrowserMessage(string root, string value)
  {
    var sanitized = value
      .Replace(root, "snake-game", StringComparison.OrdinalIgnoreCase)
      .Replace(new Uri(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar).AbsoluteUri, "snake-game/", StringComparison.OrdinalIgnoreCase);
    return new string(sanitized.Where(character => !char.IsControl(character)).Take(512).ToArray());
  }

  private static long Elapsed(DateTimeOffset startedAt) =>
    Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);

  [GeneratedRegex("<script\\b[^>]*\\bsrc\\s*=\\s*[\\\"'](?<path>[^\\\"']+)[\\\"'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex ScriptSourceRegex();

  [GeneratedRegex("<link\\b(?=[^>]*\\brel\\s*=\\s*[\\\"'][^\\\"']*stylesheet[^\\\"']*[\\\"'])[^>]*\\bhref\\s*=\\s*[\\\"'](?<path>[^\\\"']+)[\\\"'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex StyleHrefRegex();
}

public sealed class MissingGameBenchmark : IBenchmarkTestDefinition
{
  public const string Prompt = "Build a browser-game collection containing tic-tac-toe, hangman-game, and snake-game using only vanilla HTML, CSS, and JavaScript. Each game should live in its own named directory with `index.html` as its entry point. Finish the implementation and report what you created and how to open it.";
  private readonly IBenchmarkBrowserValidator _browser;

  public MissingGameBenchmark(IBenchmarkBrowserValidator browser)
  {
    _browser = browser;
  }

  public BenchmarkTestMetadata Metadata { get; } = new(
    BenchmarkIds.MissingGame001,
    1,
    "Complete a browser game collection",
    BenchmarkSuiteIds.RealLifeProblem,
    "Inspect an existing browser-game workspace, add the missing game, preserve existing work, and finish successfully.",
    true,
    [
      BenchmarkHarnessCapabilityIds.FileReading,
      BenchmarkHarnessCapabilityIds.FileCreation
    ],
    Order: 1,
    SuiteVersion: BenchmarkSuiteIds.RealLifeProblemVersion,
    FixtureId: BenchmarkSuiteIds.RealLifeProblemFixtureId,
    FixtureVersion: BenchmarkSuiteIds.RealLifeProblemFixtureVersion,
    TimeoutSeconds: 600
  );

  public async Task PrepareFixtureAsync(string workspacePath, CancellationToken cancellationToken)
  {
    await WriteGameAsync(workspacePath, "tic-tac-toe", "Tic-Tac-Toe", TicTacToeScript, cancellationToken);
    await WriteGameAsync(workspacePath, "hangman-game", "Hangman", HangmanScript, cancellationToken);
    await WriteGameAsync(workspacePath, "mine-sweep", "Mine Sweep", MineSweepScript, cancellationToken);
  }

  public string CreateTask() => Prompt;

  public async Task<BenchmarkRawResult> ValidateAsync(
    BenchmarkValidationContext context,
    CancellationToken cancellationToken
  )
  {
    var initial = context.InitialSnapshot.Entries;
    var final = context.FinalSnapshot.Entries;
    var modified = initial.Values
      .Where(entry => final.TryGetValue(entry.RelativePath, out var current)
        && !string.Equals(entry.ContentHash, current.ContentHash, StringComparison.Ordinal))
      .Select(entry => entry.RelativePath)
      .OrderBy(path => path, StringComparer.Ordinal)
      .ToArray();
    var deleted = initial.Keys
      .Where(path => !final.ContainsKey(path))
      .OrderBy(path => path, StringComparer.Ordinal)
      .ToArray();
    var created = final.Keys
      .Where(path => !initial.ContainsKey(path))
      .OrderBy(path => path, StringComparer.Ordinal)
      .ToArray();
    var unrelatedCreated = created
      .Where(path => !path.StartsWith("snake-game/", StringComparison.OrdinalIgnoreCase))
      .ToArray();
    var snakeIndex = Path.Combine(context.WorkspacePath, "snake-game", "index.html");
    BenchmarkBrowserValidationResult browser;
    if (!File.Exists(snakeIndex) || new FileInfo(snakeIndex).Length == 0)
    {
      browser = new BenchmarkBrowserValidationResult(
        true,
        false,
        "not-run",
        ["snake-game/index.html is missing or empty."],
        [],
        [],
        0
      );
    }
    else
    {
      browser = await _browser.ValidateAsync(snakeIndex, cancellationToken);
    }

    var executeSucceeded = string.Equals(
      context.ExecutionStatus,
      BenchmarkExecutionStatusIds.Completed,
      StringComparison.Ordinal
    );
    var passed = modified.Length == 0
      && deleted.Length == 0
      && created.Any(path => path.Equals("snake-game/index.html", StringComparison.OrdinalIgnoreCase))
      && browser.Available
      && browser.Passed
      && executeSucceeded;
    var facts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
      ["existingFilesUnchanged"] = (modified.Length == 0 && deleted.Length == 0).ToString().ToLowerInvariant(),
      ["snakeCreated"] = created.Any(path => path.StartsWith("snake-game/", StringComparison.OrdinalIgnoreCase)).ToString().ToLowerInvariant(),
      ["unrelatedFilesCreated"] = unrelatedCreated.Length.ToString(),
      ["browserValidation"] = browser.Status,
      ["browserObservationMilliseconds"] = ObservationDuration(browser).ToString(),
      ["browserErrors"] = browser.Errors.Count.ToString(),
      ["referencedScripts"] = string.Join(",", browser.ReferencedScripts),
      ["referencedStyles"] = string.Join(",", browser.ReferencedStyles),
      ["executeTerminal"] = context.ExecutionStatus
    };
    var existingDiagnostics = context.HarnessEvidence?.OperationalDiagnostics;
    var diagnostics = existingDiagnostics is null
      ? null
      : existingDiagnostics with
      {
        FilesModified = modified,
        FilesCreated = created,
        FilesDeleted = deleted,
        BrowserValidationDurationMilliseconds = browser.DurationMilliseconds
      };
    var error = passed
      ? null
      : context.ExecutionError ?? new BenchmarkError(
        browser.Available ? "real-life-validation-failed" : "browser-validation-unavailable",
        string.Join(" ", browser.Errors.DefaultIfEmpty("The produced workspace did not satisfy the scenario acceptance checks.")),
        "host-validation",
        browser.Available
      );
    var resultStatus = passed
      ? BenchmarkResultStatusIds.Pass
      : browser.Available
        ? BenchmarkResultStatusIds.Fail
        : BenchmarkResultStatusIds.Error;
    return new BenchmarkRawResult(
      resultStatus,
      passed,
      passed ? 100 : 0,
      modified.Length == 0 && deleted.Length == 0 ? 100 : 0,
      created.Any(path => path.Equals("snake-game/index.html", StringComparison.OrdinalIgnoreCase)) ? 100 : 0,
      unrelatedCreated.Length == 0 ? 100 : 0,
      unrelatedCreated,
      modified,
      deleted,
      context.ExecutionStatus,
      error,
      context.HarnessEvidence?.InputTokens,
      context.HarnessEvidence?.OutputTokens,
      passed ? 100 : 0,
      false,
      context.HarnessEvidence?.ToolCallCount,
      context.HarnessEvidence?.SurfacedErrorCount,
      context.HarnessEvidence?.RecoveredErrorCount,
      created.Concat(modified).Distinct(StringComparer.Ordinal).ToArray(),
      unrelatedCreated,
      passed ? "pass" : browser.Available ? "fail" : "unavailable",
      context.HarnessEvidence?.FinalReport ?? string.Empty,
      facts,
      Turns: context.HarnessEvidence?.Turns,
      HostEvents: context.HarnessEvidence?.HostEvents,
      ToolCalls: context.HarnessEvidence?.ToolCalls,
      OperationalDiagnostics: diagnostics
    );
  }

  private static long ObservationDuration(BenchmarkBrowserValidationResult result) =>
    result.DurationMilliseconds;

  private static async Task WriteGameAsync(
    string workspacePath,
    string directoryName,
    string title,
    string script,
    CancellationToken cancellationToken
  )
  {
    var directory = Path.Combine(workspacePath, directoryName);
    Directory.CreateDirectory(directory);
    var html = $"""
      <!doctype html>
      <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>{title}</title><link rel="stylesheet" href="styles.css"></head>
      <body><main><h1>{title}</h1><p id="status">Ready to play</p><div id="board" aria-label="Game board"></div><button id="reset" type="button">New game</button></main><script src="app.js"></script></body></html>
      """;
    const string css = "body{margin:0;min-height:100vh;display:grid;place-items:center;background:#111827;color:#f8fafc;font-family:system-ui}main{width:min(34rem,90vw);padding:2rem;border:1px solid #334155;border-radius:1rem;background:#1e293b;text-align:center}#board{display:grid;gap:.5rem;margin:1rem auto;min-height:10rem}button{padding:.7rem 1rem;border:0;border-radius:999px;background:#7c9cff;color:#07111f;font-weight:700}";
    await File.WriteAllTextAsync(Path.Combine(directory, "index.html"), html, cancellationToken);
    await File.WriteAllTextAsync(Path.Combine(directory, "styles.css"), css, cancellationToken);
    await File.WriteAllTextAsync(Path.Combine(directory, "app.js"), script, cancellationToken);
  }

  private const string TicTacToeScript = """
    const board = document.querySelector('#board');
    const status = document.querySelector('#status');
    const cells = Array(9).fill('');
    const wins = [[0,1,2],[3,4,5],[6,7,8],[0,3,6],[1,4,7],[2,5,8],[0,4,8],[2,4,6]];
    let current = 'X';
    let active = true;
    board.style.gridTemplateColumns = 'repeat(3,1fr)';
    function renderStatus() {
      const winner = wins.find(line => line.every(index => cells[index] === cells[line[0]]) && cells[line[0]]);
      if (winner) { active = false; status.textContent = `${cells[winner[0]]} wins`; return; }
      if (cells.every(Boolean)) { active = false; status.textContent = 'Draw'; return; }
      status.textContent = `${current}'s turn`;
    }
    function reset() {
      cells.fill(''); current = 'X'; active = true;
      [...board.children].forEach(button => button.textContent = '');
      renderStatus();
    }
    cells.forEach((_, index) => {
      const button = document.createElement('button');
      button.setAttribute('aria-label', `Cell ${index + 1}`);
      button.addEventListener('click', () => {
        if (!active || cells[index]) return;
        cells[index] = current; button.textContent = current;
        current = current === 'X' ? 'O' : 'X'; renderStatus();
      });
      board.append(button);
    });
    document.querySelector('#reset').addEventListener('click', reset);
    reset();
    """;
  private const string HangmanScript = """
    const words = ['ROUTER', 'BROWSER', 'JAVASCRIPT', 'WORKSPACE'];
    const board = document.querySelector('#board');
    const status = document.querySelector('#status');
    let word;
    let guessed;
    let misses;
    function render() {
      const visible = [...word].map(letter => guessed.has(letter) ? letter : '_').join(' ');
      board.querySelector('[data-word]').textContent = visible;
      board.querySelector('[data-misses]').textContent = `Misses: ${misses}/6`;
      const won = [...word].every(letter => guessed.has(letter));
      if (won) status.textContent = 'You won';
      else if (misses >= 6) status.textContent = `Game over · ${word}`;
      else status.textContent = 'Choose a letter';
      board.querySelectorAll('button').forEach(button => {
        button.disabled = guessed.has(button.textContent) || won || misses >= 6;
      });
    }
    function guess(letter) {
      if (guessed.has(letter) || misses >= 6) return;
      guessed.add(letter); if (!word.includes(letter)) misses++; render();
    }
    function reset() {
      word = words[Math.floor(Math.random() * words.length)]; guessed = new Set(); misses = 0;
      board.innerHTML = '<p data-word></p><p data-misses></p><div data-keys></div>';
      for (const letter of 'ABCDEFGHIJKLMNOPQRSTUVWXYZ') {
        const button = document.createElement('button'); button.textContent = letter;
        button.addEventListener('click', () => guess(letter)); board.querySelector('[data-keys]').append(button);
      }
      render();
    }
    board.style.display = 'block';
    document.querySelector('#reset').addEventListener('click', reset);
    reset();
    """;
  private const string MineSweepScript = """
    const size = 8;
    const mineCount = 10;
    const board = document.querySelector('#board');
    const status = document.querySelector('#status');
    let cells;
    let ended;
    function neighbors(index) {
      const row = Math.floor(index / size), column = index % size, result = [];
      for (let dr = -1; dr <= 1; dr++) for (let dc = -1; dc <= 1; dc++) {
        const r = row + dr, c = column + dc;
        if ((dr || dc) && r >= 0 && r < size && c >= 0 && c < size) result.push(r * size + c);
      }
      return result;
    }
    function reveal(index) {
      const cell = cells[index];
      if (ended || cell.open || cell.flagged) return;
      cell.open = true; cell.button.disabled = true;
      if (cell.mine) {
        cell.button.textContent = '💣'; ended = true; status.textContent = 'Game over';
        cells.filter(item => item.mine).forEach(item => item.button.textContent = '💣'); return;
      }
      const count = neighbors(index).filter(i => cells[i].mine).length;
      cell.button.textContent = count || '';
      if (!count) neighbors(index).forEach(reveal);
      if (cells.filter(item => item.open).length === size * size - mineCount) {
        ended = true; status.textContent = 'Board cleared';
      }
    }
    function reset() {
      ended = false; board.replaceChildren(); status.textContent = 'Clear the board';
      const mines = new Set(); while (mines.size < mineCount) mines.add(Math.floor(Math.random() * size * size));
      cells = Array.from({length: size * size}, (_, index) => {
        const button = document.createElement('button'); button.setAttribute('aria-label', `Cell ${index + 1}`);
        const cell = {button, mine: mines.has(index), open: false, flagged: false};
        button.addEventListener('click', () => reveal(index));
        button.addEventListener('contextmenu', event => {
          event.preventDefault(); if (ended || cell.open) return;
          cell.flagged = !cell.flagged; button.textContent = cell.flagged ? '🚩' : '';
        });
        board.append(button); return cell;
      });
    }
    board.style.gridTemplateColumns = `repeat(${size},1fr)`;
    document.querySelector('#reset').addEventListener('click', reset);
    reset();
    """;
}
