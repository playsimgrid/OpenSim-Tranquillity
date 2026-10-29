/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using SkiaSharp;
using System.Globalization;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using log4net;
using System.Reflection;
using CoreJ2K;
using CoreJ2K.Configuration;

//using Cairo;

namespace OpenSim.Region.CoreModules.Scripting.VectorRender;

public class VectorRenderModule : ISharedRegionModule, IDynamicTextureRender
{
    // These fields exist for testing purposes, please do not remove.
//        private static bool s_flipper;
//        private static byte[] s_asset1Data;
//        private static byte[] s_asset2Data;

    private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private static object thisLock = new object();
    private static SKTypeface m_typeface = null; // SkiaSharp typeface for measurements

    /// <summary>
    /// Build the J2K encoder configuration for an image of the given size.
    /// </summary>
    /// <remarks>
    /// The tile size is pinned to the image dimensions so the codestream is always a
    /// SINGLE TILE. Second Life viewers cannot render multi-tile JPEG2000: the decoder
    /// derives geometry from the tile grid and consumes a discard level per grid halving
    /// (indra/llimagej2coj/llimagej2coj.cpp), so a tiled texture arrives as a blank grey
    /// prim with no error anywhere in the pipeline. A fixed 256x256 tile size did this to
    /// every dynamic texture larger than 256 in either dimension, while smaller ones came
    /// out single-tile by accident and worked - which made it read as an intermittent
    /// delivery fault rather than an encoding one.
    ///
    /// Pinned explicitly rather than relying on the library default, so an upstream
    /// change to that default cannot silently reintroduce the fault.
    ///
    /// Decomposition levels are 5, not the 4 this shipped with. That is alignment
    /// rather than a proven fix: of 130 single-tile textures measured in this grid's
    /// asset cache, 126 use 5 levels and every one at 512px or larger does, while no
    /// large texture known to render uses 4. The viewer clamps requested discard to
    /// MAX_DISCARD_LEVEL = 5 (indra/llimage/llimage.h:41), so 5 levels keeps every
    /// discard the viewer may ask for available in the codestream. It costs nothing --
    /// at 1024x1024 the output is smaller at 5 levels than at 4.
    /// </remarks>
    private static J2KEncoderConfiguration BuildEncoderConfig(int width, int height)
    {
        return new J2KEncoderConfiguration()
            // Emit a RAW J2C codestream, not a JP2 container.
            //
            // CoreJ2K wraps its output in the JP2 file format by default: a
            // signature box, an 'ftypjp2' brand box and a jp2h header box, with the
            // codestream buried in a jp2c box 85 bytes in. Second Life viewers decode
            // raw .j2c and cannot read that wrapper, so the face stays blank with no
            // error at any layer - the asset is valid, decodable, correctly sized and
            // single-tile, and still never renders.
            //
            // Stock OpenSimulator used OpenJPEG.EncodeFromImage, which always emitted
            // a raw codestream. The wrapper arrived with the CoreJ2K rewrite.
            //
            // Measured both ways on a 512x512 bitmap:
            //   default                 1932 bytes, starts 00 00 00 0c 6a 50 20 20
            //   WithFileFormat(false)   1847 bytes, starts ff 4f ff 51
            // The raw codestream is also smaller; the container was pure overhead.
            .WithFileFormat(false)
            .WithTiles(t => t.SetSize(width, height))
            .WithWavelet(w => w.UseIrreversible97().WithDecompositionLevels(5))
            .WithProgression(p => p.WithOrder(ProgressionOrder.LRCP).WithQualityLayers(0.1f, 0.5f, 1.0f));
    }

    private Scene m_scene;
    private IDynamicTextureManager m_textureManager;

    private string m_fontName = "Arial";

    public VectorRenderModule()
    {
    }

    #region IDynamicTextureRender Members

    public string GetContentType()
    {
        return "vector";
    }

    public string GetName()
    {
        return Name;
    }

    public bool SupportsAsynchronous()
    {
        return true;
    }

//        public bool AlwaysIdenticalConversion(string bodyData, string extraParams)
//        {
//            string[] lines = GetLines(bodyData);
//            return lines.Any((str, r) => str.StartsWith("Image"));
//        }

    public IDynamicTexture ConvertUrl(string url, string extraParams)
    {
        return null;
    }

    public IDynamicTexture ConvertData(string bodyData, string extraParams)
    {
        return Draw(bodyData, extraParams);
    }

    public bool AsyncConvertUrl(UUID id, string url, string extraParams)
    {
        return false;
    }

    public bool AsyncConvertData(UUID id, string bodyData, string extraParams)
    {
        if (m_textureManager == null)
        {
            m_log.Warn("[VECTORRENDERMODULE]: No texture manager. Can't function");
            return false;
        }
        // XXX: This isn't actually being done asynchronously!
        m_textureManager.ReturnData(id, ConvertData(bodyData, extraParams));

        return true;
    }

