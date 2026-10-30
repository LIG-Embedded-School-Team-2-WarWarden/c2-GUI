using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace AadcCommandCenter;

// C++ 송신 프로그램과 공유하는 매핑 이름, 버전, 구조체 크기 설정
public static class SharedMemoryProtocol
{
    // 공유 메모리 매핑과 동기화 뮤텍스 이름
    public const string DefaultName = "Local\\ControlStationMock.SharedMemory.v2";
    public const string DefaultMutexName = "Local\\ControlStationMock.SharedMemory.Mutex.v2";
    // 프레임 식별값과 데이터 구조 버전
    public const uint Magic = 0x314D4354;
    public const uint Version = 2;
    // 프레임에 담을 자산 및 표적 최대 개수
    public const int RdrCapacity = 1;
    public const int FcsCapacity = 1;
    public const int TargetCapacity = 10;
    // 헤더와 페이로드의 크기(바이트)
    public const int HeaderSize = 32;
    public const int PayloadSize = 452;
    public const int FrameSize = HeaderSize + PayloadSize;
}

/// <summary>RDR 자산 상태와 좌표를 담는 공유 구조체</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SharedRdrAsset
{
    public uint rdrId;             // RDR 자산 식별 번호
    public uint isEnabled;         // 사용 가능 여부: 0=불가, 1=가능
    public uint isOperating;       // 구동 상태: 0=정지, 1=구동 중
    public uint isConnected;       // 연결 상태: 0=끊김, 1=연결됨
    public int azimuthDeg;         // 현재 방위각: 0~270도, 화면 기준 -135~+135도
    public int elevationDeg;       // 현재 고각: 0~90도
    public float positionX;       // 공통 평면 좌표계에서 RDR의 X 위치(미터)
    public float positionY;       // 공통 평면 좌표계에서 RDR의 Y 위치(미터)
    public float fieldOfViewDeg;  // 방위각 중심선을 기준으로 한 전체 시야각(도)
}

/// <summary>FCS 자산 상태와 좌표를 담는 공유 구조체</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SharedFcsAsset
{
    public uint fcsId;             // FCS 자산 식별 번호
    public uint isEnabled;         // 사용 가능 여부: 0=불가, 1=가능
    public uint isOperating;       // 구동 상태: 0=정지, 1=구동 중
    public uint isConnected;       // 연결 상태: 0=끊김, 1=연결됨
    public uint laserOn;           // 레이저 상태: 0=꺼짐, 1=켜짐
    public int azimuthDeg;         // 현재 방위각: 0~270도, 화면 기준 -135~+135도
    public int elevationDeg;       // 현재 고각: 0~90도
    public float positionX;       // 공통 평면 좌표계에서 FCS의 X 위치(미터)
    public float positionY;       // 공통 평면 좌표계에서 FCS의 Y 위치(미터)
}

/// <summary>탐지된 표적의 방향, 좌표, 상태를 담는 공유 구조체</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SharedTarget
{
    public uint targetId;          // 표적 고유 번호
    public int azimuthDeg;         // RDR 기준 표적 방위각(도)
    public int elevationDeg;       // 표적 고각(도)
    public float distanceM;        // RDR 기준 표적 거리(미터)
    public float lidarX;           // 공통 평면 좌표계에서 표적의 X 위치(미터)
    public float lidarY;           // 공통 평면 좌표계에서 표적의 Y 위치(미터)
    public float lidarZ;           // 라이다 좌표계에서 표적의 Z 위치(미터)
    public uint rdrId;             // 표적을 감지한 RDR 자산 번호
    public byte status;            // 상태: 0=생존, 1=격추, 2=미검출
    public byte valid;             // 슬롯 사용 여부: 0=미사용, 1=유효한 표적
    public ushort reserved;        // 정렬과 향후 확장을 위한 예약값, 현재 0
}

/// <summary>RDR·FCS 자산과 표적 목록을 담는 프레임 페이로드</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SharedPayload
{
    // 고정 크기 RDR 자산 배열
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SharedMemoryProtocol.RdrCapacity)]
    public SharedRdrAsset[] rdrAssets;
    // 고정 크기 FCS 자산 배열
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SharedMemoryProtocol.FcsCapacity)]
    public SharedFcsAsset[] fcsAssets;
    public uint rdrCount;          // 실제 등록된 RDR 수
    public uint fcsCount;          // 실제 등록된 FCS 수
    public uint selectedRdrId;     // GUI에서 선택한 RDR ID, 0은 선택 없음
    public uint selectedFcsId;     // GUI에서 선택한 FCS ID, 0은 선택 없음
    public uint targetCount;       // 현재 등록된 표적 수
    // 고정 크기 표적 배열
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SharedMemoryProtocol.TargetCapacity)]
    public SharedTarget[] targets;
}

/// <summary>공유 메모리 프레임의 식별·크기·시간·검증 정보를 담는 헤더</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SharedFrameHeader
{
    public uint magic;             // 프레임 식별값, SharedMemoryProtocol.Magic과 일치해야 함
    public uint version;           // 구조체 버전, 현재 2
    public uint frameSize;         // 헤더 포함 전체 프레임 크기(바이트)
    public uint sequence;          // 새 프레임마다 증가하는 순번
    public ulong timestampMs;      // Unix epoch 기준 프레임 생성 시각(밀리초)
    public uint payloadSize;       // 페이로드 크기(바이트)
    public uint checksum;          // 페이로드 무결성 확인용 FNV-1a 32비트 체크섬
}

/// <summary>헤더와 페이로드를 결합한 공유 메모리 전체 프레임</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SharedFrame
{
    public SharedFrameHeader header; // 프레임 메타데이터
    public SharedPayload payload;    // 자산 상태와 표적 정보
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
        if (Marshal.SizeOf<SharedRdrAsset>() != 36 || Marshal.SizeOf<SharedFcsAsset>() != 36 ||
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
