using System.Text.Json;

namespace AgenticRouter.EndToEndTests;

internal static class ForkGameExecutionFixture
{
  public const string ExactRequest =
    "Create a fork game, generate a collection of 256 fixed substantive words to be used as words for the game. "
    + "The code will need to be created using html, vanilla js and CSS only. It does not use Node and does not "
    + "executes, it will test it manually. And integrate the fireworks effects that is in on fireworks folder into "
    + "the code, to be triggered when the game ends.";

  public const string ExistingFireworksJavaScript = """
    (() => {
      window.FireworksEffect = Object.freeze({
        launch({ outcome, word }) {
          document.body.dataset.fireworksOutcome = outcome;
          document.body.dataset.fireworksWord = word;
          document.dispatchEvent(new CustomEvent("fireworks:launch", {
            detail: { outcome, word }
          }));
        }
      });
    })();
    """;

  public const string ExistingFireworksCss = """
    body[data-fireworks-outcome]::after {
      content: "";
      position: fixed;
      inset: 0;
      pointer-events: none;
      background: radial-gradient(circle at center, #ffd166 0 2px, transparent 3px);
      animation: fireworks-burst 700ms ease-out;
    }

    @keyframes fireworks-burst {
      from { transform: scale(0.2); opacity: 1; }
      to { transform: scale(2); opacity: 0; }
    }
    """;

  public static readonly string[] Words =
  [
    "acorn", "actor", "airport", "alarm", "album", "anchor", "animal", "apple",
    "apron", "arch", "arrow", "artist", "attic", "author", "avenue", "award",
    "badge", "bakery", "balloon", "banana", "basket", "beach", "beard", "bedroom",
    "bee", "bell", "bicycle", "blanket", "boat", "book", "bottle", "bridge",
    "cabin", "cactus", "camera", "candle", "canyon", "carpet", "castle", "chair",
    "cherry", "circle", "city", "clock", "cloud", "coast", "comet", "crown",
    "dancer", "desert", "diamond", "diary", "dinner", "doctor", "dolphin", "donkey",
    "door", "dragon", "drawer", "dream", "dress", "drum", "duck", "dust",
    "eagle", "earth", "egg", "elbow", "engine", "envelope", "evening", "event",
    "exam", "exit", "eye", "ear", "editor", "emerald", "estate", "exhibit",
    "factory", "family", "farmer", "feather", "ferry", "field", "finger", "fire",
    "flag", "flower", "flute", "forest", "fork", "fountain", "fox", "frame",
    "galaxy", "garden", "gate", "ghost", "gift", "giraffe", "glass", "globe",
    "glove", "goat", "grape", "grass", "guitar", "gym", "garage", "goose",
    "hammer", "harbor", "hat", "heart", "helmet", "hill", "honey", "horse",
    "hospital", "hotel", "house", "human", "hurricane", "hyena", "harp", "hallway",
    "island", "ice", "idea", "image", "insect", "iron", "item", "ivory",
    "igloo", "ink", "invitation", "industry", "inventor", "iris", "ivy", "instrument",
    "jacket", "jail", "jar", "jewel", "job", "journal", "journey", "judge",
    "juice", "jungle", "jury", "joystick", "jeep", "jet", "joke", "junction",
    "kangaroo", "kettle", "key", "keyboard", "king", "kitchen", "kite", "kitten",
    "knee", "knife", "knight", "knot", "koala", "kayak", "kernel", "kingdom",
    "ladder", "lake", "lamp", "leaf", "lemon", "library", "lighthouse", "lion",
    "lizard", "lock", "lunch", "luggage", "loaf", "lobster", "locomotive", "lottery",
    "machine", "magazine", "map", "marble", "market", "mask", "meadow", "medal",
    "melon", "mirror", "monkey", "moon", "mountain", "museum", "mushroom", "music",
    "nail", "name", "necklace", "needle", "nest", "net", "newspaper", "night",
    "nose", "notebook", "nurse", "nut", "napkin", "nation", "nature", "noodle",
    "ocean", "office", "olive", "onion", "opera", "orange", "orchestra", "oven",
    "owl", "owner", "oasis", "object", "observatory", "octopus", "oil", "orchard",
    "palace", "panda", "paper", "park", "parrot", "pearl", "pencil", "piano",
    "picnic", "pillow", "planet", "plate", "pocket", "pond", "potato", "pyramid"
  ];

  public static string WordsJavaScript =>
    $"window.FORK_GAME_WORDS = Object.freeze({JsonSerializer.Serialize(Words)});";

