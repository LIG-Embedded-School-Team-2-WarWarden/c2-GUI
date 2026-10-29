using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace AadcCommandCenter;

internal enum CommandType : uint
{
    GetStatus = 0,
    AssignTarget = 1,
    CancelAssignment = 2,
    Fire = 3
}

internal enum ResultCode : uint
{
    Success = 0,
    InvalidRequest = 1,
    AssetUnavailable = 2,
    NotAssigned = 3,
    AlreadyAssigned = 4,
    NotSupported = 5,
    DeviceError = 6
}

internal enum AssignmentState : uint
{
    Idle = 0,
    Adjusting = 1,
    Assigned = 2,
    Failed = 3
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PipeCommand
{
    public uint Magic;
    public uint Version;
    public uint CommandType;
    public uint RequestId;
    public uint RdrId;
    public uint FcsId;
    public uint TargetId;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PipeResponse
{
    public uint Magic;
    public uint Version;
    public uint RequestId;
    public uint CommandType;
    public uint ResultCode;
    public uint FcsId;
    public uint AssignedTargetId;
    public uint AssignmentState;
    public uint FireState;
    public uint IsConnected;
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
