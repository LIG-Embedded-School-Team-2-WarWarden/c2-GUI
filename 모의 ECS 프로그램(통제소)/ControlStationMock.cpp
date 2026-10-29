// ECS 통제소 모의 프로그램
// 공유 메모리에 고정 표적 10개를 1초마다 기록하고 WPF와 네임드 파이프로 통신
// 빌드: cl /utf-8 /std:c++17 /EHsc ControlStationMock.cpp /Fe:ControlStationMock.exe

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include "SharedMemoryData.h"
#include "NamedPipeProtocol.h"

#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <io.h>
#include <fcntl.h>
#include <sstream>
#include <string>
#include <thread>

namespace
{
    constexpr wchar_t kMappingName[] = L"Local\\ControlStationMock.SharedMemory.v1";
    constexpr wchar_t kMutexName[] = L"Local\\ControlStationMock.SharedMemory.Mutex.v1";
    constexpr DWORD kMappingSize = 4096;

    std::mutex g_consoleMutex;
    std::mutex g_stateMutex;
    std::atomic<bool> g_running{ true };
    std::atomic<HANDLE> g_activePipe{ INVALID_HANDLE_VALUE };

    bool g_connected = true;
    bool g_available = true;
    std::uint32_t g_targetId = 0;
    std::uint32_t g_fcsId = 1;
    AssignmentState g_assignment = AssignmentState::Idle;
    std::uint32_t g_fireState = 0;
    bool g_laserOn = false;
    std::chrono::steady_clock::time_point g_laserOffAt{};
    std::array<bool, kMaxTargets> g_targetKilled{};
    std::chrono::steady_clock::time_point g_adjustmentStarted{};

    // 콘솔에 한글 메시지를 출력하며 여러 스레드의 출력이 섞이지 않도록 보호
    void Print(const std::wstring& message)
    {
        std::lock_guard<std::mutex> lock(g_consoleMutex);
        std::wcout << message << L"\n" << std::flush;
    }

    // Unix epoch 기준 현재 시각을 밀리초 단위로 반환
    std::uint64_t UnixMs()
    {
        using namespace std::chrono;
        return static_cast<std::uint64_t>(
            duration_cast<milliseconds>(system_clock::now().time_since_epoch()).count());
    }

    // 페이로드 데이터의 FNV-1a 32비트 체크섬 계산
    std::uint32_t Fnv1a(const std::uint8_t* data, std::size_t size)
    {
        std::uint32_t hash = 2166136261u;
        for (std::size_t i = 0; i < size; ++i)
        {
            hash ^= data[i];
            hash *= 16777619u;
        }
        return hash;
    }

    // 표적 10개의 정보를 시작 시 한 번 설정
    void InitializeFixedTargets(Payload& payload)
    {
        payload.targetCount = kMaxTargets;
        for (std::uint32_t i = 0; i < kMaxTargets; ++i)
        {
            Target& target = payload.targets[i];
            target = {};
            target.targetId = 1001 + i;
            target.azimuthDeg = static_cast<std::int32_t>((i * 31 + 12) % 360);
            target.elevationDeg = static_cast<std::int32_t>(5 + (i * 4) % 45);
            target.distanceM = 250.0f + static_cast<float>(i) * 95.0f;
            target.lidarX = -45.0f + static_cast<float>(i) * 10.0f;
            target.lidarY = 20.0f + static_cast<float>(i) * 3.5f;
            target.lidarZ = 8.0f + static_cast<float>(i) * 2.0f;
            target.rdrId = 1;
            target.status = kTargetAlive;
            target.valid = 1;
            target.reserved = 0;
        }
    }

    // RDR/FCS 자산과 고정 표적 목록을 초기화
    void InitializeFramePayload(Payload& payload)
    {
        payload = {};
        payload.rdrCount = 1;
        payload.fcsCount = 1;

        RDRAsset& rdr = payload.rdrAssets[0];
        rdr.rdrId = 1;
        rdr.isEnabled = 1;
        rdr.isOperating = 1;
        rdr.isConnected = 1;
        rdr.azimuthDeg = 0;
        rdr.elevationDeg = 0;

        FCSAsset& fcs = payload.fcsAssets[0];
        fcs.fcsId = 1;
        fcs.isEnabled = 1;
        fcs.isOperating = 1;
        fcs.isConnected = 1;
        fcs.laserOn = 0;
        fcs.azimuthDeg = 0;
        fcs.elevationDeg = 0;

        InitializeFixedTargets(payload);
    }

