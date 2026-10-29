using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace AadcCommandCenter;

// Keep these values and the mapping name in sync with the C++ sender.
public static class SharedMemoryProtocol
{
    public const string DefaultName = "Local\\ControlStationMock.SharedMemory.v1";
    public const string DefaultMutexName = "Local\\ControlStationMock.SharedMemory.Mutex.v1";
    public const uint Magic = 0x314D4354;
    public const uint Version = 1;
    public const int RdrCapacity = 1;
    public const int FcsCapacity = 1;
    public const int TargetCapacity = 10;
    public const int HeaderSize = 32;
    public const int PayloadSize = 432;
    public const int FrameSize = HeaderSize + PayloadSize;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SharedRdrAsset
{
    public uint rdrId, isEnabled, isOperating, isConnected;
    public int azimuthDeg, elevationDeg;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SharedFcsAsset
{
    public uint fcsId, isEnabled, isOperating, isConnected, laserOn;
    public int azimuthDeg, elevationDeg;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SharedTarget
{
    public uint targetId;
    public int azimuthDeg, elevationDeg;
    public float distanceM, lidarX, lidarY, lidarZ;
    public uint rdrId;
    public byte status, valid;
    public ushort reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SharedPayload
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SharedMemoryProtocol.RdrCapacity)]
    public SharedRdrAsset[] rdrAssets;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SharedMemoryProtocol.FcsCapacity)]
    public SharedFcsAsset[] fcsAssets;
    public uint rdrCount, fcsCount, selectedRdrId, selectedFcsId, targetCount;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SharedMemoryProtocol.TargetCapacity)]
    public SharedTarget[] targets;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SharedFrameHeader
{
    public uint magic, version, frameSize, sequence;
    public ulong timestampMs;
    public uint payloadSize, checksum;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SharedFrame
{
    public SharedFrameHeader header;
    public SharedPayload payload;
}

/// <summary>Read-only poller for the C++ named shared-memory frame.</summary>
public sealed class SharedMemoryFrameReader : IDisposable
{
    private readonly string _name;
    private readonly string _mutexName;
    private readonly Timer _timer;
    private uint? _lastSequence;
    private string? _lastState;
    private int _reading;
    private bool _disposed;

    public event Action<SharedFrame>? FrameReceived;
    public event Action<string>? StateChanged;

    public SharedMemoryFrameReader(string? name = null, int pollMilliseconds = 50)
    {
        _name = string.IsNullOrWhiteSpace(name)
            ? Environment.GetEnvironmentVariable("AADC9_SHARED_MEMORY_NAME") ?? SharedMemoryProtocol.DefaultName
            : name;
        _mutexName = Environment.GetEnvironmentVariable("AADC9_SHARED_MEMORY_MUTEX_NAME") ?? SharedMemoryProtocol.DefaultMutexName;
        ValidateManagedLayout();
        _timer = new Timer(Poll, null, Timeout.Infinite, Timeout.Infinite);
        PollMilliseconds = Math.Clamp(pollMilliseconds, 20, 1000);
    }

    public int PollMilliseconds { get; }
    public string MappingName => _name;
    public string MutexName => _mutexName;

    public void Start() => _timer.Change(0, PollMilliseconds);
    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    private static void ValidateManagedLayout()
    {
        if (Marshal.SizeOf<SharedRdrAsset>() != 24 || Marshal.SizeOf<SharedFcsAsset>() != 28 ||
            Marshal.SizeOf<SharedTarget>() != 36 || Marshal.SizeOf<SharedPayload>() != SharedMemoryProtocol.PayloadSize ||
            Marshal.SizeOf<SharedFrameHeader>() != SharedMemoryProtocol.HeaderSize || Marshal.SizeOf<SharedFrame>() != SharedMemoryProtocol.FrameSize)
            throw new InvalidOperationException("C# 공유 메모리 구조체 크기가 C++ 프레임과 일치하지 않습니다.");
    }

    private void Poll(object? _)
    {
        if (Interlocked.Exchange(ref _reading, 1) != 0) return;
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(_name, MemoryMappedFileRights.Read);
            using var view = mapping.CreateViewAccessor(0, SharedMemoryProtocol.FrameSize, MemoryMappedFileAccess.Read);
            using var mutex = Mutex.OpenExisting(_mutexName);
            bool ownsMutex;
            try { ownsMutex = mutex.WaitOne(250); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex)
            {
                SetState("송신 뮤텍스 대기 시간 초과");
                return;
            }

            try
            {
            var bytes = new byte[SharedMemoryProtocol.FrameSize];
            if (view.ReadArray(0, bytes, 0, bytes.Length) != bytes.Length)
            {
                SetState("프레임 크기 부족");
                return;
            }

            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            SharedFrame frame;
            try { frame = Marshal.PtrToStructure<SharedFrame>(handle.AddrOfPinnedObject()); }
            finally { handle.Free(); }

            if (frame.header.magic != SharedMemoryProtocol.Magic || frame.header.version != SharedMemoryProtocol.Version ||
                frame.header.frameSize != SharedMemoryProtocol.FrameSize || frame.header.payloadSize != SharedMemoryProtocol.PayloadSize)
            {
                SetState("프레임 헤더 불일치");
                return;
            }

            // Reject a torn read if the sender advanced its sequence during our snapshot.
            if (view.ReadUInt32(12) != frame.header.sequence)
            {
                SetState("프레임 갱신 중 · 재시도");
                return;
            }
            if (Checksum(bytes, SharedMemoryProtocol.HeaderSize, SharedMemoryProtocol.PayloadSize) != frame.header.checksum)
            {
                SetState("체크섬 오류 · 재시도");
                return;
            }

            var payload = frame.payload;
            if (payload.rdrCount > SharedMemoryProtocol.RdrCapacity || payload.fcsCount > SharedMemoryProtocol.FcsCapacity || payload.targetCount > SharedMemoryProtocol.TargetCapacity)
            {
                SetState("등록 개수 초과");
                return;
            }

            if (_lastSequence == frame.header.sequence)
            {
                SetState($"LIVE · #{frame.header.sequence}");
                return;
            }
            _lastSequence = frame.header.sequence;
            SetState($"LIVE · #{frame.header.sequence}");
            FrameReceived?.Invoke(frame);
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (FileNotFoundException) { SetState($"공유 메모리 대기 · {_name}"); }
        catch (WaitHandleCannotBeOpenedException) { SetState($"공유 메모리 대기 · {_name}"); }
        catch (UnauthorizedAccessException) { SetState("공유 메모리 접근 거부"); }
        catch (IOException ex) { SetState($"공유 메모리 읽기 오류 · {ex.Message}"); }
        catch (Exception ex) { SetState($"프레임 오류 · {ex.Message}"); }
        finally { Volatile.Write(ref _reading, 0); }
    }

    private void SetState(string state)
    {
        if (state == _lastState) return;
        _lastState = state;
        StateChanged?.Invoke(state);
    }

    private static uint Checksum(byte[] bytes, int offset, int count)
    {
        uint hash = 2166136261;
        unchecked
        {
            for (var i = offset; i < offset + count; i++)
            {
                hash ^= bytes[i];
                hash *= 16777619;
            }
        }
        return hash;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
