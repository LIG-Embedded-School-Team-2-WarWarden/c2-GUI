# 관측 중단 표시

ECS GUI v3의 객체 status=4/valid=0은 관측 일시중지, status=5/valid=0은 재관측 대기다. 마지막 좌표를 회색으로 표시하며 동일 ID의 행과 선택을 유지한다. 할당·실행은 차단한다. 같은 ID의 실제 새 관측(status=0/valid=1)이 오면 정상 표시로 복원한다. 다른 ID는 새 객체이며 자동으로 치환하지 않는다.

대기 기간은 ECS가 관리한다(중단 최대 10초, 재개 후 최대 3초). GUI가 임의로 보존 기간을 연장하지 않는다. 순차 목록은 같은 ID의 재관측을 최대 13초 기다리며 실패 시 기존 오류 처리로 종료한다. 정상 소실·연결 변경의 목록 제거는 유지한다. 구형 ECS와의 기존 492byte 프레임 및 40byte 명령 구조는 바뀌지 않는다.

테스트: `dotnet run --project tests/ObservationRetention.csproj -c Release`. 실제 WPF ApplyFrame을 통해 행 유지, 명령 차단, 새 ID 구분, 일반 소실을 검증한다. `RETENTION_RENDER` 환경변수에 PNG 경로를 지정하면 대기 화면을 렌더링한다.

동일 ID의 재관측은 RDR 펌웨어 지원이 필요하다. 상세 요구사항은 [RDR-ECS ICD](https://ligproject2team.atlassian.net/wiki/spaces/MFS/pages/37355617/RDR-ECS+ICD)를 따른다.
