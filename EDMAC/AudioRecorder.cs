using NAudio.Wave;

namespace EDMAC;

public sealed class AudioRecorder : IDisposable
{
    private const int BufferCount = 128;
    private const int BufferSize = 65536;

    private readonly WaveFormat waveFormat;
    private readonly object sync = new();
    private readonly byte[][] buffers;
    private readonly int[] lengths;
    private readonly IntRingQueue freeBuffers = new(BufferCount);
    private readonly IntRingQueue readyBuffers = new(BufferCount);
    private WaveFileWriter? writer;
    private Task? writerTask;
    private bool recording;
    private bool stopping;
    private bool disposed;

    public AudioRecorder(WaveFormat waveFormat)
    {
        this.waveFormat = waveFormat;
        buffers = new byte[BufferCount][];
        lengths = new int[BufferCount];

        for (var index = 0; index < BufferCount; index++)
        {
            buffers[index] = new byte[BufferSize];
            freeBuffers.TryEnqueue(index);
        }
    }

    public bool IsRecording
    {
        get
        {
            lock (sync)
            {
                return recording;
            }
        }
    }

    public string? CurrentPath { get; private set; }

    public string Start(string recordingsDirectory)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        Directory.CreateDirectory(recordingsDirectory);
        string path = Path.Combine(
            recordingsDirectory,
            $"edmac-{DateTime.Now:yyyyMMdd-HHmmss-fff}.wav");

        lock (sync)
        {
            if (recording)
            {
                return CurrentPath!;
            }

            writer = new WaveFileWriter(path, waveFormat);
            CurrentPath = path;
            stopping = false;
            recording = true;
            writerTask = Task.Factory.StartNew(
                WriteLoop,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        return path;
    }

    public string? Stop()
    {
        Task? task;
        string? path;

        lock (sync)
        {
            if (!recording && writerTask is null)
            {
                return null;
            }

            path = CurrentPath;
            recording = false;
            stopping = true;
            task = writerTask;
            Monitor.PulseAll(sync);
        }

        task?.GetAwaiter().GetResult();
        return path;
    }

    public void Capture(byte[] buffer, int offset, int count)
    {
        lock (sync)
        {
            if (!recording || count > BufferSize || !freeBuffers.TryDequeue(out int bufferIndex))
            {
                return;
            }

            Buffer.BlockCopy(buffer, offset, buffers[bufferIndex], 0, count);
            lengths[bufferIndex] = count;
            readyBuffers.TryEnqueue(bufferIndex);
            Monitor.Pulse(sync);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Stop();
    }

    private void WriteLoop()
    {
        Thread.CurrentThread.Name ??= "EDMAC WAV Recorder";
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;

        while (true)
        {
            int bufferIndex;
            int length;
            WaveFileWriter? currentWriter;

            lock (sync)
            {
                while (readyBuffers.Count == 0 && !stopping)
                {
                    Monitor.Wait(sync);
                }

                if (readyBuffers.Count == 0 && stopping)
                {
                    writer?.Dispose();
                    writer = null;
                    writerTask = null;
                    stopping = false;
                    Monitor.PulseAll(sync);
                    return;
                }

                readyBuffers.TryDequeue(out bufferIndex);
                length = lengths[bufferIndex];
                currentWriter = writer;
            }

            currentWriter?.Write(buffers[bufferIndex], 0, length);

            lock (sync)
            {
                freeBuffers.TryEnqueue(bufferIndex);
            }
        }
    }

    private sealed class IntRingQueue
    {
        private readonly int[] items;
        private int head;
        private int tail;

        public IntRingQueue(int capacity)
        {
            items = new int[capacity];
        }

        public int Count { get; private set; }

        public bool TryEnqueue(int item)
        {
            if (Count == items.Length)
            {
                return false;
            }

            items[tail] = item;
            tail = (tail + 1) % items.Length;
            Count++;
            return true;
        }

        public bool TryDequeue(out int item)
        {
            if (Count == 0)
            {
                item = 0;
                return false;
            }

            item = items[head];
            head = (head + 1) % items.Length;
            Count--;
            return true;
        }
    }
}
