using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// The starting slots on a circuit: where a car is put, and which way it faces.
    ///
    /// <para><b>Baked now because it is cheap now and expensive later.</b> Nothing races yet — there are
    /// no opponents and no start procedure. What there is, is a grid painted on the road, and the twelve
    /// places that paint marks are worth having as data rather than as a picture: it is what the player
    /// is put on so a lap starts where a lap should start, and it is what a field of cars would be put
    /// on the day there is one. Deriving it again then, from a mesh, would be guesswork — which is the
    /// argument <c>RoadCourse</c> already makes for carrying its features rather than re-reading them
    /// off finished geometry.</para>
    ///
    /// <para><b>It comes from the same table the paint does</b> — <c>CircuitMeshes.GridSlot</c> — and
    /// that is the whole point of it being a table. Two copies of the arithmetic would be twelve cars
    /// parked beside their boxes rather than on them: obvious in a picture, and impossible to attribute,
    /// because each half looks right on its own.</para>
    ///
    /// <para>Positions are already lifted to the car's ride height, for the reason the spawn table
    /// records: a spawn point is a fixed place in a baked scene and any of the ten bodies may arrive at
    /// it, so it has to clear the one that rides highest.</para>
    /// </summary>
    public sealed class StartingGrid : MonoBehaviour
    {
        [SerializeField] private string circuitName = string.Empty;

        [Tooltip("World positions, pole first, already at ride height.")]
        [SerializeField] private Vector3[] positions = new Vector3[0];

        [Tooltip("Heading of each slot in degrees about Y, matching its position.")]
        [SerializeField] private float[] headings = new float[0];

        /// <summary>Which circuit these belong to.</summary>
        public string CircuitName => circuitName;

        /// <summary>How many slots there are. Pole is 0.</summary>
        public int SlotCount => positions != null ? positions.Length : 0;

        /// <summary>
        /// One slot's pose.
        /// </summary>
        /// <returns>False if the index is outside the grid, leaving the outputs at identity.</returns>
        public bool TryGetSlot(int slot, out Vector3 position, out Quaternion rotation)
        {
            if (positions == null || slot < 0 || slot >= positions.Length)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }

            position = positions[slot];
            rotation = Quaternion.Euler(0f, headings[slot], 0f);
            return true;
        }

        /// <summary>Baked by the rebuild. The two arrays must be the same length.</summary>
        public void SetGrid(string name, Vector3[] slotPositions, float[] slotHeadings)
        {
            circuitName = name;
            positions = slotPositions ?? new Vector3[0];
            headings = slotHeadings ?? new float[0];

            if (positions.Length != headings.Length)
            {
                Debug.LogError(
                    $"[Horizon] The {name} grid was given {positions.Length} positions and "
                    + $"{headings.Length} headings. They are one table in two arrays and have to stay "
                    + "the same length, or a slot faces the way a different slot does.");

                positions = new Vector3[0];
                headings = new float[0];
            }
        }
    }
}