    public void GetDrawStringSize(string text, string fontName, int fontSize,
                                  out double xSize, out double ySize)
    {
        lock (thisLock)
        {
            var typeface = SKTypeface.FromFamilyName(fontName, SKFontStyle.Normal);
            using (var font = new SKFont(typeface, fontSize))
            {
                // GDI+ MeasureString measured the whole block, newlines included. SKFont
                // measures one run, so a multi-line string reported the width of every line
                // laid end to end and the height of a single line. Scripts centre text with
                // this, so the error moved the text as well as mis-sizing the texture.
                string[] lines = SplitTextLines(text);
                SKFontMetrics metrics = font.Metrics;

                float widest = 0f;
                foreach (string line in lines)
                {
                    float w = font.MeasureText(line);
                    if (w > widest)
                        widest = w;
                }

                xSize = (int)widest;
                ySize = (int)(metrics.Descent - metrics.Ascent + (lines.Length - 1) * LineHeight(metrics));
            }
        }
    }

    /// <summary>
    /// Parse color from hex or name
    /// </summary>
    private static SKColor ParseColor(string value, SKColor defaultColor)
    {
        int hex = 0;
        if (int.TryParse(value, System.Globalization.NumberStyles.HexNumber, 
            System.Globalization.CultureInfo.InvariantCulture, out hex))
        {
            return new SKColor((uint)hex);
        }
        
        return KnownColors.TryGetValue(value.Trim(), out uint argb) ? new SKColor(argb) : defaultColor;
    }

