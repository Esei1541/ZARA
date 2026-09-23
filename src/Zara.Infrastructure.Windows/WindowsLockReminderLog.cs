using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace Zara.Infrastructure.Windows;

/// <summary>Records lock reminder decisions without doing file I/O on the caller's thread.</summary>
public sealed class WindowsLockReminderLog : IDisposable
{
    private const int QueueCapacity = 512;
    private const int MaxMessageCharacters = 2048;
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly string _directoryPath;
    private readonly long _maxFileBytes;
    private readonly Channel<LogItem> _queue;
    private readonly Task _consumer;
    private Exception? _lastFailure;
    private int _disposed;

    public WindowsLockReminderLog()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZARA",
            "logs"))
    {
    }

    internal WindowsLockReminderLog(string directoryPath, long maxFileBytes = 262144)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileBytes, 128);

        _directoryPath = directoryPath;
        _maxFileBytes = maxFileBytes;
        _queue = Channel.CreateBounded<LogItem>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _consumer = Task.Run(ConsumeAsync);
    }

    internal Exception? LastFailure => Volatile.Read(ref _lastFailure);

    public void Write(string message)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        message ??= string.Empty;
        string cleanMessage = message.Replace('\r', ' ').Replace('\n', ' ');
        if (cleanMessage.Length > MaxMessageCharacters)
        {
            cleanMessage = cleanMessage[..(MaxMessageCharacters - 3)] + "...";
        }

        string line = $"{DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)} [pid={Environment.ProcessId}] {cleanMessage}{Environment.NewLine}";
        _queue.Writer.TryWrite(new LogItem(line, null));
    }

    internal async Task FlushAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _queue.Writer.WriteAsync(new LogItem(null, completion)).ConfigureAwait(false);
            await completion.Task.ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // Disposing the log also drains the queue, within its bounded timeout.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        try
        {
            _consumer.Wait(DisposeDrainTimeout);
        }
        catch (AggregateException exception)
        {
            Volatile.Write(ref _lastFailure, exception);
        }
    }

    private async Task ConsumeAsync()
    {
        await foreach (LogItem item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (item.Completion is not null)
            {
                item.Completion.TrySetResult(true);
                continue;
            }

            try
            {
                AppendLine(item.Line!);
                Volatile.Write(ref _lastFailure, null);
            }
            catch (Exception exception)
            {
                // Retry access on the next event; diagnostics must never affect the lock policy.
                Volatile.Write(ref _lastFailure, exception);
            }
        }
    }

    private void AppendLine(string line)
    {
        Directory.CreateDirectory(_directoryPath);
        string currentPath = Path.Combine(_directoryPath, "lock-reminders.log");
        string previousPath = Path.Combine(_directoryPath, "lock-reminders.previous.log");

        byte[] bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.LongLength > _maxFileBytes)
        {
            string shortened = line.TrimEnd('\r', '\n');
            while (Encoding.UTF8.GetByteCount(shortened) + Environment.NewLine.Length > _maxFileBytes)
            {
                shortened = shortened[..^1];
            }

            bytes = Encoding.UTF8.GetBytes(shortened + Environment.NewLine);
        }

        if (File.Exists(currentPath))
        {
            long currentLength = new FileInfo(currentPath).Length;
            if (currentLength > 0 && currentLength + bytes.LongLength > _maxFileBytes)
            {
                File.Move(currentPath, previousPath, overwrite: true);
            }
        }

        using var stream = new FileStream(currentPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
    }

    private readonly record struct LogItem(string? Line, TaskCompletionSource<bool>? Completion);
}
