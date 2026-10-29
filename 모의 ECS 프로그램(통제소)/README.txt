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

콘솔 명령어
- help
- status
- connected on
- connected off
- available on
- available off
- reset
- quit

공유 메모리
- 이름: Local\ControlStationMock.SharedMemory.v1
- 뮤텍스: Local\ControlStationMock.SharedMemory.Mutex.v1
- 프레임 크기: 464바이트
- 표적 1001~1010의 데이터는 시작 시 생성하며 고정
- sequence, timestampMs, checksum은 프레임마다 갱신

네임드 파이프
- 경로: \\.\pipe\ECS_NamedPipe
- 프로토콜 버전: 2
- PipeCommand 크기: 28바이트
- PipeResponse 크기: 40바이트

참고
- WPF 구조체도 Pack=4 및 동일한 필드 순서를 사용해야 함
- Windows와 MSVC 컴파일러가 필요
- 모의 교전 명령은 UI/IPC 확인용이며 실제 장비를 제어하지 않음


[한글 콘솔 출력 수정]
Windows 콘솔의 유니코드 입출력을 위해 _setmode를 적용했습니다.
빌드: cl /utf-8 /std:c++17 /EHsc ControlStationMock.cpp /Fe:ControlStationMock.exe


[격추 및 레이저 상태 반영]
- Fire 명령이 수락되면 할당된 표적의 상태를 kTargetDead(격추)로 변경
- 격추된 표적 상태는 이후 공유 메모리 프레임에서도 유지
- FCSAsset.laserOn을 1로 설정하고 5초 경과 후 0으로 자동 전환
- reset 명령을 실행하면 레이저와 격추 표적 기록을 초기화
