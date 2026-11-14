#pragma once

#include <cstdint>
// RDR, FCS 및 객체의 각도와 좌표는 GUI와 공유 메모리에서 모두 float 형식으로 통신함
// ============================================================
// 공유 메모리 공통 상수
// ============================================================

// 공유 메모리 프레임을 식별하기 위한 고정값
constexpr std::uint32_t kSharedMemoryMagic = 0x314D4354;

// 공유 메모리 데이터 구조의 버전
constexpr std::uint32_t kSharedMemoryVersion = 3;

// 공유 메모리에서 관리하는 최대 RDR 자산 수
constexpr std::uint32_t kMaxRDRAssets = 1;

// 공유 메모리에서 관리하는 최대 FCS 자산 수
constexpr std::uint32_t kMaxFCSAssets = 1;

// 공유 메모리에서 관리하는 최대 객체 수
constexpr std::uint32_t kMaxTargets = 10;


// ============================================================
// 객체 상태 상수
// ============================================================

// 객체가 정상적으로 존재하는 상태
constexpr std::uint8_t kTargetAlive = 0;

// 객체가 격추된 것으로 처리된 상태
constexpr std::uint8_t kTargetDead = 1;

// 객체가 감지되지 않는 상태
constexpr std::uint8_t kTargetMissing = 2;
constexpr std::uint8_t kTargetObservationPaused = 4; // 마지막 관측 위치만 표시 (valid=0)


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
    std::uint32_t rdrId;          // RDR 자산 식별 번호
    std::uint32_t isEnabled;      // 사용 가능 여부: 0=사용 불가, 1=사용 가능
    std::uint32_t isOperating;    // 구동 상태: 0=정지, 1=구동 중
    std::uint32_t isConnected;    // 연결 상태: 0=끊김, 1=연결됨

    float azimuthDeg;             // 현재 방위각: 0~270도, 화면 기준 -135~+135도
    float elevationDeg;           // 현재 고각: 0~90도
    float positionX;              // 평면 좌표계에서 RDR의 X 위치(미터)
    float positionY;              // 평면 좌표계에서 RDR의 Y 위치(미터)
    float positionZ;              // RDR의 Z 위치(미터)
    float fieldOfViewDeg;          // 방위각 중심선을 기준으로 한 전체 시야각(도)
}; // 전체 크기: 40바이트


// ============================================================
// FCS 자산 정보
// ============================================================

struct FCSAsset
{
    std::uint32_t fcsId;          // FCS 자산 식별 번호
    std::uint32_t isEnabled;      // 사용 가능 여부: 0=사용 불가, 1=사용 가능
    std::uint32_t isOperating;    // 구동 상태: 0=정지, 1=구동 중
    std::uint32_t isConnected;    // 연결 상태: 0=끊김, 1=연결됨
    std::uint32_t laserOn;        // 레이저 상태: 0=꺼짐, 1=켜짐

    float azimuthDeg;             // 현재 FCS 방위각: 0~360도, 135도가 화면 기준 정면
    float elevationDeg;           // 현재 FCS 고각: 0~90도
    float positionX;              // 평면 좌표계에서 FCS의 X 위치(미터)
    float positionY;              // 평면 좌표계에서 FCS의 Y 위치(미터)
    float positionZ;              // FCS의 Z 위치(미터)
}; // 전체 크기: 40바이트


// ============================================================
// 객체 정보
// ============================================================

struct Target
{
    std::uint32_t targetId;       // 객체 고유 번호

    float azimuthDeg;             // RDR 기준 객체 방위각(도)
    float elevationDeg;           // 객체 고각(도)

    float distanceM;              // RDR 기준 객체 거리(미터)

    float lidarX;                  // 공통 평면 좌표계에서 객체의 X 위치(미터)
    float lidarY;                  // 공통 평면 좌표계에서 객체의 Y 위치(미터)
    float lidarZ;                  // 라이다 좌표계에서 객체의 Z 위치(미터)

    std::uint32_t rdrId;           // 객체를 감지한 RDR 자산 번호

    std::uint8_t status;           // 객체 상태: 0=생존, 1=격추, 2=미검출
    std::uint8_t valid;            // 슬롯 사용 여부: 0=미사용, 1=유효한 객체
    std::uint16_t reserved;        // 정렬과 향후 확장을 위한 예약값, 현재 0
}; // 전체 크기: 36바이트


// ============================================================
// 페이로드
// ============================================================

struct Payload
{
    RDRAsset rdrAssets[kMaxRDRAssets];        // 등록된 RDR 자산 정보
    FCSAsset fcsAssets[kMaxFCSAssets];        // 등록된 FCS 자산 정보

    std::uint32_t rdrCount;                   // 실제 등록된 RDR 수
    std::uint32_t fcsCount;                   // 실제 등록된 FCS 수

    std::uint32_t selectedRdrId;              // GUI에서 선택한 RDR ID, 0은 선택 없음
    std::uint32_t selectedFcsId;              // GUI에서 선택한 FCS ID, 0은 선택 없음

    std::uint32_t targetCount;                // 현재 등록된 객체 수
    Target targets[kMaxTargets];              // 객체 정보 목록
}; // 전체 크기: 460바이트


// ============================================================
// 프레임 헤더
// ============================================================

struct FrameHeader
{
    std::uint32_t magic;                      // 프레임 식별값, kSharedMemoryMagic과 일치해야 함
    std::uint32_t version;                    // 구조체 버전, 현재 3
    std::uint32_t frameSize;                  // 헤더 포함 전체 프레임 크기(바이트)
    std::uint32_t sequence;                   // 새 프레임마다 증가하는 순번

    std::uint64_t timestampMs;                // Unix epoch 기준 프레임 생성 시각(밀리초)

    std::uint32_t payloadSize;                // 페이로드 크기(바이트)
    std::uint32_t checksum;                   // 페이로드 무결성 확인용 FNV-1a 32비트 체크섬
}; // 전체 크기: 32바이트


// ============================================================
// 전체 프레임
// ============================================================

struct Frame
{
    FrameHeader header;                       // 프레임 식별·크기·순번·시각·체크섬 정보
    Payload payload;                          // RDR/FCS 자산 상태와 객체 정보
}; // 전체 크기: 484바이트

// 이전에 설정한 구조체 정렬을 복원
#pragma pack(pop)


// ============================================================
// 컴파일 시 구조체 크기 검증
// ============================================================

static_assert(sizeof(RDRAsset) == 40, "RDRAsset 크기 오류");
static_assert(sizeof(FCSAsset) == 40, "FCSAsset 크기 오류");
static_assert(sizeof(Target) == 36, "Target 크기 오류");
static_assert(sizeof(Payload) == 460, "Payload 크기 오류");
static_assert(sizeof(FrameHeader) == 32, "FrameHeader 크기 오류");
static_assert(sizeof(Frame) == 492, "Frame 크기 오류");
static_assert(sizeof(Frame) % 4 == 0, "Frame 크기는 4바이트의 배수여야 함");
