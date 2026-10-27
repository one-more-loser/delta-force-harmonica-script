namespace Harmonica.Core;
public static class ScoreWriter
{
    public static string Serialize(Score score) => System.Text.Json.JsonSerializer.Serialize(new {
        format = "gtiartist-score-v1", title = score.Title, tracks = score.Tracks,
        notes = score.Notes.Select(n => new { pitch = n.Pitch, start = n.StartMs / 1000, duration = n.DurationMs / 1000, track = n.Track, velocity = n.Velocity }),
        playback_settings = new { track = score.Settings.Track, transpose = score.Settings.Transpose, speed = score.Settings.Speed, gate = score.Settings.Gate, fold_octaves = score.Settings.Fold }
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
