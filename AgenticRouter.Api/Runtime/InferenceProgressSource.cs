using System.Globalization;
using System.Text.RegularExpressions;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Runtime;

// Passive, bounded telemetry from an owned runtime. No polling or inference calls.
public sealed partial class InferenceProgressSource
{
  private readonly object _gate = new();
  private readonly HashSet<Subscription> _active = [];

  public Subscription Subscribe(Action<InferenceProgressView>? observer)
  {
    lock (_gate)
    {
      var subscription = new Subscription(this, observer);
      _active.Add(subscription);
      if (_active.Count > 1)
        foreach (var active in _active) active.Invalidate("concurrent-inference");
      else subscription.Publish(new("waiting", null, "runtime", "Awaiting runtime progress."));
      return subscription;
    }
  }

  public void Observe(string line)
  {
    lock (_gate)
    {
      if (_active.Count != 1) return;
      var active = _active.First();
      if (line.Contains("loading model", StringComparison.OrdinalIgnoreCase)
        || line.Contains("load_tensors: loading", StringComparison.Ordinal))
        active.Publish(new("loading-model", null, "runtime"));
      else if (line.Contains("new prompt", StringComparison.Ordinal))
        active.Publish(new("processing-prompt", null, "runtime"));
      else if (PromptProgress().Match(line) is { Success: true } match
        && double.TryParse(match.Groups[1].Value, NumberStyles.Float,
          CultureInfo.InvariantCulture, out var fraction) && fraction is >= 0 and <= 1)
        active.Publish(new("processing-prompt", Math.Round(fraction * 100, 1), "runtime"));
    }
  }

  [GeneratedRegex(@"\bprompt processing,.*\bprogress\s*=\s*([0-9.]+)(?:,|\s|$)", RegexOptions.CultureInvariant)]
  private static partial Regex PromptProgress();

  public sealed class Subscription : IDisposable
  {
    private readonly InferenceProgressSource? _source;
    private readonly Action<InferenceProgressView>? _observer;
    private bool _invalid;
    private bool _generating;
    private bool _disposed;
    private InferenceProgressView? _last;

    internal Subscription(InferenceProgressSource? source, Action<InferenceProgressView>? observer)
    { _source = source; _observer = observer; }

    public static Subscription Unavailable(Action<InferenceProgressView>? observer)
    {
      var subscription = new Subscription(null, observer);
      subscription.Invalidate("Runtime progress is unavailable for this endpoint.");
      return subscription;
    }

    internal void Invalidate(string reason)
    {
      Publish(new("waiting", null, "unavailable", reason));
      _invalid = true;
    }

    internal void Publish(InferenceProgressView progress)
    {
      if (_disposed || _generating || _invalid || _last == progress) return;
      _last = progress;
      _observer?.Invoke(progress);
    }

    public void Generating()
    {
      lock (_source?._gate ?? this)
      {
        if (_disposed || _generating) return;
        _generating = true;
        _observer?.Invoke(new("generating", null, "stream"));
      }
    }

    public void Dispose()
    {
      lock (_source?._gate ?? this)
      {
        if (_disposed) return;
        _disposed = true;
        _source?._active.Remove(this);
      }
    }
  }
}
