// ECS 통제소 모의 프로그램
// 공유 메모리에 고정 객체 10개를 1초마다 기록하고 WPF와 네임드 파이프로 통신
// 빌드: cl /utf-8 /std:c++17 /EHsc ControlStationMock.cpp /Fe:ControlStationMock.exe

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include "SharedMemoryData.h"
#include "NamedPipeProtocol.h"

#include <array>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
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
    constexpr wchar_t kMappingName[] = L"Local\\ControlStationMock.SharedMemory.v3";
    constexpr wchar_t kMutexName[] = L"Local\\ControlStationMock.SharedMemory.Mutex.v3";
    constexpr DWORD kMappingSize = 4096;

    std::mutex g_consoleMutex;
    std::mutex g_stateMutex;
    std::atomic<bool> g_running{ true };
    std::atomic<HANDLE> g_activePipe{ INVALID_HANDLE_VALUE };

    bool g_connected = true;
    bool g_available = true;
    bool g_rdrOperating = false;
    float g_rdrElevationDeg = 0;
    float g_rdrElevationTargetDeg = 0;
    float g_fcsAzimuthDeg = 135;
    float g_fcsElevationDeg = 0;
    float g_fcsTargetAzimuthDeg = 135;
    float g_fcsTargetElevationDeg = 0;
    std::uint32_t g_targetId = 0;
    std::uint32_t g_fcsId = 1;
    AssignmentState g_assignment = AssignmentState::Idle;
    bool IsManualScanPaused()
    {
        return g_assignment == AssignmentState::ScanPaused || g_assignment == AssignmentState::Firing ||
            g_assignment == AssignmentState::FireConfirmed;
    }
    std::uint32_t g_fireState = 0;
    bool g_laserOn = false;
    std::chrono::steady_clock::time_point g_laserOffAt{};
    std::chrono::steady_clock::time_point g_fcsBusyUntil{};
    std::array<bool, kMaxTargets> g_targetKilled{};
    std::array<bool, kMaxTargets> g_targetRemoved{};
    enum class ShotMode { Automatic, Hit, Miss };
    ShotMode g_shotMode = ShotMode::Automatic;
    std::array<bool, kMaxTargets> g_hasCompletedShot{};
    bool g_currentShotHits = false;
    std::uint32_t g_observationRound = 0;
    std::array<float, kMaxTargets> g_targetX{};
    std::array<float, kMaxTargets> g_targetY{};
    constexpr float kInitialRdrX = 1.0f;
    constexpr float kInitialFcsX = -1.0f;
    constexpr float kInitialRdrBlindRadius = 2.2f;
    constexpr float kInitialRdrMaxRange = 10.0f;
    // The outermost fixed target is at most 9.5 m + the 2 m installation offset from FCS in plan view.
    constexpr float kInitialFcsMaxRange = 12.0f;
    float g_rdrX = kInitialRdrX, g_rdrY = 0.0f, g_rdrZ = 0.0f;
    float g_fcsX = kInitialFcsX, g_fcsY = 0.0f, g_fcsZ = 0.0f;
    std::array<std::array<float, 3>, 4> g_cameraSettings{{
        {{kInitialRdrX, 0.0f, 0.0f}},
        {{kInitialFcsX, 0.0f, 0.0f}},
        {{kInitialRdrBlindRadius, kInitialRdrMaxRange, kInitialFcsMaxRange}},
        {{120.0f, 45.0f, 0.0f}}
    }};

    std::uint32_t FloatBits(float value)
    {
        std::uint32_t bits{};
        static_assert(sizeof(bits) == sizeof(value));
        std::memcpy(&bits, &value, sizeof(bits));
        return bits;
    }

    void CompleteAdjustmentIfAligned();

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

    float TargetAzimuth(std::uint32_t targetId)
    {
        return static_cast<float>(targetId - 1001) * 29.5f + 0.3f;
    }

    float TargetElevation(std::uint32_t targetId)
    {
        const auto index = targetId - 1001;
        return 5.3f + std::fmod(static_cast<float>(index) * 4.7f, 45.0f);
    }

    double TargetHorizontalDistance(std::uint32_t targetId)
    {
        return 5.0 + static_cast<double>(targetId - 1001) * 0.5;
    }

    double TargetPositionX(std::uint32_t targetId)
    {
        const auto bearing = (TargetAzimuth(targetId) - 135) * 3.14159265358979323846 / 180.0;
        return g_rdrX + std::sin(bearing) * TargetHorizontalDistance(targetId);
    }

    double TargetPositionY(std::uint32_t targetId)
    {
        const auto bearing = (TargetAzimuth(targetId) - 135) * 3.14159265358979323846 / 180.0;
        return g_rdrY + std::cos(bearing) * TargetHorizontalDistance(targetId);
    }

    float TargetAzimuthFromFcs(std::uint32_t targetId)
    {
        // FCS bearing is measured from the FCS origin, not copied from the RDR-relative target angle.
        const auto index = targetId - 1001;
        const auto bearing = std::atan2(g_targetX[index] - g_fcsX, g_targetY[index] - g_fcsY) * 180.0 / 3.14159265358979323846;
        float azimuth = static_cast<float>(bearing + 135.0);
        if (azimuth < 0.0f) azimuth += 360.0f;
        return azimuth;
    }

    float MoveToward(float current, float target, float step)
    {
        if (current < target) return std::min(current + step, target);
        if (current > target) return std::max(current - step, target);
        return current;
    }

    float MoveTowardAzimuth(float current, float target, float step)
    {
        float difference = std::fmod(target - current + 540.0f, 360.0f) - 180.0f;
        if (std::fabs(difference) <= step) return target;
        float next = std::fmod(current + std::copysign(step, difference), 360.0f);
        return next < 0.0f ? next + 360.0f : next;
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

    // 객체 10개의 정보를 시작 시 한 번 설정
    void InitializeFixedTargets(Payload& payload)
    {
        payload.targetCount = kMaxTargets;
        for (std::uint32_t i = 0; i < kMaxTargets; ++i)
        {
            Target& target = payload.targets[i];
            target = {};
            target.targetId = 1001 + i;
            target.azimuthDeg = TargetAzimuth(target.targetId);
            target.elevationDeg = TargetElevation(target.targetId);
            const auto horizontalDistance = TargetHorizontalDistance(target.targetId);
            const auto elevationRadians = target.elevationDeg * 3.14159265358979323846 / 180.0;
            const auto height = std::tan(elevationRadians) * horizontalDistance;
            target.distanceM = static_cast<float>(std::hypot(horizontalDistance, height));
            target.lidarX = static_cast<float>(TargetPositionX(target.targetId));
            target.lidarY = static_cast<float>(TargetPositionY(target.targetId));
            g_targetX[i] = target.lidarX;
            g_targetY[i] = target.lidarY;
            target.lidarZ = static_cast<float>(g_rdrZ + height);
            target.rdrId = 1;
            target.status = kTargetAlive;
            target.valid = 1;
            target.reserved = 0;
        }
    }

    // RDR/FCS 자산과 고정 객체 목록을 초기화
    void InitializeFramePayload(Payload& payload)
    {
        payload = {};
        payload.rdrCount = 1;
        payload.fcsCount = 1;

        RDRAsset& rdr = payload.rdrAssets[0];
        rdr.rdrId = 1;
        rdr.isEnabled = 1;
        rdr.isOperating = g_rdrOperating ? 1u : 0u;
        rdr.isConnected = 1;
        rdr.azimuthDeg = 0;
        rdr.elevationDeg = 0;
        rdr.positionX = g_rdrX;
        rdr.positionY = g_rdrY;
        rdr.positionZ = g_rdrZ;
        rdr.fieldOfViewDeg = 90.0f;

        FCSAsset& fcs = payload.fcsAssets[0];
        fcs.fcsId = 1;
        fcs.isEnabled = 1;
        fcs.isOperating = 1;
        fcs.isConnected = 1;
        fcs.laserOn = 0;
        fcs.azimuthDeg = g_fcsAzimuthDeg;
        fcs.elevationDeg = g_fcsElevationDeg;
        fcs.positionX = g_fcsX;
        fcs.positionY = g_fcsY;
        fcs.positionZ = g_fcsZ;

        InitializeFixedTargets(payload);
    }

    // 공유 메모리를 생성하고 100ms마다 새 프레임 기록
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
        const auto fixedTargets = frame.payload;
        std::uint32_t sequence = 0;
        Print(L"[공유 메모리] 송신 시작. 고정 객체 10개, 갱신 주기 100ms");

        auto nextTick = std::chrono::steady_clock::now();
        float mockRdrAzimuth = 0.0f;
        float mockRdrDirection = 2.0f;
        while (g_running)
        {
            nextTick += std::chrono::milliseconds(100);

            frame.header.magic = kSharedMemoryMagic;
            frame.header.version = kSharedMemoryVersion;
            frame.header.frameSize = static_cast<std::uint32_t>(sizeof(Frame));
            frame.header.sequence = ++sequence;
            frame.header.timestampMs = UnixMs();
            frame.header.payloadSize = static_cast<std::uint32_t>(sizeof(Payload));

            std::uint32_t resultRound = 0;
            std::uint32_t resultTargetId = 0;
            // 네임드 파이프 명령으로 변경된 자산 상태를 공유 메모리에도 반영
            {
                std::lock_guard<std::mutex> lock(g_stateMutex);
                // 5초가 지나면 레이저를 자동으로 끔
                if (g_laserOn && std::chrono::steady_clock::now() >= g_laserOffAt)
                {
                    g_laserOn = false;
                    if (g_assignment == AssignmentState::Firing)
                    {
                        g_fireState = 1;
                        g_hasCompletedShot[g_targetId - 1001] = true;
                        g_assignment = AssignmentState::FireConfirmed;
                    }
                    Print(L"[교전] 5초 경과로 레이저 OFF");
                }

                frame.payload.rdrAssets[0].isEnabled = IsManualScanPaused() ? 0u : 1u;
                frame.payload.rdrAssets[0].isConnected = g_connected ? 1u : 0u;
                frame.payload.rdrAssets[0].isOperating = g_rdrOperating ? 1u : 0u;
                frame.payload.targetCount = 0;
                frame.payload.rdrAssets[0].positionX = g_rdrX;
                frame.payload.rdrAssets[0].positionY = g_rdrY;
                frame.payload.rdrAssets[0].positionZ = g_rdrZ;
                // 모의 RDR는 화면의 -135~+135도 범위를 10도/초로 왕복 주사
                if (g_rdrOperating && !IsManualScanPaused())
                {
                    mockRdrAzimuth += mockRdrDirection;
                    if (mockRdrAzimuth >= 270.0f || mockRdrAzimuth <= 0.0f)
                    {
                        mockRdrAzimuth = std::clamp(mockRdrAzimuth, 0.0f, 270.0f);
                        mockRdrDirection *= -1;
                    }
                }
                frame.payload.rdrAssets[0].azimuthDeg = mockRdrAzimuth;
                if (g_connected && !IsManualScanPaused())
                    g_rdrElevationDeg = MoveToward(g_rdrElevationDeg, g_rdrElevationTargetDeg, 2.5f); // 100ms당 최대 2.5도, 25도/초
                frame.payload.rdrAssets[0].elevationDeg = g_rdrElevationDeg;
                frame.payload.fcsAssets[0].isConnected = g_connected ? 1u : 0u;
                frame.payload.fcsAssets[0].positionX = g_fcsX;
                frame.payload.fcsAssets[0].positionY = g_fcsY;
                frame.payload.fcsAssets[0].positionZ = g_fcsZ;
                if (g_connected && g_available &&
                    (g_assignment == AssignmentState::Adjusting || g_assignment == AssignmentState::ManualAiming))
                {
                    g_fcsAzimuthDeg = MoveTowardAzimuth(g_fcsAzimuthDeg, g_fcsTargetAzimuthDeg, 2.5f); // 100ms당 최대 2.5도
                    g_fcsElevationDeg = MoveToward(g_fcsElevationDeg, g_fcsTargetElevationDeg, 2.5f); // 100ms당 최대 2.5도, 25도/초
                }
                frame.payload.fcsAssets[0].azimuthDeg = g_fcsAzimuthDeg;
                frame.payload.fcsAssets[0].elevationDeg = g_fcsElevationDeg;
                frame.payload.fcsAssets[0].isEnabled = g_available ? 1u : 0u;
                frame.payload.fcsAssets[0].isOperating = g_available ? 1u : 0u;
                frame.payload.fcsAssets[0].laserOn = g_laserOn ? 1u : 0u;
                frame.payload.selectedRdrId = (g_targetId != 0) ? 1u : 0u;
                frame.payload.selectedFcsId = (g_targetId != 0) ? g_fcsId : 0u;

                // 격추 처리된 객체는 이후 프레임에서도 격추 상태를 유지
                for (std::uint32_t i = 0; i < kMaxTargets; ++i)
                {
                    if (g_targetRemoved[i]) continue;
                    auto& target = frame.payload.targets[frame.payload.targetCount++];
                    target = fixedTargets.targets[i];
                    const bool observationPaused = IsManualScanPaused() && !g_targetKilled[i];
                    target.status = g_targetKilled[i] ? kTargetDead : observationPaused ? kTargetObservationPaused : kTargetAlive;
                    target.valid = observationPaused ? 0 : 1;
                }
                for (auto i = frame.payload.targetCount; i < kMaxTargets; ++i)
                    frame.payload.targets[i] = {};
                if (g_assignment == AssignmentState::ScanResumed && g_connected && g_rdrOperating)
                {
                    resultRound = g_observationRound;
                    resultTargetId = g_targetId;
                }
            }

            CompleteAdjustmentIfAligned();

            frame.header.checksum = Fnv1a(
                reinterpret_cast<const std::uint8_t*>(&frame.payload), sizeof(Payload));

            const DWORD wait = WaitForSingleObject(mutex, 1000);
            if (wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED)
            {
                std::memcpy(view, &frame, sizeof(frame));
                MemoryBarrier();
                ReleaseMutex(mutex);
                if (resultTargetId != 0)
                {
                    const auto* begin = frame.payload.targets;
                    const auto* end = begin + frame.payload.targetCount;
                    std::lock_guard<std::mutex> lock(g_stateMutex);
                    if (g_assignment == AssignmentState::ScanResumed && resultRound == g_observationRound && g_connected && g_rdrOperating)
                    {
                        const bool targetObserved = std::any_of(begin, end, [resultTargetId](const Target& target)
                        {
                            return target.targetId == resultTargetId;
                        });
                        g_assignment = targetObserved ? AssignmentState::TargetStillPresent : AssignmentState::ResultConfirmed;
                        if (!targetObserved)
                        {
                            // Publish the confirmed hit as a result row after checking absence.
                            const auto index = resultTargetId - 1001;
                            g_targetKilled[index] = true;
                            g_targetRemoved[index] = false;
                        }
                    }
                }
            }
            else
            {
                Print(L"[공유 메모리] 뮤텍스 대기 실패. 이번 프레임은 건너뜀");
            }

            if (sequence % 10 == 0)
            {
                std::wostringstream line;
                line << L"[공유 메모리] 순번=" << sequence
                     << L", RDR 고각=" << frame.payload.rdrAssets[0].elevationDeg << L"도"
                     << L", FCS 방위각/고각=" << frame.payload.fcsAssets[0].azimuthDeg << L"/" << frame.payload.fcsAssets[0].elevationDeg << L"도"
                     << L", 프레임 크기=" << sizeof(Frame) << L"바이트"
                     << L", 체크섬=0x" << std::hex << std::uppercase
                     << std::setw(8) << std::setfill(L'0') << frame.header.checksum;
                Print(line.str());
            }

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
        case CommandType::AssignTarget: return L"객체 할당";
        case CommandType::CancelAssignment: return L"할당 취소";
        case CommandType::Fire: return L"모의 교전";
        case CommandType::SetRdrElevation: return L"RDR 고각 설정";
        case CommandType::SetRdrOperating: return L"라이다 구동 상태 설정";
        case CommandType::GetFcsBusy: return L"FCS 작업 상태 조회";
        case CommandType::GetCameraSettings: return L"장비 설정 조회";
        case CommandType::SetCameraSettings: return L"장비 설정 저장";
        case CommandType::GetRdrStartupStatus: return L"라이다 구동 상태 확인";
        case CommandType::RegisterManualTarget: return L"수동 절차 객체 등록";
        case CommandType::ConfirmManualAssignment: return L"수동 할당";
        case CommandType::StartManualAim: return L"수동 조준";
        case CommandType::PauseManualScan: return L"조사 중지";
        case CommandType::StartManualFire: return L"모의 발사";
        case CommandType::ConfirmManualFire: return L"발사 완료 확인";
        case CommandType::ResumeManualScan: return L"조사 재개";
        case CommandType::ConfirmManualResult: return L"결과 확인";
        case CommandType::RetryManualAssignment: return L"다시 시도";
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
        case ResultCode::NotAssigned: return L"객체 미할당";
        case ResultCode::AlreadyAssigned: return L"이미 객체 할당됨";
        case ResultCode::NotSupported: return L"지원하지 않는 명령";
        case ResultCode::DeviceError: return L"모의 장치 오류";
        default: return L"알 수 없는 결과";
        }
    }

    // 객체 할당 상태를 한글 설명으로 변환
    std::wstring AssignmentName(std::uint32_t value)
    {
        switch (static_cast<AssignmentState>(value))
        {
        case AssignmentState::Idle: return L"대기";
        case AssignmentState::Adjusting: return L"방향 조정 중";
        case AssignmentState::Assigned: return L"할당 완료";
        case AssignmentState::Failed: return L"할당 실패";
        case AssignmentState::Detected: return L"탐지 완료";
        case AssignmentState::Confirmed: return L"수동 할당 완료";
        case AssignmentState::ManualAiming: return L"수동 조준 중";
        case AssignmentState::Aimed: return L"조준 완료";
        case AssignmentState::ScanPaused: return L"조사 중지";
        case AssignmentState::Firing: return L"모의 발사 요청 완료";
        case AssignmentState::FireConfirmed: return L"발사 완료 확인";
        case AssignmentState::ScanResumed: return L"조사 재개";
        case AssignmentState::ResultConfirmed: return L"결과 확인";
        case AssignmentState::TargetStillPresent: return L"객체 유지";
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

    // FCS가 객체 방위각과 고각에 도달하면 할당 완료 처리
    void CompleteAdjustmentIfAligned()
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        const float azimuthError = std::fabs(std::fmod(g_fcsAzimuthDeg - g_fcsTargetAzimuthDeg + 540.0f, 360.0f) - 180.0f);
        if ((g_assignment != AssignmentState::Adjusting && g_assignment != AssignmentState::ManualAiming) || azimuthError > 0.05f ||
            std::fabs(g_fcsElevationDeg - g_fcsTargetElevationDeg) > 0.05f)
            return;

        g_assignment = g_assignment == AssignmentState::ManualAiming ? AssignmentState::Aimed : AssignmentState::Assigned;
        Print(L"[상태] FCS 방위각/고각 목표 도달");
    }

    // WPF 명령을 검증하고 모의 응답 생성
    PipeResponse HandleCommand(const PipeCommand& command)
    {
        CompleteAdjustmentIfAligned();
        std::lock_guard<std::mutex> lock(g_stateMutex);
        ResultCode result = ResultCode::Success;
        const auto type = static_cast<CommandType>(command.commandType);
        const auto now = std::chrono::steady_clock::now();
        const bool fcsBusy = now < g_fcsBusyUntil;
        const bool manualCommand = type >= CommandType::RegisterManualTarget && type <= CommandType::RetryManualAssignment;

        const bool setAssetPosition = type == CommandType::SetAssetPosition;
        const bool cameraSettings = type == CommandType::GetCameraSettings || type == CommandType::SetCameraSettings;
        const bool validIds = setAssetPosition
            ? ((command.rdrId == 1 && command.fcsId == 0) || (command.rdrId == 0 && command.fcsId == 1))
            : cameraSettings ? (command.rdrId == 0 && command.fcsId == 0)
            : type == CommandType::GetRdrStartupStatus ? true
            : (command.rdrId != 0 && command.fcsId != 0);
        if (command.magic != kPipeMagic || command.version != kPipeVersion ||
            command.requestId == 0 || !validIds)
        {
            result = ResultCode::InvalidRequest;
        }
        else if (type != CommandType::GetStatus && type != CommandType::AssignTarget &&
                 type != CommandType::CancelAssignment && type != CommandType::Fire &&
                 type != CommandType::SetRdrElevation && type != CommandType::SetAssetPosition &&
                 type != CommandType::SetRdrOperating && type != CommandType::GetFcsBusy &&
                 type != CommandType::GetCameraSettings && type != CommandType::SetCameraSettings &&
                 type != CommandType::GetRdrStartupStatus && !manualCommand)
        {
            result = ResultCode::NotSupported;
        }
        else
        {
            switch (type)
            {
            case CommandType::GetStatus:
                break;

            case CommandType::GetFcsBusy:
                if (command.rdrId != 1 || command.fcsId != 1) result = ResultCode::InvalidRequest;
                else if (!g_connected) result = ResultCode::AssetUnavailable;
                else if (!g_available) result = ResultCode::DeviceError;
                break;

            case CommandType::GetRdrStartupStatus:
                if (!g_connected) result = ResultCode::LidarDisconnected;
                else if (!g_rdrOperating) result = ResultCode::RdrStartUnconfirmed;
                break;

            case CommandType::GetCameraSettings:
                if (command.targetId >= g_cameraSettings.size()) result = ResultCode::InvalidRequest;
                break;

            case CommandType::SetCameraSettings:
                if (command.targetId >= g_cameraSettings.size() ||
                    !std::isfinite(command.positionX) || !std::isfinite(command.positionY) || !std::isfinite(command.positionZ))
                    result = ResultCode::InvalidRequest;
                else
                {
                    auto& values = g_cameraSettings[command.targetId];
                    values = { command.positionX, command.positionY, command.positionZ };
                    if (command.targetId == 0) { g_rdrX = command.positionX; g_rdrY = command.positionY; g_rdrZ = command.positionZ; }
                    else if (command.targetId == 1) { g_fcsX = command.positionX; g_fcsY = command.positionY; g_fcsZ = command.positionZ; }
                }
                break;

            case CommandType::SetRdrElevation:
                if (!g_connected)
                    result = ResultCode::AssetUnavailable;
                else if (command.rdrId != 1 || command.targetId > 900)
                    result = ResultCode::InvalidRequest;
                else
                {
                    g_rdrElevationTargetDeg = static_cast<float>(command.targetId) / 10.0f;
                    Print(L"[센서] RDR-001 고각 설정 요청 · 목표 " + std::to_wstring(g_rdrElevationTargetDeg) + L"도, 현재 " + std::to_wstring(g_rdrElevationDeg) + L"도");
                }
                break;

            case CommandType::SetRdrOperating:
                if (!g_connected) result = ResultCode::AssetUnavailable;
                else if (command.rdrId != 1 || command.targetId > 1) result = ResultCode::InvalidRequest;
                else if (IsManualScanPaused()) result = ResultCode::LidarBusy;
                else { g_rdrOperating = command.targetId == 1; Print(g_rdrOperating ? L"[RDR] 라이다 구동" : L"[RDR] 라이다 정지"); }
                break;

            case CommandType::SetAssetPosition:
                if (!g_connected) result = ResultCode::AssetUnavailable;
                else if (command.rdrId == 1 && command.fcsId == 0 && std::isfinite(command.positionX) && std::isfinite(command.positionY) && std::isfinite(command.positionZ)) { g_rdrX=command.positionX; g_rdrY=command.positionY; g_rdrZ=command.positionZ; g_cameraSettings[0] = { g_rdrX, g_rdrY, g_rdrZ }; }
                else if (command.fcsId == 1 && command.rdrId == 0 && std::isfinite(command.positionX) && std::isfinite(command.positionY) && std::isfinite(command.positionZ)) { g_fcsX=command.positionX; g_fcsY=command.positionY; g_fcsZ=command.positionZ; g_cameraSettings[1] = { g_fcsX, g_fcsY, g_fcsZ }; }
                else result = ResultCode::InvalidRequest;
                break;

            case CommandType::AssignTarget:
                if (!g_connected || !g_available || fcsBusy)
                    result = ResultCode::AssetUnavailable;
                else if (command.targetId < 1001 || command.targetId > 1010)
                    result = ResultCode::InvalidRequest;
                else if (g_assignment != AssignmentState::Idle && g_assignment != AssignmentState::Failed)
                    result = ResultCode::AlreadyAssigned;
                else
                {
                    g_targetId = command.targetId;
                    g_fcsId = command.fcsId;
                    g_fcsTargetAzimuthDeg = TargetAzimuthFromFcs(command.targetId);
                    g_fcsTargetElevationDeg = TargetElevation(command.targetId);
                    g_assignment = AssignmentState::Adjusting;
                    g_fireState = 0;
                }
                break;

            case CommandType::RegisterManualTarget:
                if (command.rdrId != 1 || command.fcsId != 1 ||
                    command.targetId < 1001 || command.targetId >= 1001 + kMaxTargets)
                    result = ResultCode::InvalidRequest;
                else if (!g_connected || !g_available || !g_rdrOperating || fcsBusy)
                    result = ResultCode::AssetUnavailable;
                else if (g_targetId != 0)
                    result = ResultCode::AlreadyAssigned;
                else if (g_targetKilled[command.targetId - 1001] || g_targetRemoved[command.targetId - 1001])
                    result = ResultCode::InvalidRequest;
                else
                {
                    g_targetId = command.targetId;
                    g_fcsId = command.fcsId;
                    g_assignment = AssignmentState::Confirmed;
                    g_fireState = 0;
                }
                break;

            case CommandType::ConfirmManualAssignment:
            case CommandType::StartManualAim:
            case CommandType::PauseManualScan:
            case CommandType::StartManualFire:
            case CommandType::ConfirmManualFire:
            case CommandType::ResumeManualScan:
            case CommandType::ConfirmManualResult:
            case CommandType::RetryManualAssignment:
            {
                const auto step = static_cast<std::uint32_t>(type) - 1000;
                const auto previous = type == CommandType::RetryManualAssignment
                    ? AssignmentState::TargetStillPresent : static_cast<AssignmentState>(100 + step - 1);
                if (command.rdrId != 1 || command.fcsId != g_fcsId || command.targetId == 0 || command.targetId != g_targetId)
                    result = ResultCode::NotAssigned;
                else if (!g_connected || !g_available)
                    result = ResultCode::AssetUnavailable;
                else if (g_assignment != previous)
                    result = ResultCode::InvalidRequest;
                else if (step == 2 || step == 6 || step == 8)
                    result = ResultCode::NotSupported;
                else if (step == 5 && fcsBusy)
                    result = ResultCode::StatusPending;
                else if ((step == 4 || step == 5) && !g_rdrOperating)
                    result = ResultCode::AssetUnavailable;
                else if (type == CommandType::RetryManualAssignment)
                {
                    g_assignment = AssignmentState::Confirmed;
                    g_fireState = 0;
                }
                else if (step == 3)
                {
                    g_fcsTargetAzimuthDeg = TargetAzimuthFromFcs(g_targetId);
                    g_fcsTargetElevationDeg = TargetElevation(g_targetId);
                    g_assignment = AssignmentState::ManualAiming;
                }
                else
                {
                    g_assignment = static_cast<AssignmentState>(100 + step);
                    if (step == 5)
                    {
                        g_currentShotHits = g_shotMode == ShotMode::Hit ||
                            (g_shotMode == ShotMode::Automatic && (g_targetId < 1005 || g_hasCompletedShot[g_targetId - 1001]));
                        g_laserOn = true;
                        g_laserOffAt = now + std::chrono::seconds(5);
                        g_fcsBusyUntil = g_laserOffAt + std::chrono::seconds(2);
                    }
                    else if (step == 7)
                    {
                        if (g_currentShotHits) g_targetRemoved[g_targetId - 1001] = true;
                        ++g_observationRound;
                    }
                }
                break;
            }

            case CommandType::CancelAssignment:
                if (g_assignment == AssignmentState::Idle || command.fcsId != g_fcsId ||
                    (command.targetId != 0 && command.targetId != g_targetId))
                    result = ResultCode::NotAssigned;
                else
                {
                    g_targetId = 0;
                    g_fcsId = command.fcsId;
                    g_assignment = AssignmentState::Idle;
                    g_fireState = 0;
                    g_laserOn = false;
                    g_fcsTargetAzimuthDeg = g_fcsAzimuthDeg;
                    g_fcsTargetElevationDeg = g_fcsElevationDeg;
                }
                break;

            case CommandType::Fire:
                // UI와 IPC 연동 확인용 모의 상태만 변경하며 실제 장비를 제어하지 않음
                if (!g_connected || !g_available || fcsBusy)
                    result = ResultCode::AssetUnavailable;
                else if (g_assignment != AssignmentState::Assigned)
                    result = ResultCode::NotAssigned;
                else
                {
                    // 교전 명령이 수락되면 현재 객체를 격추 처리하고 레이저를 5초간 켬
                    g_fireState = 1;
                    g_laserOn = true;
                    g_laserOffAt = now + std::chrono::seconds(5);
                    // Keep the mock device busy after the display turns off and the assignment is canceled.
                    g_fcsBusyUntil = g_laserOffAt + std::chrono::seconds(2);

                    if (g_targetId >= 1001 && g_targetId <= 1010)
                    {
                        g_targetKilled[g_targetId - 1001] = true;
                    }

                    Print(L"[교전] 객체 " + std::to_wstring(g_targetId) +
                          L" 격추 처리, 레이저 ON (5초)");
                }
                break;
            }
        }

        PipeResponse response{
            kPipeMagic, kPipeVersion, command.requestId, command.commandType,
            static_cast<std::uint32_t>(result), g_fcsId, g_targetId,
            static_cast<std::uint32_t>(g_assignment),
            type == CommandType::GetFcsBusy ? (result == ResultCode::Success && fcsBusy ? 1u : 0u) : g_fireState,
            g_connected ? 1u : 0u
        };
        if (type == CommandType::GetCameraSettings && result == ResultCode::Success)
        {
            const auto& values = g_cameraSettings[command.targetId];
            response.fcsId = FloatBits(values[0]);
            response.assignedTargetId = FloatBits(values[1]);
            response.assignmentState = FloatBits(values[2]);
        }
        return response;
    }

    // 수신한 명령과 생성한 응답을 한글로 출력
    void PrintRequestResponse(const PipeCommand& request, const PipeResponse& response)
    {
        std::wostringstream line;
        line << L"[파이프 요청 " << request.requestId << L"] "
             << CommandName(request.commandType)
             << L", RDR=" << request.rdrId << L", FCS=" << request.fcsId;
        if (request.commandType == static_cast<std::uint32_t>(CommandType::SetRdrElevation))
            line << L", 고각=" << request.targetId << L"도";
        else if (request.targetId != 0)
            line << L", 객체=" << request.targetId;
        line << L" -> " << ResultName(response.resultCode)
             << L", 할당 상태=" << AssignmentName(response.assignmentState)
             << L", 할당 객체=" << response.assignedTargetId
             << (request.commandType == static_cast<std::uint32_t>(CommandType::GetFcsBusy)
                 ? L", FCS busy=" : L", 모의 교전 상태=") << response.fireState;
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
        CompleteAdjustmentIfAligned();
        std::lock_guard<std::mutex> lock(g_stateMutex);
        std::wostringstream line;
        line << L"[상태] RDR-001 연결="
             << (g_connected ? L"연결됨" : L"연결 끊김")
             << L", RDR 고각=" << g_rdrElevationDeg << L"→" << g_rdrElevationTargetDeg << L"도"
             << L", FCS-001=" << (g_available ? L"사용 가능" : L"사용 불가")
             << L", FCS AZ/EL=" << g_fcsAzimuthDeg << L"/" << g_fcsElevationDeg
             << L"→" << g_fcsTargetAzimuthDeg << L"/" << g_fcsTargetElevationDeg << L"도"
             << L", 할당 상태=" << AssignmentName(static_cast<std::uint32_t>(g_assignment))
             << L", 객체=" << g_targetId
             << L", 모의 교전 상태=" << g_fireState;
        Print(line.str());
    }

    // ECS 모의 상태를 초기 상태로 복원
    void ResetState()
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        g_connected = true;
        g_available = true;
        g_rdrOperating = false;
        g_rdrElevationDeg = 0;
        g_rdrElevationTargetDeg = 0;
        g_fcsAzimuthDeg = 135;
        g_fcsElevationDeg = 0;
        g_fcsTargetAzimuthDeg = 135;
        g_fcsTargetElevationDeg = 0;
        g_targetId = 0;
        g_fcsId = 1;
        g_assignment = AssignmentState::Idle;
        g_fireState = 0;
        g_laserOn = false;
        g_laserOffAt = {};
        g_fcsBusyUntil = {};
        g_targetKilled.fill(false);
        g_targetRemoved.fill(false);
        g_shotMode = ShotMode::Automatic;
        g_hasCompletedShot.fill(false);
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
        << L"공유 메모리: Local\\ControlStationMock.SharedMemory.v3\n"
        << L"네임드 파이프: \\\\.\\pipe\\ECS_NamedPipe | 프로토콜 버전 3\n"
        << L"고정 객체: 10개(1001~1010) | 갱신 주기: 100ms | 모터: AZ 25°/s, EL 25°/s\n"
        << L"명령어: help, status, connected on|off, available on|off, shot auto|hit|miss, reset, quit\n";

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
            Print(L"명령어: status | connected on|off | available on|off | shot auto|hit|miss | reset | quit");
        else if (command == L"status")
            PrintState();
        else if (command == L"shot")
        {
            if (value != L"auto" && value != L"hit" && value != L"miss")
                Print(L"사용법: shot auto|hit|miss");
            else
            {
                std::lock_guard<std::mutex> lock(g_stateMutex);
                g_shotMode = value == L"hit" ? ShotMode::Hit : value == L"miss" ? ShotMode::Miss : ShotMode::Automatic;
                Print(L"[모의 상태] 발사 모드: " + value);
            }
        }
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
