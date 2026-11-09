





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

namespace OpenSim;

public class VersionInfo
{
    public const string VersionNumber = ThisAssembly.AssemblyInformationalVersion;
    public const string AssemblyVersionNumber = ThisAssembly.AssemblyFileVersion;
    public const string Configuration = ThisAssembly.AssemblyConfiguration;

    /// <summary>
    /// The release number as we number it, with whatever build metadata the informational
    /// version carries.
    /// </summary>
    /// <remarks>
    /// Nerdbank.GitVersioning renders AssemblyInformationalVersion from the first THREE
    /// components of version.json plus the short commit id - "1.0.49+946836453c" - so the
    /// fourth component, which is the only part that distinguishes one of our releases from
    /// the next, never reached the string a resident can see. AssemblyFileVersion carries all
    /// four. Compose the two: take the four-part number from the file version and carry over
    /// the "+..." metadata the informational version appends, if it appends any.
    ///
    /// Both inputs are compile-time constants, so this cannot vary between assemblies in one
    /// build; it is computed rather than declared const only because the splice is not a
    /// constant expression.
    /// </remarks>
    public static readonly string FullVersionNumber =
            ComposeVersionNumber(AssemblyVersionNumber, VersionNumber);

    public static string Version
    {
        get { return GetVersionString(FullVersionNumber, Configuration); }
    }

    /// <summary>
    /// Joins the four-part release number to the build metadata of a semver string.
    /// </summary>
    /// <param name="assemblyVersionNumber">Four-part number, e.g. "1.0.49.10".</param>
    /// <param name="informationalVersion">
    /// Semver string that may carry "+metadata", e.g. "1.0.49+946836453c".
    /// </param>
    /// <returns>
    /// The four-part number with the metadata appended - "1.0.49.10+946836453c" - or the
    /// four-part number alone when there is no metadata to carry.
    /// </returns>
    public static string ComposeVersionNumber(string assemblyVersionNumber, string informationalVersion)
    {
        if (string.IsNullOrEmpty(assemblyVersionNumber))
            return informationalVersion ?? string.Empty;

        if (string.IsNullOrEmpty(informationalVersion))
            return assemblyVersionNumber;

        int metadata = informationalVersion.IndexOf('+');
        return metadata < 0
                ? assemblyVersionNumber
                : assemblyVersionNumber + informationalVersion.Substring(metadata);
    }

    public static string GetVersionString(string versionNumber, string configuration)
    {
        string versionString = $"OpenSim-NGC Tranquillity {configuration} {versionNumber}";
        return versionString.PadRight(VERSIONINFO_VERSION_LENGTH);
    }

    public const int VERSIONINFO_VERSION_LENGTH = 48;

    /// <summary>
    /// This rules versioning regarding teleports, and compatibility between simulators in that regard.
    /// </summary>
    ///
    /// <remarks>
    /// The protocol version that we will use for outgoing transfers
    /// Valid values are
    /// "SIMULATION/0.8"
    ///     - up to 45 avatar textures - 11 baked
    /// "SIMULATION/0.7"
    ///     TP uses call back again
    /// "SIMULATION/0.3"
    ///   - supports teleports to variable-sized regions
    ///   - Older versions can teleport to this one, but only if the destination region
    ///     is 256x256
    /// "SIMULATION/0.2"
    ///   - A source simulator which only implements "SIMULATION/0.1" can still teleport here
    ///   - this protocol is more efficient than "SIMULATION/0.1"
    /// "SIMULATION/0.1"
    ///   - this is an older teleport protocol used in OpenSimulator 0.7.5 and before.
    /// </remarks>
    public readonly static float SimulationServiceVersionAcceptedMin = 0.3f;
    public readonly static float SimulationServiceVersionAcceptedMax = 0.8f;
    public readonly static float SimulationServiceVersionSupportedMin = 0.3f;
    public readonly static float SimulationServiceVersionSupportedMax = 0.8f;
}
