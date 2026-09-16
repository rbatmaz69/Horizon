using Horizon.Vehicle;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// How far this player has driven, kept across sessions.
    ///
    /// <para><b>The only number in this game that is a history rather than a state.</b> Everything else
    /// the player accumulates — the viewpoints they have stood at, their best lap — is a set of things
    /// done; this is the one that simply goes up, and in a game with no objective it is most of what
    /// makes a save feel like it belongs to somebody.</para>
    ///
    /// <para><b>Speed integrated, never position.</b> Every start, every respawn and every recovery from
    /// the water moves the car by kilometres in one frame, and a position delta would bill the driver
    /// for all of them — a player who spent an afternoon tapping through the start places would come out
    /// having driven further than one who crossed the world. <c>ForwardSpeed</c> is what the wheels
    /// actually did.</para>
    ///
    /// <para><b>Scaled time, so a paused game adds nothing.</b> That is the opposite of the rule
    /// <c>ScreenFade</c>, <c>PhotoMode</c> and <c>NetSession</c> follow, and for the opposite reason:
    /// they have to work while the world is stopped, and this is a measurement <i>of</i> the world.
    /// It falls out for free — <c>Time.deltaTime</c> is zero at <c>timeScale</c> zero.</para>
    ///
    /// <para><b>Written in steps rather than continuously.</b> <c>PlayerPrefs.Save</c> touches the disk,
    /// and doing that sixty times a second in the driving loop is exactly the kind of thing this
    /// project's performance budget forbids. What a killed app costs is the last few hundred metres,
    /// which is the right side to be wrong on.</para>
    /// </summary>
    public sealed class Odometer : MonoBehaviour
    {
        [Tooltip("Found at run time — the car arrives with the additive world load, so it cannot be "
               + "wired when this scene is built.")]
        [SerializeField] private VehicleController vehicle;

        [Tooltip("How much driving is allowed to go unwritten, metres.")]
        [SerializeField] private float writeStep = 500f;

        [Tooltip("Below this the car is standing still and the reading is float noise, not travel.")]
        [SerializeField] private float creepSpeed = 0.2f;

        private float unwritten;

        private void Update()
        {
            if (vehicle == null)
            {
                vehicle = FindFirstObjectByType<VehicleController>();
                if (vehicle == null)
                {
                    return;
                }
            }

            float speed = Mathf.Abs(vehicle.ForwardSpeed);
            if (speed < creepSpeed)
            {
                return;
            }

            unwritten += speed * Time.deltaTime;

            if (unwritten < writeStep)
            {
                return;
            }

            PlayerChoices.AddDistance(unwritten);
            unwritten = 0f;
        }
    }
}
