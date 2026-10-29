#pragma once

#include <cstdint>

// ============================================================
// 공유 메모리 공통 상수
// ============================================================

// 공유 메모리 프레임을 식별하기 위한 고정값
constexpr std::uint32_t kSharedMemoryMagic = 0x314D4354;

// 공유 메모리 데이터 구조의 버전
constexpr std::uint32_t kSharedMemoryVersion = 1;

// 공유 메모리에서 관리하는 최대 RDR 자산 수
constexpr std::uint32_t kMaxRDRAssets = 1;

// 공유 메모리에서 관리하는 최대 FCS 자산 수
constexpr std::uint32_t kMaxFCSAssets = 1;

// 공유 메모리에서 관리하는 최대 표적 수
constexpr std::uint32_t kMaxTargets = 10;


// ============================================================
// 표적 상태 상수
// ============================================================

// 표적이 정상적으로 존재하는 상태
constexpr std::uint8_t kTargetAlive = 0;

// 표적이 격추된 것으로 처리된 상태
constexpr std::uint8_t kTargetDead = 1;

// 표적이 감지되지 않는 상태
constexpr std::uint8_t kTargetMissing = 2;


// ============================================================
// 구조체 메모리 정렬 설정
// ============================================================

// C++과 C#의 구조체 메모리 배치를 맞추기 위해 4바이트 정렬을 적용
#pragma pack(push, 4)


// ============================================================
// RDR 자산 정보
// ============================================================

struct RDRAsset
{
    std::uint32_t rdrId;         // RDR 자산 식별 번호
    std::uint32_t isEnabled;     // 사용 가능 여부: 0=사용 불가, 1=사용 가능
    std::uint32_t isOperating;   // 동작 상태: 0=정지, 1=동작 중
    std::uint32_t isConnected;   // 연결 상태: 0=연결 끊김, 1=연결됨

    std::int32_t azimuthDeg;     // RDR 방위각(도)
    std::int32_t elevationDeg;   // RDR 고각(도)
}; // 전체 크기: 24바이트


// ============================================================
// FCS 자산 정보
// ============================================================

struct FCSAsset
{
    std::uint32_t fcsId;         // FCS 자산 식별 번호
    std::uint32_t isEnabled;     // 사용 가능 여부: 0=사용 불가, 1=사용 가능
    std::uint32_t isOperating;   // 동작 상태: 0=정지, 1=동작 중
    std::uint32_t isConnected;   // 연결 상태: 0=연결 끊김, 1=연결됨
    std::uint32_t laserOn;       // 레이저 상태: 0=꺼짐, 1=켜짐

    std::int32_t azimuthDeg;     // FCS 방위각(도)
    std::int32_t elevationDeg;   // FCS 고각(도)
}; // 전체 크기: 28바이트


// ============================================================
// 표적 정보
// ============================================================

struct Target
{
    std::uint32_t targetId;      // 표적을 구분하는 고유 번호

    std::int32_t azimuthDeg;     // 표적 방위각(도)
    std::int32_t elevationDeg;   // 표적 고각(도)

    float distanceM;             // 센서 기준 표적 거리(미터)

    float lidarX;                 // 라이다 좌표계의 X 좌표
    float lidarY;                 // 라이다 좌표계의 Y 좌표
    float lidarZ;                 // 라이다 좌표계의 Z 좌표

    std::uint32_t rdrId;         // 표적을 감지한 RDR 자산 번호

    std::uint8_t status;         // 표적 상태: 0=생존, 1=격추, 2=미검출
    std::uint8_t valid;          // 슬롯 유효 여부: 0=미사용, 1=유효
    std::uint16_t reserved;      // 구조 확장을 위한 예약 필드, 현재는 0
}; // 전체 크기: 36바이트


// ============================================================
// 페이로드
// ============================================================

struct Payload
{
    RDRAsset rdrAssets[kMaxRDRAssets];       // RDR 자산 정보 배열
    FCSAsset fcsAssets[kMaxFCSAssets];       // FCS 자산 정보 배열

    std::uint32_t rdrCount;                  // 현재 등록된 RDR 자산 수
    std::uint32_t fcsCount;                  // 현재 등록된 FCS 자산 수

    std::uint32_t selectedRdrId;             // 선택된 RDR 번호, 0은 선택 없음
    std::uint32_t selectedFcsId;             // 선택된 FCS 번호, 0은 선택 없음

    std::uint32_t targetCount;               // 현재 등록된 표적 수
    Target targets[kMaxTargets];             // 표적 정보 배열
}; // 전체 크기: 432바이트


// ============================================================
// 프레임 헤더
// ============================================================

struct FrameHeader
{
    std::uint32_t magic;                     // 프레임 식별값
    std::uint32_t version;                   // 데이터 구조 버전
    std::uint32_t frameSize;                  // 헤더를 포함한 전체 프레임 크기(바이트)
    std::uint32_t sequence;                   // 프레임 갱신 순번

    std::uint64_t timestampMs;                // Unix epoch 기준 프레임 생성 시각(밀리초)

    std::uint32_t payloadSize;                // 페이로드 크기(바이트)
    std::uint32_t checksum;                   // 페이로드에 대한 FNV-1a 32비트 체크섬
}; // 전체 크기: 32바이트


// ============================================================
// 전체 프레임
// ============================================================

struct Frame
{
    FrameHeader header;                       // 프레임 식별값, 버전, 시각, 체크섬 등의 메타데이터
    Payload payload;                          // RDR/FCS 자산 상태와 표적 목록
}; // 전체 크기: 464바이트

// 이전에 설정한 구조체 정렬을 복원
#pragma pack(pop)


// ============================================================
// 컴파일 시 구조체 크기 검증
// ============================================================

static_assert(sizeof(RDRAsset) == 24, "RDRAsset 크기 오류");
static_assert(sizeof(FCSAsset) == 28, "FCSAsset 크기 오류");
static_assert(sizeof(Target) == 36, "Target 크기 오류");
static_assert(sizeof(Payload) == 432, "Payload 크기 오류");
static_assert(sizeof(FrameHeader) == 32, "FrameHeader 크기 오류");
static_assert(sizeof(Frame) == 464, "Frame 크기 오류");
static_assert(sizeof(Frame) % 4 == 0, "Frame 크기는 4바이트의 배수여야 함");