    /// <summary>
    /// The colour names a script may use, as GDI+ <c>Color.FromName</c> knew them.
    /// </summary>
    /// <remarks>
    /// Stock OpenSimulator resolved pen and background colours with
    /// <c>System.Drawing.Color.FromName</c>, which accepts the full HTML/X11 set. The
    /// SkiaSharp port (#143) replaced it with a nine-case switch - white, black, red, green,
    /// blue, yellow, cyan, magenta, gray - and everything else fell through to the default,
    /// which at both call sites is black. So a script asking for Lime, Orange, Navy, Gold,
    /// Silver, Teal, Pink or any of the other ~133 names drew in black, with no warning and
    /// no error: on the usual black background, invisible.
    ///
    /// That is what the bug looked like from in world - a script that draws correctly on
    /// every other grid producing an empty black rectangle here. Separately the text was also
    /// being positioned off-canvas; see DrawTextBlock. Both had to be fixed to get the texture
    /// back, and either alone still renders nothing.
    ///
    /// Generated from System.Drawing's KnownColor table (142 entries, system/UI colours
    /// excluded) rather than hand-typed, and embedded rather than looked up at runtime so
    /// this does not depend on System.Drawing being usable on the host.
    /// </remarks>
    private static readonly Dictionary<string, uint> KnownColors =
        new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            { "transparent", 0x00FFFFFFu },
            { "aliceblue", 0xFFF0F8FFu },
            { "antiquewhite", 0xFFFAEBD7u },
            { "aqua", 0xFF00FFFFu },
            { "aquamarine", 0xFF7FFFD4u },
            { "azure", 0xFFF0FFFFu },
            { "beige", 0xFFF5F5DCu },
            { "bisque", 0xFFFFE4C4u },
            { "black", 0xFF000000u },
            { "blanchedalmond", 0xFFFFEBCDu },
            { "blue", 0xFF0000FFu },
            { "blueviolet", 0xFF8A2BE2u },
            { "brown", 0xFFA52A2Au },
            { "burlywood", 0xFFDEB887u },
            { "cadetblue", 0xFF5F9EA0u },
            { "chartreuse", 0xFF7FFF00u },
            { "chocolate", 0xFFD2691Eu },
            { "coral", 0xFFFF7F50u },
            { "cornflowerblue", 0xFF6495EDu },
            { "cornsilk", 0xFFFFF8DCu },
            { "crimson", 0xFFDC143Cu },
            { "cyan", 0xFF00FFFFu },
            { "darkblue", 0xFF00008Bu },
            { "darkcyan", 0xFF008B8Bu },
            { "darkgoldenrod", 0xFFB8860Bu },
            { "darkgray", 0xFFA9A9A9u },
            { "darkgreen", 0xFF006400u },
            { "darkkhaki", 0xFFBDB76Bu },
            { "darkmagenta", 0xFF8B008Bu },
            { "darkolivegreen", 0xFF556B2Fu },
            { "darkorange", 0xFFFF8C00u },
            { "darkorchid", 0xFF9932CCu },
            { "darkred", 0xFF8B0000u },
            { "darksalmon", 0xFFE9967Au },
            { "darkseagreen", 0xFF8FBC8Fu },
            { "darkslateblue", 0xFF483D8Bu },
            { "darkslategray", 0xFF2F4F4Fu },
            { "darkturquoise", 0xFF00CED1u },
            { "darkviolet", 0xFF9400D3u },
            { "deeppink", 0xFFFF1493u },
            { "deepskyblue", 0xFF00BFFFu },
            { "dimgray", 0xFF696969u },
            { "dodgerblue", 0xFF1E90FFu },
            { "firebrick", 0xFFB22222u },
            { "floralwhite", 0xFFFFFAF0u },
            { "forestgreen", 0xFF228B22u },
            { "fuchsia", 0xFFFF00FFu },
            { "gainsboro", 0xFFDCDCDCu },
            { "ghostwhite", 0xFFF8F8FFu },
            { "gold", 0xFFFFD700u },
            { "goldenrod", 0xFFDAA520u },
            { "gray", 0xFF808080u },
            { "green", 0xFF008000u },
            { "greenyellow", 0xFFADFF2Fu },
            { "honeydew", 0xFFF0FFF0u },
            { "hotpink", 0xFFFF69B4u },
            { "indianred", 0xFFCD5C5Cu },
            { "indigo", 0xFF4B0082u },
            { "ivory", 0xFFFFFFF0u },
            { "khaki", 0xFFF0E68Cu },
            { "lavender", 0xFFE6E6FAu },
            { "lavenderblush", 0xFFFFF0F5u },
            { "lawngreen", 0xFF7CFC00u },
            { "lemonchiffon", 0xFFFFFACDu },
            { "lightblue", 0xFFADD8E6u },
            { "lightcoral", 0xFFF08080u },
            { "lightcyan", 0xFFE0FFFFu },
            { "lightgoldenrodyellow", 0xFFFAFAD2u },
            { "lightgray", 0xFFD3D3D3u },
            { "lightgreen", 0xFF90EE90u },
            { "lightpink", 0xFFFFB6C1u },
            { "lightsalmon", 0xFFFFA07Au },
            { "lightseagreen", 0xFF20B2AAu },
            { "lightskyblue", 0xFF87CEFAu },
            { "lightslategray", 0xFF778899u },
            { "lightsteelblue", 0xFFB0C4DEu },
            { "lightyellow", 0xFFFFFFE0u },
            { "lime", 0xFF00FF00u },
            { "limegreen", 0xFF32CD32u },
            { "linen", 0xFFFAF0E6u },
            { "magenta", 0xFFFF00FFu },
            { "maroon", 0xFF800000u },
            { "mediumaquamarine", 0xFF66CDAAu },
            { "mediumblue", 0xFF0000CDu },
            { "mediumorchid", 0xFFBA55D3u },
            { "mediumpurple", 0xFF9370DBu },
            { "mediumseagreen", 0xFF3CB371u },
            { "mediumslateblue", 0xFF7B68EEu },
            { "mediumspringgreen", 0xFF00FA9Au },
            { "mediumturquoise", 0xFF48D1CCu },
            { "mediumvioletred", 0xFFC71585u },
            { "midnightblue", 0xFF191970u },
            { "mintcream", 0xFFF5FFFAu },
            { "mistyrose", 0xFFFFE4E1u },
            { "moccasin", 0xFFFFE4B5u },
            { "navajowhite", 0xFFFFDEADu },
            { "navy", 0xFF000080u },
            { "oldlace", 0xFFFDF5E6u },
            { "olive", 0xFF808000u },
            { "olivedrab", 0xFF6B8E23u },
            { "orange", 0xFFFFA500u },
            { "orangered", 0xFFFF4500u },
            { "orchid", 0xFFDA70D6u },
            { "palegoldenrod", 0xFFEEE8AAu },
            { "palegreen", 0xFF98FB98u },
            { "paleturquoise", 0xFFAFEEEEu },
            { "palevioletred", 0xFFDB7093u },
            { "papayawhip", 0xFFFFEFD5u },
            { "peachpuff", 0xFFFFDAB9u },
            { "peru", 0xFFCD853Fu },
            { "pink", 0xFFFFC0CBu },
            { "plum", 0xFFDDA0DDu },
            { "powderblue", 0xFFB0E0E6u },
            { "purple", 0xFF800080u },
            { "red", 0xFFFF0000u },
            { "rosybrown", 0xFFBC8F8Fu },
            { "royalblue", 0xFF4169E1u },
            { "saddlebrown", 0xFF8B4513u },
            { "salmon", 0xFFFA8072u },
            { "sandybrown", 0xFFF4A460u },
            { "seagreen", 0xFF2E8B57u },
            { "seashell", 0xFFFFF5EEu },
            { "sienna", 0xFFA0522Du },
            { "silver", 0xFFC0C0C0u },
            { "skyblue", 0xFF87CEEBu },
            { "slateblue", 0xFF6A5ACDu },
            { "slategray", 0xFF708090u },
            { "snow", 0xFFFFFAFAu },
            { "springgreen", 0xFF00FF7Fu },
            { "steelblue", 0xFF4682B4u },
            { "tan", 0xFFD2B48Cu },
            { "teal", 0xFF008080u },
            { "thistle", 0xFFD8BFD8u },
            { "tomato", 0xFFFF6347u },
            { "turquoise", 0xFF40E0D0u },
            { "violet", 0xFFEE82EEu },
            { "wheat", 0xFFF5DEB3u },
            { "white", 0xFFFFFFFFu },
            { "whitesmoke", 0xFFF5F5F5u },
            { "yellow", 0xFFFFFF00u },
            { "yellowgreen", 0xFF9ACD32u },
            { "rebeccapurple", 0xFF663399u },
        };

    #endregion

    #region ISharedRegionModule Members

    public void Initialise(IConfigSource config)
    {
        IConfig cfg = config.Configs["VectorRender"];
        if (null != cfg)
        {
            m_fontName = cfg.GetString("font_name", m_fontName);
        }
        m_log.DebugFormat("[VECTORRENDERMODULE]: using font \"{0}\" for text rendering.", m_fontName);

        // We won't dispose of these explicitly since this module is only removed when the entire simulator
        // is shut down.
        lock(thisLock)
        {
            if(m_typeface == null)
            {
                m_typeface = SKTypeface.FromFamilyName(m_fontName, SKFontStyle.Normal);
            }
        }
    }

    public void PostInitialise()
    {
    }

    public void AddRegion(Scene scene)
    {
        if (m_scene == null)
        {
            m_scene = scene;
        }
    }

    public void RegionLoaded(Scene scene)
    {
        if (m_textureManager == null && m_scene == scene)
        {
            m_textureManager = m_scene.RequestModuleInterface<IDynamicTextureManager>();
            if (m_textureManager != null)
            {
                m_textureManager.RegisterRender(GetContentType(), this);
            }
        }
    }

    public void RemoveRegion(Scene scene)
    {
    }

    public void Close()
    {
    }

    public string Name
    {
        get { return "VectorRenderModule"; }
    }

    public Type ReplaceableInterface
    {
        get { return null; }
    }

    #endregion

    private IDynamicTexture Draw(string data, string extraParams)
    {
        // We need to cater for old scripts that didn't use extraParams neatly, they use either an integer size which represents both width and height, or setalpha
        // we will now support multiple comma separated params in the form  width:256,height:512,alpha:255
        int width = 256;
        int height = 256;
        int alpha = 255; // 0 is transparent
        SKColor bgColor = SKColors.White;  // Default background color
        char altDataDelim = ';';

        char[] paramDelimiter = { ',' };
        char[] nvpDelimiter = { ':' };

        extraParams = extraParams.Trim();
        extraParams = extraParams.ToLower();

        string[] nvps = extraParams.Split(paramDelimiter);

        int temp = -1;
        foreach (string pair in nvps)
        {
            string[] nvp = pair.Split(nvpDelimiter);
            string name = "";
            string value = "";

            if (nvp[0] != null)
            {
                name = nvp[0].Trim();
            }

            if (nvp.Length == 2)
            {
                value = nvp[1].Trim();
            }

            switch (name)
            {
                case "width":
                    temp = parseIntParam(value);
                    if (temp != -1)
                    {
                        if (temp < 1)
                        {
                            width = 1;
                        }
                        else if (temp > 2048)
                        {
                            width = 2048;
                        }
                        else
                        {
                            width = temp;
                        }
                    }
                    break;
                case "height":
                    temp = parseIntParam(value);
                    if (temp != -1)
                    {
                        if (temp < 1)
                        {
                            height = 1;
                        }
                        else if (temp > 2048)
                        {
                            height = 2048;
                        }
                        else
                        {
                            height = temp;
                        }
                    }
                    break;
                 case "alpha":
                      temp = parseIntParam(value);
                      if (temp != -1)
                      {
                          if (temp < 0)
                          {
                              alpha = 0;
                          }
                          else if (temp > 255)
                          {
                              alpha = 255;
                          }
                          else
                          {
                              alpha = temp;
                          }
                      }
                      // Allow a bitmap w/o the alpha component to be created
                      else if (value.ToLower() == "false") {
                           alpha = 256;
                      }
                      break;
                 case "bgcolor":
                 case "bgcolour":
                      int hex = 0;
                     if (Int32.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hex))
                     {
                         bgColor = new SKColor((uint)hex);
                     }
                     else
                     {
                         bgColor = ParseColor(value, SKColors.White);
                     }
                     break;
                case "altdatadelim":
                    altDataDelim = value.ToCharArray()[0];
                    break;
                case "":
                     // blank string has been passed do nothing just use defaults
                 break;
                 default: // this is all for backwards compat, all a bit ugly hopfully can be removed in future
                     // could be either set alpha or just an int
                     if (name == "setalpha")
                     {
                         alpha = 0; // set the texture to have transparent background (maintains backwards compat)
                     }
                     else
                     {
                         // this function used to accept an int on its own that represented both
                         // width and height, this is to maintain backwards compat, could be removed
                         // but would break existing scripts
                         temp = parseIntParam(name);
                         if (temp != -1)
                         {
                             if (temp > 1024)
                                temp = 1024;

                             if (temp < 128)
                                 temp = 128;

                             width = temp;
                             height = temp;
                         }
                     }
                     break;
            }
        }

        SKBitmap bitmap = null;
        SKCanvas canvas = null;
        bool reuseable = false;

        try
        {
            lock (this)
            {
                if (alpha == 256 && bgColor.Alpha != 255)
                    alpha = bgColor.Alpha;

                bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                canvas = new SKCanvas(bitmap);
                
                if (alpha == 256)
                {
                    canvas.Clear(bgColor);
                }
                else
                {
                    SKColor newbg = new SKColor(bgColor.Red, bgColor.Green, bgColor.Blue, (byte)alpha);
                    canvas.Clear(newbg);
                }
                GDIDraw(data, canvas, altDataDelim, out reuseable);
            }

            byte[] imageJ2000 = Array.Empty<byte>();

            // This code exists for testing purposes, please do not remove.
//                if (s_flipper)
//                    imageJ2000 = s_asset1Data;
//                else
//                    imageJ2000 = s_asset2Data;
//
//                s_flipper = !s_flipper;

            try
            {
                imageJ2000 = J2kImage.ToBytes(bitmap, BuildEncoderConfig(bitmap.Width, bitmap.Height));
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[VECTORRENDERMODULE]: Image Encoding Failed.  Exception {0}{1}",
                    e.Message, e.StackTrace);
            }

            return new OpenSim.Region.CoreModules.Scripting.DynamicTexture.DynamicTexture(
                data, extraParams, imageJ2000, (width, height), reuseable);
        }
        finally
        {
            lock (thisLock)
            {
                canvas?.Dispose();
                bitmap?.Dispose();
            }
        }
    }

    private int parseIntParam(string strInt)
    {
        int parsed;
        try
        {
            parsed = Convert.ToInt32(strInt);
        }
        catch (Exception)
        {
            //Ckrinke: Add a WriteLine to remove the warning about 'e' defined but not used
            // m_log.Debug("Problem with Draw. Please verify parameters." + e.ToString());
            parsed = -1;
        }

        return parsed;
    }

