using System.Windows;
using System.Windows.Controls;

namespace NotchApp;

/// <summary>
/// The Timer tab: a countdown timer (presets, ±1 minute) and a stopwatch with laps.
/// While either runs, the pill shows it (timer orange, stopwatch green). Nothing ticks
/// unless one of them is running.
/// </summary>
public partial class MainWindow
{
    bool _swRunning;
    DateTime _swStartedAt;
    TimeSpan _swBefore;                       // time banked before the current run
    readonly List<TimeSpan> _laps = new();

    TimeSpan SwElapsed => _swBefore + (_swRunning ? DateTime.Now - _swStartedAt : TimeSpan.Zero);

    void InitTimers()
    {
        _focusLeft = FocusLength;
        // the ruler: live preview in the bubble while dragging, the new length when let go
        _card.TimerRuler.Previewing += m =>
        {
            if (!_focusRunning) _card.TimerBig.Text = Clock(TimeSpan.FromMinutes(Math.Clamp(Math.Round(m), 1, 180)));
        };
        _card.TimerRuler.Picked += PickLength;
        _card.TimerStart.Click += Focus_Click;
        _card.TimerReset.Click += FocusReset_Click;
        _card.TimerPlus.Click += (_, _) => Nudge(+1);
        _card.TimerMinus.Click += (_, _) => Nudge(-1);
        _card.StopwatchStart.Click += (_, _) => ToggleStopwatch();
        _card.StopwatchLap.Click += (_, _) => LapOrReset();
        UpdateFocusUi();
        UpdateStopwatchUi();
    }

    // ------------------------------------------------------------------ countdown timer

    /// <summary>A preset was picked: that's the new length, ready to start.</summary>
    void PickLength(int minutes)
    {
        _settings.TimerMinutes = minutes;
        _settings.Save();
        _focusRunning = false;
        _focusLeft = FocusLength;
        AfterTimerChange();
    }

    /// <summary>±1 minute: stretches a running or paused timer; on a fresh one it changes the length.</summary>
    void Nudge(int minutes)
    {
        var step = TimeSpan.FromMinutes(minutes);
        if (_focusRunning)
        {
            var left = _focusEnd + step - DateTime.Now;
            if (left < TimeSpan.FromSeconds(1)) left = TimeSpan.FromSeconds(1);
            _focusEnd = DateTime.Now + left;
            _focusLeft = left;
        }
        else if (_focusLeft != FocusLength)
        {
            var left = _focusLeft + step;
            _focusLeft = left < TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : left > TimeSpan.FromHours(3) ? TimeSpan.FromHours(3) : left;
        }
        else
        {
            _settings.TimerMinutes = Math.Clamp(_settings.TimerMinutes + minutes, 1, 180);
            _settings.Save();
            _focusLeft = FocusLength;
        }
        AfterTimerChange();
    }

    void AfterTimerChange()
    {
        UpdateFocusUi();
        UpdateCompact();
        UpdateTickState();
    }

    // ------------------------------------------------------------------ stopwatch

    void ToggleStopwatch()
    {
        if (_swRunning) { _swBefore = SwElapsed; _swRunning = false; }
        else { _swStartedAt = DateTime.Now; _swRunning = true; }
        UpdateStopwatchUi();
        UpdateCompact();
        UpdateTickState();
    }

    void LapOrReset()
    {
        if (_swRunning) _laps.Add(SwElapsed);
        else { _swBefore = TimeSpan.Zero; _laps.Clear(); }
        UpdateStopwatchUi();
        UpdateCompact();
    }

    void UpdateStopwatchUi()
    {
        var t = SwElapsed;
        _card.StopwatchBig.Text = Clock(t, tenths: true);
        _card.StopwatchStartIcon.Text = _swRunning ? "" : "";
        _card.StopwatchStartText.Text = _swRunning ? "Pause" : t > TimeSpan.Zero ? "Resume" : "Start";
        _card.StopwatchLapText.Text = _swRunning || t == TimeSpan.Zero ? "Lap" : "Reset";
        _card.StopwatchLap.IsEnabled = _swRunning || t > TimeSpan.Zero;
        if (_laps.Count == 0) _card.LapsText.Text = "";
        else
        {
            var last = _laps[^1] - (_laps.Count > 1 ? _laps[^2] : TimeSpan.Zero);
            _card.LapsText.Text = $"Lap {_laps.Count} · {Clock(last, tenths: true)}";
        }
        string pill = Clock(t);
        if (CompactStopwatch.Text != pill)
        {
            bool grew = CompactStopwatch.Text.Length != pill.Length;
            CompactStopwatch.Text = pill;
            if (grew) UpdateCompact(); // past an hour: the pill gets a bit wider
        }
        UpdateDockTimer();
    }

    // ------------------------------------------------------------------ shared

    /// <summary>The dock shows the timer while it's running or paused part-way, else a running stopwatch.</summary>
    void UpdateDockTimer()
    {
        if (_dock == null) return;
        if (_focusRunning || _focusLeft != FocusLength) _dock.SetTimer(CompactTimer.Text);
        else if (_swRunning) _dock.SetTimer(Clock(SwElapsed));
        else _dock.SetTimer(null);
    }

    /// <summary>"04:59", "1:02:03", or with tenths "00:12.3".</summary>
    static string Clock(TimeSpan t, bool tenths = false)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        string s = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
        return tenths ? $"{s}.{t.Milliseconds / 100}" : s;
    }

    static string LengthText(TimeSpan t) =>
        t.TotalMinutes >= 60 && t.Minutes == 0 ? $"{(int)t.TotalHours} h" : $"{(int)t.TotalMinutes} min";
}
