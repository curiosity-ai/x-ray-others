using System.Text;

namespace XRay.ThreeD.Tests;

/// <summary>Formats that are identified from their first bytes without a reader (JT, CATIA V5, Creo/Pro/ENGINEER), so that a file with the wrong or no extension is still named.</summary>
public class ContainerSniffTests
{
    [Fact]
    public void JtHeader()
    {
        var header = Encoding.ASCII.GetBytes("Version 9.5 JT".PadRight(80)).Concat(new byte[200]).ToArray();
        Assert.Equal("jt", ModelLoader.Detect(header, "part.bin").Id);
        Assert.Equal(FormatSupport.Full, ModelLoader.Detect(header, null).Support);
    }

    [Fact]
    public void CatiaV5Container()
    {
        var data = Encoding.ASCII.GetBytes("V5_CFV2\0").Concat(new byte[200]).ToArray();
        Assert.Equal("catia", ModelLoader.Detect(data, "x.dat").Id);
    }

    [Fact]
    public void CreoUgcHeader()
    {
        var data = Encoding.ASCII.GetBytes("#UGC:2 PRO_PART 20 99\n#-END_OF_UGC_HEADER\n").Concat(new byte[100]).ToArray();
        Assert.Equal("creo", ModelLoader.Detect(data, "x.dat").Id);
    }

    [Fact]
    public void PlainTextIsNotTakenForAny()
    {
        Assert.NotEqual("jt", ModelLoader.Detect(Encoding.ASCII.GetBytes("Version 2 of the notes\nabout the JT format"), null).Id);
        Assert.NotEqual("creo", ModelLoader.Detect(Encoding.ASCII.GetBytes("#UGC is not a header here, just a comment"), null).Id);
    }
}
