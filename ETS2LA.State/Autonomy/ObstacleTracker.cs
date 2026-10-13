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
    }
    private readonly List<Track> tracks = [];
    private int sequence;
    public void Update(IReadOnlyList<SceneObstacle> observations, long observedAt)
    {
        var used = new HashSet<Track>();
        foreach (var observation in observations)
        {
            var existing = tracks.Where(t => !used.Contains(t) && t.Obstacle.Kind == observation.Kind && Vector3.Distance(t.Obstacle.Position, observation.Position) < 6)
                .OrderBy(t => Vector3.Distance(t.Obstacle.Position, observation.Position)).FirstOrDefault();
            if (existing == null) { var track = new Track(observation, observedAt, $"vision:{++sequence}"); tracks.Add(track); used.Add(track); }
            else
            {
                if (observedAt <= existing.Last) continue;
                existing.Obstacle = observation with { Id = existing.Obstacle.Id, Position = Vector3.Lerp(existing.Obstacle.Position, observation.Position, 0.45f) };
                existing.Last = observedAt; existing.Hits++; used.Add(existing);
            }
        }
        tracks.RemoveAll(t => observedAt - t.Last > 5000);
    }
    public IReadOnlyList<SceneObstacle> Confirmed(long now) => tracks.Where(t => t.Hits >= 3 && t.Last - t.First >= 350 && now - t.Last <= 5000).Select(t => t.Obstacle).ToArray();
    public void Clear() => tracks.Clear();
}