    // 공유 메모리를 생성하고 1초마다 새 프레임 기록
    void SharedMemoryLoop()
    {
        HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
                                            0, kMappingSize, kMappingName);
        if (!mapping)
        {
            Print(L"[공유 메모리] 생성 실패. 오류 코드: " + std::to_wstring(GetLastError()));
            g_running = false;
            return;
        }

        void* view = MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, kMappingSize);
        if (!view)
        {
            Print(L"[공유 메모리] 연결 실패. 오류 코드: " + std::to_wstring(GetLastError()));
            CloseHandle(mapping);
            g_running = false;
            return;
        }

        HANDLE mutex = CreateMutexW(nullptr, FALSE, kMutexName);
        if (!mutex)
        {
            Print(L"[공유 메모리] 뮤텍스 생성 실패. 오류 코드: " + std::to_wstring(GetLastError()));
            UnmapViewOfFile(view);
            CloseHandle(mapping);
            g_running = false;
            return;
        }

        Frame frame{};
        InitializeFramePayload(frame.payload);
        std::uint32_t sequence = 0;
        Print(L"[공유 메모리] 송신 시작. 고정 표적 10개, 갱신 주기 1초");

        auto nextTick = std::chrono::steady_clock::now();
        while (g_running)
        {
            nextTick += std::chrono::seconds(1);

            frame.header.magic = kSharedMemoryMagic;
            frame.header.version = kSharedMemoryVersion;
            frame.header.frameSize = static_cast<std::uint32_t>(sizeof(Frame));
            frame.header.sequence = ++sequence;
            frame.header.timestampMs = UnixMs();
            frame.header.payloadSize = static_cast<std::uint32_t>(sizeof(Payload));

            // 네임드 파이프 명령으로 변경된 자산 상태를 공유 메모리에도 반영
            {
                std::lock_guard<std::mutex> lock(g_stateMutex);
                // 5초가 지나면 레이저를 자동으로 끔
                if (g_laserOn && std::chrono::steady_clock::now() >= g_laserOffAt)
                {
                    g_laserOn = false;
                    Print(L"[교전] 5초 경과로 레이저 OFF");
                }

                frame.payload.rdrAssets[0].isConnected = g_connected ? 1u : 0u;
                frame.payload.fcsAssets[0].isConnected = g_connected ? 1u : 0u;
                frame.payload.fcsAssets[0].isEnabled = g_available ? 1u : 0u;
                frame.payload.fcsAssets[0].isOperating = g_available ? 1u : 0u;
                frame.payload.fcsAssets[0].laserOn = g_laserOn ? 1u : 0u;
                frame.payload.selectedRdrId = (g_targetId != 0) ? 1u : 0u;
                frame.payload.selectedFcsId = (g_targetId != 0) ? g_fcsId : 0u;

                // 격추 처리된 표적은 이후 프레임에서도 격추 상태를 유지
                for (std::uint32_t i = 0; i < kMaxTargets; ++i)
                {
                    frame.payload.targets[i].status = g_targetKilled[i] ? kTargetDead : kTargetAlive;
                }
            }

            frame.header.checksum = Fnv1a(
                reinterpret_cast<const std::uint8_t*>(&frame.payload), sizeof(Payload));

            const DWORD wait = WaitForSingleObject(mutex, 1000);
            if (wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED)
            {
                std::memcpy(view, &frame, sizeof(frame));
                MemoryBarrier();
                ReleaseMutex(mutex);
            }
            else
            {
                Print(L"[공유 메모리] 뮤텍스 대기 실패. 이번 프레임은 건너뜀");
            }

            std::wostringstream line;
            line << L"[공유 메모리] 순번=" << sequence
                 << L", 표적 수=" << frame.payload.targetCount
                 << L", 프레임 크기=" << sizeof(Frame) << L"바이트"
                 << L", 체크섬=0x" << std::hex << std::uppercase
                 << std::setw(8) << std::setfill(L'0') << frame.header.checksum;
            Print(line.str());

            std::this_thread::sleep_until(nextTick);
        }

        CloseHandle(mutex);
        UnmapViewOfFile(view);
        CloseHandle(mapping);
        Print(L"[공유 메모리] 송신 종료");
    }

    // 명령 번호를 한글 설명으로 변환
    std::wstring CommandName(std::uint32_t value)
    {
        switch (static_cast<CommandType>(value))
        {
        case CommandType::GetStatus: return L"상태 조회";
        case CommandType::AssignTarget: return L"표적 할당";
        case CommandType::CancelAssignment: return L"할당 취소";
        case CommandType::Fire: return L"모의 교전";
        default: return L"알 수 없는 명령";
        }
    }

    // 처리 결과 코드를 한글 설명으로 변환
    std::wstring ResultName(std::uint32_t value)
    {
        switch (static_cast<ResultCode>(value))
        {
        case ResultCode::Success: return L"성공";
        case ResultCode::InvalidRequest: return L"잘못된 요청";
        case ResultCode::AssetUnavailable: return L"자산 사용 불가";
        case ResultCode::NotAssigned: return L"표적 미할당";
        case ResultCode::AlreadyAssigned: return L"이미 표적 할당됨";
        case ResultCode::NotSupported: return L"지원하지 않는 명령";
        case ResultCode::DeviceError: return L"모의 장치 오류";
        default: return L"알 수 없는 결과";
        }
    }

    // 표적 할당 상태를 한글 설명으로 변환
    std::wstring AssignmentName(std::uint32_t value)
    {
        switch (static_cast<AssignmentState>(value))
        {
        case AssignmentState::Idle: return L"대기";
        case AssignmentState::Adjusting: return L"방향 조정 중";
        case AssignmentState::Assigned: return L"할당 완료";
        case AssignmentState::Failed: return L"할당 실패";
        default: return L"알 수 없는 상태";
        }
    }

    // 지정한 크기만큼 데이터를 모두 수신
    bool ReadExact(HANDLE pipe, void* destination, DWORD size)
    {
        auto* bytes = static_cast<std::uint8_t*>(destination);
        DWORD total = 0;
        while (total < size && g_running)
        {
            DWORD read = 0;
            if (!ReadFile(pipe, bytes + total, size - total, &read, nullptr) || read == 0)
                return false;
            total += read;
        }
        return total == size;
    }

    // 지정한 크기만큼 데이터를 모두 송신
    bool WriteExact(HANDLE pipe, const void* source, DWORD size)
    {
        const auto* bytes = static_cast<const std::uint8_t*>(source);
        DWORD total = 0;
        while (total < size && g_running)
        {
            DWORD written = 0;
            if (!WriteFile(pipe, bytes + total, size - total, &written, nullptr) || written == 0)
                return false;
            total += written;
        }
        return total == size;
    }

    // 표적 방향 조정 모의를 시작한 뒤 3초가 지나면 할당 완료 처리
    void CompleteAdjustmentIfDue()
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        if (g_assignment != AssignmentState::Adjusting ||
            std::chrono::steady_clock::now() - g_adjustmentStarted < std::chrono::seconds(3))
            return;

        g_assignment = AssignmentState::Assigned;
        Print(L"[상태] 모의 방향 조정 완료. 표적 할당 완료");
    }

    // WPF 명령을 검증하고 모의 응답 생성
    PipeResponse HandleCommand(const PipeCommand& command)
    {
        CompleteAdjustmentIfDue();
        std::lock_guard<std::mutex> lock(g_stateMutex);
        ResultCode result = ResultCode::Success;

        if (command.magic != kPipeMagic || command.version != kPipeVersion ||
            command.requestId == 0 || command.rdrId == 0 || command.fcsId == 0)
        {
            result = ResultCode::InvalidRequest;
        }
        else if (command.commandType > static_cast<std::uint32_t>(CommandType::Fire))
        {
            result = ResultCode::NotSupported;
        }
        else
        {
            switch (static_cast<CommandType>(command.commandType))
            {
            case CommandType::GetStatus:
                break;

            case CommandType::AssignTarget:
                if (!g_connected || !g_available)
                    result = ResultCode::AssetUnavailable;
                else if (command.targetId < 1001 || command.targetId > 1010)
                    result = ResultCode::InvalidRequest;
                else if (g_assignment == AssignmentState::Adjusting ||
                         g_assignment == AssignmentState::Assigned)
                    result = ResultCode::AlreadyAssigned;
                else
                {
                    g_targetId = command.targetId;
                    g_fcsId = command.fcsId;
                    g_assignment = AssignmentState::Adjusting;
                    g_fireState = 0;
                    g_adjustmentStarted = std::chrono::steady_clock::now();
                }
                break;

            case CommandType::CancelAssignment:
                if (g_assignment == AssignmentState::Idle)
                    result = ResultCode::NotAssigned;
                else
                {
                    g_targetId = 0;
                    g_fcsId = command.fcsId;
                    g_assignment = AssignmentState::Idle;
                    g_fireState = 0;
                    g_adjustmentStarted = {};
                }
                break;

            case CommandType::Fire:
                // UI와 IPC 연동 확인용 모의 상태만 변경하며 실제 장비를 제어하지 않음
                if (!g_connected || !g_available)
                    result = ResultCode::AssetUnavailable;
                else if (g_assignment != AssignmentState::Assigned)
                    result = ResultCode::NotAssigned;
                else
                {
                    // 교전 명령이 수락되면 현재 표적을 격추 처리하고 레이저를 5초간 켬
                    g_fireState = 1;
                    g_laserOn = true;
                    g_laserOffAt = std::chrono::steady_clock::now() + std::chrono::seconds(5);

                    if (g_targetId >= 1001 && g_targetId <= 1010)
                    {
                        g_targetKilled[g_targetId - 1001] = true;
                    }

                    Print(L"[교전] 표적 " + std::to_wstring(g_targetId) +
                          L" 격추 처리, 레이저 ON (5초)");
                }
                break;
            }
        }

        return PipeResponse{
            kPipeMagic, kPipeVersion, command.requestId, command.commandType,
            static_cast<std::uint32_t>(result), g_fcsId, g_targetId,
            static_cast<std::uint32_t>(g_assignment), g_fireState, g_connected ? 1u : 0u
        };
    }

    // 수신한 명령과 생성한 응답을 한글로 출력
    void PrintRequestResponse(const PipeCommand& request, const PipeResponse& response)
    {
        std::wostringstream line;
        line << L"[파이프 요청 " << request.requestId << L"] "
             << CommandName(request.commandType)
             << L", RDR=" << request.rdrId << L", FCS=" << request.fcsId;
        if (request.targetId != 0)
            line << L", 표적=" << request.targetId;
        line << L" -> " << ResultName(response.resultCode)
             << L", 할당 상태=" << AssignmentName(response.assignmentState)
             << L", 할당 표적=" << response.assignedTargetId
             << L", 모의 교전 상태=" << response.fireState;
        Print(line.str());
    }

    // WPF 연결을 기다리고 명령을 처리하는 네임드 파이프 서버
    void NamedPipeServerLoop()
    {
        while (g_running)
        {
            HANDLE pipe = CreateNamedPipeW(
                kPipePath, PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                1, sizeof(PipeResponse), sizeof(PipeCommand), 0, nullptr);

            if (pipe == INVALID_HANDLE_VALUE)
            {
                Print(L"[네임드 파이프] 서버 생성 실패. 오류 코드: " +
                      std::to_wstring(GetLastError()));
                std::this_thread::sleep_for(std::chrono::milliseconds(500));
                continue;
            }

            g_activePipe = pipe;
            const BOOL connected = ConnectNamedPipe(pipe, nullptr)
                ? TRUE : (GetLastError() == ERROR_PIPE_CONNECTED);

            if (connected && g_running)
            {
                Print(L"[네임드 파이프] WPF 클라이언트 연결됨");
                while (g_running)
                {
                    PipeCommand request{};
                    if (!ReadExact(pipe, &request, sizeof(request)))
                        break;

                    PipeResponse response = HandleCommand(request);
                    PrintRequestResponse(request, response);
                    if (!WriteExact(pipe, &response, sizeof(response)))
                        break;
                }
                Print(L"[네임드 파이프] WPF 클라이언트 연결 해제");
            }

            g_activePipe = INVALID_HANDLE_VALUE;
            DisconnectNamedPipe(pipe);
            CloseHandle(pipe);
        }
    }

    // 현재 ECS 모의 상태를 콘솔에 출력
    void PrintState()
    {
        CompleteAdjustmentIfDue();
        std::lock_guard<std::mutex> lock(g_stateMutex);
        std::wostringstream line;
        line << L"[상태] RDR-001 연결="
             << (g_connected ? L"연결됨" : L"연결 끊김")
             << L", FCS-001=" << (g_available ? L"사용 가능" : L"사용 불가")
             << L", 할당 상태=" << AssignmentName(static_cast<std::uint32_t>(g_assignment))
             << L", 표적=" << g_targetId
             << L", 모의 교전 상태=" << g_fireState;
        Print(line.str());
    }

    // ECS 모의 상태를 초기 상태로 복원
    void ResetState()
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        g_connected = true;
        g_available = true;
        g_targetId = 0;
        g_fcsId = 1;
        g_assignment = AssignmentState::Idle;
        g_fireState = 0;
        g_laserOn = false;
        g_laserOffAt = {};
        g_targetKilled.fill(false);
        g_adjustmentStarted = {};
    }

    // 콘솔 입력값을 연결/사용 가능 상태로 변환
    bool ParseToggle(const std::wstring& value, bool& parsed)
    {
        if (value == L"on" || value == L"ON") { parsed = true; return true; }
        if (value == L"off" || value == L"OFF") { parsed = false; return true; }
        return false;
    }
}

