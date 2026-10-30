#pragma once

#include <cstdint>

// ============================================================
// 네임드 파이프 공통 설정
// ============================================================

// WPF와 C++ 서버가 요청 및 응답에서 공통으로 사용하는 식별값
constexpr std::uint32_t kPipeMagic = 0x50495045;

// 네임드 파이프 통신 프로토콜 버전
constexpr std::uint32_t kPipeVersion = 2;

// Windows 네임드 파이프 경로
constexpr wchar_t kPipePath[] = L"\\\\.\\pipe\\ECS_NamedPipe";


// ============================================================
// 명령 종류
// ============================================================

enum class CommandType : std::uint32_t
{
    GetStatus = 0,       // 현재 모의 상태 조회
    AssignTarget = 1,    // 표적 할당 요청
    CancelAssignment = 2,// 표적 할당 취소 요청
    Fire = 3,            // 모의 교전 결과 요청
    SetRdrElevation = 4  // RDR 고각 설정 요청, targetId 필드에 각도(0~90도)
};


// ============================================================
// 명령 처리 결과
// ============================================================

enum class ResultCode : std::uint32_t
{
    Success = 0,          // 요청 처리 성공
    InvalidRequest = 1,   // 요청 데이터가 유효하지 않음
    AssetUnavailable = 2, // 자산이 연결되지 않았거나 사용 불가
    NotAssigned = 3,      // 할당된 표적이 없음
    AlreadyAssigned = 4,  // 이미 표적이 할당되어 있음
    NotSupported = 5,     // 지원하지 않는 명령
    DeviceError = 6       // 모의 장치 처리 오류
};


// ============================================================
// 표적 할당 상태
// ============================================================

enum class AssignmentState : std::uint32_t
{
    Idle = 0,       // 할당된 표적이 없음
    Adjusting = 1,  // 표적 방향 조정 모의 진행 중
    Assigned = 2,   // 표적 할당 완료
    Failed = 3      // 표적 할당 실패
};


// ============================================================
// 구조체 메모리 정렬 설정
// ============================================================

// WPF의 Pack = 4 설정과 일치하도록 4바이트 정렬 적용
#pragma pack(push, 4)


// ============================================================
// WPF에서 ECS로 보내는 명령 구조체
// ============================================================

struct PipeCommand
{
    std::uint32_t magic;        // 프로토콜 식별값, kPipeMagic과 일치해야 함
    std::uint32_t version;      // 프로토콜 버전, kPipeVersion과 일치해야 함
    std::uint32_t commandType;  // CommandType에 정의된 요청 종류
    std::uint32_t requestId;    // 요청과 응답을 짝짓는 순번
    std::uint32_t rdrId;        // 명령 대상 RDR ID
    std::uint32_t fcsId;        // 명령 대상 FCS ID
    std::uint32_t targetId;     // 표적 ID, 표적 없음은 0; SetRdrElevation 요청에서는 고각(0~90도)
}; // 전체 크기: 28바이트


// ============================================================
// ECS에서 WPF로 보내는 응답 구조체
// ============================================================

struct PipeResponse
{
    std::uint32_t magic;             // 프로토콜 식별값, kPipeMagic과 일치해야 함
    std::uint32_t version;           // 프로토콜 버전, kPipeVersion과 일치해야 함
    std::uint32_t requestId;         // 응답 대상 요청의 순번
    std::uint32_t commandType;       // 처리한 CommandType 값
    std::uint32_t resultCode;        // 처리 결과를 나타내는 ResultCode 값

    std::uint32_t fcsId;             // 응답 상태에 해당하는 FCS ID
    std::uint32_t assignedTargetId;  // 현재 FCS에 할당된 표적 ID, 미할당은 0
    std::uint32_t assignmentState;   // 현재 할당 단계를 나타내는 AssignmentState 값
    std::uint32_t fireState;         // 모의 발사 상태: 0=대기, 1=발사 결과 확인
    std::uint32_t isConnected;       // ECS 연결 상태: 0=끊김, 1=연결됨
}; // 전체 크기: 40바이트

// 이전에 설정한 구조체 정렬을 복원
#pragma pack(pop)


// ============================================================
// 컴파일 시 구조체 크기 검증
// ============================================================

static_assert(sizeof(PipeCommand) == 28, "PipeCommand 크기 오류");
static_assert(sizeof(PipeResponse) == 40, "PipeResponse 크기 오류");
