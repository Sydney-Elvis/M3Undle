namespace M3Undle.Web.Application;

public interface IXmlTvSerializer
{
    IResult Serialize(RenderedLineup lineup);
}

public sealed class XmlTvSerializer : IXmlTvSerializer
{
    public IResult Serialize(RenderedLineup lineup)
    {
        if (string.IsNullOrWhiteSpace(lineup.XmltvPath) || !File.Exists(lineup.XmltvPath))
        {
            return TypedResults.Problem(
                "Active snapshot data is unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return TypedResults.PhysicalFile(
            lineup.XmltvPath,
            contentType: "application/xml; charset=utf-8");
    }
}
