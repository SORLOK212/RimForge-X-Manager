using RimForge.Core.Parsing;

namespace RimForge.Core.Tests;

public class AboutXmlParserTests
{
    private const string Sample = """
        
        <?xml version="1.0" encoding="utf-8"?>
        <ModMetaData>
          <name>Designator Shapes</name>
          <author>merthsoft, Halno</author>
          <packageId>Merthsoft.DesignatorShapes</packageId>
          <supportedVersions><li>1.5</li><li>1.6</li></supportedVersions>
          <modDependencies>
            <li><packageId>Old.Dep</packageId><displayName>Old</displayName></li>
          </modDependencies>
          <modDependenciesByVersion>
            <v1.6>
              <li>
                <packageId>brrainz.harmony</packageId>
                <displayName>Harmony</displayName>
                <steamWorkshopUrl>https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077</steamWorkshopUrl>
              </li>
            </v1.6>
          </modDependenciesByVersion>
          <LoadAfter><li>Ludeon.RimWorld</li></LoadAfter>
          <incompatibleWith><li>Some.Other</li></incompatibleWith>
        </ModMetaData>
        """;

    [Fact]
    public void ReadsBasicFields_EvenWithLeadingWhitespaceAndOddCasing()
    {
        var about = AboutXmlParser.Parse(Sample);

        Assert.Equal("Designator Shapes", about.Name);
        Assert.Equal("Merthsoft.DesignatorShapes", about.PackageId);
        Assert.Equal(["merthsoft", "Halno"], about.Authors);
        Assert.Equal(["1.5", "1.6"], about.SupportedVersions);
        Assert.Equal(["ludeon.rimworld"], about.LoadAfter);
        Assert.Equal(["some.other"], about.IncompatibleWith);
    }

    [Fact]
    public void VersionedDependencies_ReplacePlainList()
    {
        var about = AboutXmlParser.Parse(Sample);

        var for16 = AboutXml.Resolve(about.Dependencies, about.DependenciesByVersion, "1.6");
        var for15 = AboutXml.Resolve(about.Dependencies, about.DependenciesByVersion, "1.5");

        Assert.Equal("brrainz.harmony", Assert.Single(for16).PackageId);
        Assert.Equal("old.dep", Assert.Single(for15).PackageId);
    }

    [Fact]
    public void AuthorsList_IsSupported()
    {
        var about = AboutXmlParser.Parse("<ModMetaData><authors><li>A</li><li>B</li></authors></ModMetaData>");
        Assert.Equal(["A", "B"], about.Authors);
    }
}
