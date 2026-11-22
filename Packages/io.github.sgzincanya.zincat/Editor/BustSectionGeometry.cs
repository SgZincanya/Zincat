using System.Collections.Generic;
using UnityEngine;

namespace Zincat.AvatarMeasurement
{
    internal readonly struct Section
    {
        public readonly Vector3[] Loop;
        public readonly float Length;
        public readonly bool IsEstimated;
        public bool IsValid => Loop != null;

        public Section(Vector3[] loop, float length, bool isEstimated)
        {
            Loop = loop;
            Length = length;
            IsEstimated = isEstimated;
        }
    }

    internal static class BustSectionGeometry
    {
        private sealed class Point
        {
            public readonly Vector2 Position;
            public readonly HashSet<int> Neighbors = new HashSet<int>();

            public Point(Vector2 position) { Position = position; }
        }

        private sealed class Contour
        {
            public readonly List<Vector2> Points = new List<Vector2>();
            public Vector2 Min = Vector2.one * float.PositiveInfinity;
            public Vector2 Max = Vector2.one * float.NegativeInfinity;
            public Vector2 Center => (Min + Max) * 0.5f;
            public Vector2 Size => Max - Min;

            public void Add(Vector2 position)
            {
                Points.Add(position);
                Min = Vector2.Min(Min, position);
                Max = Vector2.Max(Max, position);
            }
        }

        public static Section Measure(Vector3[] vertices, int[] triangles, Vector3 pivot, float tilt, float tolerance,
            float bodyHeight, bool includeChest)
        {
            var rotation = Quaternion.AngleAxis(tilt, Vector3.right);
            var inverse = Quaternion.Inverse(rotation);
            var positions = new Vector3[vertices.Length];
            for (int i = 0; i < positions.Length; i++) positions[i] = inverse * (vertices[i] - pivot);

            var points = new List<Point>();
            var buckets = new Dictionary<Vector2Int, List<int>>();
            var crossings = new Vector2[3];
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 a = positions[triangles[i]];
                Vector3 b = positions[triangles[i + 1]];
                Vector3 c = positions[triangles[i + 2]];
                int count = 0;
                CrossEdge(a, b, crossings, ref count);
                CrossEdge(b, c, crossings, ref count);
                CrossEdge(c, a, crossings, ref count);
                if (count != 2 || crossings[0].Equals(crossings[1])) continue;

                int start = FindOrAdd(crossings[0], tolerance, points, buckets);
                int end = FindOrAdd(crossings[1], tolerance, points, buckets);
                if (start == end) continue;
                points[start].Neighbors.Add(end);
                points[end].Neighbors.Add(start);
            }

            // Repair ordinary mesh seams first. A heavily deformed mesh may need a wider gap budget.
            CloseSmallGaps(points, tolerance * 20f);
            var contours = FindContours(points);
            var torso = FindTorso(contours, bodyHeight, tolerance);
            bool isEstimated = torso == null;
            if (isEstimated && bodyHeight * 0.02f > tolerance * 20f)
            {
                CloseSmallGaps(points, bodyHeight * 0.02f);
                contours = FindContours(points);
                torso = FindTorso(contours, bodyHeight, tolerance);
            }
            if (torso == null) return default;

            var outer = new List<Vector2>(torso.Points);
            if (includeChest)
            {
                foreach (var contour in contours)
                {
                    if (contour == torso || !IsChest(contour, torso)) continue;
                    outer.AddRange(contour.Points);
                }
            }

            var hull = ConvexHull(outer);
            if (hull.Count < 4) return default;
            float length = 0f;
            var loop = new Vector3[hull.Count];
            for (int i = 0; i < hull.Count; i++)
            {
                if (i > 0) length += Vector2.Distance(hull[i - 1], hull[i]);
                loop[i] = pivot + rotation * new Vector3(hull[i].x, 0f, hull[i].y);
            }
            return new Section(loop, length, isEstimated);
        }

        private static Contour FindTorso(List<Contour> contours, float bodyHeight, float tolerance)
        {
            Contour torso = null;
            float bestScore = float.NegativeInfinity;
            foreach (var contour in contours)
            {
                Vector2 size = contour.Size;
                // A torso loop must cross the centerline and have enough width and depth to enclose the trunk.
                if (contour.Min.x > tolerance || contour.Max.x < -tolerance) continue;
                if (size.x < bodyHeight * 0.07f || size.y < bodyHeight * 0.025f) continue;

                // Favor broad loops near the center, with rear support rather than forward-only islands.
                float depthGap = Mathf.Max(0f, contour.Min.y, -contour.Max.y);
                float score = size.x + size.y * 0.5f - Mathf.Abs(contour.Center.x) * 2f - depthGap * 4f;
                if (score <= bestScore) continue;
                bestScore = score;
                torso = contour;
            }
            return torso;
        }