int wmain()
{
    // Windows 콘솔에서 한글 유니코드 입출력을 사용하도록 설정
    _setmode(_fileno(stdout), _O_U16TEXT);
    _setmode(_fileno(stdin), _O_U16TEXT);

    std::wcout
        << L"ECS 통제소 모의 프로그램\n"
        << L"공유 메모리: Local\\ControlStationMock.SharedMemory.v1\n"
        << L"네임드 파이프: \\\\.\\pipe\\ECS_NamedPipe | 프로토콜 버전 2\n"
        << L"고정 표적: 10개(1001~1010) | 갱신 주기: 1초\n"
        << L"명령어: help, status, connected on|off, available on|off, reset, quit\n";

    std::thread shmThread(SharedMemoryLoop);
    std::thread pipeThread(NamedPipeServerLoop);
    PrintState();

    std::wstring input;
    while (g_running && std::wcout << L"ECS> " && std::getline(std::wcin, input))
    {
        std::wistringstream tokens(input);
        std::wstring command, value;
        tokens >> command >> value;

        if (command == L"help")
            Print(L"명령어: status | connected on|off | available on|off | reset | quit");
        else if (command == L"status")
            PrintState();
        else if (command == L"connected" || command == L"available")
        {
            bool enabled = false;
            if (!ParseToggle(value, enabled))
                Print(L"사용법: connected on|off 또는 available on|off");
            else
            {
                std::lock_guard<std::mutex> lock(g_stateMutex);
                if (command == L"connected") g_connected = enabled;
                else g_available = enabled;
                Print(L"[모의 상태] 설정 변경 완료");
            }
        }
        else if (command == L"reset")
        {
            ResetState();
            Print(L"[모의 상태] 초기화 완료");
            PrintState();
        }
        else if (command == L"quit" || command == L"exit")
            break;
        else if (!command.empty())
            Print(L"알 수 없는 명령입니다. help를 입력하세요.");
    }

    g_running = false;
    HANDLE active = g_activePipe.load();
    if (active != INVALID_HANDLE_VALUE)
    {
        CancelIoEx(active, nullptr);
        DisconnectNamedPipe(active);
    }

    if (pipeThread.joinable())
    {
        CancelSynchronousIo(pipeThread.native_handle());
        pipeThread.join();
    }
    if (shmThread.joinable())
        shmThread.join();

    Print(L"ECS 통제소 모의 프로그램 종료");
    return 0;
}
