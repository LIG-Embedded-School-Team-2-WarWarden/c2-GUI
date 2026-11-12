ECS 통제소 모의 프로그램

파일 구성
- ControlStationMock.cpp : 공유 메모리 송신 및 네임드 파이프 서버
- SharedMemoryData.h : 공유 메모리 구조체 정의. 모든 구조체 멤버에 한글 주석 포함
- NamedPipeProtocol.h : 네임드 파이프 프로토콜 및 구조체 정의. 모든 멤버에 한글 주석 포함

빌드
Visual Studio Developer Command Prompt에서 이 폴더로 이동한 뒤 실행:
cl /utf-8 /std:c++17 /EHsc ControlStationMock.cpp /Fe:ControlStationMock.exe

실행
ControlStationMock.exe

순차 할당 대기 확인
- GUI에서 객체 2개를 순차 처리하면 첫 객체의 레이저 표시가 5초 뒤 꺼지고 할당이 취소됩니다.
- 모의 FCS는 레이저 표시가 꺼진 뒤 2초 동안 작업 중으로 응답합니다. GUI는 이때 FCS 작업 종료 대기를 표시하고, 해제 확인 후 두 번째 객체를 할당합니다.
- GetFcsBusy(7)의 성공 응답은 fireState=1(작업 중) 또는 0(작업 종료)을 반환합니다. 기존 GetStatus 응답과 IPC 구조체 크기는 그대로입니다.

콘솔 명령어
- help
- status
- connected on
- connected off
- available on
- available off
- reset
- quit

고각 슬라이더
- WPF의 라이다 고각 슬라이더(0~90도)는 Named Pipe의 SetRdrElevation 명령으로 모의 ECS에 전송
- 모의 ECS는 요청을 수신한 뒤 다음 SharedMemory 프레임의 RDRAsset.elevationDeg에 반영
- 고각 설정 요청에는 RDR ID와 targetId 필드에 0.1도 단위의 정수 값을 사용하며, 파이프 구조체 크기는 바뀌지 않음
- RDR 고각은 0.1도 단위 목표로 설정되며, 현재값은 최대 초당 25도씩 이동해 공유 메모리에 송신
- 객체 할당 후 FCS는 FCS 좌표에서 객체 위치까지의 방위각·고각(각 최대 초당 25도)에 맞춰 이동하고, FCS 좌표에서 공유 메모리에 기록된 객체 위치까지의 목표 방위각과 고각이 맞으면 할당 완료 응답
- 공유 메모리는 100ms마다 갱신하며 모의 RDR 방위각은 100ms마다 2도(초당 20도)씩 0~270도 왕복

공유 메모리
- 이름: Local\ControlStationMock.SharedMemory.v2
- 뮤텍스: Local\ControlStationMock.SharedMemory.Mutex.v2
- 프레임 크기: 492바이트 (프로토콜 버전 3)
- RDRAsset: 40바이트 (각도 float, 위치 X/Y/Z 및 전체 시야각, 위치 단위 m)
- FCSAsset: 40바이트 (각도 float, 위치 X/Y/Z, 위치 단위 m)
- 객체 1001~1010의 데이터는 시작 시 생성하며 고정
- sequence, timestampMs, checksum은 프레임마다 갱신

네임드 파이프
- 경로: \\.\pipe\ECS_NamedPipe
- 프로토콜 버전: 3
- PipeCommand 크기: 40바이트
- PipeResponse 크기: 40바이트

참고
- WPF 구조체도 Pack=4 및 동일한 필드 순서를 사용해야 함
- Windows와 MSVC 컴파일러가 필요
- 모의 교전 명령은 UI/IPC 확인용이며 실제 장비를 제어하지 않음


[한글 콘솔 출력 수정]
Windows 콘솔의 유니코드 입출력을 위해 _setmode를 적용했습니다.
빌드: cl /utf-8 /std:c++17 /EHsc ControlStationMock.cpp /Fe:ControlStationMock.exe


[격추 및 레이저 상태 반영]
- Fire 명령이 수락되면 할당된 객체의 상태를 kTargetDead(격추)로 변경
- 격추된 객체 상태는 이후 공유 메모리 프레임에서도 유지
- FCSAsset.laserOn을 1로 설정하고 5초 경과 후 0으로 자동 전환
- reset 명령을 실행하면 레이저와 격추 객체 기록을 초기화

WPF 자산 좌표 편집
- RDR/FCS의 X/Y/Z 좌표 변경은 SetAssetPosition 파이프 명령으로 모의 ECS에 전달
- 모의 ECS는 변경된 좌표를 보존해 다음 공유 메모리 프레임에 전송
- RDR/FCS와 객체 방위각·고각은 float로 송신되며 화면에는 소수점 한 자리까지 표시; FCS 방위각은 0~360도 전체 회전 지원

RDR 작동 상태
- 모의 ECS 시작 시 RDR은 비활성 상태로 시작
- WPF RDR 패널의 라이다 구동/라이다 정지 버튼을 통해 Named Pipe로 상태 변경
- 정지 상태에서는 RDR 방위각 주사만 멈추며 공유 메모리와 객체 데이터 송신은 계속
