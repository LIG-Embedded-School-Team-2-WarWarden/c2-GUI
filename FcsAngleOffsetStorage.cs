using System.IO;
using System.Text.Json;

namespace AadcCommandCenter;

internal sealed record FcsAngleOffsetFile(int Version, float Pan, float Tilt);

internal static class FcsAngleOffsetStorage
{
    internal static float[] Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [0f, 0f, 0f];
            var saved = JsonSerializer.Deserialize<FcsAngleOffsetFile>(File.ReadAllText(path));
            if (saved is null || saved.Version != 1 || !IsValid(saved.Pan, saved.Tilt))
                return [0f, 0f, 0f];
            return [saved.Pan, saved.Tilt, 0f];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [0f, 0f, 0f];
        }
    }

    internal static void Save(string path, float pan, float tilt)
    {
        if (!IsValid(pan, tilt)) throw new ArgumentOutOfRangeException(nameof(pan));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new FcsAngleOffsetFile(1, pan, tilt)));
        File.Move(temporary, path, true);
    }

    internal static bool IsValid(float pan, float tilt) =>
        float.IsFinite(pan) && Math.Abs(pan) <= 180 &&
        float.IsFinite(tilt) && Math.Abs(tilt) <= 90;
}
