using System.IO;
using System.Windows;

namespace AadcCommandCenter;

internal enum ContinuousPhotoPhase : uint
{
    Idle,
    Running,
    Completed,
    Stopped,
    Failed
}

public partial class MainWindow
{
    private static readonly string[] ContinuousPhotoDirections =
        ["위", "오른쪽 위", "오른쪽", "오른쪽 아래", "아래", "왼쪽 아래", "왼쪽", "왼쪽 위"];
    private readonly CancellationTokenSource _continuousPhotoLifetime = new();
    private TaskCompletionSource<bool>? _continuousPhotoStopSignal;
    private bool _continuousPhotoStopping;

    private bool ContinuousPhotosActive => _continuousPhotoStopSignal != null;

    private void InitializeContinuousPhotos()
    {
        _data.Assignments.CollectionChanged += (_, _) => RefreshContinuousPhotoControls();
        Closed += (_, _) => _continuousPhotoLifetime.Cancel();
        RefreshContinuousPhotoControls();
    }

    private bool CanStartContinuousPhotos =>
        _isSharedMemoryLive && _isCommandPipeLive && _supportsBatchAssignments &&
        RdrSelector.SelectedItem is RdrAsset { IsConnected: true } &&
        _data.Assignments.Count > 0 &&
        _data.Assignments.All(row => row.HasAssignment && row.IsBatch && row.IsScanPaused &&
            !row.IsStageCommandPending);

    private void RefreshContinuousPhotoControls()
    {
        bool active = ContinuousPhotosActive;
        foreach (var row in _data.Assignments)
            row.ContinuousPhotosActive = active;

        ContinuousPhotoButton.Content = active
            ? _continuousPhotoStopping ? "중지 확인 중" : "연속 촬영 중지"
            : "전체 연속 촬영";
        ContinuousPhotoButton.IsEnabled = active ? !_continuousPhotoStopping : CanStartContinuousPhotos;
        if (active)
        {
            PauseAssignmentScanButton.IsEnabled = false;
            ResumeAssignmentScanButton.IsEnabled = false;
        }
    }

    private readonly record struct ContinuousPhotoSnapshot(ContinuousPhotoPhase Phase, int Total,
        int Confirmed, ResultCode Result);

    private static bool TryReadContinuousPhotoResponse(PipeResponse response, CommandType command,
        int previousConfirmed, int? expectedTotal, out ContinuousPhotoSnapshot snapshot, out string error)
    {
        snapshot = default;
        error = "ECS 연속 촬영 응답의 회차, 장수 또는 상태가 일치하지 않습니다.";
        if (response.CommandType != (uint)command || response.FcsId != 1 ||
            response.AssignmentState > (uint)ContinuousPhotoPhase.Failed ||
            response.AssignedTargetId > int.MaxValue || response.FireState > int.MaxValue)
            return false;

        var phase = (ContinuousPhotoPhase)response.AssignmentState;
        int total = (int)response.AssignedTargetId;
        int confirmed = (int)response.FireState;
        var result = (ResultCode)response.ResultCode;
        if (total == 0)
        {
            if (phase != ContinuousPhotoPhase.Idle || confirmed != 0) return false;
        }
        else
        {
            if (confirmed < previousConfirmed || confirmed > total) return false;
            if (expectedTotal is int expected && total != expected) return false;
            if (phase == ContinuousPhotoPhase.Idle) return false;
            if (phase == ContinuousPhotoPhase.Completed && confirmed != total) return false;
            if (phase is ContinuousPhotoPhase.Running or ContinuousPhotoPhase.Completed or ContinuousPhotoPhase.Stopped &&
                result is not (ResultCode.Success or ResultCode.StatusPending))
                return false;
            if (phase == ContinuousPhotoPhase.Running && response.IsConnected == 0) return false;
        }
        snapshot = new(phase, total, confirmed, result);
        error = "";
        return true;
    }

