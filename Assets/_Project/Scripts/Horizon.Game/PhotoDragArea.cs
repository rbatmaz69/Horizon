using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Horizon.Game
{
    /// <summary>
    /// <b>Its own file, and that is a hard Unity rule rather than tidiness.</b> Unity makes one
    /// <c>MonoScript</c> asset per <c>.cs</c> file, named after the file — so a <c>MonoBehaviour</c>
    /// declared in a file of another name has no asset for a scene to point at. Living inside
    /// <c>PhotoMode.cs</c>, <c>AddComponent&lt;PhotoDragArea&gt;()</c> could not serialise a reference
    /// to one: <c>Bootstrap.unity</c> came out carrying an <i>embedded</i> <c>!u!115 MonoScript</c> stub
    /// and a component whose <c>m_Script</c> was a local fileID with no guid, which is what
    /// "Script attached to 'DragSurface' … is missing or no valid script is attached" means. The photo
    /// page's drag has therefore never worked, in the editor or on a phone, since the day it was
    /// written — and the only thing that ever said so was one line in a build log.
    /// </summary>
    public sealed class PhotoDragArea : MonoBehaviour, IDragHandler
    {
        [SerializeField] private PhotoMode photo;

        /// <summary>Cached rather than walked for. A drag is every frame a finger is down.</summary>
        private CanvasScaler scaler;

        /// <summary>Hands the surface its target. Called by the setup tool.</summary>
        public void SetPhotoMode(PhotoMode value)
        {
            photo = value;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (photo == null)
            {
                return;
            }

            if (scaler == null)
            {
                scaler = GetComponentInParent<CanvasScaler>();
            }

            // Scaled out of screen pixels into canvas units, so the same swipe turns the camera by the
            // same amount on a phone and on a tablet. The scaler's reference height is 1080.
            float scale = scaler != null && Screen.height > 0
                ? scaler.referenceResolution.y / Screen.height
                : 1f;

            photo.Drag(eventData.delta * scale);
        }
    }
}
