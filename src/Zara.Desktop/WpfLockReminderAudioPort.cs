using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Zara.Application.UsagePolicy;

namespace Zara.Desktop;

/// <summary>Plays the packaged MP3 clips asynchronously in the interactive desktop session.</summary>
internal sealed class WpfLockReminderAudioPort : ILockReminderAudioPort
{
    private MediaPlayer? _player;
    private Func<bool>? _isStillValid;

    public void Play(int minutes, Func<bool> isStillValid)
    {
        ArgumentNullException.ThrowIfNull(isStillValid);
        if (minutes is not (30 or 10 or 5 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(minutes));
        }

        StopPlayback();
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Audio",
            minutes.ToString(CultureInfo.InvariantCulture) + "min.mp3");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The lock reminder audio is missing.", path);
        }

        var player = new MediaPlayer();
        _player = player;
        _isStillValid = isStillValid;
        player.MediaOpened += OnMediaOpened;
        player.MediaEnded += OnMediaEnded;
        player.MediaFailed += OnMediaFailed;
        player.Volume = 1;
        player.Open(new Uri(path, UriKind.Absolute));
    }

    public void StopPlayback()
    {
        MediaPlayer? player = _player;
        _player = null;
        _isStillValid = null;
        if (player is null)
        {
            return;
        }

        player.MediaOpened -= OnMediaOpened;
        player.MediaEnded -= OnMediaEnded;
        player.MediaFailed -= OnMediaFailed;
        player.Close();
    }

    private void OnMediaOpened(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _player))
        {
            return;
        }

#pragma warning disable CA1031 // Media callbacks cannot allow an optional reminder to crash the app.
        try
        {
            if (_isStillValid?.Invoke() == true)
            {
                _player?.Play();
            }
            else
            {
                StopPlayback();
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("The lock reminder audio failed: {0}", exception);
            StopAfterMediaEvent();
        }
#pragma warning restore CA1031
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _player))
        {
            StopAfterMediaEvent();
        }
    }

    private void OnMediaFailed(object? sender, ExceptionEventArgs e)
    {
        if (ReferenceEquals(sender, _player))
        {
            Trace.TraceError("The lock reminder audio could not be loaded: {0}", e.ErrorException);
            StopAfterMediaEvent();
        }
    }

    private void StopAfterMediaEvent()
    {
#pragma warning disable CA1031 // Release optional media without propagating asynchronous failures.
        try
        {
            StopPlayback();
        }
        catch (Exception exception)
        {
            Trace.TraceError("The lock reminder media could not close: {0}", exception);
        }
#pragma warning restore CA1031
    }
}