/*
    private void CairoDraw(string data, System.Drawing.Graphics graph)
    {
        using (Win32Surface draw = new Win32Surface(graph.GetHdc()))
        {
            Context contex = new Context(draw);

            contex.Antialias = Antialias.None;    //fastest method but low quality
            contex.LineWidth = 7;
            char[] lineDelimiter = { ';' };
            char[] partsDelimiter = { ',' };
            string[] lines = data.Split(lineDelimiter);

            foreach (string line in lines)
            {
                string nextLine = line.Trim();

                if (nextLine.StartsWith("MoveTO"))
                {
                    float x = 0;
                    float y = 0;
                    GetParams(partsDelimiter, ref nextLine, ref x, ref y);
                    contex.MoveTo(x, y);
                }
                else if (nextLine.StartsWith("LineTo"))
                {
                    float x = 0;
                    float y = 0;
                    GetParams(partsDelimiter, ref nextLine, ref x, ref y);
                    contex.LineTo(x, y);
                    contex.Stroke();
                }
            }
        }
        graph.ReleaseHdc();
    }
*/

    /// <summary>
    /// Split input data into discrete command lines.
    /// </summary>
    /// <returns></returns>
    /// <param name='data'></param>
    /// <param name='dataDelim'></param>
    /// <summary>
    /// Distance from one baseline to the next.
    /// </summary>
    private static float LineHeight(SKFontMetrics metrics)
    {
        return metrics.Descent - metrics.Ascent + metrics.Leading;
    }

    /// <summary>
    /// Split on newlines the way GDI+ did, tolerating CRLF.
    /// </summary>
    private static string[] SplitTextLines(string text)
    {
        return text.Replace("\r\n", "\n").Split('\n', '\r');
    }

    /// <summary>
    /// Draw text with <paramref name="top"/> as the TOP of the text box, splitting on
    /// embedded newlines.
    /// </summary>
    /// <remarks>
    /// This is what the pen position has always meant to a script. Stock OpenSimulator drew
    /// this command with GDI+ <c>Graphics.DrawString(text, font, brush, point)</c>, where the
    /// point is the upper-left corner of the text and the string is laid out line by line.
    /// The SkiaSharp port (#143) swapped in <c>SKCanvas.DrawText</c>, which takes the
    /// *baseline* and renders a single run, and kept the same coordinate - so the meaning of
    /// every osDrawText changed without the call looking any different.
    ///
    /// Measured on a 1024x128 canvas with the pen at 10,6 and a 24pt font, drawing three
    /// lines of text:
    ///
    ///   baseline at y, one run   1298 green px, 1 band, rows 0..6   (ascent is off-canvas,
    ///                                                                only descenders show)
    ///   top at y, one run        3561 green px, 1 band, rows 7..24  (the three lines collapse)
    ///   top at y, split lines    3561 green px, 3 bands, rows 7..72 (what every other grid draws)
    ///
    /// Both halves are needed: fixing only the origin still renders the three lines on top
    /// of one another as a single run.
    ///
    /// Note this moves existing text DOWN by the font's ascent. Content authored against the
    /// broken behaviour - which could only ever have been authored here - will shift; content
    /// authored anywhere else starts working.
    /// </remarks>
    private static void DrawTextBlock(SKCanvas canvas, string text, float left, float top, SKFont font, SKPaint paint)
    {
        SKFontMetrics metrics = font.Metrics;
        float lineHeight = LineHeight(metrics);
        float baseline = top - metrics.Ascent;   // Ascent is negative: this moves down from the top

        foreach (string line in SplitTextLines(text))
        {
            canvas.DrawText(line, left, baseline, SKTextAlign.Left, font, paint);
            baseline += lineHeight;
        }
    }

    private string[] GetLines(string data, char dataDelim)
    {
        char[] lineDelimiter = { dataDelim };
        return data.Split(lineDelimiter);
    }

    private void GDIDraw(string data, SKCanvas canvas, char dataDelim, out bool reuseable)
    {
        reuseable = true;
        SKPoint startPoint = new SKPoint(0, 0);
        SKPoint endPoint = new SKPoint(0, 0);
        SKPaint drawPaint = null;
        SKTypeface myTypeface = null;
        SKFont myFont = null;
        SKColor drawColor = SKColors.Black;

        try
        {
            drawPaint = new SKPaint
            {
                Color = SKColors.Black,
                StrokeWidth = 7,
                IsStroke = true,
                Style = SKPaintStyle.Stroke,
                StrokeCap = SKStrokeCap.Butt,
                StrokeJoin = SKStrokeJoin.Miter,
                IsAntialias = true
            };

            string fontName = m_fontName;
            float fontSize = 14;
            myTypeface = SKTypeface.FromFamilyName(fontName, SKFontStyle.Normal);
            myFont = new SKFont(myTypeface, fontSize);

            char[] partsDelimiter = {','};

            foreach (string line in GetLines(data, dataDelim))
            {
                string nextLine = line.TrimStart();

//                    m_log.DebugFormat("[VECTOR RENDER MODULE]: Processing line '{0}'", nextLine);

                if (nextLine.StartsWith("Text") && nextLine.Length > 5)
                {
                    int start = 4;
                    if (nextLine[4] == ' ')
                        start++;
                    if (start < nextLine.Length)
                    {
                        nextLine = nextLine.Substring(start);
                        var textPaint = new SKPaint
                        {
                            Color = drawColor,
                            IsAntialias = true
                        };
                        DrawTextBlock(canvas, nextLine, startPoint.X, startPoint.Y, myFont, textPaint);
                        textPaint.Dispose();
                    }
                    continue;
                }

                nextLine = nextLine.TrimEnd();
                if (nextLine.StartsWith("ResetTransf"))
                {
                    canvas.ResetMatrix();
                }
                else if (nextLine.StartsWith("TransTransf"))
                {
                    float x = 0;
                    float y = 0;
                    GetParams(partsDelimiter, ref nextLine, 11, ref x, ref y);
                    canvas.Translate(x, y);
                }
                else if (nextLine.StartsWith("ScaleTransf"))
                {
                    float x = 0;
                    float y = 0;
                    GetParams(partsDelimiter, ref nextLine, 11, ref x, ref y);
                    canvas.Scale(x, y);
                }
                else if (nextLine.StartsWith("RotTransf"))
                {
                    float x = 0;
                    GetParams(partsDelimiter, ref nextLine, 9, ref x);
                    canvas.RotateDegrees(x);
                }
                //replace with switch, or even better, do some proper parsing
                else if (nextLine.StartsWith("MoveTo"))
                {
                    float x = 0;
                    float y = 0;
                    GetParams(partsDelimiter, ref nextLine, 6, ref x, ref y);
                    startPoint = new SKPoint(x, y);
                }
                else if (nextLine.StartsWith("LineTo"))
                {
                    float x = 0;
                    float y = 0;
                    GetParams(partsDelimiter, ref nextLine, 6, ref x, ref y);
                    endPoint = new SKPoint(x, y);
                    canvas.DrawLine(startPoint, endPoint, drawPaint);
                    startPoint = endPoint;
                }
                else if (nextLine.StartsWith("Image"))
                {
                    // We cannot reuse any generated texture involving fetching an image via HTTP since that image
                    // can change.
                    reuseable = false;

                    float x = 0;
                    float y = 0;
                    GetParams(partsDelimiter, ref nextLine, 5, ref x, ref y);
                    endPoint = new SKPoint(x, y);

                    var image = ImageHttpRequest(nextLine);
                    if (image != null)
                    {
                        var srcRect = SKRect.Create(0, 0, image.Width, image.Height);
                        var dstRect = SKRect.Create(startPoint.X, startPoint.Y, x, y);
                        canvas.DrawImage(image, srcRect, dstRect);
                        image.Dispose();
                    }
                    else
                    {
                        var errorFont = new SKFont(SKTypeface.FromFamilyName(m_fontName, SKFontStyle.Normal), 6);
                        var errorPaint = new SKPaint
                        {
                            Color = drawColor,
                            IsAntialias = true
                        };
                        DrawTextBlock(canvas, "URL couldn't be resolved or is", startPoint.X, startPoint.Y, errorFont, errorPaint);
                        DrawTextBlock(canvas, "not an image. Please check URL.", startPoint.X, startPoint.Y + 12, errorFont, errorPaint);
                        errorPaint.Dispose();
                        errorFont.Dispose();

                        // Draw rectangle to show error area
                        var rectPaint = new SKPaint
                        {
                            Color = drawColor,
                            StrokeWidth = drawPaint.StrokeWidth,
                            Style = SKPaintStyle.Stroke,
                            IsAntialias = true
                        };
                        var rect = SKRect.Create(startPoint.X, startPoint.Y, endPoint.X, endPoint.Y);
                        canvas.DrawRect(rect, rectPaint);
                        rectPaint.Dispose();
                    }

                    startPoint = new SKPoint(startPoint.X + endPoint.X, startPoint.Y + endPoint.Y);
                }
                else if (nextLine.StartsWith("Rectangle"))
                {
                    float x = 0;
                    float y = 0;
                    GetParams(partsDelimiter, ref nextLine, 9, ref x, ref y);
                    endPoint = new SKPoint(x, y);
                    var rect = SKRect.Create(startPoint.X, startPoint.Y, endPoint.X, endPoint.Y);
                    canvas.DrawRect(rect, drawPaint);
                    startPoint = new SKPoint(startPoint.X + endPoint.X, startPoint.Y + endPoint.Y);
                }
                else if (nextLine.StartsWith("FillRectangle"))
                {
                    float x = 0;
                    float y = 0;
                    GetParams(partsDelimiter, ref nextLine, 13, ref x, ref y);
                    endPoint = new SKPoint(x, y);
                    var fillPaint = new SKPaint { Color = drawColor, Style = SKPaintStyle.Fill };
                    var rect = SKRect.Create(startPoint.X, startPoint.Y, endPoint.X, endPoint.Y);
                    canvas.DrawRect(rect, fillPaint);
                    fillPaint.Dispose();
                    startPoint = new SKPoint(startPoint.X + endPoint.X, startPoint.Y + endPoint.Y);
                }
                else if (nextLine.StartsWith("FillPolygon"))
                {
                    SKPoint[] points = null;
                    GetParams(partsDelimiter, ref nextLine, 11, ref points);
                    var fillPaint = new SKPaint { Color = drawColor, Style = SKPaintStyle.Fill };
                    using (var path = new SKPath())
                    {
                        if (points != null && points.Length > 0)
                        {
                            path.MoveTo(points[0]);
                            for (int i = 1; i < points.Length; i++)
                                path.LineTo(points[i]);
                            path.Close();
                            canvas.DrawPath(path, fillPaint);
                        }
                    }
                    fillPaint.Dispose();
                }
                else if (nextLine.StartsWith("Polygon"))
                {
                    SKPoint[] points = null;
                    GetParams(partsDelimiter, ref nextLine, 7, ref points);
                    using (var path = new SKPath())
                    {
                        if (points != null && points.Length > 0)
                        {
                            path.MoveTo(points[0]);
                            for (int i = 1; i < points.Length; i++)
                                path.LineTo(points[i]);
                            path.Close();
                            canvas.DrawPath(path, drawPaint);
                        }
                    }
                }
                else if (nextLine.StartsWith("Ellipse"))
                {
                    float x = 0;
                    float y = 0;
                    GetParams(partsDelimiter, ref nextLine, 7, ref x, ref y);
                    endPoint = new SKPoint(x, y);
                    var rect = SKRect.Create(startPoint.X, startPoint.Y, endPoint.X, endPoint.Y);
                    canvas.DrawOval(rect, drawPaint);
                    startPoint = new SKPoint(startPoint.X + endPoint.X, startPoint.Y + endPoint.Y);
                }
                else if (nextLine.StartsWith("FillEllipse"))
                {
                    float x = 0;
                    float y = 0;
                    GetParams(partsDelimiter, ref nextLine, 11, ref x, ref y);
                    endPoint = new SKPoint(x, y);
                    var fillPaint = new SKPaint { Color = drawColor, Style = SKPaintStyle.Fill };
                    var rect = SKRect.Create(startPoint.X, startPoint.Y, endPoint.X, endPoint.Y);
                    canvas.DrawOval(rect, fillPaint);
                    fillPaint.Dispose();
                    startPoint = new SKPoint(startPoint.X + endPoint.X, startPoint.Y + endPoint.Y);
                }
                else if (nextLine.StartsWith("FontSize"))
                {
                    nextLine = nextLine.Remove(0, 8);
                    nextLine = nextLine.Trim();
                    fontSize = Convert.ToSingle(nextLine, CultureInfo.InvariantCulture);

                    myFont?.Dispose();
                    myFont = new SKFont(myTypeface, fontSize);
                }
                else if (nextLine.StartsWith("FontProp"))
                {
                    SKFontStyle currentStyle = myTypeface.FontStyle;
                    bool bold = currentStyle == SKFontStyle.Bold || currentStyle == SKFontStyle.BoldItalic;
                    bool italic = currentStyle == SKFontStyle.Italic || currentStyle == SKFontStyle.BoldItalic;

                    nextLine = nextLine.Remove(0, 8);
                    nextLine = nextLine.Trim();

                    string[] fprops = nextLine.Split(partsDelimiter);
                    foreach (string prop in fprops)
                    {
                        switch (prop.Trim())
                        {
                            case "B":
                                bold = true;
                                break;
                            case "I":
                                italic = true;
                                break;
                            case "U":
                                // SkiaSharp doesn't directly support underline, would need custom drawing
                                break;
                            case "S":
                                // SkiaSharp doesn't directly support strikeout, would need custom drawing
                                break;
                            case "R":   //This special case resets all font properties
                                bold = false;
                                italic = false;
                                break;
                        }
                    }
                    SKFontStyle newStyle = SKFontStyle.Normal;
                    if (bold && italic)
                        newStyle = SKFontStyle.BoldItalic;
                    else if (bold)
                        newStyle = SKFontStyle.Bold;
                    else if (italic)
                        newStyle = SKFontStyle.Italic;

                    if (newStyle != currentStyle)
                    {
                        myTypeface?.Dispose();
                        myTypeface = SKTypeface.FromFamilyName(myTypeface.FamilyName, newStyle);
                        myFont?.Dispose();
                        myFont = new SKFont(myTypeface, fontSize);
                    }
                }
                else if (nextLine.StartsWith("FontName"))
                {
                    nextLine = nextLine.Remove(0, 8);
                    string newFontName = nextLine.Trim();
                    myTypeface?.Dispose();
                    myTypeface = SKTypeface.FromFamilyName(newFontName, SKFontStyle.Normal);
                    myFont?.Dispose();
                    myFont = new SKFont(myTypeface, fontSize);
                }
                else if (nextLine.StartsWith("PenSize"))
                {
                    nextLine = nextLine.Remove(0, 7);
                    nextLine = nextLine.Trim();
                    float size = Convert.ToSingle(nextLine, CultureInfo.InvariantCulture);
                    drawPaint.StrokeWidth = size;
                }
                else if (nextLine.StartsWith("PenCap"))
                {
                    bool start = true, end = true;
                    nextLine = nextLine.Remove(0, 6);
                    nextLine = nextLine.Trim();
                    string[] cap = nextLine.Split(partsDelimiter);
                    if (cap[0].ToLower() == "start")
                        end = false;
                    else if (cap[0].ToLower() == "end")
                        start = false;
                    else if (cap[0].ToLower() != "both")
                        return;
                    string type = cap[1].ToLower().Trim();

                    SKStrokeCap skCap = SKStrokeCap.Butt;
                    switch (type)
                    {
                        case "arrow":
                            // SkiaSharp doesn't have arrow caps, use round as fallback
                            skCap = SKStrokeCap.Round;
                            break;
                        case "round":
                            skCap = SKStrokeCap.Round;
                            break;
                        case "diamond":
                            // SkiaSharp doesn't have diamond caps, use round as fallback
                            skCap = SKStrokeCap.Round;
                            break;
                        case "flat":
                            skCap = SKStrokeCap.Butt;
                            break;
                    }
                    
                    if (end || start)
                    {
                        drawPaint.StrokeCap = skCap;
                    }
                }
                else if (nextLine.StartsWith("PenColour") || nextLine.StartsWith("PenColor"))
                {
                    nextLine = nextLine.Remove(0, 9);
                    nextLine = nextLine.Trim();
                    int hex = 0;

                    SKColor newColor;
                    if (Int32.TryParse(nextLine, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hex))
                    {
                        newColor = new SKColor((uint)hex);
                    }
                    else
                    {
                        newColor = ParseColor(nextLine, SKColors.Black);
                    }

                    drawColor = newColor;
                    drawPaint.Color = newColor;
                }
            }
        }
        finally
        {
            if (drawPaint != null)
                drawPaint.Dispose();

            if (myFont != null)
                myFont.Dispose();

            if (myTypeface != null)
                myTypeface.Dispose();
        }
    }

    private static void GetParams(char[] partsDelimiter, ref string line, int startLength, ref float x)
    {
        line = line.Remove(0, startLength);
        string[] parts = line.Split(partsDelimiter);
        if (parts.Length > 0)
        {
            string xVal = parts[0].Trim();
            x = Convert.ToSingle(xVal, CultureInfo.InvariantCulture);
        }
    }

    private static void GetParams(char[] partsDelimiter, ref string line, int startLength, ref float x, ref float y)
    {
        line = line.Remove(0, startLength);
        string[] parts = line.Split(partsDelimiter);
        if (parts.Length == 2)
        {
            string xVal = parts[0].Trim();
            string yVal = parts[1].Trim();
            x = Convert.ToSingle(xVal, CultureInfo.InvariantCulture);
            y = Convert.ToSingle(yVal, CultureInfo.InvariantCulture);
        }
        else if (parts.Length > 2)
        {
            string xVal = parts[0].Trim();
            string yVal = parts[1].Trim();
            x = Convert.ToSingle(xVal, CultureInfo.InvariantCulture);
            y = Convert.ToSingle(yVal, CultureInfo.InvariantCulture);

            line = "";
            for (int i = 2; i < parts.Length; i++)
            {
                line = line + parts[i].Trim();
                line = line + " ";
            }
        }
    }

    private static void GetParams(char[] partsDelimiter, ref string line, int startLength, ref SKPoint[] points)
    {
        line = line.Remove(0, startLength);
        string[] parts = line.Split(partsDelimiter);
        if (parts.Length > 1 && parts.Length % 2 == 0)
        {
            points = new SKPoint[parts.Length / 2];
            for (int i = 0; i < parts.Length; i = i + 2)
            {
                string xVal = parts[i].Trim();
                string yVal = parts[i+1].Trim();
                float x = Convert.ToSingle(xVal, CultureInfo.InvariantCulture);
                float y = Convert.ToSingle(yVal, CultureInfo.InvariantCulture);
                SKPoint point = new SKPoint(x, y);
                points[i / 2] = point;

//                    m_log.DebugFormat("[VECTOR RENDER MODULE]: Got point {0}", points[i / 2]);
            }
        }
    }

    private SKImage ImageHttpRequest(string url)
    {
        try
        {
            var handler = new HttpClientHandler();
            using (var client = new System.Net.Http.HttpClient(handler))
            {
                using (var response = client.GetAsync(url).Result)
                {
                    if (response.IsSuccessStatusCode)
                    {
                        using (var s = response.Content.ReadAsStreamAsync().Result)
                        {
                            var data = new byte[s.Length];
                            s.Read(data, 0, (int)s.Length);
                            return SKImage.FromEncodedData(data);
                        }
                    }
                }
            }
        }
        catch { }

        return null;
    }
}