  public const string IndexHtml = """
    <!doctype html>
    <html lang="en">
    <head>
      <meta charset="utf-8">
      <meta name="viewport" content="width=device-width, initial-scale=1">
      <title>Fork Word Game</title>
      <link rel="stylesheet" href="fireworks/fireworks.css">
      <link rel="stylesheet" href="styles.css">
    </head>
    <body>
      <main class="game" aria-labelledby="game-title">
        <h1 id="game-title">Fork Word Game</h1>
        <p>Guess one letter at a time before eight incorrect guesses.</p>
        <output id="word" class="word" aria-live="polite"></output>
        <form id="guess-form">
          <label for="guess">Letter</label>
          <input id="guess" name="guess" maxlength="1" pattern="[A-Za-z]" autocomplete="off" required>
          <button type="submit">Guess</button>
        </form>
        <p id="status" role="status"></p>
        <p>Incorrect guesses: <span id="misses">0</span>/8</p>
        <p>Used letters: <span id="used">none</span></p>
        <button id="new-game" type="button">New game</button>
      </main>
      <script src="words.js"></script>
      <script src="fireworks/firework_engine.js"></script>
      <script src="game.js"></script>
    </body>
    </html>
    """;

  public const string StylesCss = """
    :root { color-scheme: dark; font-family: system-ui, sans-serif; }
    body { min-height: 100vh; margin: 0; display: grid; place-items: center; background: #101426; color: #f7f7ff; }
    .game { width: min(34rem, calc(100% - 2rem)); padding: 2rem; border: 1px solid #424b73; border-radius: 1rem; background: #1a2038; }
    .word { display: block; margin: 2rem 0; font: 700 clamp(1.5rem, 7vw, 3rem) monospace; letter-spacing: 0.3em; }
    form { display: flex; gap: 0.75rem; align-items: end; }
    label { display: grid; gap: 0.35rem; }
    input { width: 3rem; padding: 0.65rem; font: inherit; text-transform: uppercase; }
    button { padding: 0.7rem 1rem; border: 0; border-radius: 0.5rem; background: #ffd166; color: #171717; font-weight: 700; cursor: pointer; }
    button:focus-visible, input:focus-visible { outline: 3px solid #6ee7ff; outline-offset: 3px; }
    """;

  public const string GameJavaScript = """
    (() => {
      "use strict";

      const maximumMisses = 8;
      const wordOutput = document.querySelector("#word");
      const statusOutput = document.querySelector("#status");
      const missesOutput = document.querySelector("#misses");
      const usedOutput = document.querySelector("#used");
      const form = document.querySelector("#guess-form");
      const input = document.querySelector("#guess");
      const newGameButton = document.querySelector("#new-game");
      let word = "";
      let guessed = new Set();
      let misses = 0;
      let finished = false;

      function visibleWord() {
        return [...word].map(letter => guessed.has(letter) ? letter.toUpperCase() : "_").join(" ");
      }

      function hasWon() {
        return [...word].every(letter => guessed.has(letter));
      }

      function render(message = "Choose a letter.") {
        wordOutput.textContent = visibleWord();
        statusOutput.textContent = message;
        missesOutput.textContent = String(misses);
        usedOutput.textContent = guessed.size ? [...guessed].sort().join(", ").toUpperCase() : "none";
        input.disabled = finished;
      }

      function finishGame(outcome) {
        finished = true;
        const won = outcome === "won";
        render(won ? `You won! The word was ${word}.` : `Game over. The word was ${word}.`);
        window.FireworksEffect.launch({ outcome, word });
      }

      function startGame() {
        word = window.FORK_GAME_WORDS[Math.floor(Math.random() * window.FORK_GAME_WORDS.length)];
        guessed = new Set();
        misses = 0;
        finished = false;
        render();
        input.focus();
      }

      form.addEventListener("submit", event => {
        event.preventDefault();
        const letter = input.value.trim().toLowerCase();
        input.value = "";
        if (!/^[a-z]$/.test(letter)) {
          render("Enter one letter from A to Z.");
          return;
        }
        if (guessed.has(letter)) {
          render(`${letter.toUpperCase()} was already used.`);
          return;
        }
        guessed.add(letter);
        if (!word.includes(letter)) misses += 1;
        if (hasWon()) finishGame("won");
        else if (misses >= maximumMisses) finishGame("lost");
        else render(word.includes(letter) ? "Good guess." : "That letter is not in the word.");
      });

      newGameButton.addEventListener("click", startGame);
      startGame();
    })();
    """;

  public static bool Matches(string request)
  {
    return request.Contains(
      "256 fixed substantive words",
      StringComparison.OrdinalIgnoreCase
    ) && request.Contains(
      "fireworks folder",
      StringComparison.OrdinalIgnoreCase
    );
  }
}
