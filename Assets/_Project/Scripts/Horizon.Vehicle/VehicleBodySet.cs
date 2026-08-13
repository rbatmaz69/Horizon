using UnityEngine;

namespace Horizon.Vehicle
{
    /// <summary>
    /// The five bodies the player may drive, and the paints they may wear.
    ///
    /// <para><b>One vehicle with five bodies parented under it, not five vehicles.</b> The prefab is
    /// barely a mesh: it is a Rigidbody, four wheel anchors, four wheel pivots, four audio sources, a
    /// reverb filter, <see cref="EngineAudio"/>, <see cref="VehicleCover"/> and
    /// <see cref="VehicleLights"/>. Five copies of that would be five places for a wiring mistake and
    /// five things to keep in step. It would also mean the car did not exist until something
    /// instantiated it, and both <c>GameBootstrap.WireUpWorld</c> and <c>PauseMenu</c> find the vehicle
    /// by type on the frame the world loads — so the camera binding and the spawn capture would become
    /// a race. One instance, placed in the world scene as it always has been, keeps all of that
    /// untouched.</para>
    ///
    /// <para>What differs per body is only ever four things: the mesh, the collider box, the lamps, and
    /// the handling asset. Wheels, track, wheelbase and ride height are shared by construction — see
    /// the note on <c>CarMeshBuilder.CarProfile</c> for why every silhouette is drawn around the same
    /// running gear.</para>
    /// </summary>
    public sealed class VehicleBodySet : MonoBehaviour
    {
        /// <summary>One selectable car: its shape, its lamps, its collider and how it drives.</summary>
        [System.Serializable]
        public struct Body
        {
            /// <summary>What the menu calls it. Comes from the profile, so the two cannot disagree.</summary>
            public string Name;

            /// <summary>Holds the mesh, the beams and the exhaust emitters. Exactly one is active.</summary>
            public GameObject Root;

            /// <summary>
            /// Five material slots in <c>CarMeshBuilder</c>'s constant order — body, glass, headlight,
            /// taillight, chrome. Uncompacted, which is what lets the lamps stay at 2 and 3.
            /// </summary>
            public MeshRenderer Renderer;

            public Light[] Headlights;

            public VehicleConfig Config;

            /// <summary>Baked from <c>CarMeshBuilder.HullBounds</c> at build time.</summary>
            public Vector3 ColliderCenter;

            public Vector3 ColliderSize;
        }

        [SerializeField] private Body[] bodies = new Body[0];

        /// <summary>
        /// The paints, as whole ready-made materials rather than colours.
        ///
        /// <para>This is the project's settled answer to runtime colour and it is worth restating,
        /// because the obvious alternatives are both wrong. A <see cref="MaterialPropertyBlock"/> —
        /// which <see cref="VehicleLights"/> does use, for one submesh on one object — makes the whole
        /// renderer SRP-Batcher-incompatible. Writing <c>_BaseColor</c> onto the shared material would
        /// work at runtime and <i>permanently edit the .mat asset</i> while playing in the editor, so a
        /// player trying colours would leave the repository dirty. Swapping a finished material into the
        /// slot costs nothing and lets each paint carry its own metallic and smoothness.
        /// <c>TownLights</c> makes the same argument at length.</para>
        /// </summary>
        [SerializeField] private Material[] paints = new Material[0];

        [SerializeField] private VehicleController controller;
        [SerializeField] private VehicleLights lights;
        [SerializeField] private BoxCollider hull;

        /// <summary>
        /// One material array per body, allocated once.
        ///
        /// <para>Cached because <c>Renderer.sharedMaterials</c> allocates a fresh array on every read,
        /// and repainting would otherwise produce garbage on a UI interaction. It is not a physics-step
        /// cost, but the habit is the one this project keeps.</para>
        /// </summary>
        private Material[][] slots;

        public int BodyCount => bodies != null ? bodies.Length : 0;

        public int PaintCount => paints != null ? paints.Length : 0;

        public int ActiveBody { get; private set; } = -1;

        public int ActivePaint { get; private set; } = -1;

        /// <summary>The menu label for a body, or an empty string if the index is not one.</summary>
        public string NameOf(int bodyIndex)
        {
            return bodyIndex >= 0 && bodyIndex < BodyCount ? bodies[bodyIndex].Name : string.Empty;
        }

        private void Awake()
        {
            slots = new Material[BodyCount][];

            for (int i = 0; i < BodyCount; i++)
            {
                MeshRenderer renderer = bodies[i].Renderer;
                slots[i] = renderer != null ? renderer.sharedMaterials : new Material[0];
            }
        }

        /// <summary>
        /// Puts the player in one of the bodies, in one of the paints.
        ///
        /// <para>Indices are clamped rather than rejected. They arrive from saved preferences, and a
        /// rebuild that removes a body would otherwise turn a returning player's launch into an
        /// exception — which is the one player who would never see it coming.</para>
        ///
        /// <para><b>Pause first.</b> This resizes a collider and changes the mass and centre of mass of a
        /// live Rigidbody; see <see cref="VehicleController.SetConfig"/>. Follow it with
        /// <see cref="VehicleController.Teleport"/> so a taller body does not begin the next physics step
        /// with its sills inside the road.</para>
        /// </summary>
        public void Select(int bodyIndex, int paintIndex)
        {
            if (BodyCount == 0)
            {
                return;
            }

            bodyIndex = Mathf.Clamp(bodyIndex, 0, BodyCount - 1);

            for (int i = 0; i < BodyCount; i++)
            {
                if (bodies[i].Root != null)
                {
                    bodies[i].Root.SetActive(i == bodyIndex);
                }
            }

            Body body = bodies[bodyIndex];
            ActiveBody = bodyIndex;

            if (hull != null)
            {
                hull.center = body.ColliderCenter;
                hull.size = body.ColliderSize;
            }

            if (controller != null)
            {
                controller.SetConfig(body.Config);
            }

            if (lights != null)
            {
                lights.SetBody(body.Renderer, body.Headlights);
            }

            SetPaint(paintIndex);
        }

        /// <summary>Repaints the active body. Cheap enough to call on every tap of a swatch.</summary>
        public void SetPaint(int paintIndex)
        {
            if (PaintCount == 0 || ActiveBody < 0)
            {
                return;
            }

            paintIndex = Mathf.Clamp(paintIndex, 0, PaintCount - 1);
            ActivePaint = paintIndex;

            // Awake has not run if this is called from the editor, and slots is what it builds.
            if (slots == null || slots.Length != BodyCount)
            {
                Awake();
            }

            Material[] materials = slots[ActiveBody];
            if (materials == null || materials.Length <= CarBodySubmesh)
            {
                return;
            }

            materials[CarBodySubmesh] = paints[paintIndex];

            MeshRenderer renderer = bodies[ActiveBody].Renderer;
            if (renderer != null)
            {
                renderer.sharedMaterials = materials;
            }
        }

        /// <summary>
        /// Which slot the paint goes in.
        ///
        /// <para>Duplicated from <c>CarMeshBuilder.BodySubmesh</c> rather than referenced, because that
        /// class is editor-only and this one has to run in a build. Both are zero and neither can move:
        /// the paint is the first submesh a body writes.</para>
        /// </summary>
        private const int CarBodySubmesh = 0;
    }
}
