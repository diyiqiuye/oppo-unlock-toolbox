using System.Collections.ObjectModel;
using System.IO;
using System.Text;

namespace OppoUnlockToolbox.Services;

/// <summary>
/// 「实时输出」那一栏：内存里的行 + 落盘到 logs\session_*.log。
/// </summary>
public sealed class LogBook : IDisposable
{
    private readonly List<LogRecord> _records = new();
    private StreamWriter? _writer;

    public ObservableCollection<LogRecord> Entries { get; } = new();

    /// <summary>新行进来，界面据此决定要不要滚到底。</summary>
    public event Action<LogRecord>? Appended;

    public IReadOnlyList<LogRecord> Records => _records;

    public void Append(string level, string message)
    {
        var record = new LogRecord(DateTime.Now.ToString("HH:mm:ss"), message, level);
        _records.Add(record);
        Entries.Add(record);
        Persist(record);
        Appended?.Invoke(record);
    }

    public void Clear()
    {
        _records.Clear();
        Entries.Clear();
    }

    public void SaveTo(string path) =>
        File.WriteAllLines(path, _records.Select(r => $"{r.TimeText}  {r.MessageText}"));

    private void Persist(LogRecord record)
    {
        try
        {
            if (_writer is null)
            {
                var dir = AppPaths.LogDir();
                Directory.CreateDirectory(dir);
                _writer = new StreamWriter(
                    Path.Combine(dir, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.log"),
                    append: false, encoding: Encoding.UTF8);
            }
            _writer.WriteLine($"{record.TimeText}  {record.MessageText}");
            _writer.Flush();
        }
        catch
        {

        }
    }

    public void Dispose() => _writer?.Dispose();
}
