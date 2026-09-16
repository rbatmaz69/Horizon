using System.IO;
using UnityEditor;
using UnityEngine;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Photographs the app icon the way a launcher will actually draw it.
    ///
    /// <para><b>The one picture this project could not take.</b> Every preview renderer here stands a
    /// camera in the world, in the HUD or in the garage; an icon is none of those. It is three PNGs on
    /// disk that a build hands to <c>PlayerSettings</c>, and what a phone puts on its home screen is a
    /// fourth image that exists nowhere — the foreground composited over the background, cropped to the
    /// two thirds Android guarantees, and masked. Looking at the source files tells you almost nothing
    /// about that: <c>AppIcon_Foreground.png</c> is a picture with its sky missing, and the flattened
    /// one is the composition the adaptive kinds never show.</para>
    ///
    /// <para><b>And the acceptance frame is the small one.</b> An icon is read at about 48 pixels among
    /// thirty others, which is smaller than anything else this project photographs; judged at 432 every
    /// icon looks fine. <c>IconPreview_Thumb.png</c> is the masked icon resampled to 48 and then blown
    /// back up without smoothing, so what is on screen is exactly the pixels the launcher has to work
    /// with. It is the same argument <c>DriverPreviewRenderer</c> makes from the other end, where
    /// shooting at two thirds of the game's resolution would make every artefact look two thirds as
    /// bad.</para>
    ///
    /// <para>The PNGs are read off disk and decoded here rather than sampled through
    /// <c>AssetDatabase</c>, because the importer leaves them unreadable — which is right for an asset
    /// that only ever goes into an APK, and would make <c>GetPixels</c> throw.</para>
    /// </summary>
    public static class IconPreviewRenderer
    {
        /// <summary>Where <see cref="AndroidBuild"/> generates the artwork.</summary>
        private const string IconFolder = "Assets/_Project/Art/Icon";

        /// <summary>
        /// Android's own 72 of 108: the share of an adaptive layer that survives every launcher mask.
        /// The same number <c>HorizonAssetUtility.IconContentScale</c> draws to, stated again here
        /// because this side has to crop to what that side drew to, and a second copy that can drift is
        /// better than a silent agreement nobody can see.
        /// </summary>
        private const float SafeFraction = 72f / 108f;

        /// <summary>How big the full-size frames are written.</summary>
        private const int Size = 432;

        /// <summary>What a home screen really gives an icon.</summary>
        private const int ThumbSize = 48;

        [MenuItem("Tools/Horizon/Render Icon Preview", priority = 53)]
        public static void Render()
        {
            Texture2D background = Decode($"{IconFolder}/AppIcon_Background.png");
            Texture2D foreground = Decode($"{IconFolder}/AppIcon_Foreground.png");
            Texture2D flattened = Decode($"{IconFolder}/AppIcon.png");

            if (background == null || foreground == null || flattened == null)
            {
                Debug.LogError(
                    $"[Horizon] No icon artwork in {IconFolder}. Run Tools > Horizon > Configure Android "
                    + "Player first — it is what generates it.");
                return;
            }

            Texture2D adaptive = Mask(Crop(Over(foreground, background), SafeFraction), Size);
            Texture2D legacy = Resample(flattened, Size);

            // Before the writes, because Write consumes what it is handed and the thumbnail is a
            // resample of the masked frame rather than a second pass over the layers.
            Texture2D thumb = Magnify(Resample(adaptive, ThumbSize), 4);

            Write(adaptive, "IconPreview_Adaptive.png");
            Write(legacy, "IconPreview_Legacy.png");
            Write(thumb, "IconPreview_Thumb.png");

            Object.DestroyImmediate(background);
            Object.DestroyImmediate(foreground);
            Object.DestroyImmediate(flattened);

            Debug.Log(
                "[Horizon] Icon preview: IconPreview_Adaptive.png (masked, as a launcher draws it), "
                + $"IconPreview_Legacy.png (full bleed), IconPreview_Thumb.png ({ThumbSize} px, which is "
                + "the size it is read at).");
        }

        /// <summary>Reads a PNG off disk into a readable texture, bypassing the importer entirely.</summary>
        private static Texture2D Decode(string assetPath)
        {
            if (!File.Exists(assetPath))
            {
                return null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(File.ReadAllBytes(assetPath)))
            {
                Object.DestroyImmediate(texture);
                return null;
            }

            return texture;
        }

        /// <summary>Source-over, straight alpha, and opaque at the end because a home screen is.</summary>
        private static Texture2D Over(Texture2D source, Texture2D destination)
        {
            int size = Mathf.Min(source.width, destination.width);
            Texture2D top = Resample(source, size);
            Texture2D bottom = Resample(destination, size);

            Color[] above = top.GetPixels();
            Color[] below = bottom.GetPixels();

            for (int i = 0; i < above.Length; i++)
            {
                float a = above[i].a;
                below[i] = new Color(
                    above[i].r * a + below[i].r * (1f - a),
                    above[i].g * a + below[i].g * (1f - a),
                    above[i].b * a + below[i].b * (1f - a),
                    1f);
            }

            bottom.SetPixels(below);
            bottom.Apply();

            Object.DestroyImmediate(top);
            return bottom;
        }

        /// <summary>Keeps the middle <paramref name="fraction"/> of a square texture.</summary>
        private static Texture2D Crop(Texture2D source, float fraction)
        {
            int size = Mathf.Max(1, Mathf.RoundToInt(source.width * fraction));
            int origin = (source.width - size) / 2;

            var cropped = new Texture2D(size, size, TextureFormat.RGBA32, false);
            cropped.SetPixels(source.GetPixels(origin, origin, size, size));
            cropped.Apply();

            Object.DestroyImmediate(source);
            return cropped;
        }

        /// <summary>
        /// The launcher's mask, as a circle.
        ///
        /// <para>A circle rather than one of Android's rounded squares because it is the harshest of
        /// them — anything that survives it survives the rest, and an icon judged against the gentlest
        /// mask is an icon judged against the one nobody's phone uses.</para>
        /// </summary>
        private static Texture2D Mask(Texture2D source, int size)
        {
            Texture2D scaled = Resample(source, size);
            Color[] pixels = scaled.GetPixels();

            float half = size * 0.5f;
            float pixel = 1f / half;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f - half) / half;
                    float v = (y + 0.5f - half) / half;

                    float inside = Mathf.Clamp01((0.995f - new Vector2(u, v).magnitude) / pixel);
                    pixels[y * size + x].a *= inside;
                }
            }

            scaled.SetPixels(pixels);
            scaled.Apply();

            Object.DestroyImmediate(source);
            return scaled;
        }

        /// <summary>
        /// A square resample: a box filter going down, bilinear going up.
        ///
        /// <para><b>The box half alone is what the first version had, and it made the tool lie.</b>
        /// Cropping 512 to its middle two thirds leaves 341, and writing that at 432 is an
        /// <i>up</i>scale — where a box whose footprint is under a pixel wide collapses to
        /// nearest-neighbour. Every diagonal in the icon came back with a staircase on it, and the icon
        /// was smooth the whole time. <i>Fix the instrument before trusting the reading.</i></para>
        /// </summary>
        private static Texture2D Resample(Texture2D source, int size)
        {
            if (source.width == size && source.height == size)
            {
                var copy = new Texture2D(size, size, TextureFormat.RGBA32, false);
                copy.SetPixels(source.GetPixels());
                copy.Apply();
                return copy;
            }

            var result = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];

            float step = source.width / (float)size;

            if (step <= 1f)
            {
                source.filterMode = FilterMode.Bilinear;

                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        pixels[y * size + x] =
                            source.GetPixelBilinear((x + 0.5f) / size, (y + 0.5f) / size);
                    }
                }

                result.SetPixels(pixels);
                result.Apply();
                return result;
            }

            for (int y = 0; y < size; y++)
            {
                int y0 = Mathf.FloorToInt(y * step);
                int y1 = Mathf.Max(y0 + 1, Mathf.FloorToInt((y + 1) * step));

                for (int x = 0; x < size; x++)
                {
                    int x0 = Mathf.FloorToInt(x * step);
                    int x1 = Mathf.Max(x0 + 1, Mathf.FloorToInt((x + 1) * step));

                    Color sum = Color.clear;
                    int taken = 0;

                    for (int sy = y0; sy < y1 && sy < source.height; sy++)
                    {
                        for (int sx = x0; sx < x1 && sx < source.width; sx++)
                        {
                            sum += source.GetPixel(sx, sy);
                            taken++;
                        }
                    }

                    pixels[y * size + x] = taken > 0 ? sum / taken : Color.clear;
                }
            }

            result.SetPixels(pixels);
            result.Apply();
            return result;
        }

        /// <summary>
        /// Blows a thumbnail up without smoothing, so the pixels stay countable.
        ///
        /// <para>A 48-pixel PNG opened in a viewer is scaled by whatever that viewer likes, which is
        /// usually a bilinear filter — and a bilinear filter is exactly what hides the blocking this
        /// frame exists to show.</para>
        /// </summary>
        private static Texture2D Magnify(Texture2D source, int factor)
        {
            int size = source.width * factor;
            var result = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    pixels[y * size + x] = source.GetPixel(x / factor, y / factor);
                }
            }

            result.SetPixels(pixels);
            result.Apply();

            Object.DestroyImmediate(source);
            return result;
        }

        /// <summary>Writes a frame beside the other previews, at the repo root.</summary>
        private static void Write(Texture2D texture, string fileName)
        {
            File.WriteAllBytes(fileName, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
        }
    }
}
