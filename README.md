# 대공방어 통제 시스템(GUI)

## 공유 메모리 프레임 수신

WPF는 C++ `Frame`을 named shared memory에서 읽습니다. `control_mock` 송신 프로그램과 동일하게 매핑 이름 `Local\ControlStationMock.SharedMemory.v1`, 뮤텍스 이름 `Local\ControlStationMock.SharedMemory.Mutex.v1`을 사용합니다. 다른 이름을 쓸 때는 `AADC9_SHARED_MEMORY_NAME`과 `AADC9_SHARED_MEMORY_MUTEX_NAME` 환경 변수를 지정합니다.

수신기는 50ms마다 송신 뮤텍스를 획득한 뒤 464바이트 프레임을 읽고 magic(`0x314D4354`), version(1), 구조체 크기, FNV-1a payload 체크섬, 자산 및 표적 개수를 확인합니다. C# 구조체는 `Pack=4`이며 공유 메모리는 읽기 전용으로 엽니다. 현재 용량은 RDR 1개, FCS 1개, 표적 10개입니다. 용량을 변경할 때는 C++과 C#의 상수 및 구조체 크기를 함께 갱신해야 합니다.

현재 프레임에는 발사 명령용 필드가 없으므로 표적 행의 버튼은 모의 동작만 합니다. 실제 명령 송신은 별도 프로토콜이 정의된 뒤 연결해야 합니다.

## 프로젝트 소개

라이다 센서를 활용한 공중 위협 표적 탐지와 통제소 기반 표적 정보 전시 및 할당 기능을 갖춘 WPF 기반 대공방어 통제 시스템임
탐지 자산과 통제소 및 타격 자산 간의 연동을 고려하여 대공방어 무기체계의 통제 GUI를 구현하는 것을 목표로 함

## 주요 기능

표적 탐지 정보 전시

표적 위치 및 탐지 정보 시각화

표적 정보 수신 및 관리

통제소 기반 표적 할당 화면

발사 명령을 위한 통제 인터페이스

발사대 상태 및 제어 정보 전시

## 개발 환경

개발 언어 C#(WPF)

화면 설계 XAML

개발 도구 Visual Studio

형상 관리 GitHub

실행 환경 Windows

## 시스템 구성

탐지 자산

라이다 센서를 활용한 공중 표적 탐지

통제소

탐지 정보 수신 및 전시

표적 정보 관리 및 할당

타격 자산에 대한 명령 인터페이스

타격 자산

모터 및 레이저를 이용한 타격 장치

발사대의 방위각 및 고각 제어 인터페이스

## GUI 개발

WPF와 XAML을 활용하여 대공방어 통제소의 사용자 인터페이스를 구현한다.

표적 정보와 시스템 상태를 확인할 수 있는 대시보드 형태로 화면을 구성하며 탐지 정보 전시와 표적 관리 기능을 중심으로 설계한다.

## 개발 목표

요구사항 분석을 통한 소프트웨어 설계

UML을 활용한 시스템 구조 설계

WPF 기반 통제 GUI 개발

네트워크 통신을 고려한 시스템 연동 구조 설계

임베디드 시스템과의 인터페이스 설계

요구사항 기반 시험 및 단위 시험

GitHub를 활용한 프로젝트 형상 관리

## 담당자

<table>
  <tr>
    <td align="center">
      <a href="https://github.com/Changhee-Cho">
        <img src="https://github.com/changhee-cho.png" width="100px;" alt="조창희 프로필"/><br />
        <sub><b>조창희 (Changhee-Cho)</b></sub>
      </a><br />
      <sub>Developer</sub>
    </td>
  </tr>
</table>
