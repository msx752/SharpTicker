using System.Text;

namespace SharpTicker.Scenarios.Harness;

/// <summary>
/// A write-only stream that receives one container's output: it writes every line to the container's log file
/// and passes stdout lines to the run log.
/// </summary>
internal sealed class LogSink : Stream
{
    private readonly string _container;
    private readonly RunLog? _runLog;
    private readonly StreamWriter _file;
    private readonly object _gate;
    private readonly List<byte> _pending = new();

    public LogSink(string container, StreamWriter file, object fileGate, RunLog? runLog)
    {
        _container = container;
        _file = file;
        _gate = fileGate;
        _runLog = runLog;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_pending)
        {
            for (var i = offset; i < offset + count; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    Emit(Encoding.UTF8.GetString(_pending.ToArray()).TrimEnd('\r'));
                    _pending.Clear();
                }
                else
                {
                    _pending.Add(buffer[i]);
                }
            }
        }
    }

    private void Emit(string line)
    {
        lock (_gate)
        {
            _file.WriteLine(line);
            _file.Flush();
        }

        _runLog?.Add(_container, line);
    }

    protected override void Dispose(bool disposing)
    {
        lock (_pending)
        {
            if (_pending.Count > 0)
            {
                Emit(Encoding.UTF8.GetString(_pending.ToArray()));
                _pending.Clear();
            }
        }

        base.Dispose(disposing);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