    private async Task<PipeResponse> SendContinuousPhotoAsync(CommandType command)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_continuousPhotoLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        return await _pipeClient.SendAsync(command, targetId: 0, rdrId: 1, fcsId: 1,
            cancellationToken: timeout.Token);
    }

    private string ContinuousPhotoProgress(string[] targets, int confirmed, int total)
    {
        if (confirmed <= 0) return $"FCS 촬영 확인 대기 · 0/{total}장";
        int last = confirmed - 1;
        int position = last / targets.Length;
        string location;
        if (position == 0) location = "중심";
        else
        {
            int ring = (position - 1) / ContinuousPhotoDirections.Length + 1;
            string direction = ContinuousPhotoDirections[(position - 1) % ContinuousPhotoDirections.Length];
            int positions = total / targets.Length;
            int rings = Math.Max(1, (positions - 1) / ContinuousPhotoDirections.Length);
            float radiusCm = _cameraSettings is { Length: > 3 } settings
                ? settings[3][0] * 100f * ring / rings : 5f * ring / rings;
            location = $"{ring}단계 {direction} · {radiusCm:0.##}cm";
        }
        return $"{targets[last % targets.Length]} · {location} · FCS 확인 {confirmed}/{total}장";
    }

    private async void ContinuousPhotoClick(object sender, RoutedEventArgs e)
    {
        try { await ContinuousPhotoClickAsync(); }
        catch (Exception error)
        {
            // WPF async-void event handlers must never surface an exception to the dispatcher.
            // If ownership is uncertain, leave every conflicting control locked until shutdown.
            if (!_continuousPhotoLifetime.IsCancellationRequested)
            {
                try
                {
                    if (ContinuousPhotosActive)
                    {
                        ContinuousPhotoStatus.Visibility = Visibility.Visible;
                        ContinuousPhotoStatus.Text = "촬영 상태 처리 오류 · 안전 잠금 유지";
                    }
                    AddLog($"전체 연속 촬영 처리 오류 · {error.Message}");
                }
                catch { }
            }
        }
    }

    private async Task ContinuousPhotoClickAsync()
    {
        if (_continuousPhotoStopSignal != null)
        {
            _continuousPhotoStopping = true;
            _continuousPhotoStopSignal.TrySetResult(true);
            RefreshContinuousPhotoControls();
            return;
        }
        if (!CanStartContinuousPhotos) return;

        string[] targets = _data.Assignments
            .Where(row => row.HasAssignment && row.IsBatch)
            .Select(row => row.TargetName).ToArray();
        if (targets.Length == 0) return;

        var stop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _continuousPhotoStopSignal = stop;
        _continuousPhotoStopping = false;
        ContinuousPhotoStatus.Visibility = Visibility.Visible;
        ContinuousPhotoStatus.Text = "ECS 연속 촬영 시작 확인 중";
        RefreshAssignmentScanControls();

        int confirmed = 0, total = 0;
        bool authoritativeTerminal = false;
        PipeResponse? response = null;
        CommandType responseCommand = CommandType.StartContinuousPhotos;
        string? lastReconcileError = null;
        try
        {
            try { response = await SendContinuousPhotoAsync(responseCommand); }
            catch (Exception error)
            {
                if (!_continuousPhotoLifetime.IsCancellationRequested)
                {
                    lastReconcileError = error.Message;
                    ContinuousPhotoStatus.Text = "시작 응답 미확인 · ECS 상태 재확인 중";
                    AddLog($"전체 연속 촬영 시작 응답 미확인 · {error.Message}");
                }
            }

            while (!_continuousPhotoLifetime.IsCancellationRequested && !authoritativeTerminal)
            {
                try
                {
                    if (response is PipeResponse current)
                    {
                        if (TryReadContinuousPhotoResponse(current, responseCommand, confirmed,
                            total > 0 ? total : null, out var state, out var responseError))
                        {
                            if (state.Total == 0)
                            {
                                string reason = CommandResultText(state.Result);
                                ContinuousPhotoStatus.Text = responseCommand == CommandType.StartContinuousPhotos &&
                                    state.Result != ResultCode.Success
                                    ? $"연속 촬영 시작 거부 · {reason}"
                                    : _continuousPhotoStopping
                                        ? $"중지 · 마지막 확인 {confirmed}/{Math.Max(total, confirmed)}장"
                                        : $"ECS 촬영 세션 종료 · 마지막 확인 {confirmed}/{Math.Max(total, confirmed)}장";
                                AddLog(ContinuousPhotoStatus.Text);
                                authoritativeTerminal = true;
                            }
                            else
                            {
                                total = state.Total;
                                confirmed = state.Confirmed;
                                switch (state.Phase)
                                {
                                    case ContinuousPhotoPhase.Completed:
                                        ContinuousPhotoStatus.Text =
                                            $"완료 · {targets.Length}개 타겟 · {confirmed}/{total}장 · FCS 출력 확인";
                                        AddLog($"전체 연속 촬영 완료 · 타겟 {targets.Length}개 · {confirmed}/{total}장");
                                        authoritativeTerminal = true;
                                        break;
                                    case ContinuousPhotoPhase.Stopped:
                                        ContinuousPhotoStatus.Text = $"중지 · FCS 확인 {confirmed}/{total}장";
                                        AddLog($"전체 연속 촬영 중지 · {confirmed}/{total}장");
                                        authoritativeTerminal = true;
                                        break;
                                    case ContinuousPhotoPhase.Failed:
                                        string reason = CommandResultText(state.Result);
                                        ContinuousPhotoStatus.Text =
                                            $"촬영 중단 · 마지막 확인 {confirmed}/{total}장 · {reason}";
                                        AddLog($"전체 연속 촬영 실패 · {confirmed}/{total}장 · {reason}");
                                        authoritativeTerminal = true;
                                        break;
                                    case ContinuousPhotoPhase.Running:
                                        ContinuousPhotoStatus.Text =
                                            ContinuousPhotoProgress(targets, confirmed, total);
                                        break;
                                }
                            }
                            lastReconcileError = null;
                        }
                        else if (lastReconcileError != responseError)
                        {
                            lastReconcileError = responseError;
                            ContinuousPhotoStatus.Text =
                                $"촬영 상태 응답 불일치 · 마지막 확인 {confirmed}/{Math.Max(total, confirmed)}장 · 재확인 중";
                            AddLog($"전체 연속 촬영 상태 재확인 · {responseError}");
                        }
                    }
                    if (authoritativeTerminal) break;

                    if (stop.Task.IsCompleted)
                        await Task.Delay(300, _continuousPhotoLifetime.Token);
                    else
                        await Task.WhenAny(stop.Task,
                            Task.Delay(300, _continuousPhotoLifetime.Token));
                    _continuousPhotoLifetime.Token.ThrowIfCancellationRequested();

                    responseCommand = stop.Task.IsCompleted
                        ? CommandType.StopContinuousPhotos
                        : CommandType.GetContinuousPhotoStatus;
                    response = null;
                    try { response = await SendContinuousPhotoAsync(responseCommand); }
                    catch (Exception error)
                    {
                        if (_continuousPhotoLifetime.IsCancellationRequested) break;
                        if (lastReconcileError != error.Message)
                        {
                            lastReconcileError = error.Message;
                            ContinuousPhotoStatus.Text =
                                $"ECS 응답 재연결 중 · 마지막 확인 {confirmed}/{Math.Max(total, confirmed)}장";
                            AddLog($"전체 연속 촬영 응답 재확인 · {error.Message}");
                        }
                    }
                }
                catch (Exception error)
                {
                    if (_continuousPhotoLifetime.IsCancellationRequested) break;
                    response = null;
                    if (lastReconcileError != error.Message)
                    {
                        lastReconcileError = error.Message;
                        ContinuousPhotoStatus.Text =
                            $"촬영 상태 재확인 중 · 마지막 확인 {confirmed}/{Math.Max(total, confirmed)}장";
                        AddLog($"전체 연속 촬영 상태 처리 재시도 · {error.Message}");
                    }
                    try { await Task.Delay(300, _continuousPhotoLifetime.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        catch (Exception error)
        {
            if (!_continuousPhotoLifetime.IsCancellationRequested)
            {
                // Keep ownership locked. A detached reconciliation loop would race with this
                // handler, so remain here until an authoritative status or app shutdown.
                while (!_continuousPhotoLifetime.IsCancellationRequested && !authoritativeTerminal)
                {
                    ContinuousPhotoStatus.Text =
                        $"촬영 상태 복구 중 · 마지막 확인 {confirmed}/{Math.Max(total, confirmed)}장";
                    try
                    {
                        responseCommand = stop.Task.IsCompleted
                            ? CommandType.StopContinuousPhotos : CommandType.GetContinuousPhotoStatus;
                        var retry = await SendContinuousPhotoAsync(responseCommand);
                        if (TryReadContinuousPhotoResponse(retry, responseCommand, confirmed,
                            total > 0 ? total : null, out var state, out _) &&
                            state.Phase is ContinuousPhotoPhase.Idle or ContinuousPhotoPhase.Completed or
                                ContinuousPhotoPhase.Stopped or ContinuousPhotoPhase.Failed)
                        {
                            response = retry;
                            authoritativeTerminal = true;
                            confirmed = state.Confirmed;
                            total = state.Total;
                            ContinuousPhotoStatus.Text = state.Phase switch
                            {
                                ContinuousPhotoPhase.Completed => $"완료 · {confirmed}/{total}장 · FCS 출력 확인",
                                ContinuousPhotoPhase.Stopped => $"중지 · FCS 확인 {confirmed}/{total}장",
                                ContinuousPhotoPhase.Failed => $"촬영 중단 · 마지막 확인 {confirmed}/{total}장 · {CommandResultText(state.Result)}",
                                _ => $"ECS 촬영 세션 종료 · 마지막 확인 {confirmed}/{Math.Max(total, confirmed)}장"
                            };
                            break;
                        }
                    }
                    catch (Exception)
                    {
                        if (_continuousPhotoLifetime.IsCancellationRequested) break;
                    }
                    try { await Task.Delay(500, _continuousPhotoLifetime.Token); }
                    catch (OperationCanceledException) { break; }
                }
                AddLog($"전체 연속 촬영 상태 복구 · {error.Message}");
            }
        }
        finally
        {
            if (authoritativeTerminal || _continuousPhotoLifetime.IsCancellationRequested)
            {
                _continuousPhotoStopSignal = null;
                _continuousPhotoStopping = false;
                if (!_continuousPhotoLifetime.IsCancellationRequested)
                    RefreshAssignmentScanControls();
            }
        }
    }
}