        private static bool IsChest(Contour contour, Contour torso)
        {
            Vector2 offset = contour.Center - torso.Center;
            float sideGap = Mathf.Max(torso.Min.x - contour.Max.x, contour.Min.x - torso.Max.x);
            return Mathf.Abs(offset.x) <= torso.Size.x * 0.8f
                && sideGap <= torso.Size.x * 0.12f
                && contour.Max.y > torso.Max.y
                && offset.y > 0f
                && contour.Min.y <= torso.Max.y + Mathf.Max(torso.Size.y, torso.Size.x) * 0.5f;
        }

        private static void CrossEdge(Vector3 a, Vector3 b, Vector2[] crossings, ref int count)
        {
            if ((a.y < 0f) == (b.y < 0f)) return;
            Vector3 point = Vector3.LerpUnclamped(a, b, a.y / (a.y - b.y));
            crossings[count++] = new Vector2(point.x, point.z);
        }

        private static int FindOrAdd(Vector2 position, float tolerance, List<Point> points, Dictionary<Vector2Int, List<int>> buckets)
        {
            var cell = new Vector2Int(Mathf.FloorToInt(position.x / tolerance), Mathf.FloorToInt(position.y / tolerance));
            for (int y = -1; y <= 1; y++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    if (!buckets.TryGetValue(cell + new Vector2Int(x, y), out var nearby)) continue;
                    foreach (int index in nearby)
                        if ((points[index].Position - position).sqrMagnitude <= tolerance * tolerance) return index;
                }
            }

            if (!buckets.TryGetValue(cell, out var bucket))
            {
                bucket = new List<int>();
                buckets.Add(cell, bucket);
            }
            int next = points.Count;
            bucket.Add(next);
            points.Add(new Point(position));
            return next;
        }

        private static void CloseSmallGaps(List<Point> points, float maxGap)
        {
            var ends = new List<int>();
            for (int i = 0; i < points.Count; i++)
                if (points[i].Neighbors.Count == 1) ends.Add(i);

            var closest = new int[ends.Count];
            var distances = new float[ends.Count];
            float maxGapSquared = maxGap * maxGap;
            for (int i = 0; i < ends.Count; i++)
            {
                closest[i] = -1;
                distances[i] = maxGapSquared;
            }

            for (int i = 0; i < ends.Count; i++)
            {
                Point a = points[ends[i]];
                for (int j = i + 1; j < ends.Count; j++)
                {
                    if (a.Neighbors.Contains(ends[j])) continue;
                    float distance = (a.Position - points[ends[j]].Position).sqrMagnitude;
                    if (distance < distances[i])
                    {
                        closest[i] = j;
                        distances[i] = distance;
                    }
                    if (distance < distances[j])
                    {
                        closest[j] = i;
                        distances[j] = distance;
                    }
                }
            }

            // Join only mutually nearest ends. Any remaining open branches are trimmed below.
            for (int i = 0; i < ends.Count; i++)
            {
                int j = closest[i];
                if (j <= i || closest[j] != i) continue;
                points[ends[i]].Neighbors.Add(ends[j]);
                points[ends[j]].Neighbors.Add(ends[i]);
            }
        }

        private static List<Contour> FindContours(List<Point> points)
        {
            // Trim open ends so a surface seam or a stray open strip cannot form a tape loop.
            var queue = new Queue<int>();
            var degree = new int[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                degree[i] = points[i].Neighbors.Count;
                if (degree[i] == 1) queue.Enqueue(i);
            }
            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                degree[index] = 0;
                foreach (int neighbor in points[index].Neighbors)
                    if (degree[neighbor] > 0 && --degree[neighbor] == 1) queue.Enqueue(neighbor);
            }

            var contours = new List<Contour>();
            for (int i = 0; i < points.Count; i++)
            {
                if (degree[i] < 2) continue;
                var contour = new Contour();
                degree[i] = 0;
                queue.Enqueue(i);
                while (queue.Count > 0)
                {
                    int index = queue.Dequeue();
                    contour.Add(points[index].Position);
                    foreach (int neighbor in points[index].Neighbors)
                    {
                        if (degree[neighbor] < 2) continue;
                        degree[neighbor] = 0;
                        queue.Enqueue(neighbor);
                    }
                }
                if (contour.Points.Count >= 3) contours.Add(contour);
            }
            return contours;
        }

        private static List<Vector2> ConvexHull(List<Vector2> points)
        {
            points.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            var hull = new List<Vector2>();
            foreach (Vector2 point in points) AppendHullPoint(hull, point, 0);
            int lowerEnd = hull.Count - 1;
            for (int i = points.Count - 2; i >= 0; i--) AppendHullPoint(hull, points[i], lowerEnd);
            return hull;
        }

        private static void AppendHullPoint(List<Vector2> hull, Vector2 point, int start)
        {
            while (hull.Count > start + 1)
            {
                Vector2 a = hull[hull.Count - 1] - hull[hull.Count - 2];
                Vector2 b = point - hull[hull.Count - 1];
                if (a.x * b.y - a.y * b.x > 0f) break;
                hull.RemoveAt(hull.Count - 1);
            }
            hull.Add(point);
        }
    }
}
