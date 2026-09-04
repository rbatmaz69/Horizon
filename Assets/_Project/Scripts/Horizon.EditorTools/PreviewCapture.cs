using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.Universal;

namespace Horizon.EditorTools
{
    /// <summary>
    /// The one place a preview frame is taken.
    ///
    /// <para><b>There were five copies of this, and two of them carried fixes the other three did
    /// not.</b> Every preview tool in this project built its own render target and called
    /// <c>camera.Render()</c> into it, which was harmless while the five agreed. They stopped agreeing:
    /// the map preview learned that a descriptor defaults to linear and had to ask for
    /// <see cref="RenderTextureDescriptor.sRGB"/> — "every frame came back dark and the accent orange
    /// came back red" — and that a target without a stencil does not clip a uGUI mask and does not
    /// complain either. Neither fix reached the other four, so the world and weather frames have been
    /// taken through a target with no stencil since the day they were written.</para>
    ///
    /// <para>This is the argument <c>TrunkForkBuilder.MouthHalfWidth</c> already makes: a second copy of
    /// a formula agrees with the first right up until one of them is wrong, and then the build goes on
    /// reporting the wrong one.</para>
    /// </summary>
    internal static class PreviewCapture
    {
        /// <summary>
        /// Renders <paramref name="camera"/> to a PNG at <paramref name="filePath"/>.
        /// </summary>
        /// <param name="msaa">
        /// Samples. One turns multisampling off, which the HUD shot needs.
        /// </param>
        /// <param name="post">
        /// Whether to run the post-processing stack. <b>True for anything photographing the world and
        /// false for anything photographing the canvas</b>, and that is not a preference — the game's
        /// HUD canvas is <c>ScreenSpaceOverlay</c>, which URP composites <i>after</i> post, so the tone
        /// map and the bloom never touch it. The HUD preview flips that canvas to
        /// <c>ScreenSpaceCamera</c> to photograph it at all; running post on that frame would tone map a
        /// HUD the player sees untouched, and the picture would be wrong in the one direction nobody
        /// would think to check.
        /// </param>
        /// <param name="fog">
        /// Whether to leave the scene's fog on. Off for overview and canvas shots: the world's
        /// exponential-squared fog turns a camera hundreds of metres back into a flat rectangle of fog
        /// colour, which is what the first course preview produced.
        /// </param>
        /// <param name="stencil">
        /// Whether the target needs a stencil buffer, which in practice means "is this a canvas shot".
        ///
        /// <para><b>The two target constructions are kept apart because unifying them broke the rain
        /// frames, and it broke them quietly.</b> A uGUI <c>Mask</c> clips by writing the stencil, so a
        /// target without one does not clip and does not complain — the round minimap was rebuilt as a
        /// square on the strength of a frame taken through such a target. The canvas shots therefore need
        /// an explicit descriptor. The world shots do not, and must keep the plain
        /// <c>RenderTexture.GetTemporary</c> they have always used: moving them onto the descriptor blew
        /// <c>M_SkyOvercast</c> from (139,152,132) to pure white in every overcast and rain frame, day and
        /// night, while leaving the procedural clear sky and the road pixel-identical. Three quarters of
        /// the frames were unchanged, which is exactly the shape of a regression that ships.</para>
        /// </param>
        internal static void Shoot(
            Camera camera,
            int width,
            int height,
            string filePath,
            int msaa = 4,
            bool post = true,
            bool fog = true,
            bool stencil = false)
        {
            // Post is off by default on a camera built with AddComponent, because renderPostProcessing
            // lives on UniversalAdditionalCameraData and a bare AddComponent<Camera>() has none. Every
            // preview camera in this project is built that way, so without this line every picture the
            // project takes of itself would show a world with no tone map — while the player's world had
            // one. A frame that cannot show the thing under test is worse than no frame.
            UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = post;

            RenderTexture renderTexture;

            if (stencil)
            {
                // `depth: 24` is documented as depth-plus-stencil and evidently is not always, so the
                // format is named rather than inferred. sRGB likewise: a descriptor defaults to linear
                // where the plain constructor takes the project's colour space, and left off every map
                // frame came back dark with the accent orange gone red.
                var descriptor = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 24)
                {
                    msaaSamples = Mathf.Max(1, msaa),
                    depthStencilFormat = GraphicsFormat.D24_UNorm_S8_UInt,
                    sRGB = true,
                };

                renderTexture = new RenderTexture(descriptor);
            }
            else
            {
                renderTexture = RenderTexture.GetTemporary(
                    width, height, 24, RenderTextureFormat.ARGB32);
                renderTexture.antiAliasing = Mathf.Max(1, msaa);
            }
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);

            RenderTexture previous = RenderTexture.active;
            bool fogWasOn = RenderSettings.fog;
            RenderSettings.fog = fog && fogWasOn;

            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();

                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                texture.Apply();

                File.WriteAllBytes(filePath, texture.EncodeToPNG());
            }
            finally
            {
                RenderSettings.fog = fogWasOn;
                camera.targetTexture = null;
                RenderTexture.active = previous;
                Object.DestroyImmediate(texture);

                if (stencil)
                {
                    renderTexture.Release();
                    Object.DestroyImmediate(renderTexture);
                }
                else
                {
                    RenderTexture.ReleaseTemporary(renderTexture);
                }
            }
        }
    }
}
