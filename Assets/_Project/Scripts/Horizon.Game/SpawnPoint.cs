using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// One place the player may choose to start from.
    ///
    /// <para>A plain serialized record rather than a marker object in the scene. The positions come from
    /// the courses at build time — a summit is a distance along a road, a city gateway is a node in a
    /// layout table — so they belong in the same generated output as everything else, and a scene full
    /// of hand-placed transforms would be four more things to keep in step with a road that moves.</para>
    /// </summary>
    [System.Serializable]
    public struct SpawnPoint
    {
        /// <summary>What the button says.</summary>
        public string Name;

        public Vector3 Position;

        public Quaternion Rotation;

        public SpawnPoint(string name, Vector3 position, Quaternion rotation)
        {
            Name = name;
            Position = position;
            Rotation = rotation;
        }
    }
}
