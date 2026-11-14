# Main map range display

XT-M60 output FOV1 defaults to horizontal 120 degrees and vertical 45 degrees. Both values can be changed in equipment settings and persisted by ECS.
Source: Confluence KAN-29 section 3:
https://ligproject2team.atlassian.net/wiki/spaces/MFS/pages/14057478/KAN-29+XT-M6+0+LiDAR

The 130 x 52 degree illumination FOV2 is not used as a detection field.
The main map is an XY plan-view schematic of horizontal FOV; vertical FOV and
reported elevation are labelled, not projected as a 3D ground intersection.
Existing shared-memory pan convention (0..270 -> -135..135 degrees) is retained.

Camera settings read from ECS determine the RDR inner red disk, outer cyan ring
and FCS capture circle with yellow shading inside. Settings are not
applied to target assignment, device commands or output control.
The green sector uses the saved horizontal FOV and reported RDR direction. The vertical FOV is labelled. The maximum shoot distance is editable in equipment settings; yellow indicates the inside of that circle. Color explanation legends are omitted. It does not animate a fictitious scan.
The map extent includes both device ranges as well as displayed objects.


## ECS pairing and startup

Deploy with the ECS camera-settings/startup change. IPC v3 sizes remain unchanged;
commands 8/9 read/write camera settings, command 18 queries GUI RDR startup,
and results 15–23 describe interlock/startup failures. Startup acceptance does not
mean completion: the button remains pending until ECS confirms FCS OFF, RDR
preparation and ED07 resume, or returns a failure.

Validation (Windows, .NET 10):

```
dotnet build AadcCommandCenter.csproj -c Debug
dotnet run --project tests/ObservationRetention.csproj -c Debug
dotnet run --project tests/camera-settings/CameraSettings.csproj -c Debug
```

The camera test checks validation, failed-save rollback, diagnostics and actual
WPF map rendering. Evidence is written under `artifacts/camera-settings/`.
