using AadcCommandCenter;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Reflection;

static class FrameFreshnessChecks
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc = DateTimeOffset.UtcNow;
        public long Ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ticks;
        public override DateTimeOffset GetUtcNow() => Utc;
        public void Advance(int ms, bool wall = true) { Ticks += ms; if (wall) Utc = Utc.AddMilliseconds(ms); }
    }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run(Action<string> stateChanged, Action<SharedFrame> received, Func<int> rowCount, Func<bool> canAim)
    {
        var name = "Local\\FreshnessTest" + Guid.NewGuid().ToString("N");
        var mutexName = name + "Mutex";
        var previous = Environment.GetEnvironmentVariable("AADC9_SHARED_MEMORY_MUTEX_NAME");
        Environment.SetEnvironmentVariable("AADC9_SHARED_MEMORY_MUTEX_NAME", mutexName);
        try
        {
            using var mutex = new Mutex(false, mutexName);
            using var mapping = MemoryMappedFile.CreateNew(name, 492);
            using var view = mapping.CreateViewAccessor();
            var time = new Clock();
            using var reader = new SharedMemoryFrameReader(name, timeProvider: time);
            string state = ""; int frames = 0;
            reader.StateChanged += s => { state = s; stateChanged(s); };
            reader.FrameReceived += f => { frames++; received(f); };
            var poll = typeof(SharedMemoryFrameReader).GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void Poll() => poll.Invoke(reader, new object?[] { null });
            void Write(uint sequence, long timestamp, bool corrupt = false, byte status = 4)
            {
                var bytes = new byte[492];
                void Word(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
                Word(0, 0x314d4354); Word(4, 3); Word(8, 492); Word(12, sequence); Word(24, 460);
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), (ulong)timestamp);
                foreach (var offset in new[] { 32, 36, 40, 44, 72, 76, 80, 84, 112, 116, 120, 124, 128 }) Word(offset, 1);
                Word(132, 7); Word(160, 1); bytes[164] = status; bytes[165] = status == 0 ? (byte)1 : (byte)0;
                uint hash = 2166136261;
                unchecked { foreach (var value in bytes.AsSpan(32)) hash = (hash ^ value) * 16777619; }
                Word(28, corrupt ? hash ^ 1 : hash);
                view.WriteArray(0, bytes, 0, bytes.Length);
            }
            var stamp = time.Utc.ToUnixTimeMilliseconds();
            Write(7, stamp, status: 0); Poll(); Check(state.StartsWith("LIVE") && frames == 1 && rowCount() == 1 && canAim(), "fresh assigned frame rejected");
            time.Advance(1999); Poll(); Check(state.StartsWith("LIVE") && frames == 1, "fresh duplicate rejected");
            time.Advance(1); Poll(); Check(!state.StartsWith("LIVE") && rowCount() == 0 && !canAim(), "frozen frame remained live at timeout");
            time.Advance(13000); Poll(); Check(!state.StartsWith("LIVE") && frames == 1 && rowCount() == 0, "frozen frame revived");
            // First read of an old mapping must also fail; new sequence alone is insufficient.
            Write(8, stamp); Poll(); Check(!state.StartsWith("LIVE") && frames == 1, "old header accepted");
            // Writer restart may reuse a sequence, but has a new timestamp.
            stamp = time.Utc.ToUnixTimeMilliseconds(); Write(7, stamp); Poll();
            Check(state.StartsWith("LIVE") && frames == 2 && rowCount() == 1, "same-sequence restart failed");
            Write(7, stamp, true); Poll(); Check(!state.StartsWith("LIVE") && rowCount() == 0, "corruption left rows active");
            Write(7, stamp); Poll(); Check(state.StartsWith("LIVE") && frames == 3 && rowCount() == 1, "fresh recovery did not repopulate rows");
            time.Advance(2000, wall: false); Poll();
            Check(!state.StartsWith("LIVE") && rowCount() == 0, "wall clock stalled monotonic timeout");
            Write(9, time.Utc.ToUnixTimeMilliseconds() + 60000); Poll(); Check(!state.StartsWith("LIVE"), "future header accepted");
            Console.WriteLine("PASS: frame timeout, stale mapping, restart, corruption recovery, monotonic expiry, view invalidation");
        }
        finally { Environment.SetEnvironmentVariable("AADC9_SHARED_MEMORY_MUTEX_NAME", previous); }
    }
}
