using UnityEngine;

namespace Horizon.Vehicle
{
    /// <summary>
    /// The ten bodies the player may drive, and the paints they may wear.
    ///
    /// <para><b>One vehicle with ten bodies parented under it, not ten vehicles.</b> The prefab is
    /// barely a mesh: it is a Rigidbody, four wheel anchors, four wheel pivots, four audio sources, a
    /// reverb filter, <see cref="EngineAudio"/>, <see cref="VehicleCover"/> and
    /// <see cref="VehicleLights"/>. Ten copies of that would be ten places for a wiring mistake and
    /// ten things to keep in step. It would also mean the car did not exist until something
    /// instantiated it, and both <c>GameBootstrap.WireUpWorld</c> and <c>PauseMenu</c> find the vehicle
    /// by type on the frame the world loads — so the camera binding and the spawn capture would become
    /// a race. One instance, placed in the world scene as it always has been, keeps all of that
    /// untouched.</para>
    ///
    /// <para>What differs per body is the mesh, the collider box, the lamps, the wheel and the handling
    /// asset — and the engine note, which the handling asset carries rather than being a sixth. Track
    /// and wheelbase are shared by construction; see the note on <c>CarMeshBuilder.CarProfile</c> for
    /// why the anchors are in the same place on every one of them and the tyre hanging off them is
    /// not.</para>
    ///
    /// <para><b>The wheel is the one of those that is not parented to the body.</b> The four pivots
    /// belong to the chassis — the controller writes their position and spin every physics step — so
    /// swapping a shell cannot take its tyres with it the way it takes its headlight beams. The mesh has
    /// to be assigned here instead, and a body that forgot to would put a hatchback's 0.40 m tyre inside
    /// an off-roader's arch.</para>
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

            /// <summary>
            /// This car's tyre, for the four shared pivots. Two submeshes, tyre then rim, matching
            /// <c>CarMeshBuilder.BuildWheel</c> — so the materials already on the pivots keep working
            /// whichever wheel is in them.
            /// </summary>
            public Mesh WheelMesh;
        }

        [SerializeField] private Body[] bodies = new Body[0];

        [Tooltip("The four wheel pivots' mesh filters, in the controller's order. They live on the "
               + "chassis rather than on a body, so the wheel has to be swapped explicitly when the "
               + "shell changes.")]
        [SerializeField] private MeshFilter[] wheelFilters = new MeshFilter[0];

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
        /// Told to re-synthesise its drone after the config changes, because a diesel and a
        /// turbocharged six are different notes rather than one note at two pitches. Optional: a body
        /// set with no audio simply swaps in silence, which is what the editor preview does.
        /// </summary>
        [SerializeField] private EngineAudio engineAudio;

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

            if (body.WheelMesh != null)
            {
                for (int i = 0; i < wheelFilters.Length; i++)
                {
                    if (wheelFilters[i] != null)
                    {
                        wheelFilters[i].sharedMesh = body.WheelMesh;
                    }
                }
            }

            if (controller != null)
            {
                controller.SetConfig(body.Config);

                // After SetConfig, never before — it reads the config off the controller, and building
                // the old car's engine one more time is worse than not building it at all.
                if (engineAudio != null)
                {
                    engineAudio.RebuildClips();
                }
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
