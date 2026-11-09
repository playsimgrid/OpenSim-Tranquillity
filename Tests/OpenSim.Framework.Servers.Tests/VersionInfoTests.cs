using System;
using System.Collections.Generic;
using System.Text;
using Xunit;
using OpenSim.Tests.Common;

namespace OpenSim.Framework.Servers.Tests
{
    public class VersionInfoTests : OpenSimTestCase
    {
        /// <summary>
        /// VERSIONINFO_VERSION_LENGTH is the PadRight width, which is a floor and not a cap -
        /// PadRight never truncates. The old assertion of exact equality held only by
        /// coincidence: a Debug build landed on exactly 48 characters, while a Release build of
        /// the same commit was already 50 and would have failed it. It never ran either way,
        /// because this project is not in Tranquillity.sln.
        /// </summary>
        [Fact]
        public void TestVersionIsAtLeastPaddedLength()
        {
            Assert.True(
                VersionInfo.Version.Length >= VersionInfo.VERSIONINFO_VERSION_LENGTH,
                $"VersionInfo.Version is {VersionInfo.Version.Length} chars, shorter than the "
                    + $"{VersionInfo.VERSIONINFO_VERSION_LENGTH}-char pad: '{VersionInfo.Version}'");
        }

        /// <summary>
        /// The reported version string is what a resident sees in the viewer and what
        /// `show version` prints, so it has to carry the number we release under - all four
        /// components. Nerdbank.GitVersioning renders the informational version three-part plus
        /// a short commit id, so reading the four-part number out of the assembly proves nothing
        /// about this string: that is exactly the gap this asserts against.
        /// </summary>
        [Fact]
        public void TestVersionCarriesTheFourPartReleaseNumber()
        {
            Assert.Equal(4, VersionInfo.AssemblyVersionNumber.Split('.').Length);

            Assert.Contains(VersionInfo.AssemblyVersionNumber, VersionInfo.Version);
        }

        [Theory]
        // Metadata present: the four-part number replaces the three-part one, metadata carried.
        [InlineData("1.0.49.10", "1.0.49+946836453c", "1.0.49.10+946836453c")]
        // No metadata to carry.
        [InlineData("1.0.49.10", "1.0.49", "1.0.49.10")]
        // Prerelease and metadata together - everything from '+' onwards is carried verbatim.
        [InlineData("1.0.50.1", "1.0.50-beta+abcdef1234", "1.0.50.1+abcdef1234")]
        // Degenerate inputs must not throw; the caller gets whatever is usable.
        [InlineData("", "1.0.49+946836453c", "1.0.49+946836453c")]
        [InlineData("1.0.49.10", "", "1.0.49.10")]
        public void TestComposeVersionNumber(string assemblyVersionNumber, string informationalVersion, string expected)
        {
            Assert.Equal(expected, VersionInfo.ComposeVersionNumber(assemblyVersionNumber, informationalVersion));
        }
    }
}
