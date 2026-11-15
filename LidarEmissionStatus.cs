namespace AadcCommandCenter;

public static class LidarEmissionStatus
{
    public static string Text(bool operating, bool? emissionOn) =>
        (operating ? "구동 중" : "구동 정지") + " / " +
        (emissionOn is null ? "방출 상태 미확인" : emissionOn.Value ? "방출 ON 확인" : "방출 OFF 확인");
}
