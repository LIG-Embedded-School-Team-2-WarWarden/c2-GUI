using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace AadcCommandCenter;

/// <summary>ECS에 보낼 Named Pipe 명령 종류</summary>
internal enum CommandType : uint
{
    GetStatus = 0,          // 현재 연결과 표적 할당 상태 조회
    AssignTarget = 1,       // 표적을 타격자산에 할당
    CancelAssignment = 2,   // 현재 표적 할당 취소
    Fire = 3,               // 할당된 표적에 발사 명령
    SetRdrElevation = 4     // RDR 목표 고각 설정, PipeCommand.TargetId에 정수 각도(0~90도) 전달
}

/// <summary>Named Pipe 명령 처리 결과</summary>
internal enum ResultCode : uint
{
    Success = 0,          // 요청 처리 성공
    InvalidRequest = 1,   // 요청 필드나 값이 잘못됨
    AssetUnavailable = 2,// 자산 연결이 끊겼거나 사용할 수 없음
    NotAssigned = 3,     // 발사·취소할 표적이 할당되지 않음
    AlreadyAssigned = 4, // 타격자산에 이미 표적이 할당됨
    NotSupported = 5,    // 지원하지 않는 명령 종류
    DeviceError = 6      // 모의 장치 처리 중 오류 발생
}

/// <summary>타격자산의 표적 할당 진행 상태</summary>
internal enum AssignmentState : uint
{
    Idle = 0,       // 할당된 표적 없음
    Adjusting = 1,  // 타격자산이 표적 방향으로 조정 중
    Assigned = 2,   // 표적 방위각과 고각에 도달
    Failed = 3      // 표적 할당 실패
}

/// <summary>WPF에서 ECS로 보내는 28바이트 요청 구조체</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PipeCommand
{
    public uint Magic;       // 프로토콜 식별값, PipeCommandClient.Magic과 일치해야 함
    public uint Version;     // 프로토콜 버전, PipeCommandClient.Version과 일치해야 함
    public uint CommandType; // 요청 종류를 나타내는 CommandType 값
    public uint RequestId;   // 응답과 짝짓는 요청 순번
    public uint RdrId;       // 명령 대상 RDR ID
    public uint FcsId;       // 명령 대상 FCS ID
    public uint TargetId;    // 표적 ID, 미사용은 0; 고각 설정 명령에서는 목표 고각(0~90도)
}

/// <summary>ECS에서 WPF로 돌려주는 40바이트 응답 구조체</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PipeResponse
{
    public uint Magic;             // 프로토콜 식별값, PipeCommandClient.Magic과 일치해야 함
    public uint Version;           // 프로토콜 버전
    public uint RequestId;         // 응답 대상 요청 순번
    public uint CommandType;       // 처리한 CommandType 값
    public uint ResultCode;        // 처리 결과를 나타내는 ResultCode 값
    public uint FcsId;             // 응답 상태에 해당하는 FCS ID
    public uint AssignedTargetId;  // 현재 할당된 표적 ID, 미할당은 0
    public uint AssignmentState;   // 현재 표적 할당 단계를 나타내는 AssignmentState 값
    public uint FireState;         // 발사 상태: 0=대기, 1=발사 결과 확인
    public uint IsConnected;       // ECS 연결 상태: 0=끊김, 1=연결됨
}

internal sealed class PipeCommandClient : IDisposable
{
    internal const uint Magic = 0x50495045;
    internal const uint Version = 2;
    private const string Server = ".";
    private const string PipeName = "ECS_NamedPipe";
    private readonly SemaphoreSlim gate = new(1, 1);
    private NamedPipeClientStream? pipe;
    private uint requestId = 1;
    public bool IsConnected => pipe?.IsConnected == true;

    public async Task<PipeResponse> SendAsync(CommandType type, uint targetId = 0, uint rdrId = 1, uint fcsId = 1, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);
            var command = new PipeCommand
            {
                Magic = Magic,
                Version = Version,
                CommandType = (uint)type,
                RequestId = requestId++,
                RdrId = rdrId,
                FcsId = fcsId,
                TargetId = targetId
            };
            try
            {
                await WriteStructAsync(pipe!, command, cancellationToken);
                PipeResponse response = await ReadStructAsync<PipeResponse>(pipe!, cancellationToken);
                if (response.Magic != Magic || response.Version != Version || response.RequestId != command.RequestId)
                    throw new IOException("ECS 응답의 식별값, 버전 또는 요청 번호가 일치하지 않습니다.");
                return response;
            }
            catch
            {
                Disconnect();
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (IsConnected) return;
        Disconnect();
        pipe = new NamedPipeClientStream(Server, PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(1000, cancellationToken); }
        catch { Disconnect(); throw; }
    }

    private void Disconnect()
    {
        pipe?.Dispose();
        pipe = null;
    }

    private static async Task WriteStructAsync<T>(Stream stream, T value, CancellationToken cancellationToken) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        byte[] bytes = new byte[size];
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            Marshal.Copy(ptr, bytes, 0, size);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static async Task<T> ReadStructAsync<T>(Stream stream, CancellationToken cancellationToken) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        byte[] bytes = new byte[size];
        int total = 0;
        while (total < size)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(total, size - total), cancellationToken);
            if (read == 0) throw new IOException("ECS 서버가 연결을 종료했습니다.");
            total += read;
        }
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(bytes, 0, ptr, size);
            return Marshal.PtrToStructure<T>(ptr);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public void Dispose()
    {
        Disconnect();
        gate.Dispose();
    }
}
