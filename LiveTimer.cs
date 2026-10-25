namespace NotchApp;

// Timer (contagem regressiva) ou cronômetro. Guarda só o instante de início e o tempo acumulado,
// então pausar, retomar e o relógio do sistema mudando não acumulam erro.
public sealed class LiveTimer
{
    public enum Kind { None, Countdown, Stopwatch }

    TimeSpan accumulated;
    DateTime? startedAt;

    public Kind Mode { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool Running => startedAt != null;
    public bool Active => Mode != Kind.None;

    public TimeSpan Elapsed(DateTime now) => accumulated + (startedAt is { } s ? now - s : TimeSpan.Zero);

    public TimeSpan Remaining(DateTime now)
    {
        var left = Duration - Elapsed(now);
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    public bool Finished(DateTime now) => Mode == Kind.Countdown && Elapsed(now) >= Duration;

    public double Progress(DateTime now) => Mode == Kind.Countdown && Duration > TimeSpan.Zero ? Math.Clamp(Elapsed(now) / Duration, 0, 1) : 0;

    public void StartCountdown(TimeSpan duration, DateTime now)
    {
        Mode = Kind.Countdown;
        Duration = duration;
        accumulated = TimeSpan.Zero;
        startedAt = now;
    }

    public void StartStopwatch(DateTime now)
    {
        Mode = Kind.Stopwatch;
        Duration = TimeSpan.Zero;
        accumulated = TimeSpan.Zero;
        startedAt = now;
    }

    public void Pause(DateTime now)
    {
        accumulated = Elapsed(now);
        startedAt = null;
    }

    public void Resume(DateTime now) => startedAt ??= now;

    public void Cancel()
    {
        Mode = Kind.None;
        accumulated = TimeSpan.Zero;
        startedAt = null;
    }

    public string Display(DateTime now)
    {
        var t = Mode == Kind.Countdown ? Remaining(now) : Elapsed(now);
        if (Mode == Kind.Countdown) t = TimeSpan.FromSeconds(Math.Ceiling(t.TotalSeconds)); // 4:59,2 mostra 5:00 como no iOS
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
