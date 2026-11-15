namespace AadcCommandCenter;

public static class LidarEmissionStatus
{
    public static string ThermalText(uint? state, bool protectiveStop) =>
        protectiveStop ? "고온 오류로 보호 정지 · 자동 재개 안 함" : state switch {
            7 => "온도 경고 · 경고만으로 작업을 중단하지 않음",
            8 => "온도 상승으로 주파수 저하 · 감속 운용",
            9 => "고온 오류 · 보호 정지",
            null => "온도 상태 미확인",
            2 or 3 => "온도 경고 없음",
            _ => $"센서 상태 {state} · 온도 정상 여부 미확인"
        };

    public static string Text(bool operating, bool? emissionOn) =>
        (operating ? "구동 중" : "구동 정지") + " / " +
        (emissionOn is null ? "방출 상태 미확인" : emissionOn.Value ? "방출 ON 확인" : "방출 OFF 확인");
}
