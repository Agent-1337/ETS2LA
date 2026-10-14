using System.Numerics;
using ETS2LA.Game.Perception;

namespace ETS2LA.State.Autonomy;

/// <summary>Requires independent observations and remembers confirmed obstructions through brief occlusion.</summary>
public sealed class ObstacleTracker
{
    private sealed class Track(SceneObstacle obstacle, long now, string id)
    {
        public SceneObstacle Obstacle = obstacle with { Id = id };
        public long First = now, Last = now;
        public int Hits = 1;
        public float LargestResidual;
    }
    private readonly List<Track> tracks = [];
    private int sequence;
    public void Update(IReadOnlyList<SceneObstacle> observations, long observedAt)
    {
        var used = new HashSet<Track>();
        foreach (var observation in observations)
        {
            if (!float.IsFinite(observation.Position.X + observation.Position.Y + observation.Position.Z + observation.Confidence) || observation.Confidence < 0.5f) continue;
            var existing = tracks.Where(t => !used.Contains(t) && t.Obstacle.Kind == observation.Kind && Vector3.Distance(t.Obstacle.Position, observation.Position) < 2.5f)
                .OrderBy(t => Vector3.Distance(t.Obstacle.Position, observation.Position)).FirstOrDefault();
            if (existing == null) { var track = new Track(observation, observedAt, $"vision:{++sequence}"); tracks.Add(track); used.Add(track); }
            else
            {
                if (observedAt <= existing.Last) continue;
                if (observedAt - existing.Last > 1400) { existing.Hits = 0; existing.First = observedAt; existing.LargestResidual = 0; }
                existing.LargestResidual = Math.Max(existing.LargestResidual, Vector3.Distance(existing.Obstacle.Position, observation.Position));
                existing.Obstacle = observation with { Id = existing.Obstacle.Id, Position = Vector3.Lerp(existing.Obstacle.Position, observation.Position, 0.45f) };
                existing.Last = observedAt; existing.Hits++; used.Add(existing);
            }
        }
        tracks.RemoveAll(t => observedAt - t.Last > 5000);
    }
    public IReadOnlyList<SceneObstacle> Confirmed(long now) => tracks.Where(t => t.Hits >= 3 && t.Last - t.First >= 650 && t.LargestResidual <= 2 && now - t.Last <= 3000).Select(t => t.Obstacle).ToArray();
    public void Clear() => tracks.Clear();
}
